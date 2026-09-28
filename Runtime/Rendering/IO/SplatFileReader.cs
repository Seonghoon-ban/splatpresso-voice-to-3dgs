// SPDX-License-Identifier: MIT
// Adapted from aras-p/UnityGaussianSplatting (MIT), Editor/Utils/GaussianFileReader.cs.
// Changes vs upstream: runtime assembly + renamed types; PLY only (TripoSplat emits SH0-only PLY, SPZ is
// not needed); fixed a native-memory leak: upstream never disposed the raw PLY buffer (~18 MB per
// TripoSplat file, Allocator.Persistent) - leaked on every load and also when the attribute check threw.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace SplatPresso.Rendering.IO
{
    /// <summary>
    /// Reads gaussian splat files (binary little-endian PLY) into <see cref="SplatInputData"/>, linearized
    /// the same way upstream's editor importer does before building an asset.
    /// </summary>
    [BurstCompile]
    public static class SplatFileReader
    {
        // Managed lookup tables live in a separate (non-Burst) class: this class holds Burst direct-call entry
        // points and must stay free of managed static state.
        static class AttributeNames
        {
            public static readonly string[] Required =
            {
                "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
                "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3",
            };

            // File attribute feeding each float of SplatInputData, in struct order (62 entries).
            public static readonly string[] PerSplatFloat = Build();

            static string[] Build()
            {
                var names = new List<string> { "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2" };
                for (int i = 0; i < 45; ++i)
                    names.Add("f_rest_" + i.ToString(CultureInfo.InvariantCulture));
                names.AddRange(new[] { "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" });
                return names.ToArray();
            }
        }

        /// <summary>True if the file has an extension this reader supports (.ply).</summary>
        public static bool IsSupportedFile(string filePath) => IsPly(filePath);

        /// <summary>Returns the splat count declared in the file header, or 0 if the file is missing or unsupported.</summary>
        public static int ReadFileHeader(string filePath)
        {
            int vertexCount = 0;
            if (File.Exists(filePath) && IsPly(filePath))
                SplatPlyReader.ReadFileHeader(filePath, out vertexCount, out _, out _);
            return vertexCount;
        }

        /// <summary>
        /// True if the file contains higher-order SH coefficients (<c>f_rest_0</c>). When false, render with
        /// <c>m_SHOrder = 0</c> and the SH table can be stored compactly (see <see cref="RuntimeSplatAssetFactory"/>).
        /// </summary>
        public static bool FileHasSHCoefficients(string filePath)
        {
            if (IsPly(filePath))
            {
                SplatPlyReader.ReadFileHeader(filePath, out _, out _, out List<(string, SplatPlyReader.ElementType)> attrs);
                return attrs.Contains(("f_rest_0", SplatPlyReader.ElementType.Float));
            }
            return true; // other formats (upstream: spz etc.) - assume yes
        }

        /// <summary>
        /// Reads and linearizes all splats of a PLY file. <paramref name="splats"/> is allocated with
        /// <see cref="Allocator.Persistent"/>; the caller owns it and must dispose it.
        /// </summary>
        /// <exception cref="IOException">Unsupported format, missing gaussian properties or a truncated file.</exception>
        public static unsafe void ReadFile(string filePath, out NativeArray<SplatInputData> splats)
        {
            splats = default;
            if (!IsPly(filePath))
                throw new IOException($"File {filePath} is not a supported format (only binary little-endian .ply)");

            NativeArray<byte> plyRawData = default;
            NativeArray<SplatInputData> result = default;
            try
            {
                SplatPlyReader.ReadFile(filePath, out int splatCount, out int vertexStride, out List<(string, SplatPlyReader.ElementType)> attributes, out plyRawData);
                string attrError = CheckPLYAttributes(attributes);
                if (!string.IsNullOrEmpty(attrError))
                    throw new IOException($"PLY file is probably not a Gaussian Splat file? Missing properties: {attrError}");
                result = PLYDataToSplats(plyRawData, splatCount, vertexStride, attributes);
                ReorderSHs(splatCount, (float*)result.GetUnsafePtr());
                LinearizeData(result);
                splats = result;
                result = default; // ownership passed to the caller
            }
            finally
            {
                if (plyRawData.IsCreated)
                    plyRawData.Dispose();
                if (result.IsCreated)
                    result.Dispose();
            }
        }

        static bool IsPly(string filePath) => !string.IsNullOrEmpty(filePath) && filePath.EndsWith(".ply", true, CultureInfo.InvariantCulture);

        static string CheckPLYAttributes(List<(string, SplatPlyReader.ElementType)> attributes)
        {
            var missing = new List<string>();
            foreach (string req in AttributeNames.Required)
            {
                if (!attributes.Contains((req, SplatPlyReader.ElementType.Float)))
                    missing.Add(req);
            }
            return missing.Count == 0 ? null : string.Join(",", missing);
        }

        static unsafe NativeArray<SplatInputData> PLYDataToSplats(NativeArray<byte> input, int count, int stride, List<(string, SplatPlyReader.ElementType)> attributes)
        {
            string[] splatAttributes = AttributeNames.PerSplatFloat;
            Assert.AreEqual(UnsafeUtility.SizeOf<SplatInputData>() / 4, splatAttributes.Length);

            var fileAttrOffsets = new NativeArray<int>(attributes.Count, Allocator.Temp);
            var srcOffsets = new NativeArray<int>(splatAttributes.Length, Allocator.Temp);
            try
            {
                int offset = 0;
                for (int ai = 0; ai < attributes.Count; ai++)
                {
                    fileAttrOffsets[ai] = offset;
                    offset += SplatPlyReader.TypeToSize(attributes[ai].Item2);
                }

                // missing optional attributes (normals, f_rest_*) get -1 and stay zero in the output
                for (int ai = 0; ai < splatAttributes.Length; ai++)
                {
                    int attrIndex = attributes.IndexOf((splatAttributes[ai], SplatPlyReader.ElementType.Float));
                    srcOffsets[ai] = attrIndex >= 0 ? fileAttrOffsets[attrIndex] : -1;
                }

                var dst = new NativeArray<SplatInputData>(count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                ReorderPLYData(count, (byte*)input.GetUnsafeReadOnlyPtr(), stride, (byte*)dst.GetUnsafePtr(), UnsafeUtility.SizeOf<SplatInputData>(), (int*)srcOffsets.GetUnsafeReadOnlyPtr());
                return dst;
            }
            finally
            {
                fileAttrOffsets.Dispose();
                srcOffsets.Dispose();
            }
        }

        [BurstCompile]
        static unsafe void ReorderPLYData(int splatCount, byte* src, int srcStride, byte* dst, int dstStride, int* srcOffsets)
        {
            for (int i = 0; i < splatCount; i++)
            {
                for (int attr = 0; attr < dstStride / 4; attr++)
                {
                    if (srcOffsets[attr] >= 0)
                        *(int*)(dst + attr * 4) = *(int*)(src + srcOffsets[attr]);
                }
                src += srcStride;
                dst += dstStride;
            }
        }

        // PLY stores SH as all R coefficients, then all G, then all B; the renderer wants RGB triplets.
        [BurstCompile]
        static unsafe void ReorderSHs(int splatCount, float* data)
        {
            int splatStride = UnsafeUtility.SizeOf<SplatInputData>() / 4;
            int shStartOffset = 9, shCount = 15;
            float* tmp = stackalloc float[shCount * 3];
            int idx = shStartOffset;
            for (int i = 0; i < splatCount; ++i)
            {
                for (int j = 0; j < shCount; ++j)
                {
                    tmp[j * 3 + 0] = data[idx + j];
                    tmp[j * 3 + 1] = data[idx + j + shCount];
                    tmp[j * 3 + 2] = data[idx + j + shCount * 2];
                }

                for (int j = 0; j < shCount * 3; ++j)
                {
                    data[idx + j] = tmp[j];
                }

                idx += splatStride;
            }
        }

        [BurstCompile]
        struct LinearizeDataJob : IJobParallelFor
        {
            public NativeArray<SplatInputData> splatData;

            public void Execute(int index)
            {
                var splat = splatData[index];

                // rot
                var q = splat.rot;
                var qq = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
                qq = GaussianUtils.PackSmallest3Rotation(qq);
                splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

                // scale
                splat.scale = GaussianUtils.LinearScale(splat.scale);

                // color
                splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
                splat.opacity = GaussianUtils.Sigmoid(splat.opacity);

                splatData[index] = splat;
            }
        }

        static void LinearizeData(NativeArray<SplatInputData> splatData)
        {
            var job = new LinearizeDataJob { splatData = splatData };
            job.Schedule(splatData.Length, 4096).Complete();
        }
    }
}
