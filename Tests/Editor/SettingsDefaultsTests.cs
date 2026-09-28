using NUnit.Framework;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Factory defaults that encode calibration or verified API facts. A silent change here breaks placement
    /// (TripoSplat orientation/scale) or model routing, so the values are pinned.
    /// </summary>
    public class SettingsDefaultsTests
    {
        SplatPressoSettings m_Settings;

        [SetUp]
        public void SetUp() => m_Settings = ScriptableObject.CreateInstance<SplatPressoSettings>();

        [TearDown]
        public void TearDown()
        {
            if (m_Settings != null)
                Object.DestroyImmediate(m_Settings);
        }

        [Test]
        public void PlacementTuning_UsesTheCalibratedTripoSplatValues()
        {
            var t = new PlacementTuning();
            // the content transform diag(1,-1,1) takes the 3DGS / TripoSplat Y-down convention to Unity
            Assert.AreEqual(new Vector3(180f, 0f, 0f), t.contentRotationEuler);
            Assert.AreEqual(new Vector3(1f, 1f, -1f), t.contentScale);
            // calibrated with the nudge controller (NOT the older code defaults 0 / 1.0)
            Assert.AreEqual(270f, t.yawOffsetDeg);
            Assert.AreEqual(0.9f, t.uniformScaleFactor);
            Assert.AreEqual(0.5f, t.defaultObjectSizeM);
            Assert.AreEqual(0.3f, t.minDistance);
            Assert.AreEqual(50f, t.maxDistance);
            Assert.AreEqual(0.05f, t.groundAnchorBandFraction);
            Assert.AreEqual(0.3f, t.depthAlphaThreshold);
            Assert.AreEqual(Vector3.zero, t.meshContentRotationEuler);
            Assert.AreEqual(180f, t.meshYawOffsetDeg);
            Assert.AreEqual(1f, t.meshUniformScaleFactor);
        }

        [Test]
        public void PlacementTuning_SceneAwareOrientationDefaults()
        {
            var t = new PlacementTuning();
            Assert.AreEqual(OrientationMode.SceneAware, t.orientationMode);
            Assert.AreEqual(0, (int)OrientationMode.CameraFacing);
            Assert.AreEqual(1, (int)OrientationMode.Shadow);
            Assert.AreEqual(2, (int)OrientationMode.SceneAware);
            Assert.IsTrue(t.wallFlush);
            Assert.AreEqual(0.01f, t.wallGapM);
            Assert.AreEqual(0.30f, t.wallMaxStandoffM);
            Assert.AreEqual(1.5f, t.wallMaxShiftM);
            Assert.IsTrue(t.wallBackedCenterOnFootprint);
            Assert.AreEqual(0.75f, t.wallMaxLateralFrac);
            Assert.AreEqual(300, t.wallMinPoints);
            Assert.AreEqual(12000, t.wallMaxPoints);
            Assert.AreEqual(256, t.wallRansacIterations);
            Assert.AreEqual(0.03f, t.wallTauMinM);
            Assert.AreEqual(0.01f, t.wallTauPerMeter);
            Assert.AreEqual(0.35f, t.wallMinInlierFraction);
            Assert.AreEqual(0.15f, t.wallMaxBehindFraction);
            Assert.AreEqual(8f, t.wallMaxSplitDeg);
            Assert.AreEqual(0.15f, t.wallMinGrazingCos);
            Assert.AreEqual(0.15f, t.wallMountedCenterTolM);
            Assert.AreEqual(1.0f, t.wallMountedMaxAnchorDistM);
            Assert.AreEqual(0.5f, t.wallBackedMaxDistSlackM);
            Assert.IsTrue(t.useCategoryWallHeuristic);
            Assert.AreEqual("bookshelf,bookcase,shelving unit,cabinet,cupboard,wardrobe,armoire,dresser,chest of drawers,sideboard,credenza," +
                            "buffet,hutch,tv stand,tv console,media console,console table,entertainment center,desk,headboard,bed,piano," +
                            "fireplace,radiator,refrigerator,fridge", t.wallBackedCategories);
            Assert.IsTrue(t.orientationGizmos);

            var c = t.Clone();
            c.orientationMode = OrientationMode.CameraFacing;
            c.wallBackedCategories = "x";
            Assert.AreEqual(OrientationMode.SceneAware, t.orientationMode, "Clone is a copy");
            Assert.AreEqual(PlacementTuning.DefaultWallBackedCategories, t.wallBackedCategories);
        }

        [Test]
        public void PlacementTuning_CloneIsIndependent()
        {
            var a = new PlacementTuning();
            var b = a.Clone();
            b.yawOffsetDeg = 12f;
            Assert.AreEqual(270f, a.yawOffsetDeg);
        }

        [Test]
        public void Settings_Defaults()
        {
            var s = m_Settings;
            Assert.AreEqual("https://genpresso.ai/api/v1", s.apiBaseUrl);
            Assert.AreEqual("", s.apiKey, "no key may ship in the defaults");
            Assert.AreEqual(MediaProvider.Genpresso, s.mediaProvider);
            Assert.AreEqual("google/gemini-3.5-flash-lite", s.chatModel);
            Assert.AreEqual("google/gemini-3.8-flash", s.chatFallbackModel);
            Assert.AreEqual(GenerationMode.SceneContextual, s.defaultMode);
            Assert.AreEqual(ObjectRepresentation.GaussianSplat, s.representation);
            Assert.AreEqual(262144, s.numGaussians);
            Assert.AreEqual(1280, s.maxImageLongSide);
            Assert.AreEqual(85, s.jpegQuality);
            Assert.AreEqual(KeyCode.Space, s.pushToTalkKey);
            Assert.LessOrEqual(s.maxUtteranceSeconds, 85f, "about 93 s of 16 kHz WAV already exceeds the 4 MB body cap");
            Assert.NotNull(s.placement);
            Assert.AreEqual(270f, s.placement.yawOffsetDeg);
            Assert.IsTrue(s.askVlmForOrientation);
            Assert.AreEqual(OrientationMode.SceneAware, s.placement.orientationMode);
        }

        [Test]
        public void Routes_HaveTheVerifiedPathsInOrder()
        {
            var s = m_Settings;
            // live: gp/tripo3d/triposplat is accepted at submit but its job FAILS with 404 "Path /triposplat not found";
            // TripoSplat lives at tripo3d/triposplat (aliased as gp/triposplat)
            Assert.AreEqual(new[] { "tripo3d/triposplat", "gp/triposplat" }, s.imageToSplat.genpressoPaths);
            Assert.AreEqual("tripo3d/triposplat", s.imageToSplat.falEndpoint);
            Assert.AreEqual(1.5f, s.imageToSplat.estimatedCost);
            Assert.AreEqual(600, s.imageToSplat.timeoutSec);
            Assert.AreEqual("google/nano-banana-2-lite/edit", s.edit.genpressoPaths[0]);
            Assert.AreEqual("gp/sam-3/image", s.segment.genpressoPaths[0]);
            Assert.AreEqual("gp/birefnet/v2", s.removeBackground.genpressoPaths[0]);
            Assert.AreEqual("gp/image-preprocessors/depth-anything/v2", s.depth.genpressoPaths[0]);
            Assert.AreEqual("gp/hyper3d/rodin/v2.5/fast", s.imageToMesh.genpressoPaths[0]);
            Assert.AreEqual("gp/hyper3d/rodin/v2.5/text-to-3d/fast", s.textToMesh.genpressoPaths[0]);

            // the fal-ai/ owner never appears in a GenPresso path (GenPresso renames it to gp/)
            foreach (var kv in s.EnumerateRoutes())
            {
                Assert.NotNull(kv.Value, kv.Key);
                Assert.Greater(kv.Value.genpressoPaths.Length, 0, kv.Key);
                Assert.Greater(kv.Value.timeoutSec, 0, kv.Key);
                foreach (var p in kv.Value.genpressoPaths)
                    Assert.IsFalse(p.StartsWith("fal-ai/"), kv.Key + ": " + p);
            }
        }

        [Test]
        public void GetRoute_MatchesTheFields_AndUnknownIsNull()
        {
            var s = m_Settings;
            foreach (var key in MediaRouteKeys.All)
                Assert.NotNull(s.GetRoute(key), key);
            Assert.AreSame(s.imageToSplat, s.GetRoute(MediaRouteKeys.ImageToSplat));
            Assert.AreSame(s.editFallback, s.GetRoute(MediaRouteKeys.EditFallback));
            Assert.IsNull(s.GetRoute("nope"));
            Assert.IsNull(SplatPressoSettings.DefaultRoute("nope"));
        }

        [Test]
        public void DefaultRoute_ReturnsAFreshCopy()
        {
            var a = SplatPressoSettings.DefaultRoute(MediaRouteKeys.Edit);
            a.genpressoPaths[0] = "changed";
            Assert.AreEqual("google/nano-banana-2-lite/edit", SplatPressoSettings.DefaultRoute(MediaRouteKeys.Edit).genpressoPaths[0]);
        }

        [Test]
        public void ModelRoute_CleanPaths_TrimsDedupesAndDropsBlanks()
        {
            var r = new ModelRoute(new[] { " gp/a/ ", "", null, "GP/A", "/gp/b", "  " }, "fal-ai/a", 1f, 60);
            CollectionAssert.AreEqual(new[] { "gp/a", "gp/b" }, r.CleanPaths());
            Assert.IsEmpty(new ModelRoute().CleanPaths());
        }

        [Test]
        public void ModelRoute_CloneIsDeep()
        {
            var r = new ModelRoute(new[] { "gp/a" }, "fal-ai/a", 1f, 60);
            var c = r.Clone();
            c.genpressoPaths[0] = "gp/changed";
            c.timeoutSec = 5;
            Assert.AreEqual("gp/a", r.genpressoPaths[0]);
            Assert.AreEqual(60, r.timeoutSec);
        }

        [Test]
        public void JoinUrl_UsesExactlyOneSlash()
        {
            Assert.AreEqual("https://x.test/api/v1/media/gp/a", SplatPressoSettings.JoinUrl("https://x.test/api/v1/", "/media/gp/a"));
            Assert.AreEqual("https://x.test/api/v1/models", SplatPressoSettings.JoinUrl("https://x.test/api/v1", "models"));
            Assert.AreEqual("https://genpresso.ai/api/v1/models", SplatPressoSettings.JoinUrl("  ", "models"));
            m_Settings.apiBaseUrl = "http://127.0.0.1:1234/api/v1/";
            Assert.AreEqual("http://127.0.0.1:1234/api/v1/chat/completions", m_Settings.ApiUrl("chat/completions"));
        }

        [Test]
        public void CostLedger_CapIsPerRun()
        {
            var ledger = CostLedger.InMemory(1.0);
            Assert.IsTrue(ledger.CanSpend(0.5));
            ledger.Record("edit", 0.6);
            Assert.AreEqual(0.6, ledger.TotalCost, 1e-9);
            Assert.IsFalse(ledger.CanSpend(0.5), "0.6 + 0.5 exceeds the 1.0 cap");

            // a replay starts a new section: the cap applies to the new run only, history is kept
            ledger.BeginRun(1.0);
            Assert.AreEqual(0.0, ledger.TotalCost, 1e-9);
            Assert.AreEqual(0.6, ledger.LifetimeCost, 1e-9);
            Assert.IsTrue(ledger.CanSpend(0.5));
        }
    }
}
