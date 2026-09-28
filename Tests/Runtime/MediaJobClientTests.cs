using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using SplatPresso.Api;
using UnityEngine;
using UnityEngine.TestTools;

namespace SplatPresso.Tests
{
    /// <summary>
    /// The media queue client and typed endpoints against the mock server's GenPresso quirks (no rendering
    /// needed): candidate-path fall-through on 404 plus caching, 429 Retry-After on submit, IN_QUEUE polling, the
    /// "still in progress" result lag, FAILED jobs, cancellation, the cost cap, probes and the chat DECIDE/VERIFY calls.
    /// </summary>
    public class MediaJobClientTests : MockServerFixture
    {
        const float kTimeout = 60f;

        CostLedger m_Ledger;
        MediaEndpoints m_Endpoints;
        string m_Scratch;

        [SetUp]
        public void CreateClient()
        {
            m_Ledger = CostLedger.InMemory(25.0);
            if (Settings != null)
                m_Endpoints = new MediaEndpoints(new MediaJobClient(Settings, m_Ledger), Settings);
            m_Scratch = TestEnv.NewTempDir("media");
        }

        [TearDown]
        public void DeleteScratch() => TestEnv.DeleteDir(m_Scratch);

        int LedgerEntries(string item) => m_Ledger.entries.Count(e => e.item == item);

        [UnityTest]
        public IEnumerator GenerateSplat_FallsThroughAMissingPath_ThenReusesTheCachedOne()
        {
            ModelPathCache.Invalidate(Mock.ApiBaseUrl, MediaRouteKeys.ImageToSplat);
            string dest = Path.Combine(m_Scratch, "a", "model.ply");
            int mark = Mock.RequestCount;
            string result = null;
            var op = AsyncOp.Run("GenerateSplatAsync", kTimeout, async ct =>
                result = await m_Endpoints.GenerateSplatAsync(FixtureUrl("enhanced.png"), 262144, dest, ct));
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe(mark));

            var submits = Mock.Find(r => r.kind == "submit", mark);
            Assert.AreEqual(2, submits.Count, Mock.Describe(mark));
            Assert.AreEqual("tripo3d/triposplat", submits[0].target);
            Assert.AreEqual(404, submits[0].status, "first candidate is not hosted");
            Assert.AreEqual("gp/tripo3d/triposplat", submits[1].target);
            Assert.AreEqual(200, submits[1].status);
            Assert.AreEqual("gp/tripo3d/triposplat", ModelPathCache.Get(Mock.ApiBaseUrl, MediaRouteKeys.ImageToSplat));
            Assert.AreEqual(1, LedgerEntries("triposplat"), "a 404 at submit is neither queued nor billed");

            Assert.AreEqual(dest, result);
            CollectionAssert.AreEqual(Fixture("object.ply"), File.ReadAllBytes(dest));
            Assert.IsFalse(File.Exists(dest + ".tmp"), "downloads go through a .tmp file that is moved into place");
            Assert.GreaterOrEqual(Mock.Find(r => r.kind == "status", mark).Count, 2, "IN_QUEUE is polled until COMPLETED");
            foreach (var f in Mock.Find(r => r.kind == "file", mark))
                Assert.IsFalse(f.hadAuthorization, "result downloads must never carry the API key");

            // second job: the cached path goes first, no 404 round trip
            mark = Mock.RequestCount;
            string dest2 = Path.Combine(m_Scratch, "b", "model.ply");
            op = AsyncOp.Run("GenerateSplatAsync (cached)", kTimeout, async ct =>
                await m_Endpoints.GenerateSplatAsync(FixtureUrl("enhanced.png"), 262144, dest2, ct));
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe(mark));
            submits = Mock.Find(r => r.kind == "submit", mark);
            Assert.AreEqual(1, submits.Count, Mock.Describe(mark));
            Assert.AreEqual("gp/tripo3d/triposplat", submits[0].target);
            Assert.IsTrue(File.Exists(dest2));
        }

        [UnityTest]
        public IEnumerator EditImage_WaitsForRetryAfterOn429_AndBillsOnce()
        {
            int mark = Mock.RequestCount;
            byte[] edited = null;
            string url = null;
            var op = AsyncOp.Run("EditImageAsync", kTimeout, async ct =>
            {
                var r = await m_Endpoints.EditImageAsync(Fixture("edited.jpg"), "Add a red chair on the floor.", false, null, ct);
                edited = r.jpeg;
                url = r.url;
            });
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe(mark));

            var submits = Mock.Find(r => r.kind == "submit" && r.capability == "edit", mark);
            Assert.AreEqual(2, submits.Count, Mock.Describe(mark));
            Assert.AreEqual(429, submits[0].status);
            Assert.AreEqual(200, submits[1].status);
            Assert.GreaterOrEqual(submits[1].time - submits[0].time, 0.9, "Retry-After: 1 must be honoured");
            Assert.AreEqual("google/nano-banana-2-lite/edit", submits[1].target, "a 429 retries the SAME candidate");
            Assert.AreEqual(1, LedgerEntries("edit"), "the rejected (429) submit is not billed");
            Assert.AreEqual(FixtureUrl("edited.jpg"), url, "downstream steps reuse the hosted URL");
            CollectionAssert.AreEqual(Fixture("edited.jpg"), edited);
        }

        [UnityTest]
        public IEnumerator SegmentObject_SendsAnIntegerPixelBox()
        {
            int mark = Mock.RequestCount;
            byte[] png = null;
            float score = 0f;
            var op = AsyncOp.Run("SegmentObjectAsync", kTimeout, async ct =>
            {
                // the fixture chair box; SAM-3 422-rejects fractional box prompts, so the mock does too
                var r = await m_Endpoints.SegmentObjectAsync(FixtureUrl("edited.jpg"), "red chair", new Bbox(0.4398f, 0.4597f, 0.1203f, 0.4f), 1280, 720, ct);
                png = r.png;
                score = r.score;
            });
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe(mark));
            Assert.AreEqual(0.93f, score, 1e-4f);
            CollectionAssert.AreEqual(Fixture("cutout.png"), png);
            Assert.AreEqual(1, Mock.Find(r => r.kind == "submit" && r.capability == "segment", mark).Count);
        }

        [UnityTest]
        public IEnumerator FailedJob_SurfacesTheValidationReason_AndIsNotRetryable()
        {
            Mock.FailNextJob = true;
            int mark = Mock.RequestCount;
            var op = AsyncOp.Run("EstimateDepthAsync", kTimeout, async ct =>
                await m_Endpoints.EstimateDepthAsync(FixtureUrl("edited.jpg"), ct));
            while (op.KeepWaiting)
                yield return null;
            Assert.IsTrue(op.Completed, "timed out:\n" + Mock.Describe(mark));
            var e = op.Error as GenpressoException;
            Assert.NotNull(e, "expected a GenpressoException, got " + op.Error);
            Assert.AreEqual(GenpressoErrorKind.Validation, e.Kind, e.Message);
            Assert.IsFalse(e.Retryable, "a deterministic input error must not be re-submitted");
            StringAssert.Contains("Could not download the image", e.Message);
            Assert.AreEqual(0, Mock.Find(r => r.kind == "file", mark).Count);
        }

        [UnityTest]
        public IEnumerator Cancellation_StopsPollingAndCancelsTheQueuedJob()
        {
            Mock.HoldJobsInQueue = true;
            int mark = Mock.RequestCount;
            var cancel = new CancellationTokenSource();
            var op = AsyncOp.Run("RemoveBackgroundAsync", kTimeout, async ct =>
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancel.Token))
                    await m_Endpoints.RemoveBackgroundAsync(Fixture("enhanced.png"), "image/png", linked.Token);
            });
            double until = Time.realtimeSinceStartupAsDouble + 10.0;
            while (Mock.Find(r => r.kind == "status", mark).Count < 2 && Time.realtimeSinceStartupAsDouble < until && !op.Completed)
                yield return null;
            cancel.Cancel();
            while (op.KeepWaiting)
                yield return null;
            Assert.IsTrue(op.Completed);
            Assert.IsInstanceOf<OperationCanceledException>(op.Error, "got " + op.Error);

            // the cancel is fire-and-forget: give it a moment to arrive
            until = Time.realtimeSinceStartupAsDouble + 5.0;
            while (Mock.Find(r => r.kind == "cancel", mark).Count == 0 && Time.realtimeSinceStartupAsDouble < until)
                yield return null;
            var cancels = Mock.Find(r => r.kind == "cancel", mark);
            Assert.AreEqual(1, cancels.Count, Mock.Describe(mark));
            Assert.AreEqual("PUT", cancels[0].method);
            Assert.AreEqual(200, cancels[0].status);
        }

        [UnityTest]
        public IEnumerator CostCap_StopsBeforeAnythingIsSubmitted()
        {
            var tight = new MediaEndpoints(new MediaJobClient(Settings, CostLedger.InMemory(0.1)), Settings);
            int mark = Mock.RequestCount;
            var op = AsyncOp.Run("GenerateSplatAsync (capped)", kTimeout, async ct =>
                await tight.GenerateSplatAsync(FixtureUrl("enhanced.png"), 262144, Path.Combine(m_Scratch, "capped.ply"), ct));
            while (op.KeepWaiting)
                yield return null;
            Assert.IsInstanceOf<CostCapExceededException>(op.Error, "got " + op.Error);
            Assert.AreEqual(0, Mock.Find(r => r.kind == "submit", mark).Count);
        }

        [UnityTest]
        public IEnumerator WrongKey_FailsFastWithoutTryingOtherPaths()
        {
            ApiKeys.SetOverride(ApiKeyKind.Genpresso, "gp_wrong_key");
            int mark = Mock.RequestCount;
            var op = AsyncOp.Run("EstimateDepthAsync (bad key)", kTimeout, async ct =>
                await m_Endpoints.EstimateDepthAsync(FixtureUrl("edited.jpg"), ct));
            while (op.KeepWaiting)
                yield return null;
            var e = op.Error as GenpressoException;
            Assert.NotNull(e, "expected a GenpressoException, got " + op.Error);
            Assert.AreEqual(GenpressoErrorKind.Unauthorized, e.Kind);
            Assert.IsFalse(e.Retryable);
            Assert.AreEqual(1, Mock.Find(r => r.kind == "unauthorized", mark).Count, Mock.Describe(mark));
            StringAssert.DoesNotContain("gp_wrong_key", e.Message, "keys are never echoed");
        }

        [UnityTest]
        public IEnumerator Probe_TellsMissingFromPresentPaths()
        {
            var results = new Dictionary<string, ProbeResult>();
            var op = AsyncOp.Run("ProbeAsync", kTimeout, async ct =>
            {
                foreach (var p in new[] { "tripo3d/triposplat", "gp/tripo3d/triposplat", "gp/does-not-exist" })
                    results[p] = await MediaJobClient.ProbeAsync(Settings, p, ct);
            });
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe());
            Assert.AreEqual(ProbeOutcome.Missing, results["tripo3d/triposplat"].outcome, results["tripo3d/triposplat"].ToString());
            Assert.AreEqual(ProbeOutcome.Present, results["gp/tripo3d/triposplat"].outcome, results["gp/tripo3d/triposplat"].ToString());
            Assert.AreEqual(422, results["gp/tripo3d/triposplat"].statusCode, "the probe body fails validation: nothing is queued");
            Assert.AreEqual(ProbeOutcome.Missing, results["gp/does-not-exist"].outcome);
        }

        [UnityTest]
        public IEnumerator ConnectionTester_PassesAgainstTheMock()
        {
            ConnectionReport report = null;
            var op = AsyncOp.Run("ConnectionTester.TestAsync", kTimeout, async ct =>
                report = await ConnectionTester.TestAsync(Settings, true, ct));
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe());
            Assert.IsTrue(report.ok, report.ToString());
            Assert.IsTrue(report.reachable);
            Assert.IsTrue(report.chatOk);
            Assert.IsTrue(report.chatModelListed);
            Assert.AreEqual(KeySource.Override, report.keySource);
            Assert.IsTrue(report.probes.Any(p => p.path == "tripo3d/triposplat" && p.outcome == ProbeOutcome.Missing), report.ToString());
            Assert.IsTrue(report.probes.Any(p => p.path == "gp/tripo3d/triposplat" && p.Present), report.ToString());
            StringAssert.DoesNotContain(MockGenpressoServer.ApiKey, report.ToString(), "the report shows masked keys only");
        }

        [UnityTest]
        public IEnumerator DecideAndVerify_ParseTheStrictJsonReplies()
        {
            var chat = new GenpressoChatClient(Settings.apiBaseUrl, ApiKeys.Get(ApiKeyKind.Genpresso), m_Ledger);
            var service = new PlacementDecisionService(chat, Settings);
            var request = new PlacementRequest { intentSummary = "Add a red chair on the floor.", placementHint = "in front of the camera" };
            request.objects.Add(new RequestedObject { name = "red chair", description = "a red wooden chair", count = 1 });
            byte[] jpeg = Fixture("edited.jpg");
            int mark = Mock.RequestCount;
            DecisionResult decision = null;
            VerificationResult verification = null;
            var op = AsyncOp.Run("DECIDE + VERIFY", kTimeout, async ct =>
            {
                decision = await service.DecideAsync(request, jpeg, ct);
                verification = await service.VerifyAsync(decision, jpeg, jpeg, ct);
            });
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe(mark));

            Assert.IsTrue(decision.feasible);
            Assert.AreEqual("red chair", decision.objects[0].name);
            Assert.IsTrue(decision.objects[0].TargetBbox.IsValid);
            Assert.IsTrue(verification.objects[0].found);
            var decide = Mock.Find(r => r.kind == "chat:decide", mark);
            var verify = Mock.Find(r => r.kind == "chat:verify", mark);
            Assert.AreEqual(1, decide.Count, Mock.Describe(mark));
            Assert.AreEqual(1, verify.Count, Mock.Describe(mark));
            Assert.AreEqual("placement_decision", decide[0].schemaName);
            Assert.AreEqual("placement_verification", verify[0].schemaName);
            Assert.Less(decide[0].bodyBytes, GenpressoLimits.MaxRequestBodyBytes);
            Assert.AreEqual(2, m_Ledger.entries.Count(e => e.item.StartsWith("chat:")), "chat calls are recorded in the ledger");
        }
    }
}
