using System;
using System.IO;
using System.Reflection;
using GaussianSplatting.Runtime;
using NUnit.Framework;
using SplatPresso.Rendering;
using SplatPresso.Rendering.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SplatPresso.Tests
{
    /// <summary>
    /// PLY reading and runtime asset creation on the TripoSplat-like fixture (8192 splats, SH0 only, +/-inf
    /// opacity logits). The runtime factory must produce exactly the byte layout of upstream's editor importer at
    /// VeryHigh quality (no chunks), which is the only layout the unmodified renderer decodes without chunk data.
    /// </summary>
    public class SplatAssetTests
    {
        const int kSplats = 8192;

        static string PlyPath => EditorTestUtil.RequireFixture("object.ply");

        [Test]
        public void Header_ReportsCountAndNoSH()
        {
            Assert.AreEqual(kSplats, SplatFileReader.ReadFileHeader(PlyPath));
            Assert.IsFalse(SplatFileReader.FileHasSHCoefficients(PlyPath), "TripoSplat writes no f_rest_* properties");
            Assert.IsTrue(SplatFileReader.IsSupportedFile(PlyPath));
            Assert.IsFalse(SplatFileReader.IsSupportedFile("model.glb"));
        }

        [Test]
        public void ReadFile_LinearizesTheSplats()
        {
            SplatFileReader.ReadFile(PlyPath, out NativeArray<SplatInputData> splats);
            try
            {
                Assert.AreEqual(kSplats, splats.Length);
                int opaque = 0, invisible = 0;
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                for (int i = 0; i < splats.Length; i++)
                {
                    var s = splats[i];
                    min = Vector3.Min(min, s.pos);
                    max = Vector3.Max(max, s.pos);
                    // opacity is a sigmoid of the logit: +inf -> 1, -inf -> 0, never NaN
                    Assert.IsFalse(float.IsNaN(s.opacity), "opacity NaN at " + i);
                    Assert.That(s.opacity, Is.InRange(0f, 1f));
                    if (s.opacity >= 1f) opaque++;
                    if (s.opacity <= 0f) invisible++;
                    // scale is exp(log scale)
                    Assert.That(s.scale.x, Is.GreaterThan(0f).And.LessThan(0.05f));
                    // rotations are normalized, swizzled (rot_0 is w) and packed "smallest three": the fixture's
                    // quaternions are near-identity but NOT unit length, so the dropped (largest) component must be
                    // w (index 3 -> packed w = 3/3) and the kept ones must remap near 0.5
                    Assert.That(s.rot.w, Is.EqualTo(1f).Within(1e-6f), "largest component index at " + i);
                    Assert.That(s.rot.x, Is.InRange(0.3f, 0.7f));
                    Assert.That(s.rot.y, Is.InRange(0.3f, 0.7f));
                    Assert.That(s.rot.z, Is.InRange(0.3f, 0.7f));
                    // DC color is converted from SH to 0..1 (the fixture is a red chair)
                    Assert.That(s.dc0.x, Is.GreaterThan(s.dc0.y));
                }
                Assert.GreaterOrEqual(opaque, 1000, "the fixture has ~30% +inf opacity logits");
                Assert.GreaterOrEqual(invisible, 10, "the fixture has 10 -inf opacity logits");
                // normalized like TripoSplat output: longest axis spans [-0.5, 0.5]
                Assert.That(min.y, Is.EqualTo(-0.5f).Within(1e-4f));
                Assert.That(max.y, Is.EqualTo(0.5f).Within(1e-4f));
                Assert.That(max.x - min.x, Is.EqualTo(0.5f).Within(1e-3f));
            }
            finally
            {
                splats.Dispose();
            }
        }

        [Test]
        public void ReadFile_RejectsNonPly()
        {
            Assert.Throws<IOException>(() => SplatFileReader.ReadFile(EditorTestUtil.RequireFixture("object.glb"), out _));
        }

        [Test]
        public void CreateFromFile_BuildsAVeryHighAssetWithCompactSH()
        {
            int before = RuntimeSplatAssetFactory.RuntimeAssetCount;
            var asset = RuntimeSplatAssetFactory.CreateFromFile(PlyPath, "fixture", out bool hasSH);
            try
            {
                Assert.NotNull(asset);
                Assert.IsFalse(hasSH);
                Assert.AreEqual("fixture", asset.name);
                Assert.AreEqual(kSplats, asset.splatCount);
                Assert.AreEqual(GaussianSplatAsset.kCurrentVersion, asset.formatVersion);
                Assert.AreEqual(GaussianSplatAsset.VectorFormat.Float32, asset.posFormat);
                Assert.AreEqual(GaussianSplatAsset.VectorFormat.Float32, asset.scaleFormat);
                Assert.AreEqual(GaussianSplatAsset.ColorFormat.Float32x4, asset.colorFormat);
                Assert.AreEqual(GaussianSplatAsset.SHFormat.Norm6, asset.shFormat, "SH0-only sources use the compact zeroed Norm6 table");
                Assert.IsNull(asset.chunkData, "no chunks: upstream's VeryHigh path");

                // byte sizes of the four buffers
                var (texW, texH) = GaussianSplatAsset.CalcTextureSize(kSplats);
                Assert.AreEqual(NextMultipleOf(kSplats * 12, 8), asset.posData.dataSize, "pos");
                Assert.AreEqual(kSplats * 16, asset.otherData.dataSize, "other");
                Assert.AreEqual(texW * texH * 16, asset.colorData.dataSize, "color");
                Assert.AreEqual(2048 * 16 * 16, asset.colorData.dataSize, "color texture is 2048 x 16 for 8192 splats");
                Assert.AreEqual(kSplats * 32, asset.shData.dataSize, "sh (Norm6)");
                Assert.AreEqual(GaussianSplatAsset.CalcSHDataSize(kSplats, GaussianSplatAsset.SHFormat.Norm6), asset.shData.dataSize);

                // bounds of the normalized chair
                Assert.That(asset.boundsMin.y, Is.EqualTo(-0.5f).Within(1e-4f));
                Assert.That(asset.boundsMax.y, Is.EqualTo(0.5f).Within(1e-4f));
                Assert.That(asset.boundsMin.x, Is.EqualTo(-0.25f).Within(1e-3f));
                Assert.That(asset.boundsMax.z, Is.EqualTo(0.25f).Within(1e-3f));

                Assert.IsTrue(RuntimeSplatAssetFactory.IsRuntimeAsset(asset));
                Assert.AreEqual(before + 1, RuntimeSplatAssetFactory.RuntimeAssetCount);
            }
            finally
            {
                RuntimeSplatAssetFactory.DestroyRuntimeAsset(asset);
            }
        }

        [Test]
        public void DestroyRuntimeAsset_FreesTheAssetAndItsTextAssets()
        {
            int before = RuntimeSplatAssetFactory.RuntimeAssetCount;
            var asset = RuntimeSplatAssetFactory.CreateFromFile(PlyPath, null, out _);
            Assert.AreEqual("object", asset.name, "the file name is the default asset name");
            TextAsset[] owned = { asset.posData, asset.otherData, asset.colorData, asset.shData };
            foreach (var ta in owned)
                Assert.IsTrue(ta != null);

            RuntimeSplatAssetFactory.DestroyRuntimeAsset(asset);

            Assert.IsTrue(asset == null, "the asset is destroyed");
            foreach (var ta in owned)
                Assert.IsTrue(ta == null, "every TextAsset is destroyed (upstream GaussianSplatAsset has no OnDestroy)");
            Assert.IsFalse(RuntimeSplatAssetFactory.IsRuntimeAsset(asset));
            Assert.AreEqual(before, RuntimeSplatAssetFactory.RuntimeAssetCount);

            Assert.DoesNotThrow(() => RuntimeSplatAssetFactory.DestroyRuntimeAsset(asset), "destroying twice is a no-op");
            Assert.DoesNotThrow(() => RuntimeSplatAssetFactory.DestroyRuntimeAsset(null));
        }

        [Test]
        public void DestroyRuntimeAsset_NeverDestroysForeignAssets()
        {
            var foreign = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            try
            {
                Assert.IsFalse(RuntimeSplatAssetFactory.IsRuntimeAsset(foreign));
                RuntimeSplatAssetFactory.DestroyRuntimeAsset(foreign);
                Assert.IsTrue(foreign != null, "imported / project assets must survive");
            }
            finally
            {
                Object.DestroyImmediate(foreign);
            }
        }

        [Test]
        public void CreateFromSplats_WithoutCompactSH_UsesAFloat32Table()
        {
            SplatFileReader.ReadFile(PlyPath, out NativeArray<SplatInputData> splats);
            GaussianSplatAsset asset = null;
            try
            {
                asset = RuntimeSplatAssetFactory.CreateFromSplats(splats, "full-sh", mortonReorder: false, compactEmptySH: false);
                Assert.AreEqual(GaussianSplatAsset.SHFormat.Float32, asset.shFormat);
                Assert.AreEqual(kSplats * 192, asset.shData.dataSize);
            }
            finally
            {
                splats.Dispose();
                RuntimeSplatAssetFactory.DestroyRuntimeAsset(asset);
            }
        }

        [Test]
        public void CreateFromFile_MissingFile_Throws()
        {
            Assert.Throws<FileNotFoundException>(() => RuntimeSplatAssetFactory.CreateFromFile(
                Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".ply"), "x", out _));
        }

        [Test]
        public void CreateFromSplats_EmptyArray_ReturnsNull()
        {
            var empty = new NativeArray<SplatInputData>(0, Allocator.Temp);
            try
            {
                Assert.IsNull(RuntimeSplatAssetFactory.CreateFromSplats(empty, "empty"));
            }
            finally
            {
                empty.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------
        // Layout parity with upstream's editor importer (GaussianSplattingEditor)

        [Test]
        public void Layout_MatchesTheUpstreamEditorImporterAtVeryHigh()
        {
            var creatorType = Type.GetType("GaussianSplatting.Editor.GaussianSplatAssetCreator, GaussianSplattingEditor");
            if (creatorType == null)
                Assert.Inconclusive("GaussianSplatting.Editor.GaussianSplatAssetCreator (assembly GaussianSplattingEditor) was not found.");
            const BindingFlags kAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo fInput = creatorType.GetField("m_InputFile", kAny);
            FieldInfo fCameras = creatorType.GetField("m_ImportCameras", kAny);
            FieldInfo fFolder = creatorType.GetField("m_OutputFolder", kAny);
            FieldInfo fQuality = creatorType.GetField("m_Quality", kAny);
            FieldInfo fError = creatorType.GetField("m_ErrorMessage", kAny);
            MethodInfo mApply = creatorType.GetMethod("ApplyQualityLevel", kAny);
            MethodInfo mCreate = creatorType.GetMethod("CreateAsset", kAny, null, Type.EmptyTypes, null);
            if (fInput == null || fFolder == null || fQuality == null || mApply == null || mCreate == null)
                Assert.Inconclusive("The upstream editor importer changed shape (m_InputFile / m_OutputFolder / m_Quality / ApplyQualityLevel / CreateAsset); update this test.");

            string folder = "Assets/SplatPressoParityTmp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string plyCopy = Path.Combine(EditorTestUtil.NewTempDir("parity"), "object.ply");
            File.Copy(PlyPath, plyCopy);
            var window = ScriptableObject.CreateInstance(creatorType);
            GaussianSplatAsset runtime = null;
            try
            {
                fInput.SetValue(window, plyCopy);
                fCameras?.SetValue(window, false);
                fFolder.SetValue(window, folder);
                fQuality.SetValue(window, Enum.Parse(fQuality.FieldType, "VeryHigh"));
                mApply.Invoke(window, null);
                mCreate.Invoke(window, null);
                string error = fError?.GetValue(window) as string;
                if (!string.IsNullOrEmpty(error))
                    Assert.Inconclusive("Upstream importer failed: " + error);

                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                byte[] edPos = ReadImported(projectRoot, folder, "object_pos.bytes");
                byte[] edOth = ReadImported(projectRoot, folder, "object_oth.bytes");
                byte[] edCol = ReadImported(projectRoot, folder, "object_col.bytes");
                byte[] edShs = ReadImported(projectRoot, folder, "object_shs.bytes");
                Assert.IsFalse(File.Exists(Path.Combine(projectRoot, folder, "object_chk.bytes")), "VeryHigh must not write chunks");

                SplatFileReader.ReadFile(plyCopy, out NativeArray<SplatInputData> splats);
                try
                {
                    runtime = RuntimeSplatAssetFactory.CreateFromSplats(splats, "parity", mortonReorder: true, compactEmptySH: false);
                }
                finally
                {
                    splats.Dispose();
                }

                AssertSameBytes("pos", edPos, runtime.posData.GetData<byte>().ToArray());
                AssertSameBytes("other", edOth, runtime.otherData.GetData<byte>().ToArray());
                AssertSameBytes("color", edCol, runtime.colorData.GetData<byte>().ToArray());
                AssertSameBytes("sh", edShs, runtime.shData.GetData<byte>().ToArray());

                var imported = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(folder + "/object.asset");
                if (imported != null)
                {
                    Assert.AreEqual(imported.splatCount, runtime.splatCount);
                    Assert.That(Vector3.Distance(imported.boundsMin, runtime.boundsMin), Is.LessThan(1e-6f));
                    Assert.That(Vector3.Distance(imported.boundsMax, runtime.boundsMax), Is.LessThan(1e-6f));
                }
            }
            finally
            {
                Object.DestroyImmediate(window);
                RuntimeSplatAssetFactory.DestroyRuntimeAsset(runtime);
                if (AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.DeleteAsset(folder);
                EditorTestUtil.DeleteDir(Path.GetDirectoryName(plyCopy));
            }
        }

        static byte[] ReadImported(string projectRoot, string folder, string file)
        {
            string path = Path.Combine(projectRoot, folder, file);
            if (!File.Exists(path))
                Assert.Inconclusive("The upstream importer did not write " + folder + "/" + file);
            return File.ReadAllBytes(path);
        }

        static void AssertSameBytes(string what, byte[] expected, byte[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length, what + ": size differs from the upstream editor importer");
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] == actual[i])
                    continue;
                int stride = what == "color" ? 16 : what == "sh" ? 192 : what == "pos" ? 12 : 16;
                Assert.Fail($"{what}: first byte difference at offset {i} (element {i / stride}): upstream 0x{expected[i]:X2}, runtime 0x{actual[i]:X2}");
            }
        }

        static int NextMultipleOf(int size, int multipleOf) => (size + multipleOf - 1) / multipleOf * multipleOf;
    }
}
