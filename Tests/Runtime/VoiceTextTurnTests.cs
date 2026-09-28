using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SplatPresso.Voice;
using UnityEngine;
using UnityEngine.TestTools;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Typed turns through the GenPresso chat voice backend against the mock server: a "create" action must reach
    /// SplatPressoRoot and become a run that completes; the model's out-of-schema actions must be sanitized
    /// (named objects only, at most 4 types, 1..3 copies, default hint); and an unparseable reply must never be
    /// guessed into an action.
    /// </summary>
    public class VoiceTextTurnTests : MockServerFixture
    {
        const float kTurnTimeout = 45f;
        const float kRunTimeout = 90f;

        SceneRig m_Rig;
        readonly List<VoicePlacementRequest> m_Requests = new List<VoicePlacementRequest>();
        readonly List<string> m_Replies = new List<string>();
        readonly List<string> m_Transcripts = new List<string>();

        [TearDown]
        public void DisposeScene()
        {
            m_Rig?.Dispose();
            m_Rig = null;
            m_Requests.Clear();
            m_Replies.Clear();
            m_Transcripts.Clear();
        }

        // VoiceAgent only (no root): typed turns need no microphone and no rendering.
        void CreateVoiceOnlyScene()
        {
            m_Rig = new SceneRig { servicesGo = new GameObject("VoiceAgent (test)") };
            m_Rig.servicesGo.SetActive(false);
            m_Rig.voice = m_Rig.servicesGo.AddComponent<VoiceAgent>();
            m_Rig.voice.settings = Settings;
            m_Rig.servicesGo.SetActive(true);
            m_Rig.voice.PlacementRequested += r => m_Requests.Add(r);
            m_Rig.voice.AgentReply += r => m_Replies.Add(r);
            m_Rig.voice.UserTranscript += t => m_Transcripts.Add(t);
            m_Rig.voice.StartBackend();
        }

        GenpressoVoiceBackend Backend()
        {
            var backend = m_Rig.voice.GenpressoBackend;
            Assert.NotNull(backend, $"VoiceAgent did not start the GenPresso chat backend (active: {m_Rig.voice.ActiveBackend})");
            Assert.IsTrue(m_Rig.voice.IsReady, "the backend needs only the GenPresso key");
            return backend;
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator TypedTurn_CreateActionStartsARunThatCompletes()
        {
            TestEnv.RequireCapture();
            var resources = TestEnv.RequireRendererResources();
            m_Rig = SceneRig.CreateWithServices(Settings, resources, withRoot: true, withVoice: true);
            var root = m_Rig.root;
            RunStartedInfo started = null;
            PlacementResult completed = null;
            PlacementFailure failed = null;
            root.RunStarted += i => started ??= i;
            root.RunCompleted += r => completed ??= r;
            root.RunFailed += f => failed ??= f;
            m_Rig.voice.PlacementRequested += r => m_Requests.Add(r);
            yield return null; // Start(): the root starts the voice backend
            var backend = Backend();
            int mark = Mock.RequestCount;

            const string said = "Please put a red chair on the floor in front of me.";
            VoiceTurnResponse response = null;
            var turn = AsyncOp.Run("RunTextTurnAsync", kTurnTimeout, async ct => response = await backend.RunTextTurnAsync(said, ct));
            while (turn.KeepWaiting)
                yield return null;
            turn.AssertSucceeded(() => Mock.Describe(mark));
            Assert.NotNull(response, "the turn failed: " + m_Rig.voice.LastError);
            Assert.IsTrue(response.actions.Any(a => a.IsCreate), "the mock answers with a create action");
            Assert.AreEqual(1, m_Requests.Count, "exactly one placement request");
            Assert.AreEqual("red chair", m_Requests[0].request.objects[0].name);
            Assert.AreEqual(said, m_Requests[0].sourceUtterance, "a typed turn's utterance is the typed text");
            Assert.AreEqual(said, m_Rig.voice.LastHeardTranscript);
            var voiceCalls = Mock.Find(r => r.kind == "chat:voice", mark);
            Assert.AreEqual(1, voiceCalls.Count, Mock.Describe(mark));
            Assert.AreEqual("voice_turn", voiceCalls[0].schemaName);

            // the request becomes a run...
            double until = Time.realtimeSinceStartupAsDouble + 5.0;
            while (started == null && failed == null && Time.realtimeSinceStartupAsDouble < until)
                yield return null;
            Assert.NotNull(started, "no run started: " + failed?.reason);
            Assert.AreEqual(said, started.sourceUtterance);

            // ...that completes
            until = Time.realtimeSinceStartupAsDouble + kRunTimeout;
            while (completed == null && failed == null && Time.realtimeSinceStartupAsDouble < until)
                yield return null;
            Assert.IsNull(failed, failed != null ? $"run failed at {failed.stage}: {failed.reason}\n{Mock.Describe(mark)}" : null);
            Assert.NotNull(completed, "the run did not complete in time:\n" + Mock.Describe(mark));
            Assert.AreEqual(started.runId, completed.runId);
            Assert.AreEqual(ObjectStatus.Placed, completed.objects[0].status, completed.objects[0].skipReason);
            Assert.AreEqual(1, m_Rig.spawner.SpawnedObjects.Count);

            // pipeline notices never create more runs (narration turns come back without actions)
            until = Time.realtimeSinceStartupAsDouble + 10.0;
            while (m_Rig.voice.IsBusy && Time.realtimeSinceStartupAsDouble < until)
                yield return null; // let pending narration finish before the scene is torn down
            Assert.AreEqual(1, m_Requests.Count, Mock.Describe(mark));
        }

        [UnityTest]
        public IEnumerator TypedTurn_SanitizesOutOfSchemaActions()
        {
            CreateVoiceOnlyScene();
            yield return null;
            var backend = Backend();
            int mark = Mock.RequestCount;

            VoiceTurnResponse response = null;
            var turn = AsyncOp.Run("RunTextTurnAsync", kTurnTimeout, async ct => response = await backend.RunTextTurnAsync("CLAMP_TEST add some furniture", ct));
            while (turn.KeepWaiting)
                yield return null;
            turn.AssertSucceeded(() => Mock.Describe(mark));
            Assert.NotNull(response);

            // the second create action has no named object and is dropped entirely
            Assert.AreEqual(1, m_Requests.Count, "one valid create action");
            var req = m_Requests[0].request;
            Assert.AreEqual("anywhere sensible", req.placementHint, "an empty hint gets the default");
            Assert.LessOrEqual(req.objects.Count, 4, "at most 4 object types");
            foreach (var o in req.objects)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(o.name));
                Assert.That(o.count, Is.InRange(1, 3));
            }
            Assert.AreEqual("lamp", req.objects[0].name);
            Assert.AreEqual(3, req.objects[0].count, "9 copies are clamped to 3");
            Assert.IsTrue(req.objects.Any(o => o.name == "vase" && o.count == 1), "0 copies become 1");
            Assert.IsFalse(req.objects.Any(o => o.name == "plant"), "the 5th object type is dropped");
            CollectionAssert.Contains(m_Replies, "Adding some furniture.");
            CollectionAssert.Contains(m_Transcripts, "CLAMP_TEST add some furniture", "a typed turn's transcript is the typed text");
        }

        [UnityTest]
        public IEnumerator TypedTurn_InvalidJson_NeverGuessesActions()
        {
            CreateVoiceOnlyScene();
            yield return null;
            var backend = Backend();
            int mark = Mock.RequestCount;

            VoiceTurnResponse response = null;
            var turn = AsyncOp.Run("RunTextTurnAsync", kTurnTimeout, async ct => response = await backend.RunTextTurnAsync("GARBAGE_TEST add a chair", ct));
            while (turn.KeepWaiting)
                yield return null;
            turn.AssertSucceeded(() => Mock.Describe(mark));

            Assert.IsNull(response, "an unparseable reply is a failed turn");
            Assert.IsEmpty(m_Requests, "never guess actions from an unparseable reply (even though it mentions a chair)");
            Assert.AreEqual(2, Mock.Find(r => r.kind == "chat:voice", mark).Count, "one JSON-repair round trip, then give up:\n" + Mock.Describe(mark));
            Assert.IsNotEmpty(m_Replies, "the user gets a local apology");
            Assert.IsNotEmpty(m_Rig.voice.LastError);
        }
    }
}
