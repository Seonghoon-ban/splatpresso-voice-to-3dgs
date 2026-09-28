using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using NUnit.Framework;
using SplatPresso.Api;
using SplatPresso.Placement;
using SplatPresso.Rendering;
using UnityEngine;
using UnityEngine.TestTools;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Whole pipeline runs against the mock server in a real URP scene: capture -> DECIDE -> (edit -> depth ->
    /// VERIFY -> segment -> enhance) -> TripoSplat -> download -> placement -> a spawned splat. The tests run in
    /// order: the second one checks that the image-to-splat path resolved by the first (after the wrong first
    /// candidate's job FAILED with 404 and the client fell through) is reused from the cache.
    /// </summary>
    public class PipelineE2ETests : MockServerFixture
    {
        const float kRunTimeout = 90f;

        SceneRig m_Rig;
        PlacementResult m_Result;
        bool m_SceneContextualPassed;
        readonly List<PlacementFailure> m_Failures = new List<PlacementFailure>();
        readonly List<RequestRejectReason> m_Rejections = new List<RequestRejectReason>();

        [TearDown]
        public void DisposeScene()
        {
            m_Rig?.Dispose();
            m_Rig = null;
            m_Failures.Clear();
            m_Rejections.Clear();
        }

        static PlacementRequest ChairRequest()
        {
            var r = new PlacementRequest
            {
                intentSummary = "Add a red wooden chair standing on the floor in front of the camera.",
                placementHint = "on the floor in front of the camera",
            };
            r.objects.Add(new RequestedObject { name = "red chair", description = "a red wooden chair with a tall backrest", count = 1 });
            return r;
        }

        void BuildScene(bool requireCapture = true)
        {
            if (requireCapture)
                TestEnv.RequireCapture();
            var resources = TestEnv.RequireRendererResources();
            m_Rig = SceneRig.CreateWithServices(Settings, resources, withRoot: true, withVoice: false);
            m_Rig.root.RunFailed += f => m_Failures.Add(f);
            m_Rig.root.RequestRejected += (req, why) => m_Rejections.Add(why);
        }

        string FailureText()
        {
            string s = string.Join("\n", m_Failures.Select(f => $"run {f.runId} failed at {f.stage} ({f.kind}): {f.reason}"));
            if (m_Rejections.Count > 0)
                s += "\nrejected: " + string.Join(", ", m_Rejections);
            return s + "\nSplatCaptureFeature.LastError: " + SplatCaptureFeature.LastError + "\nmock log:\n" + Mock.Describe();
        }

        // Starts a run; the test yields while op.KeepWaiting, then calls FinishRun. (No nested coroutines: an
        // assertion thrown inside a nested coroutine would not fail the test cleanly.)
        AsyncOp StartRunOp(PlacementRequest request)
        {
            m_Result = null;
            return AsyncOp.Run("SplatPressoRoot.RunAsync", kRunTimeout, async ct => m_Result = await m_Rig.root.RunAsync(request, "test request", ct));
        }

        PlacementResult FinishRun(AsyncOp op)
        {
            op.AssertSucceeded(FailureText);
            Assert.NotNull(m_Result, "RunAsync returned null (rejected or failed):\n" + FailureText());
            return m_Result;
        }

        GeneratedObject SpawnedFor(PlacementResult result, int objectId)
        {
            foreach (var o in m_Rig.spawner.SpawnedObjects)
                if (o != null && o.objectId == objectId && (o.runId == result.runId || string.IsNullOrEmpty(o.runId)))
                    return o;
            return null;
        }

        static void AssertFiles(string dir, params string[] relative)
        {
            foreach (var rel in relative)
                Assert.IsTrue(File.Exists(Path.Combine(dir, rel)), $"session artifact missing: {rel}\n(in {dir})");
        }

        [UnityTest, Order(1), Timeout(300000)]
        public IEnumerator SceneContextual_RunCompletes_AndPlacesASplat()
        {
            BuildScene();
            m_Rig.root.Mode = GenerationMode.SceneContextual;
            m_Rig.root.Representation = ObjectRepresentation.GaussianSplat;
            ModelPathCache.Invalidate(Mock.ApiBaseUrl, MediaRouteKeys.ImageToSplat);
            yield return null; // Start()
            int mark = Mock.RequestCount;

            var op = StartRunOp(ChairRequest());
            while (op.KeepWaiting)
                yield return null;
            var result = FinishRun(op);

            // ---- result + spawned object ----
            Assert.AreEqual(1, result.objects.Count, FailureText());
            var obj = result.objects[0];
            Assert.AreEqual(ObjectStatus.Placed, obj.status, "skip reason: " + obj.skipReason);
            Assert.AreEqual(ObjectRepresentation.GaussianSplat, obj.representation);
            Assert.IsTrue(File.Exists(obj.modelPath), "model file: " + obj.modelPath);
            CollectionAssert.AreEqual(Fixture("object.ply"), File.ReadAllBytes(obj.modelPath));

            var spawned = SpawnedFor(result, obj.id);
            Assert.NotNull(spawned, "no GeneratedObject for object " + obj.id);
            Assert.AreEqual(ObjectRepresentation.GaussianSplat, spawned.representation);
            var renderer = spawned.GetComponentInChildren<GaussianSplatRenderer>();
            Assert.NotNull(renderer);
            Assert.IsTrue(renderer.HasValidAsset);
            Assert.IsTrue(RuntimeSplatAssetFactory.IsRuntimeAsset(renderer.m_Asset));
            Assert.AreEqual(0, renderer.m_SHOrder, "TripoSplat output has no higher-order SH");
            // the verified box's bottom is floor in this scene: the object must stand on it, in front of the camera
            Vector3 p = spawned.transform.position;
            Assert.That(Mathf.Abs(p.y), Is.LessThan(0.15f), "placed at " + p);
            Assert.That(p.z, Is.InRange(1f, 12f), "placed at " + p);

            // ---- session artifacts ----
            string dir = result.sessionDir;
            Assert.IsTrue(Directory.Exists(dir), dir);
            StringAssert.StartsWith(Path.GetFullPath(SessionsDir), Path.GetFullPath(dir), "sessions go to settings.sessionsFolder");
            string o = Path.Combine(PipelineSession.ObjectsDir, obj.id.ToString());
            AssertFiles(dir,
                PipelineSession.CaptureJpg, PipelineSession.CaptureDepthBin, PipelineSession.CaptureMetaJson,
                PipelineSession.RequestJson, PipelineSession.ModeTxt, PipelineSession.RepresentationTxt,
                PipelineSession.DecisionJson, PipelineSession.EditedJpg, PipelineSession.EditedUrlTxt,
                PipelineSession.VerificationJson, PipelineSession.DepthGenPng, PipelineSession.LedgerJson,
                Path.Combine(o, PipelineSession.ObjectCutoutPng), Path.Combine(o, PipelineSession.ObjectEnhancedPng),
                Path.Combine(o, PipelineSession.ObjectPly), Path.Combine(o, PipelineSession.ObjectJson));
            Assert.AreEqual("SceneContextual", File.ReadAllText(Path.Combine(dir, PipelineSession.ModeTxt)).Trim());
            Assert.AreEqual(FixtureUrl("edited.jpg"), File.ReadAllText(Path.Combine(dir, PipelineSession.EditedUrlTxt)).Trim());
            double until = Time.realtimeSinceStartupAsDouble + 2.0;
            while (!File.Exists(Path.Combine(dir, PipelineSession.ResultJson)) && Time.realtimeSinceStartupAsDouble < until)
                yield return null;
            AssertFiles(dir, PipelineSession.ResultJson);

            // ---- what the client did against the quirky queue ----
            var splatSubmits = Mock.Find(r => r.kind == "submit" && r.capability == "splat", mark);
            Assert.AreEqual(2, splatSubmits.Count, Mock.Describe(mark));
            Assert.AreEqual("tripo3d/triposplat", splatSubmits[0].target);
            Assert.AreEqual(200, splatSubmits[0].status, "GenPresso accepts a wrong model path at submit");
            var missing = Mock.Find(r => r.kind == "result" && r.requestId == splatSubmits[0].requestId, mark);
            Assert.AreEqual(1, missing.Count, Mock.Describe(mark));
            Assert.AreEqual(404, missing[0].status, "the job FAILED with 'Path /triposplat not found'");
            Assert.AreEqual("gp/triposplat", splatSubmits[1].target);
            Assert.AreEqual(200, splatSubmits[1].status);
            Assert.AreEqual("gp/triposplat", ModelPathCache.Get(Mock.ApiBaseUrl, MediaRouteKeys.ImageToSplat));

            var editSubmits = Mock.Find(r => r.kind == "submit" && r.capability == "edit", mark);
            Assert.GreaterOrEqual(editSubmits.Count, 2, Mock.Describe(mark));
            Assert.AreEqual(429, editSubmits[0].status, "the first edit submit is rate limited");
            Assert.AreEqual(200, editSubmits[1].status, "and retried after Retry-After");

            Assert.AreEqual(1, Mock.Find(r => r.kind == "chat:decide", mark).Count);
            Assert.GreaterOrEqual(Mock.Find(r => r.kind == "chat:verify", mark).Count, 1);
            foreach (var cap in new[] { "segment", "enhance", "depth" })
                Assert.GreaterOrEqual(Mock.Find(r => r.kind == "submit" && r.capability == cap && r.status == 200, mark).Count, 1, cap + "\n" + Mock.Describe(mark));
            foreach (var f in Mock.Find(r => r.kind == "file", mark))
                Assert.IsFalse(f.hadAuthorization, "downloads must not send the API key: " + f);
            Assert.IsEmpty(m_Failures, FailureText());
            m_SceneContextualPassed = true;
        }

        [UnityTest, Order(2), Timeout(300000)]
        public IEnumerator DirectTextTo3D_RunCompletes_AndReusesTheCachedSplatPath()
        {
            BuildScene();
            m_Rig.root.Mode = GenerationMode.DirectTextTo3D;
            m_Rig.root.Representation = ObjectRepresentation.GaussianSplat;
            yield return null;
            int mark = Mock.RequestCount;

            var op = StartRunOp(ChairRequest());
            while (op.KeepWaiting)
                yield return null;
            var result = FinishRun(op);

            Assert.AreEqual(1, result.objects.Count);
            var obj = result.objects[0];
            Assert.AreEqual(ObjectStatus.Placed, obj.status, "skip reason: " + obj.skipReason);
            var spawned = SpawnedFor(result, obj.id);
            Assert.NotNull(spawned);
            var renderer = spawned.GetComponentInChildren<GaussianSplatRenderer>();
            Assert.NotNull(renderer);
            Assert.IsTrue(renderer.HasValidAsset);
            Assert.That(Mathf.Abs(spawned.transform.position.y), Is.LessThan(0.15f), "placed at " + spawned.transform.position);

            string dir = result.sessionDir;
            string o = Path.Combine(PipelineSession.ObjectsDir, obj.id.ToString());
            AssertFiles(dir, PipelineSession.CaptureJpg, PipelineSession.DecisionJson, PipelineSession.ModeTxt,
                Path.Combine(o, PipelineSession.ObjectGeneratedPng), Path.Combine(o, PipelineSession.ObjectPly));
            Assert.AreEqual("DirectTextTo3D", File.ReadAllText(Path.Combine(dir, PipelineSession.ModeTxt)).Trim());
            Assert.IsFalse(File.Exists(Path.Combine(dir, PipelineSession.EditedJpg)), "direct mode never edits the capture");
            Assert.IsNull(result.editedImagePath);

            // direct mode: no edit / verify / segment / depth, the object image comes from text-to-image
            Assert.AreEqual(0, Mock.Find(r => r.kind == "submit" && (r.capability == "edit" || r.capability == "segment" || r.capability == "depth"), mark).Count, Mock.Describe(mark));
            Assert.AreEqual(0, Mock.Find(r => r.kind == "chat:verify", mark).Count);
            Assert.AreEqual(1, Mock.Find(r => r.kind == "submit" && r.capability == "t2i" && r.status == 200, mark).Count, Mock.Describe(mark));

            var splatSubmits = Mock.Find(r => r.kind == "submit" && r.capability == "splat", mark);
            if (m_SceneContextualPassed)
            {
                Assert.AreEqual(1, splatSubmits.Count, "the cached path is tried first:\n" + Mock.Describe(mark));
                Assert.AreEqual("gp/triposplat", splatSubmits[0].target);
            }
            else
            {
                Assert.AreEqual("gp/triposplat", splatSubmits.Last().target, Mock.Describe(mark));
            }
            Assert.IsEmpty(m_Failures, FailureText());
        }

        [UnityTest, Order(3), Timeout(300000)]
        public IEnumerator DirectMesh_RunPlacesAMesh_WhenGltfFastIsInstalled()
        {
            if (!MeshSpawnerRegistry.IsAvailable)
                Assert.Ignore("Mesh mode needs glTFast (com.unity.cloud.gltfast); it is not installed in this project.");
            BuildScene();
            m_Rig.root.Mode = GenerationMode.DirectTextTo3D;
            m_Rig.root.Representation = ObjectRepresentation.Mesh;
            yield return null;
            int mark = Mock.RequestCount;

            var op = StartRunOp(ChairRequest());
            while (op.KeepWaiting)
                yield return null;
            var result = FinishRun(op);

            var obj = result.objects[0];
            Assert.AreEqual(ObjectStatus.Placed, obj.status, "skip reason: " + obj.skipReason);
            Assert.AreEqual(ObjectRepresentation.Mesh, obj.representation);
            StringAssert.EndsWith(PipelineSession.ObjectGlb, obj.modelPath);
            var spawned = SpawnedFor(result, obj.id);
            Assert.NotNull(spawned);
            Assert.AreEqual(ObjectRepresentation.Mesh, spawned.representation);
            Assert.IsNotEmpty(spawned.GetComponentsInChildren<Renderer>(), "the glTF cube has a renderer");
            Assert.AreEqual(1, Mock.Find(r => r.kind == "submit" && r.capability == "mesh-text" && r.status == 200, mark).Count, Mock.Describe(mark));
            Assert.AreEqual(0, Mock.Find(r => r.kind == "submit" && r.capability == "splat", mark).Count);
            Assert.AreEqual("Mesh", File.ReadAllText(Path.Combine(result.sessionDir, PipelineSession.RepresentationTxt)).Trim());
        }

        [UnityTest, Order(4)]
        public IEnumerator MissingKey_RejectsTheRequest()
        {
            BuildScene(requireCapture: false); // rejected before anything is rendered
            yield return null;
            ApiKeys.SetOverride(ApiKeyKind.Genpresso, null);
            if (ApiKeys.Has(ApiKeyKind.Genpresso))
                Assert.Ignore("A GenPresso key is configured on this machine (environment / keys file), so a missing key cannot be simulated.");
            LogAssert.ignoreFailingMessages = true; // the root reports the missing key as an error, by design
            int mark = Mock.RequestCount;
            string runId = m_Rig.root.StartRun(ChairRequest());
            yield return null;
            Assert.IsNull(runId);
            CollectionAssert.Contains(m_Rejections, RequestRejectReason.MissingKey);
            Assert.AreEqual(0, Mock.RequestCount - mark, "nothing is sent without a key");
        }
    }
}
