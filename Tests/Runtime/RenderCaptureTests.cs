using System.Collections;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using NUnit.Framework;
using SplatPresso.Placement;
using SplatPresso.Rendering;
using UnityEngine;
using UnityEngine.TestTools;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Renders the fixture splat with the unmodified UnityGaussianSplatting renderer in a runtime-built URP scene and
    /// captures it through CaptureService / SplatCaptureFeature: color must show the splat, depth must be metric
    /// eye depth of the SPLAT at its center (~2 m) and of the floor elsewhere, with row 0 at the TOP (the
    /// upside-down-capture flip point). Needs URP with GaussianSplatURPFeature + SplatCaptureFeature on the renderer
    /// (SplatPresso > Setup Scene / SetupWizard.RunBatch); otherwise ignored.
    /// </summary>
    public class RenderCaptureTests
    {
        SceneRig m_Rig;
        SplatPressoSettings m_Settings;
        string m_SessionsDir;

        [TearDown]
        public void TearDown()
        {
            m_Rig?.Dispose();
            m_Rig = null;
            SplatPressoSettings.Active = null;
            if (m_Settings != null)
                Object.Destroy(m_Settings);
            m_Settings = null;
            TestEnv.DeleteDir(m_SessionsDir);
        }

        [UnityTest]
        public IEnumerator Capture_ReturnsColorAndMetricDepth_OfTheSplatAndTheFloor()
        {
            TestEnv.RequireCapture();
            string fixtures = TestEnv.RequireFixtures();
            var resources = TestEnv.RequireRendererResources();
            m_SessionsDir = TestEnv.NewTempDir("capture");
            m_Settings = TestEnv.CreateSettings("http://127.0.0.1:9/api/v1", m_SessionsDir); // no network in this test
            SplatPressoSettings.Active = m_Settings;
            m_Rig = SceneRig.CreateWithServices(m_Settings, resources, withRoot: false, withVoice: false);
            int assetsBefore = RuntimeSplatAssetFactory.RuntimeAssetCount;

            // The chair is 1 m tall with its pivot at the bottom: put its middle at eye height, 2 m ahead.
            GeneratedObject obj = null;
            var spawn = AsyncOp.Run("SpawnSplatFromFileAsync", 30f, async ct =>
                obj = await m_Rig.spawner.SpawnSplatFromFileAsync(Path.Combine(fixtures, "object.ply"), new Vector3(0f, 1.1f, 2f), Quaternion.identity, 1f, "fixture", ct));
            while (spawn.KeepWaiting)
                yield return null;
            spawn.AssertSucceeded();
            Assert.NotNull(obj);
            var renderer = obj.GetComponentInChildren<GaussianSplatRenderer>();
            Assert.NotNull(renderer, "the spawned object has a GaussianSplatRenderer");
            Assert.IsTrue(renderer.HasValidAsset);
            Assert.AreEqual(8192, renderer.splatCount);
            Assert.AreEqual(0, renderer.m_SHOrder);
            Assert.AreSame(renderer.m_Asset, obj.SplatAsset, "the GeneratedObject owns the runtime asset");
            Assert.IsTrue(RuntimeSplatAssetFactory.IsRuntimeAsset(obj.SplatAsset));
            Bounds b = SplatBoundsInRoot(obj.transform, renderer);
            Assert.That(b.min.y, Is.EqualTo(0f).Within(0.02f), "pivot at the bottom of the content bounds");
            Assert.That(b.size.y, Is.EqualTo(1f).Within(0.02f), "TripoSplat content is normalized to 1 m (Y-down flipped to Y-up)");

            // let the renderer upload, sort and draw
            for (int i = 0; i < 5; i++)
                yield return null;

            CaptureResult cap = null;
            var capture = AsyncOp.Run("CaptureService.CaptureAsync", 20f, async ct => cap = await m_Rig.capture.CaptureAsync(ct));
            while (capture.KeepWaiting)
                yield return null;
            capture.AssertSucceeded(() => "SplatCaptureFeature.LastError: " + SplatCaptureFeature.LastError);

            // ---- metadata ----
            Assert.AreEqual(SceneRig.Width, cap.width);
            Assert.AreEqual(SceneRig.Height, cap.height);
            Assert.NotNull(cap.rgbJpeg);
            Assert.IsTrue(ImageUtil.IsJpeg(cap.rgbJpeg));
            Assert.AreEqual(cap.width * cap.height, cap.depthMeters.Length);
            Assert.That(Vector3.Distance(cap.cameraPosition, SceneRig.CameraPosition), Is.LessThan(1e-3f));
            Assert.That(Quaternion.Angle(cap.cameraRotation, SceneRig.CameraRotation), Is.LessThan(0.1f));
            Assert.AreEqual(60f, cap.verticalFovDeg, 1e-3f);

            // ---- the splat: project its middle (front face of the solid backrest ~0.2 m behind the pivot) ----
            Vector3 target = new Vector3(0f, 1.6f, 2.2f);
            Vector3 local = Quaternion.Inverse(cap.cameraRotation) * (target - cap.cameraPosition);
            float f = cap.FocalPixels;
            int cx = Mathf.RoundToInt(local.x / local.z * f + cap.width * 0.5f - 0.5f);
            int cy = Mathf.RoundToInt(-local.y / local.z * f + cap.height * 0.5f - 0.5f);
            Assert.That(cx, Is.InRange(0, cap.width - 1));
            Assert.That(cy, Is.InRange(0, cap.height - 1));

            var depths = new List<float>();
            const int r = 8;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                float d = cap.DepthAt(cx + dx, cy + dy);
                if (d > 0f)
                    depths.Add(d);
            }
            Assert.GreaterOrEqual(depths.Count, (2 * r + 1) * (2 * r + 1) / 5, $"too few splat depth pixels around ({cx},{cy}); is the splat depth pass running?");
            depths.Sort();
            float median = depths[depths.Count / 2];
            Assert.That(median, Is.InRange(1.6f, 2.4f), "splat depth at its center");

            // color: the JPEG shows the red splat on the black clear color
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(cap.rgbJpeg));
                float sx = tex.width / (float)cap.width, sy = tex.height / (float)cap.height;
                int red = 0, total = 0;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = Mathf.Clamp(Mathf.RoundToInt((cx + dx) * sx), 0, tex.width - 1);
                    int y = Mathf.Clamp(Mathf.RoundToInt((cy + dy) * sy), 0, tex.height - 1);
                    Color c = tex.GetPixel(x, tex.height - 1 - y); // texture rows are bottom-up, capture rows top-down
                    total++;
                    if (c.r > 0.25f && c.r > c.g + 0.1f && c.r > c.b + 0.1f)
                        red++;
                }
                Assert.GreaterOrEqual(red, total / 5, "the splat's red is visible around its projected center");
            }
            finally
            {
                Object.Destroy(tex);
            }

            // ---- the floor: metric depth that matches the analytic ray/plane distance ----
            int floorChecks = 0;
            for (int row = Mathf.RoundToInt(cap.height * 0.8f); row < cap.height - 2; row += 7)
            {
                foreach (int col in new[] { cap.width / 4, cap.width / 2, cap.width * 3 / 4 })
                {
                    float expected = SceneRig.FloorEyeDepth(cap, col, row);
                    float d = cap.DepthAt(col, row);
                    Assert.Greater(expected, 0f);
                    Assert.Greater(d, 0f, $"floor pixel ({col},{row}) has no depth");
                    Assert.That(d, Is.EqualTo(expected).Within(expected * 0.1f), $"floor depth at ({col},{row})");
                    floorChecks++;
                }
            }
            Assert.Greater(floorChecks, 3);

            // row 0 is the TOP: the top rows look above the horizon (clear color, depth 0)
            Assert.AreEqual(0f, cap.DepthAt(cap.width / 2, 2), "top row must be sky; a flipped readback puts the floor here");
            Assert.AreEqual(0f, cap.DepthAt(cap.width / 8, 2));

            // ---- cleanup releases the runtime asset (GeneratedObject.OnDestroy) ----
            m_Rig.spawner.Remove(obj, "test");
            yield return null;
            yield return null;
            Assert.AreEqual(assetsBefore, RuntimeSplatAssetFactory.RuntimeAssetCount, "the runtime asset and its TextAssets are freed with the object");
        }

        // The asset's bounds after the content transform (TripoSplat Y-down -> Unity), in the placed root's space.
        static Bounds SplatBoundsInRoot(Transform root, GaussianSplatRenderer r)
        {
            Matrix4x4 m = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
            Vector3 min = r.m_Asset.boundsMin, max = r.m_Asset.boundsMax;
            var b = new Bounds(m.MultiplyPoint3x4(min), Vector3.zero);
            for (int i = 1; i < 8; i++)
                b.Encapsulate(m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z)));
            return b;
        }
    }
}
