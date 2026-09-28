using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SplatPresso.Api;
using UnityEngine;
using UnityEngine.TestTools;

namespace SplatPresso.Tests
{
    /// <summary>
    /// The media queue client and typed endpoints against the mock server's (live-mirroring) GenPresso behaviour (no
    /// rendering needed): candidate-path fall-through when a wrong path's job FAILS with 404 or a submit answers 404,
    /// caching, asynchronous validation, 429 Retry-After on submit, IN_QUEUE polling, the "still in progress" result
    /// lag, FAILED jobs, cancellation, the cost cap, probes and the chat DECIDE/VERIFY calls.
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

        const string kNotBilledSuffix = "(not billed: model path not found)";

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

            // live GenPresso accepts the wrong path at submit and only reports it once the job runs
            var submits = Mock.Find(r => r.kind == "submit", mark);
            Assert.AreEqual(2, submits.Count, Mock.Describe(mark));
            Assert.AreEqual("tripo3d/triposplat", submits[0].target);
            Assert.AreEqual(200, submits[0].status, "a wrong model path is accepted at submit");
            Assert.GreaterOrEqual(Mock.Find(r => r.kind == "status" && r.requestId == submits[0].requestId, mark).Count, 1,
                "the wrong path is only revealed by polling:\n" + Mock.Describe(mark));
            var missing = Mock.Find(r => r.kind == "result" && r.requestId == submits[0].requestId, mark);
            Assert.AreEqual(1, missing.Count, "the FAILED job's result is fetched once for the reason:\n" + Mock.Describe(mark));
            Assert.AreEqual(404, missing[0].status, "FAILED + 404 'Path /triposplat not found'");
            Assert.AreEqual("gp/triposplat", submits[1].target, "then the next candidate is tried");
            Assert.AreEqual(200, submits[1].status);
            Assert.AreEqual("gp/triposplat", ModelPathCache.Get(Mock.ApiBaseUrl, MediaRouteKeys.ImageToSplat),
                "only the path whose job completed is cached");

            // the accepted-but-missing job recorded the estimate, then refunded it (failed jobs are not billed)
            Assert.AreEqual(2, LedgerEntries("triposplat"), "each accepted submit records the estimate");
            var refunds = m_Ledger.entries.Where(e => e.item.StartsWith("triposplat") && e.item.EndsWith(kNotBilledSuffix)).ToList();
            Assert.AreEqual(1, refunds.Count, string.Join(", ", m_Ledger.entries.Select(e => $"{e.item}={e.cost}")));
            Assert.AreEqual(-Settings.imageToSplat.estimatedCost, refunds[0].cost, 1e-6);
            Assert.AreEqual(Settings.imageToSplat.estimatedCost, m_Ledger.TotalCost, 1e-6, "net cost is one splat");

            Assert.AreEqual(dest, result);
            CollectionAssert.AreEqual(Fixture("object.ply"), File.ReadAllBytes(dest));
            Assert.IsFalse(File.Exists(dest + ".tmp"), "downloads go through a .tmp file that is moved into place");
            Assert.GreaterOrEqual(Mock.Find(r => r.kind == "status", mark).Count, 2, "IN_QUEUE is polled until COMPLETED");
            foreach (var f in Mock.Find(r => r.kind == "file", mark))
                Assert.IsFalse(f.hadAuthorization, "result downloads must never carry the API key");

            // second job: the cached path goes first, no failed round trip, no refund
            mark = Mock.RequestCount;
            string dest2 = Path.Combine(m_Scratch, "b", "model.ply");
            op = AsyncOp.Run("GenerateSplatAsync (cached)", kTimeout, async ct =>
                await m_Endpoints.GenerateSplatAsync(FixtureUrl("enhanced.png"), 262144, dest2, ct));
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe(mark));
            submits = Mock.Find(r => r.kind == "submit", mark);
            Assert.AreEqual(1, submits.Count, Mock.Describe(mark));
            Assert.AreEqual("gp/triposplat", submits[0].target);
            Assert.AreEqual(1, m_Ledger.entries.Count(e => e.item.EndsWith(kNotBilledSuffix)));
            Assert.AreEqual(2 * Settings.imageToSplat.estimatedCost, m_Ledger.TotalCost, 1e-6);
            Assert.IsTrue(File.Exists(dest2));
        }

        [UnityTest]
        public IEnumerator RunAsync_FallsThroughA404AtSubmit_WithoutBillingIt()
        {
            // fal-ai/ is not a GenPresso owner: rejected at submit (404 "unknown model"), nothing queued
            const string routeKey = "test.submit404";
            var route = new ModelRoute(new[] { "fal-ai/birefnet/v2", "gp/birefnet/v2" }, "", 0.1f, 60);
            var client = new MediaJobClient(Settings, m_Ledger);
            int mark = Mock.RequestCount;
            MediaJobResult job = null;
            var op = AsyncOp.Run("RunAsync (404 at submit)", kTimeout, async ct =>
                job = await client.RunAsync(routeKey, route, t => new JObject { ["image_url"] = FixtureUrl("enhanced.png"), ["output_format"] = "png" },
                    "rembg", null, ct));
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe(mark));

            var submits = Mock.Find(r => r.kind == "submit", mark);
            Assert.AreEqual(2, submits.Count, Mock.Describe(mark));
            Assert.AreEqual("fal-ai/birefnet/v2", submits[0].target);
            Assert.AreEqual(404, submits[0].status);
            Assert.IsNull(submits[0].requestId, "nothing was queued");
            Assert.AreEqual("gp/birefnet/v2", submits[1].target);
            Assert.AreEqual("gp/birefnet/v2", job.resolvedPath);
            Assert.AreEqual("gp/birefnet/v2", ModelPathCache.Get(Mock.ApiBaseUrl, routeKey));
            Assert.AreEqual(1, LedgerEntries("rembg"), "a 404 at submit is neither queued nor billed");
            Assert.IsFalse(m_Ledger.entries.Any(e => e.cost < 0), "nothing to refund");
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
                // the fixture chair box; SAM-3 rejects fractional box prompts (the job FAILS with a 422 result), so
                // the mock does too
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
            Assert.AreEqual(0, Mock.Find(r => r.kind == "result" && r.capability == "segment" && r.status == 422, mark).Count, Mock.Describe(mark));
        }

        [UnityTest]
        public IEnumerator SegmentObject_AFractionalBoxFailsAfterPolling_AsANonRetryableValidationError()
        {
            ModelPathCache.Invalidate(Mock.ApiBaseUrl, MediaRouteKeys.Segment);
            var client = new MediaJobClient(Settings, m_Ledger);
            int mark = Mock.RequestCount;
            var op = AsyncOp.Run("RunAsync (fractional SAM-3 box)", kTimeout, async ct =>
                await client.RunAsync(MediaRouteKeys.Segment, Settings.segment, t => new JObject
                {
                    ["image_url"] = FixtureUrl("edited.jpg"),
                    ["prompt"] = "red chair",
                    ["box_prompts"] = new JArray(new JObject { ["x_min"] = 0.4398, ["y_min"] = 0.4597, ["x_max"] = 0.5601, ["y_max"] = 0.8597 }),
                }, "segment:fractional", null, ct));
            while (op.KeepWaiting)
                yield return null;
            Assert.IsTrue(op.Completed, "timed out:\n" + Mock.Describe(mark));
            var e = op.Error as GenpressoException;
            Assert.NotNull(e, "expected a GenpressoException, got " + op.Error + "\n" + Mock.Describe(mark));
            Assert.AreEqual(GenpressoErrorKind.Validation, e.Kind, e.Message);
            Assert.IsFalse(e.Retryable, "a deterministic input error must not be re-submitted");
            Assert.IsFalse(e.IsMissingModelPath);
            Assert.AreEqual(422, e.StatusCode);
            StringAssert.Contains("valid integer", e.Message);

            // validation is asynchronous: accepted at submit, revealed by the FAILED status + 422 result
            var submits = Mock.Find(r => r.kind == "submit" && r.capability == "segment", mark);
            Assert.AreEqual(1, submits.Count, "never re-submitted, never tried on another candidate:\n" + Mock.Describe(mark));
            Assert.AreEqual(200, submits[0].status);
            Assert.GreaterOrEqual(Mock.Find(r => r.kind == "status" && r.requestId == submits[0].requestId, mark).Count, 1, Mock.Describe(mark));
            var results = Mock.Find(r => r.kind == "result" && r.requestId == submits[0].requestId, mark);
            Assert.AreEqual(1, results.Count, Mock.Describe(mark));
            Assert.AreEqual(422, results[0].status);
            Assert.AreEqual("gp/sam-3/image", ModelPathCache.Get(Mock.ApiBaseUrl, MediaRouteKeys.Segment),
                "the model validated the input, so its path exists and is cached");
            Assert.AreEqual(0, Mock.Find(r => r.kind == "file", mark).Count);
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
            Assert.AreEqual(202, cancels[0].status, "a queued job is cancelled with 202");

            // live semantics: the cancelled job then reports EXPIRED, and cancelling a terminal job answers 400
            string jobUrl = Mock.ApiBaseUrl + "/media/requests/" + cancels[0].requestId;
            var auth = new Dictionary<string, string> { { "Authorization", "Bearer " + MockGenpressoServer.ApiKey } };
            HttpResponse status = null, again = null;
            var check = AsyncOp.Run("status + second cancel", 20f, async ct =>
            {
                status = await HttpJson.SendAsync("GET", jobUrl + "/status", null, null, auth, 10, ct, throwOnHttpError: false);
                again = await HttpJson.SendAsync("PUT", jobUrl + "/cancel", null, null, auth, 10, ct, throwOnHttpError: false);
            });
            while (check.KeepWaiting)
                yield return null;
            check.AssertSucceeded(() => Mock.Describe(mark));
            Assert.AreEqual("EXPIRED", (string)JObject.Parse(status.Text)["status"], status.Text);
            Assert.AreEqual(400, again.StatusCode, again.Text);
            Assert.AreEqual("CANCELED", (string)JObject.Parse(again.Text)["status"], again.Text);
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
            const string wrongPath = "tripo3d/triposplat";   // accepted at submit, the job FAILS with 404 "Path /triposplat not found"
            const string workingPath = "gp/triposplat";      // accepted at submit, the job FAILS with 422 (probe body invalid)
            const string unknownApp = "zzz/does-not-exist";  // 404 "Application \"zzz\" not found" at submit
            var paths = new[] { wrongPath, workingPath, unknownApp };
            var results = new Dictionary<string, ProbeResult>();
            int mark = Mock.RequestCount;
            var op = AsyncOp.Run("ProbeAsync", kTimeout, async ct =>
            {
                // start all at once (like the connection tester), then await in order
                var started = paths.Select(p => MediaJobClient.ProbeAsync(Settings, p, ct)).ToList();
                for (int i = 0; i < paths.Length; i++)
                    results[paths[i]] = await started[i];
            });
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe(mark));

            Assert.AreEqual(ProbeOutcome.Missing, results[wrongPath].outcome, results[wrongPath].ToString());
            Assert.AreEqual(404, results[wrongPath].statusCode, "decided by the FAILED job's result, not at submit");
            Assert.AreEqual(ProbeOutcome.Present, results[workingPath].outcome, results[workingPath].ToString());
            Assert.AreEqual(422, results[workingPath].statusCode, "the model rejected the probe input: the path exists");
            Assert.AreEqual(ProbeOutcome.Missing, results[unknownApp].outcome, results[unknownApp].ToString());
            Assert.AreEqual(404, results[unknownApp].statusCode);

            var submits = Mock.Find(r => r.kind == "submit", mark);
            Assert.AreEqual(200, submits.Single(r => r.target == wrongPath).status, "a wrong model path is accepted at submit");
            Assert.AreEqual(200, submits.Single(r => r.target == workingPath).status, "validation is asynchronous");
            var rejected = submits.Single(r => r.target == unknownApp);
            Assert.AreEqual(404, rejected.status, "an unknown application is rejected at submit");
            Assert.IsNull(rejected.requestId, "nothing is queued for it");
            foreach (var s in new[] { submits.Single(r => r.target == wrongPath), submits.Single(r => r.target == workingPath) })
                Assert.AreEqual(1, Mock.Find(r => r.kind == "result" && r.requestId == s.requestId, mark).Count, Mock.Describe(mark));
            Assert.AreEqual(0, Mock.Find(r => r.kind == "cancel", mark).Count, "settled probes are not cancelled");
            Assert.AreEqual(0, Mock.Find(r => r.kind == "file", mark).Count, "a probe never downloads anything");
        }

        [UnityTest]
        public IEnumerator ConnectionTester_PassesAgainstTheMock()
        {
            ConnectionReport report = null;
            double started = Time.realtimeSinceStartupAsDouble;
            var op = AsyncOp.Run("ConnectionTester.TestAsync", kTimeout, async ct =>
                report = await ConnectionTester.TestAsync(Settings, true, ct));
            while (op.KeepWaiting)
                yield return null;
            op.AssertSucceeded(() => Mock.Describe());
            double elapsed = Time.realtimeSinceStartupAsDouble - started;
            Assert.IsTrue(report.ok, report.ToString());
            Assert.IsTrue(report.reachable);
            Assert.IsTrue(report.chatOk);
            Assert.IsTrue(report.chatModelListed);
            Assert.AreEqual(KeySource.Override, report.keySource);
            Assert.IsTrue(report.probes.Any(p => p.path == "tripo3d/triposplat" && p.outcome == ProbeOutcome.Missing), report.ToString());
            Assert.IsTrue(report.probes.Any(p => p.path == "gp/triposplat" && p.Present), report.ToString());
            Assert.IsFalse(report.probes.Any(p => p.outcome == ProbeOutcome.Error), "every probe job settled:\n" + report);
            Assert.AreEqual("gp/triposplat", ModelPathCache.Get(Mock.ApiBaseUrl, MediaRouteKeys.ImageToSplat),
                "the first present candidate is cached");
            // every default candidate (16) is accepted at submit and settles only after a 3 s poll: one after the
            // other that would take about 50 s
            Assert.Less(elapsed, 30.0, $"the probes run concurrently ({report.probes.Count} probes took {elapsed:F1} s)");
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
