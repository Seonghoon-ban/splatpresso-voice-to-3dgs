// SPDX-License-Identifier: MIT
// Adapted from aras-p/UnityGaussianSplatting (MIT): the data-layout jobs mirror upstream's editor
// GaussianSplatAssetCreator ("VeryHigh" quality path). Ported from the SplatPresso research fork's
// GaussianSplatRuntimeAssetCreator, re-targeted at the UNMODIFIED upstream GaussianSplatAsset API.

using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using SplatPresso.Rendering.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SplatPresso.Rendering
{
    /// <summary>
    /// Builds <see cref="GaussianSplatAsset"/>s at runtime (in players too, not just the editor) from splat
    /// files or splat data, using only upstream's public API.
    /// </summary>
    /// <remarks>
    /// Always uses the lossless "VeryHigh" formats (Float32 pos/scale, Float32x4 color, Float32 SH) so that no
    /// chunking, BC7 compression or SH clustering is needed - those paths are editor-only. The data layouts
    /// must match what upstream's editor GaussianSplatAssetCreator produces for the same formats, i.e. what
    /// LoadSplatData in GaussianSplatting.hlsl expects:
    /// <list type="bullet">
    /// <item>pos: float3 per splat, tightly packed, buffer padded to a multiple of 8 bytes (matches editor serialization)</item>
    /// <item>other: uint (quaternion packed 10.10.10.2) + float3 scale, 16 bytes per splat</item>
    /// <item>color: float4 (dc0.rgb, opacity) texels, 16x16 Morton-swizzled tiles, 2048 wide</item>
    /// <item>sh: SHTableItemFloat32 per splat (15x float3 + padding), or a zeroed compact Norm6 table</item>
    /// </list>
    /// The four payload buffers become runtime TextAssets (<c>new TextAsset(ReadOnlySpan&lt;byte&gt;)</c>).
    /// The research fork patched GaussianSplatAsset with NativeArray payloads because "TextAssets can not be
    /// created from binary data in a player" - that premise is false on Unity 6, so no GS patch is needed.
    /// Lifetime: upstream GaussianSplatAsset has no OnDestroy, so destroying the asset alone would leak the
    /// TextAssets. Always release runtime assets with <see cref="DestroyRuntimeAsset"/>. Keep the asset alive
    /// while its renderer exists: GaussianSplatRenderer re-uploads from the asset on every OnEnable.
    /// All methods must be called on the main thread.
    /// </remarks>
    public static class RuntimeSplatAssetFactory
    {
        const int kOtherStride = 16; // 4 bytes rotation + 12 bytes float3 scale
        const uint kRuntimeHashTag = 0x52554E54u; // 'RUNT'

        static uint s_UniqueCounter = 1;
        // asset -> the TextAssets it owns (recorded here so they can be freed even if the asset was destroyed elsewhere)
        static readonly Dictionary<GaussianSplatAsset, TextAsset[]> s_RuntimeAssets = new Dictionary<GaussianSplatAsset, TextAsset[]>();

        // Domain reload may be disabled: statics must not survive into the next play session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_UniqueCounter = 1;
            s_RuntimeAssets.Clear();
        }

        /// <summary>Number of live assets created by this factory (diagnostics/tests).</summary>
        public static int RuntimeAssetCount => s_RuntimeAssets.Count;

        /// <summary>
        /// Reads a splat file (.ply) and creates a runtime asset from it.
        /// </summary>
        /// <param name="path">File path.</param>
        /// <param name="name">Asset name (null or empty: the file name).</param>
        /// <param name="hasSH">Whether the file contains higher-order SH data. If false, set
        /// <c>GaussianSplatRenderer.m_SHOrder = 0</c> for correctness/perf (the SH table is stored compactly as zeros).</param>
        /// <exception cref="IOException">Missing, unsupported, truncated or empty file.</exception>
        public static GaussianSplatAsset CreateFromFile(string path, string name, out bool hasSH)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Splat file path is empty", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Splat file not found", path);
            if (string.IsNullOrEmpty(name))
                name = Path.GetFileNameWithoutExtension(path);

            hasSH = SplatFileReader.FileHasSHCoefficients(path);
            SplatFileReader.ReadFile(path, out NativeArray<SplatInputData> splats);
            try
            {
                if (splats.Length == 0)
                    throw new IOException($"Splat file {path} contains no splats");
                return CreateFromSplats(splats, name, mortonReorder: true, compactEmptySH: !hasSH);
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
            }
        }

        /// <summary>
        /// Creates a runtime asset from linearized splat data (as produced by <see cref="SplatFileReader.ReadFile"/>).
        /// Returns null if <paramref name="splats"/> is empty.
        /// </summary>
        /// <param name="splats">Input splats. Ownership stays with the caller (dispose it after this call).
        /// When <paramref name="mortonReorder"/> is true the array is reordered in place.</param>
        /// <param name="name">Asset name.</param>
        /// <param name="mortonReorder">Sort splats along a Morton curve (better GPU cache locality), like the editor importer.</param>
        /// <param name="compactEmptySH">Use when the source has no higher-order SH (all zeros): the SH table is
        /// allocated in the compact Norm6 format (32 B/splat) instead of Float32 (192 B/splat). A zeroed Norm6 table
        /// decodes to zero without chunks and the SH order should be 0 anyway, so this only saves memory
        /// (~40 MB per 262k-splat object). Never pass true for data that has SH.</param>
        public static unsafe GaussianSplatAsset CreateFromSplats(NativeArray<SplatInputData> splats, string name, bool mortonReorder = true, bool compactEmptySH = false)
        {
            if (!splats.IsCreated)
                throw new ArgumentException("Splat array is not created", nameof(splats));
            int count = splats.Length;
            if (count == 0)
                return null;
            if (count > GaussianSplatAsset.kMaxSplats)
                throw new ArgumentException($"Too many splats ({count}); the renderer supports at most {GaussianSplatAsset.kMaxSplats}", nameof(splats));
            if (string.IsNullOrEmpty(name))
                name = "RuntimeSplat";

            NativeArray<byte> posData = default, otherData = default, colorData = default, shData = default;
            var textAssets = new List<TextAsset>(4);
            GaussianSplatAsset asset = null;
            try
            {
                // bounds
                float3 boundsMin, boundsMax;
                var boundsJob = new CalcBoundsJob
                {
                    m_BoundsMin = &boundsMin,
                    m_BoundsMax = &boundsMax,
                    m_SplatData = splats
                };
                boundsJob.Schedule().Complete();

                if (mortonReorder)
                    ReorderMorton(splats, boundsMin, boundsMax);

                // positions: float3, padded to multiple of 8 bytes (matches editor serialization)
                posData = new NativeArray<byte>(NextMultipleOf(count * 12, 8), Allocator.Persistent, NativeArrayOptions.ClearMemory);
                new CreatePositionsDataJob { m_Input = splats, m_Output = posData }.Schedule(count, 8192).Complete();

                // other: rotation (10.10.10.2 uint) + float3 scale = 16 bytes
                otherData = new NativeArray<byte>(NextMultipleOf(count * kOtherStride, 8), Allocator.Persistent, NativeArrayOptions.ClearMemory);
                new CreateOtherDataJob { m_Input = splats, m_Output = otherData }.Schedule(count, 8192).Complete();

                // color: float4 texture data, Morton-swizzled 16x16 tiles
                var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(count);
                colorData = new NativeArray<byte>(texWidth * texHeight * 16, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                new CreateColorDataJob { m_Input = splats, m_Output = colorData }.Schedule(count, 8192).Complete();

                // SH: SHTableItemFloat32 per splat, or a compact zeroed Norm6 table for SH0-only sources.
                // (Cleared NativeArray, not new byte[]: keeps ~8 MB off the managed heap.)
                var shFormat = compactEmptySH ? GaussianSplatAsset.SHFormat.Norm6 : GaussianSplatAsset.SHFormat.Float32;
                shData = new NativeArray<byte>((int)GaussianSplatAsset.CalcSHDataSize(count, shFormat), Allocator.Persistent, NativeArrayOptions.ClearMemory);
                if (!compactEmptySH)
                    new CreateSHDataJob { m_Input = splats, m_Output = shData }.Schedule(count, 8192).Complete();

                asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                asset.name = name;
                asset.Initialize(count,
                    GaussianSplatAsset.VectorFormat.Float32,
                    GaussianSplatAsset.VectorFormat.Float32,
                    GaussianSplatAsset.ColorFormat.Float32x4,
                    shFormat,
                    boundsMin, boundsMax, null);
                // hash only needs to be unique so that GaussianSplatRenderer.Update() change detection works
                asset.SetDataHash(new Hash128((uint)count, (uint)GaussianSplatAsset.kCurrentVersion, s_UniqueCounter++, kRuntimeHashTag));

                // Each TextAsset copies the bytes; dispose each source right away so the peak is +1 buffer, not +4.
                TextAsset pos = MakeTextAsset(ref posData, name + "_pos", textAssets);
                TextAsset oth = MakeTextAsset(ref otherData, name + "_oth", textAssets);
                TextAsset col = MakeTextAsset(ref colorData, name + "_col", textAssets);
                TextAsset shs = MakeTextAsset(ref shData, name + "_shs", textAssets);
                // No chunk data: upstream's own VeryHigh path. The renderer then uses a dummy chunk buffer with
                // chunk count 0 and the shaders skip chunk decoding. Param order: chunk, pos, other, color, sh.
                asset.SetAssetFiles(null, pos, oth, col, shs);

                s_RuntimeAssets.Add(asset, textAssets.ToArray());
                GaussianSplatAsset result = asset;
                asset = null;
                return result;
            }
            finally
            {
                if (posData.IsCreated) posData.Dispose();
                if (otherData.IsCreated) otherData.Dispose();
                if (colorData.IsCreated) colorData.Dispose();
                if (shData.IsCreated) shData.Dispose();
                if (asset != null)
                {
                    // failed half-way: do not leak the partial asset or its TextAssets
                    foreach (var ta in textAssets)
                        Kill(ta);
                    Kill(asset);
                }
            }
        }

        /// <summary>
        /// Destroys a runtime asset created by this factory together with its TextAssets (frees the CPU copy of
        /// the splat data). Does nothing for null or for assets not created here (imported project assets are
        /// never destroyed). Destroy or disable the renderer using it first (or in the same frame).
        /// </summary>
        public static void DestroyRuntimeAsset(GaussianSplatAsset asset)
        {
            // ReferenceEquals: an asset destroyed elsewhere compares == null but its TextAssets still need freeing.
            if (ReferenceEquals(asset, null))
                return;
            if (!s_RuntimeAssets.TryGetValue(asset, out TextAsset[] owned))
                return;
            s_RuntimeAssets.Remove(asset);
            foreach (var ta in owned)
                Kill(ta);
            Kill(asset);
        }

        /// <summary>True if the asset was created by this factory and has not been destroyed through it.</summary>
        public static bool IsRuntimeAsset(GaussianSplatAsset asset)
        {
            return !ReferenceEquals(asset, null) && s_RuntimeAssets.ContainsKey(asset);
        }

        static void Kill(Object o)
        {
            if (o == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(o);
            else
                Object.DestroyImmediate(o);
        }

        static TextAsset MakeTextAsset(ref NativeArray<byte> data, string name, List<TextAsset> created)
        {
            var ta = new TextAsset(data.AsReadOnlySpan()) { name = name };
            created.Add(ta);
            data.Dispose();
            data = default;
            return ta;
        }

        static int NextMultipleOf(int size, int multipleOf)
        {
            return (size + multipleOf - 1) / multipleOf * multipleOf;
        }

        // Helpers called from Burst jobs. Kept in a class without static fields: the factory itself holds managed
        // statics (the asset registry), which Burst code must never reach.
        static class Layout
        {
            public static uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
            {
                return (uint)(v.x * 1023.5f) | ((uint)(v.y * 1023.5f) << 10) | ((uint)(v.z * 1023.5f) << 20) | ((uint)(v.w * 3.5f) << 30);
            }

            public static int SplatIndexToTextureIndex(uint idx)
            {
                uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
                uint width = GaussianSplatAsset.kTextureWidth / 16;
                idx >>= 8;
                uint x = (idx % width) * 16 + xy.x;
                uint y = (idx / width) * 16 + xy.y;
                return (int)(y * GaussianSplatAsset.kTextureWidth + x);
            }
        }

        [BurstCompile]
        struct CalcBoundsJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMin;
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMax;
            [ReadOnly] public NativeArray<SplatInputData> m_SplatData;

            public unsafe void Execute()
            {
                float3 boundsMin = float.PositiveInfinity;
                float3 boundsMax = float.NegativeInfinity;
                for (int i = 0; i < m_SplatData.Length; ++i)
                {
                    float3 pos = m_SplatData[i].pos;
                    boundsMin = math.min(boundsMin, pos);
                    boundsMax = math.max(boundsMax, pos);
                }
                *m_BoundsMin = boundsMin;
                *m_BoundsMax = boundsMax;
            }
        }

        [BurstCompile]
        struct ReorderMortonJob : IJobParallelFor
        {
            const float kScaler = (float)((1 << 21) - 1);
            public float3 m_BoundsMin;
            public float3 m_InvBoundsSize;
            [ReadOnly] public NativeArray<SplatInputData> m_SplatData;
            public NativeArray<(ulong, int)> m_Order;

            public void Execute(int index)
            {
                float3 pos = ((float3)m_SplatData[index].pos - m_BoundsMin) * m_InvBoundsSize * kScaler;
                uint3 ipos = (uint3)pos;
                ulong code = GaussianUtils.MortonEncode3(ipos);
                m_Order[index] = (code, index);
            }
        }

        [BurstCompile]
        struct ApplyOrderJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SplatInputData> m_Source;
            [ReadOnly] public NativeArray<(ulong, int)> m_Order;
            [WriteOnly] public NativeArray<SplatInputData> m_Dest;

            public void Execute(int index)
            {
                m_Dest[index] = m_Source[m_Order[index].Item2];
            }
        }

        struct OrderComparer : IComparer<(ulong, int)>
        {
            public int Compare((ulong, int) a, (ulong, int) b)
            {
                if (a.Item1 < b.Item1) return -1;
                if (a.Item1 > b.Item1) return +1;
                return a.Item2 - b.Item2;
            }
        }

        static void ReorderMorton(NativeArray<SplatInputData> splatData, float3 boundsMin, float3 boundsMax)
        {
            // A flat axis (zero extent) would give inf/NaN codes; quantize it to 0 instead so the order stays meaningful.
            float3 size = boundsMax - boundsMin;
            float3 invSize = math.select(1.0f / size, float3.zero, size <= 0.0f);

            var order = new NativeArray<(ulong, int)>(splatData.Length, Allocator.TempJob);
            var copy = default(NativeArray<SplatInputData>);
            try
            {
                new ReorderMortonJob
                {
                    m_SplatData = splatData,
                    m_BoundsMin = boundsMin,
                    m_InvBoundsSize = invSize,
                    m_Order = order
                }.Schedule(splatData.Length, 4096).Complete();
                order.Sort(new OrderComparer());

                copy = new NativeArray<SplatInputData>(splatData, Allocator.TempJob);
                new ApplyOrderJob { m_Source = copy, m_Order = order, m_Dest = splatData }.Schedule(splatData.Length, 4096).Complete();
            }
            finally
            {
                if (copy.IsCreated) copy.Dispose();
                order.Dispose();
            }
        }

        [BurstCompile]
        struct CreatePositionsDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SplatInputData> m_Input;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                byte* outputPtr = (byte*)m_Output.GetUnsafePtr() + index * 12;
                float3 pos = m_Input[index].pos;
                *(float*)outputPtr = pos.x;
                *(float*)(outputPtr + 4) = pos.y;
                *(float*)(outputPtr + 8) = pos.z;
            }
        }

        [BurstCompile]
        struct CreateOtherDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SplatInputData> m_Input;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                byte* outputPtr = (byte*)m_Output.GetUnsafePtr() + index * kOtherStride;

                // rotation: already packed to smallest-3 0..1 range by SplatFileReader (LinearizeData)
                Quaternion rotQ = m_Input[index].rot;
                float4 rot = new float4(rotQ.x, rotQ.y, rotQ.z, rotQ.w);
                *(uint*)outputPtr = Layout.EncodeQuatToNorm10(rot);
                outputPtr += 4;

                // scale: linear scale, as-is
                float3 scale = m_Input[index].scale;
                *(float*)outputPtr = scale.x;
                *(float*)(outputPtr + 4) = scale.y;
                *(float*)(outputPtr + 8) = scale.z;
            }
        }

        [BurstCompile]
        struct CreateColorDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SplatInputData> m_Input;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                var splat = m_Input[index];
                int i = Layout.SplatIndexToTextureIndex((uint)index);
                float4* dst = (float4*)m_Output.GetUnsafePtr() + i;
                *dst = new float4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
            }
        }

        [BurstCompile]
        struct CreateSHDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SplatInputData> m_Input;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                var splat = m_Input[index];
                GaussianSplatAsset.SHTableItemFloat32 res;
                res.sh1 = splat.sh1;
                res.sh2 = splat.sh2;
                res.sh3 = splat.sh3;
                res.sh4 = splat.sh4;
                res.sh5 = splat.sh5;
                res.sh6 = splat.sh6;
                res.sh7 = splat.sh7;
                res.sh8 = splat.sh8;
                res.sh9 = splat.sh9;
                res.shA = splat.shA;
                res.shB = splat.shB;
                res.shC = splat.shC;
                res.shD = splat.shD;
                res.shE = splat.shE;
                res.shF = splat.shF;
                res.shPadding = default;
                ((GaussianSplatAsset.SHTableItemFloat32*)m_Output.GetUnsafePtr())[index] = res;
            }
        }
    }
}
