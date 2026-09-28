using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SplatPresso.Api;
using SplatPresso.Voice;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SplatPresso.Tests
{
    /// <summary>
    /// The OpenAI Realtime voice backend against <see cref="MockRealtimeServer"/> (one server per test, reached through
    /// <see cref="OpenAIRealtimeBackend.EndpointOverride"/> with a fake OpenAI key override): session setup and
    /// readiness, a push-to-talk turn (wire order, snapshot at press, playback), tool calls (request_placement raised at
    /// once with exactly one follow-up response, invalid arguments, cancel_generation), barge-in, rejected upgrades
    /// (Auth failure vs. retrying), reconnects, a rejected session.update, Stop/Dispose, and VoiceAgent's Auto backend
    /// falling back to GenPresso chat. Plus the socket's pure helpers (error classification, models probe).
    /// </summary>
    /// <remarks>
    /// Most tests drive the backend directly and pump <see cref="OpenAIRealtimeBackend.Tick"/> every frame; the
    /// VoiceAgent tests let the component tick itself. The models probe of a refused upgrade goes through
    /// UnityWebRequest over plain http, so (like <see cref="MockServerFixture"/>) the fixture allows insecure http in
    /// the editor for its duration and ignores the tests that need it when Unity still refuses.
    /// </remarks>
    public class RealtimeBackendTests
    {
        const string kKey = "sk-test-realtime-0000000000000001";
        const string kModel = "gpt-realtime-test";
        const float kTimeout = 15f;

        // a tiny stand-in for the view snapshot (SOI/APP0 ... EOI); the backend only base64-encodes it
        static readonly byte[] kFakeJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9 };

        MockRealtimeServer m_Mock;
        SplatPressoSettings m_Settings;
        string m_SessionsDir;
        OpenAIRealtimeBackend m_Backend;
        AudioStreamPlayer m_Player;
        VoiceAgent m_Voice;
        readonly List<GameObject> m_Objects = new List<GameObject>();
        readonly List<SplatPressoSettings> m_UsedSettings = new List<SplatPressoSettings>();

        readonly List<string> m_Replies = new List<string>();
        readonly List<string> m_Transcripts = new List<string>();
        readonly List<string> m_Errors = new List<string>();
        readonly List<VoicePlacementRequest> m_Placements = new List<VoicePlacementRequest>();
        int m_Cancels;
        int m_CaptureCalls;
        bool m_CaptureDone;

        bool m_HttpChecked;
        string m_HttpBlocked;
        string m_HttpPreflightError;
#if UNITY_EDITOR
        UnityEditor.InsecureHttpOption m_PrevHttpOption;
        bool m_ChangedHttpOption;
#endif

        [OneTimeSetUp]
        public void AllowPlainHttp()
        {
#if UNITY_EDITOR
            m_PrevHttpOption = UnityEditor.PlayerSettings.insecureHttpOption;
            if (m_PrevHttpOption != UnityEditor.InsecureHttpOption.AlwaysAllowed)
            {
                UnityEditor.PlayerSettings.insecureHttpOption = UnityEditor.InsecureHttpOption.AlwaysAllowed;
                m_ChangedHttpOption = true;
            }
#endif
        }

        [OneTimeTearDown]
        public void RestorePlainHttp()
        {
#if UNITY_EDITOR
            if (m_ChangedHttpOption)
                UnityEditor.PlayerSettings.insecureHttpOption = m_PrevHttpOption;
            m_ChangedHttpOption = false;
#endif
            foreach (var s in m_UsedSettings)
                if (s != null)
                    Object.Destroy(s);
            m_UsedSettings.Clear();
        }

        [SetUp]
        public void SetUp()
        {
            if (!RealtimeSocket.IsSupported)
                Assert.Ignore("No WebSockets on this platform (WebGL): the Realtime backend cannot run.");
            m_Mock = new MockRealtimeServer();
            // overrides are cleared when play mode starts, so set them per test (and clear them in TearDown)
            ApiKeys.SetOverride(ApiKeyKind.OpenAI, kKey);
            OpenAIRealtimeBackend.EndpointOverride = m_Mock.RealtimeUrl;
            m_SessionsDir = TestEnv.NewTempDir("realtime");
            m_Settings = TestEnv.CreateSettings(m_Mock.Origin + "/api/v1", m_SessionsDir);
            m_Settings.voiceBackend = VoiceBackendKind.OpenAIRealtime;
            m_Settings.realtimeModel = kModel;
            m_Settings.realtimeVoice = "marin";
            m_Settings.useSemanticVad = false;
            SplatPressoSettings.Active = m_Settings;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Voice != null)
                m_Voice.StopBackend();
            m_Voice = null;
            m_Backend?.Dispose();
            m_Backend = null;
            foreach (var go in m_Objects)
                if (go != null)
                    Object.Destroy(go);
            m_Objects.Clear();
            m_Player = null;
            m_Mock?.Dispose();
            m_Mock = null;
            OpenAIRealtimeBackend.EndpointOverride = null;
            ApiKeys.SetOverride(ApiKeyKind.OpenAI, null);
            SplatPressoSettings.Active = null;
            // destroyed in OneTimeTearDown: a late continuation of a finished test may still read its settings
            if (m_Settings != null)
                m_UsedSettings.Add(m_Settings);
            m_Settings = null;
            TestEnv.DeleteDir(m_SessionsDir);
            m_SessionsDir = null;
            m_Replies.Clear();
            m_Transcripts.Clear();
            m_Errors.Clear();
            m_Placements.Clear();
            m_Cancels = 0;
            m_CaptureCalls = 0;
            m_CaptureDone = false;
        }

        // ------------------------------------------------------------------------------------------
        // pure helpers of RealtimeSocket

        [Test]
        public void ClassifyConnectError_SortsHandshakeFailures()
        {
            var kind = RealtimeSocket.ClassifyConnectError(new WebSocketException("Unable to connect to the remote server",
                new TypeInitializationException("System.Net.WebSockets.WebSocketHandle", new MissingMethodException("stripped"))), out string detail);
            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Runtime, kind, "a TypeInitializationException in the chain: this build lacks what the handshake needs");
            StringAssert.Contains(" -> ", detail, "the detail is the whole message chain");
            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Runtime, Classify(new TypeInitializationException("System.Configuration.ConfigurationManager", null)));
            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Runtime, Classify(new PlatformNotSupportedException("no WebSockets")));

            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Auth,
                Classify(new WebSocketException("The server returned status code '401' when status code '101' was expected.")),
                "a readable 401 (newer runtimes) is fatal even on a bare WebSocketException");
            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Auth, Classify(new Exception("403 Forbidden")));

            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Rejected, Classify(new WebSocketException("Unable to connect to the remote server")),
                "Unity's runtime reports a refused upgrade as a bare WebSocketException: diagnose it");
            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Rejected,
                Classify(new WebSocketException("The server returned status code '429' when status code '101' was expected.")),
                "429 alone is not fatal: diagnose it");

            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Transient,
                Classify(new WebSocketException("Unable to connect to the remote server", new SocketException((int)SocketError.ConnectionRefused))),
                "network failures are wrapped around their cause: retry");
            Assert.AreEqual(RealtimeSocket.ConnectErrorKind.Transient, Classify(new IOException("Unable to read data from the transport connection")));
        }

        static RealtimeSocket.ConnectErrorKind Classify(Exception e) => RealtimeSocket.ClassifyConnectError(e, out _);

        [Test]
        public void ModelProbeUrl_MapsTheRealtimeUrlToTheModelsEndpoint()
        {
            Assert.AreEqual("https://api.openai.com/v1/models/gpt-realtime-2.1",
                RealtimeSocket.ModelProbeUrl("wss://api.openai.com/v1/realtime?model=gpt-realtime-2.1", out string model));
            Assert.AreEqual("gpt-realtime-2.1", model);

            Assert.AreEqual(m_Mock.Origin + "/v1/models/" + kModel, RealtimeSocket.ModelProbeUrl(m_Mock.RealtimeUrl + kModel, out model), "ws -> http");
            Assert.AreEqual(kModel, model);

            Assert.AreEqual("https://proxy.example.com/openai/v1/models/my-model",
                RealtimeSocket.ModelProbeUrl("wss://proxy.example.com/openai/v1/realtime?foo=1&model=my%2Dmodel", out model),
                "a proxy's path prefix is kept; the model is found among other parameters");
            Assert.AreEqual("my-model", model);

            Assert.AreEqual("https://api.openai.com/v1/models/", RealtimeSocket.ModelProbeUrl("not a url", out model));
            Assert.AreEqual("", model);
        }

        [Test]
        public void InterpretModelProbe_FailsForGoodOnlyWhenRetryingCannotHelp()
        {
            const string missingScopes = "{\"error\":{\"message\":\"You have insufficient permissions for this operation. Missing scopes: api.model.read.\"}}";
            string diagnosis;

            Assert.AreEqual(RealtimeSocket.FailureKind.None, RealtimeSocket.InterpretModelProbe(200, "{}", "m", out diagnosis));
            StringAssert.Contains("valid", diagnosis);

            Assert.AreEqual(RealtimeSocket.FailureKind.Auth, RealtimeSocket.InterpretModelProbe(401, "{\"error\":{\"code\":\"invalid_api_key\"}}", "m", out diagnosis));
            StringAssert.Contains("401", diagnosis);
            Assert.AreEqual(RealtimeSocket.FailureKind.None, RealtimeSocket.InterpretModelProbe(401, missingScopes, "m", out diagnosis),
                "a restricted key may lack the models scope and still be valid for Realtime");
            Assert.AreEqual(RealtimeSocket.FailureKind.Auth, RealtimeSocket.InterpretModelProbe(403, "{}", "m", out diagnosis));
            StringAssert.Contains("403", diagnosis);
            Assert.AreEqual(RealtimeSocket.FailureKind.None, RealtimeSocket.InterpretModelProbe(403, missingScopes, "m", out diagnosis));

            Assert.AreEqual(RealtimeSocket.FailureKind.Auth, RealtimeSocket.InterpretModelProbe(404, "{}", "gpt-nope", out diagnosis));
            StringAssert.Contains("gpt-nope", diagnosis, "a 404 names the model");

            Assert.AreEqual(RealtimeSocket.FailureKind.None, RealtimeSocket.InterpretModelProbe(429, "{\"error\":{\"code\":\"rate_limit_exceeded\"}}", "m", out diagnosis),
                "rate limiting heals");
            Assert.AreEqual(RealtimeSocket.FailureKind.Auth, RealtimeSocket.InterpretModelProbe(429, "{\"error\":{\"code\":\"insufficient_quota\"}}", "m", out diagnosis),
                "no quota left does not heal");
            StringAssert.Contains("quota", diagnosis);

            Assert.AreEqual(RealtimeSocket.FailureKind.None, RealtimeSocket.InterpretModelProbe(500, "oops", "m", out diagnosis));
            StringAssert.Contains("500", diagnosis);
            Assert.AreEqual(RealtimeSocket.FailureKind.None, RealtimeSocket.InterpretModelProbe(0, null, "m", out diagnosis), "the probe itself failed: inconclusive");
            Assert.IsNull(diagnosis);
        }

        // ------------------------------------------------------------------------------------------
        // session setup

        [UnityTest]
        public IEnumerator Connect_SendsSessionUpdateWithEventIdInstructionsAndTools_AndOpensOnSessionUpdated()
        {
            m_Mock.SessionUpdatedDelayMs = 800;
            StartBackend();
            Assert.IsFalse(m_Backend.IsAvailable, "not available before the socket opened");
            yield return Pump(() => m_Mock.CountEvents("session.update") > 0 || m_Backend.HasFailed, kTimeout);

            var upgrades = m_Mock.HttpRequests.FindAll(r => r.upgrade);
            Assert.AreEqual(1, upgrades.Count, m_Mock.Describe());
            Assert.AreEqual(101, upgrades[0].status);
            Assert.AreEqual("/v1/realtime", upgrades[0].path);
            Assert.AreEqual(kModel, upgrades[0].model, "the settings' model is in the URL");
            Assert.AreEqual("Bearer " + kKey, upgrades[0].authorization, "the OpenAI key goes in the Authorization header");

            var updates = m_Mock.EventsOfType("session.update");
            Assert.AreEqual(1, updates.Count, m_Mock.Describe());
            StringAssert.StartsWith("sp_session_", updates[0].EventId, "session.update carries an event_id (errors about it echo it back)");
            var session = (JObject)updates[0].json["session"];
            Assert.IsFalse(string.IsNullOrWhiteSpace((string)session["instructions"]), "instructions are sent");
            var tools = ((JArray)session["tools"]).Select(t => (string)t["name"]).ToList();
            CollectionAssert.AreEquivalent(new[] { VoicePromptLibrary.ToolRequestPlacement, VoicePromptLibrary.ToolCancelGeneration }, tools);
            Assert.AreEqual(JTokenType.Null, session["audio"]["input"]["turn_detection"].Type, "push-to-talk: no server-side turn detection");
            Assert.AreEqual(24000, (int)session["audio"]["input"]["format"]["rate"]);
            Assert.IsNotNull(session["audio"]["input"]["transcription"], "input transcription is requested");
            Assert.AreEqual("marin", (string)session["audio"]["output"]["voice"]);

            // the mock answers session.updated 0.8 s later: turns must not open before it
            Assert.IsFalse(m_Backend.IsAvailable, "not available before session.updated");
            bool openedEarly = false;
            yield return Pump(() =>
            {
                if (m_Backend.IsAvailable && m_Mock.CountSent("session.updated") == 0)
                    openedEarly = true;
                return m_Backend.IsAvailable;
            }, kTimeout);
            Assert.IsFalse(openedEarly, "IsAvailable turned true before session.updated was sent");
            AssertAvailable();
            double sinceUpdated = m_Mock.Now - m_Mock.SentOfType("session.updated")[0].time;
            Assert.Less(sinceUpdated, 1.5, "opened by session.updated, not by the 3 s fail-open");
            Assert.IsFalse(m_Backend.IsBusy);
            Assert.AreEqual(1, m_Mock.EventCount, "nothing but the session.update was sent:\n" + m_Mock.Describe());
            Assert.IsEmpty(m_Errors);
            Assert.IsEmpty(m_Mock.ProtocolErrors);
        }

        [UnityTest]
        public IEnumerator SessionUpdatedNeverArrives_TurnsOpenAfterTheFailOpenDelay()
        {
            m_Mock.AnswerSessionUpdate = false;
            StartBackend();
            yield return Pump(() => m_Mock.CountEvents("session.update") > 0 || m_Backend.HasFailed, kTimeout);
            Assert.AreEqual(1, m_Mock.CountEvents("session.update"), m_Mock.Describe());
            double createdAt = m_Mock.SentOfType("session.created")[0].time;

            yield return PumpFor(1.5f);
            Assert.IsFalse(m_Backend.IsAvailable, "still waiting for session.updated");
            yield return Pump(() => m_Backend.IsAvailable, kTimeout);
            AssertAvailable();
            Assert.GreaterOrEqual(m_Mock.Now - createdAt, 2.5, "turns open after the ~3 s fail-open, not at once");
            Assert.AreEqual(1, m_Mock.CountEvents("session.update"), "the update is not re-sent");
        }

        [UnityTest]
        public IEnumerator RejectedSessionUpdate_IsRetriedOnceWithCedar()
        {
            m_Settings.realtimeVoice = "not-a-voice";
            m_Mock.RejectSessionUpdates = 1;
            StartBackend();
            yield return WaitAvailable();
            AssertAvailable();
            double openedAt = m_Mock.Now;
            yield return PumpFor(0.5f);

            var updates = m_Mock.EventsOfType("session.update");
            Assert.AreEqual(2, updates.Count, m_Mock.Describe());
            Assert.AreEqual("not-a-voice", Voice(updates[0]));
            Assert.AreEqual("cedar", Voice(updates[1]), "the retry uses the default voice");
            Assert.AreNotEqual(updates[0].EventId, updates[1].EventId);
            Assert.AreEqual((string)updates[0].json["session"]["instructions"], (string)updates[1].json["session"]["instructions"], "the retry keeps the instructions");
            Assert.AreEqual(2, ((JArray)updates[1].json["session"]["tools"]).Count, "the retry keeps the tools");

            var serverErrors = m_Mock.SentOfType("error");
            Assert.AreEqual(1, serverErrors.Count);
            Assert.AreEqual(updates[0].EventId, (string)serverErrors[0].json["error"]["event_id"], "the mock rejected exactly the first update");
            Assert.AreEqual(1, m_Errors.Count, string.Join("\n", m_Errors));
            StringAssert.Contains("rejected the session settings", m_Errors[0]);
            double createdAt = m_Mock.SentOfType("session.created")[0].time;
            Assert.Less(openedAt - createdAt, 2.0, "opened by the retry's session.updated, not by the fail-open");
        }

        [UnityTest]
        public IEnumerator SessionUpdateRejectedAgain_IsNotRetriedForever()
        {
            m_Settings.realtimeVoice = "not-a-voice";
            m_Mock.RejectSessionUpdates = 100;
            StartBackend();
            yield return WaitAvailable(); // fail-open after ~3 s
            AssertAvailable();
            yield return PumpFor(0.5f);
            Assert.AreEqual(2, m_Mock.CountEvents("session.update"), "one retry only:\n" + m_Mock.Describe());
            Assert.AreEqual("cedar", Voice(m_Mock.EventsOfType("session.update")[1]));
            Assert.AreEqual(2, m_Errors.Count, "each rejection is reported:\n" + string.Join("\n", m_Errors));
        }

        // ------------------------------------------------------------------------------------------
        // turns

        [UnityTest]
        public IEnumerator PushToTalkTurn_StreamsInOrder_SnapshotsAtPress_AndPlaysTheReply()
        {
            m_Settings.sendFrameWithSpeech = true;
            m_Mock.AudioDeltaCount = 3;
            m_Mock.AudioDeltaSeconds = 1f;
            m_Mock.FragmentMessagesAbove = 16 * 1024; // the deltas arrive as a text frame plus continuation frames
            m_Mock.ReplyTranscript = "Sure, one red chair.";
            m_Mock.TranscriptDelayMs = 100;
            StartBackend(withPlayer: true, withCapture: true);
            float bufferedAtReply = -1f;
            m_Backend.AgentReply += _ => bufferedAtReply = m_Player.BufferedSeconds;
            yield return WaitAvailable();
            AssertAvailable();
            int mark = m_Mock.EventCount;

            // the last chunk is above 64 KB as JSON: a client frame with a 64-bit length
            var chunks = new[]
            {
                MockRealtimeServer.Pcm16Base64(0.1f), MockRealtimeServer.Pcm16Base64(0.1f, 330f),
                MockRealtimeServer.Pcm16Base64(0.1f, 440f), MockRealtimeServer.Pcm16Base64(1.5f),
            };
            m_Backend.OnTalkPressed();
            Assert.AreEqual(1, m_CaptureCalls, "the view is snapshotted when push-to-talk is PRESSED");
            foreach (string chunk in chunks)
            {
                m_Backend.OnAudioChunk(chunk);
                m_Backend.Tick();
                yield return null;
            }
            yield return Pump(() => m_CaptureDone, 5f);
            Assert.IsTrue(m_CaptureDone, "the snapshot finished while talk was held");
            m_Backend.OnTalkReleased(Utterance(1.8f), FakeCaptureAsync);
            Assert.AreEqual(1, m_CaptureCalls, "releasing push-to-talk does not take a second snapshot");

            yield return Pump(() => m_Replies.Count > 0 && m_Transcripts.Count > 0 && !m_Backend.IsBusy, kTimeout);
            var events = m_Mock.EventsFrom(mark);
            CollectionAssert.AreEqual(new[]
            {
                "input_audio_buffer.clear",
                "input_audio_buffer.append", "input_audio_buffer.append", "input_audio_buffer.append", "input_audio_buffer.append",
                "input_audio_buffer.commit",
                "conversation.item.create",
                "response.create",
            }, Types(events), m_Mock.Describe(mark));
            for (int i = 0; i < chunks.Length; i++)
                Assert.AreEqual(chunks[i], (string)events[1 + i].json["audio"], $"append {i} arrived intact");
            Assert.AreEqual(chunks.Sum(c => Convert.FromBase64String(c).Length), m_Mock.CommittedAudioBytes.Single(), "every appended byte was committed");

            var item = events[6].json["item"];
            Assert.AreEqual("user", (string)item["role"]);
            Assert.AreEqual("input_image", (string)item["content"][0]["type"]);
            Assert.AreEqual("data:image/jpeg;base64," + Convert.ToBase64String(kFakeJpeg), (string)item["content"][0]["image_url"],
                "the snapshot taken at press goes with the turn");
            StringAssert.StartsWith("sp_resp_", events[7].EventId, "response.create carries an event_id");

            CollectionAssert.AreEqual(new[] { "Sure, one red chair." }, m_Replies);
            CollectionAssert.AreEqual(new[] { m_Mock.UserTranscript }, m_Transcripts);
            Assert.AreEqual(3 * MockRealtimeServer.Pcm16Base64(1f).Length, m_Backend.AudioBytesThisResponse, "every audio delta was taken");
            Assert.Greater(bufferedAtReply, 1.5f, "the 3 s reply was queued on the player");
            Assert.IsEmpty(m_Errors);
            Assert.IsEmpty(m_Mock.ProtocolErrors);
        }

        [UnityTest]
        public IEnumerator RequestPlacement_IsRaisedAtOnce_WithOneFollowUpResponse()
        {
            m_Mock.FunctionCallOnFirstResponse = true;
            m_Mock.TranscriptDelayMs = 2000; // the input transcript arrives well after the tool call
            m_Mock.AudioDeltaSeconds = 1.5f; // the acknowledgement's deltas exceed 64 KB: 64-bit length server frames
            m_Mock.AckTranscript = "Okay, adding two red chairs.";
            StartBackend();
            double placedAt = -1;
            bool busyAtPlacement = false, activeAtPlacement = true;
            int transcriptsAtPlacement = -1;
            m_Backend.PlacementRequested += _ =>
            {
                placedAt = m_Mock.Now;
                busyAtPlacement = m_Backend.IsBusy;
                activeAtPlacement = m_Backend.IsResponseActive;
                transcriptsAtPlacement = m_Transcripts.Count;
            };
            yield return WaitAvailable();
            AssertAvailable();
            int mark = m_Mock.EventCount;

            yield return Talk(MockRealtimeServer.Pcm16Base64(0.2f), MockRealtimeServer.Pcm16Base64(0.2f));
            yield return Pump(() => m_Placements.Count > 0, kTimeout);
            Assert.AreEqual(1, m_Placements.Count, m_Mock.Describe(mark));
            var doneSent = m_Mock.SentOfType("response.done")[0];
            Assert.Less(placedAt - doneSent.time, 1.0, "PlacementRequested fires as soon as response.done arrives");
            Assert.AreEqual(0, transcriptsAtPlacement, "not held back until the input transcript arrives");
            Assert.IsNull(m_Placements[0].sourceUtterance, "the transcript was unknown then, and is never waited for");
            Assert.IsTrue(busyAtPlacement, "the follow-up response was already requested when the event fired");
            Assert.IsFalse(activeAtPlacement, "response.done was processed");
            var request = m_Placements[0].request;
            Assert.AreEqual("red chair", request.objects.Single().name);
            Assert.AreEqual(2, request.objects[0].count);
            Assert.AreEqual("next to the sofa", request.placementHint);
            Assert.AreEqual("Add a red chair next to the sofa.", request.intentSummary);

            yield return Pump(() => m_Replies.Count > 0 && m_Transcripts.Count > 0 && !m_Backend.IsBusy, kTimeout);
            CollectionAssert.AreEqual(new[] { "Okay, adding two red chairs." }, m_Replies, "the follow-up speaks the acknowledgement");
            CollectionAssert.AreEqual(new[] { m_Mock.UserTranscript }, m_Transcripts);
            yield return PumpFor(0.5f); // nothing else may follow

            var events = m_Mock.EventsFrom(mark);
            var creates = events.FindAll(e => e.type == "response.create");
            var outputs = FunctionOutputs(events);
            Assert.AreEqual(2, creates.Count, "the turn's response and exactly one follow-up:\n" + m_Mock.Describe(mark));
            Assert.AreEqual(1, outputs.Count, m_Mock.Describe(mark));
            string callId = (string)doneSent.json["response"]["output"][0]["call_id"];
            Assert.AreEqual(callId, (string)outputs[0].json["item"]["call_id"]);
            Assert.AreEqual("started", (string)JObject.Parse((string)outputs[0].json["item"]["output"])["status"]);
            Assert.Less(creates[0].index, outputs[0].index);
            Assert.Less(outputs[0].index, creates[1].index, "the function output precedes the follow-up response.create");
            Assert.AreEqual(0, m_Cancels);
            Assert.IsEmpty(m_Errors);
            Assert.IsEmpty(m_Mock.ProtocolErrors);
        }

        [UnityTest]
        public IEnumerator RequestPlacement_CarriesTheTurnTranscript_WhenAlreadyKnown()
        {
            m_Mock.FunctionCallOnFirstResponse = true;
            m_Mock.TranscriptDelayMs = 0;  // transcript right after committed...
            m_Mock.ResponseDelayMs = 400;  // ...and well before the tool call
            StartBackend();
            yield return WaitAvailable();
            AssertAvailable();

            yield return Talk(MockRealtimeServer.Pcm16Base64(0.2f));
            yield return Pump(() => m_Placements.Count > 0, kTimeout);
            Assert.AreEqual(1, m_Placements.Count, m_Mock.Describe());
            Assert.AreEqual(m_Mock.UserTranscript, m_Placements[0].sourceUtterance);
        }

        [UnityTest]
        public IEnumerator InvalidToolArguments_AreRejected_ButStillAnswered()
        {
            m_Mock.FunctionCallOnFirstResponse = true;
            m_Mock.FunctionCallArguments = "{\"intent_summary\":\"add something\",\"objects\":[{\"name\":\"  \"}],\"placement_hint\":\"\"}";
            StartBackend();
            yield return WaitAvailable();
            AssertAvailable();
            int mark = m_Mock.EventCount;

            m_Backend.SubmitText("add something");
            yield return Pump(() => m_Replies.Count > 0 && !m_Backend.IsBusy, kTimeout);
            yield return PumpFor(0.3f);
            Assert.IsEmpty(m_Placements, "arguments without a named object never become a placement");
            var events = m_Mock.EventsFrom(mark);
            var outputs = FunctionOutputs(events);
            Assert.AreEqual(1, outputs.Count, m_Mock.Describe(mark));
            Assert.AreEqual("error", (string)JObject.Parse((string)outputs[0].json["item"]["output"])["status"]);
            Assert.AreEqual(2, events.Count(e => e.type == "response.create"), "the model still gets its follow-up response");
            CollectionAssert.AreEqual(new[] { m_Mock.AckTranscript }, m_Replies);
        }

        [UnityTest]
        public IEnumerator CancelGenerationTool_RaisesCancelRequested()
        {
            m_Mock.FunctionCallOnFirstResponse = true;
            m_Mock.FunctionCallName = VoicePromptLibrary.ToolCancelGeneration;
            m_Mock.FunctionCallArguments = "{}";
            StartBackend();
            yield return WaitAvailable();
            AssertAvailable();
            int mark = m_Mock.EventCount;

            m_Backend.SubmitText("stop that");
            yield return Pump(() => m_Cancels > 0 && m_Replies.Count > 0 && !m_Backend.IsBusy, kTimeout);
            Assert.AreEqual(1, m_Cancels, m_Mock.Describe(mark));
            Assert.IsEmpty(m_Placements);
            var outputs = FunctionOutputs(m_Mock.EventsFrom(mark));
            Assert.AreEqual(1, outputs.Count);
            Assert.AreEqual("cancelling", (string)JObject.Parse((string)outputs[0].json["item"]["output"])["status"]);
            Assert.AreEqual(2, m_Mock.CountEvents("response.create", mark));
        }

        [UnityTest]
        public IEnumerator BargeIn_CancelsTheActiveResponse_AndFlushesPlayback()
        {
            m_Mock.AudioDeltaCount = 2;
            m_Mock.AudioDeltaSeconds = 1f;
            m_Mock.ResponseDoneDelayMs = 5000; // the response stays active until cancelled
            StartBackend(withPlayer: true);
            yield return WaitAvailable();
            AssertAvailable();

            m_Backend.SubmitText("tell me a story");
            yield return Pump(() => m_Backend.IsResponseActive && m_Player.BufferedSeconds > 0.5f, kTimeout);
            Assert.IsTrue(m_Backend.IsResponseActive, m_Mock.Describe());
            Assert.Greater(m_Player.BufferedSeconds, 0.5f, "the agent is speaking");
            int mark = m_Mock.EventCount;

            m_Backend.OnTalkPressed();
            Assert.AreEqual(0f, m_Player.BufferedSeconds, "barge-in silences the agent at once");
            yield return Pump(() => !m_Backend.IsResponseActive, kTimeout);
            Assert.IsFalse(m_Backend.IsResponseActive, "the cancelled response ended:\n" + m_Mock.Describe(mark));
            CollectionAssert.AreEqual(new[] { "input_audio_buffer.clear", "response.cancel" }, Types(m_Mock.EventsFrom(mark)));
            var done = m_Mock.SentOfType("response.done").Last();
            Assert.AreEqual("cancelled", (string)done.json["response"]["status"]);
            Assert.Less(done.time - m_Mock.EventsFrom(mark, "response.cancel")[0].time, 2.0, "ended by the cancel, not by the 5 s timer");

            // released without a usable utterance: the buffer is cleared, never committed, and nothing is requested
            m_Backend.OnTalkReleased(null, null);
            yield return PumpFor(0.5f);
            CollectionAssert.AreEqual(new[] { "input_audio_buffer.clear", "response.cancel", "input_audio_buffer.clear" }, Types(m_Mock.EventsFrom(mark)));
            Assert.AreEqual(0f, m_Player.BufferedSeconds);
            Assert.IsFalse(m_Backend.IsBusy);
            Assert.IsEmpty(m_Errors);
        }

        // ------------------------------------------------------------------------------------------
        // connection failures and lifetime

        [UnityTest]
        public IEnumerator RejectedKey_FailsWithAuth_AndStopsRetrying()
        {
            yield return CheckPlainHttp();
            RequirePlainHttp();
            m_Mock.RejectUpgradeStatus = 401;
            m_Mock.ModelProbeStatus = 401;
            LogAssert.Expect(LogType.Error, new Regex("Realtime connection failed"));

            double t0 = Time.realtimeSinceStartupAsDouble;
            StartBackend();
            yield return Pump(() => m_Backend.HasFailed, kTimeout);
            double took = Time.realtimeSinceStartupAsDouble - t0;
            Assert.IsTrue(m_Backend.HasFailed, "no failure: " + m_Backend.LastSocketError + "\n" + m_Mock.Describe());
            Assert.AreEqual(RealtimeSocket.FailureKind.Auth, m_Backend.FailureKind, m_Backend.LastSocketError);
            Assert.Less(took, 5.0, "a rejected key fails fast");
            StringAssert.Contains("401", m_Backend.LastSocketError);
            StringAssert.DoesNotContain(kKey, m_Backend.LastSocketError, "the key never appears in errors");
            Assert.IsFalse(m_Backend.IsAvailable);
            Assert.IsFalse(m_Backend.IsReconnecting);
            Assert.AreEqual(1, m_Mock.UpgradeAttempts, m_Mock.Describe());

            // Unity's runtime hides the upgrade status, so the socket asks /v1/models with the same key (a runtime that
            // reports "status code '401'" fails without the probe)
            var probes = m_Mock.ModelProbes;
            Assert.LessOrEqual(probes.Count, 1, "the models diagnosis runs at most once");
            if (probes.Count == 1)
            {
                Assert.AreEqual(kModel, probes[0].model);
                Assert.AreEqual("Bearer " + kKey, probes[0].authorization);
            }
            Assert.IsTrue(m_Errors.Any(e => e.Contains("OpenAI Realtime connection failed") && e.Contains("Check the OpenAI key")),
                string.Join("\n", m_Errors));
            foreach (string e in m_Errors)
                StringAssert.DoesNotContain(kKey, e);

            yield return PumpFor(2.5f);
            Assert.AreEqual(1, m_Mock.UpgradeAttempts, "no reconnect after an Auth failure:\n" + m_Mock.Describe());
        }

        [UnityTest]
        public IEnumerator RejectedUpgradeWithAValidKey_KeepsReconnecting()
        {
            yield return CheckPlainHttp();
            RequirePlainHttp();
            m_Mock.RejectUpgradeStatus = 503;
            m_Mock.ModelProbeStatus = 200;

            StartBackend();
            yield return Pump(() => m_Mock.UpgradeAttempts >= 2 || m_Backend.HasFailed, kTimeout);
            Assert.IsFalse(m_Backend.HasFailed, "gave up: " + m_Backend.LastSocketError);
            Assert.AreEqual(RealtimeSocket.FailureKind.None, m_Backend.FailureKind);
            Assert.GreaterOrEqual(m_Mock.UpgradeAttempts, 2, m_Mock.Describe());
            Assert.IsTrue(m_Backend.IsReconnecting);
            Assert.AreEqual(1, m_Mock.ModelProbes.Count, "diagnosed once per Connect, not on every retry:\n" + m_Mock.Describe());
            Assert.IsEmpty(m_Errors, "transient trouble is not reported as an error");
        }

        [UnityTest]
        public IEnumerator ServerDrop_Reconnects_WithAFreshSession()
        {
            StartBackend();
            yield return WaitAvailable();
            AssertAvailable();

            // a network drop (TCP reset)...
            m_Mock.DropAllConnections(graceful: false);
            yield return Pump(() => !m_Backend.IsAvailable, 5f);
            Assert.IsFalse(m_Backend.IsAvailable);
            Assert.IsTrue(m_Backend.IsReconnecting);
            yield return Pump(() => m_Backend.IsAvailable, kTimeout);
            AssertAvailable();
            Assert.AreEqual(2, m_Mock.AcceptedConnections, m_Mock.Describe());
            AssertFreshSessionOn(1);

            // ...and a server-side close handshake
            m_Mock.DropAllConnections(graceful: true);
            yield return Pump(() => !m_Backend.IsAvailable, 5f);
            Assert.IsFalse(m_Backend.IsAvailable);
            yield return Pump(() => m_Backend.IsAvailable, kTimeout);
            AssertAvailable();
            Assert.AreEqual(3, m_Mock.AcceptedConnections, m_Mock.Describe());
            AssertFreshSessionOn(2);
            CollectionAssert.Contains(m_Mock.ClientCloseCodes, 1000, "the client answered the server's close frame");
            Assert.IsEmpty(m_Errors, "a dropped connection heals silently");
            Assert.IsEmpty(m_Mock.ProtocolErrors);
        }

        void AssertFreshSessionOn(int connection)
        {
            Assert.AreEqual(1, m_Mock.CountSent("session.created", connection));
            Assert.AreEqual(1, m_Mock.CountSent("session.updated", connection));
            var events = m_Mock.EventsOfType(null, connection);
            CollectionAssert.AreEqual(new[] { "conversation.item.create", "session.update" }, Types(events), m_Mock.Describe());
            var item = events[0].json["item"];
            Assert.AreEqual("system", (string)item["role"]);
            StringAssert.Contains("reconnected", (string)item["content"][0]["text"], "the new session is told it lost the conversation");
        }

        [UnityTest]
        public IEnumerator Stop_ClosesGracefully_StartsAgain_AndDisposeLeavesNoSocketOpen()
        {
            StartBackend();
            yield return WaitAvailable();
            AssertAvailable();

            m_Backend.Stop();
            Assert.IsFalse(m_Backend.IsAvailable);
            yield return Pump(() => m_Mock.OpenWebSockets == 0, 5f);
            Assert.AreEqual(0, m_Mock.OpenWebSockets, "Stop closes the socket");
            CollectionAssert.Contains(m_Mock.ClientCloseCodes, 1000, "with a normal close frame");

            // a stopped backend can be started again: a new connection and a fresh session (no "reconnected" notice)
            m_Backend.Start(new VoiceAgentContext { settings = m_Settings });
            yield return WaitAvailable();
            AssertAvailable();
            Assert.AreEqual(2, m_Mock.AcceptedConnections);
            CollectionAssert.AreEqual(new[] { "session.update" }, Types(m_Mock.EventsOfType(null, 1)), m_Mock.Describe());

            int closes = m_Mock.ClientCloseCodes.Count;
            m_Backend.Dispose();
            m_Backend = null;
            yield return Pump(() => m_Mock.OpenWebSockets == 0, 5f);
            Assert.AreEqual(0, m_Mock.OpenWebSockets, "Dispose leaves no socket open");
            Assert.AreEqual(closes, m_Mock.ClientCloseCodes.Count, "Dispose aborts at once (no close handshake)");
            Assert.IsEmpty(m_Mock.ProtocolErrors);
        }

        // ------------------------------------------------------------------------------------------
        // VoiceAgent: Auto backend

        [UnityTest]
        public IEnumerator Auto_WithAKeyAndAHealthyServer_RunsRealtime()
        {
            m_Settings.voiceBackend = VoiceBackendKind.Auto;
            var voice = CreateVoiceAgent();
            voice.StartBackend();
            Assert.AreEqual(VoiceBackendKind.OpenAIRealtime, voice.ActiveBackend, "Auto with an OpenAI key runs Realtime");
            Assert.IsNotNull(voice.RealtimeBackend);
            yield return Pump(() => voice.IsReady, kTimeout);
            Assert.IsTrue(voice.IsReady, voice.LastError + "\n" + m_Mock.Describe());
            Assert.AreEqual(VoiceBackendKind.OpenAIRealtime, voice.ActiveBackend);
            Assert.IsNotNull(voice.player, "Realtime gets a player for its spoken replies");
            Assert.AreEqual(1, m_Mock.CountEvents("session.update"));
            Assert.IsNull(voice.LastError);

            voice.StopBackend();
            yield return Pump(() => m_Mock.OpenWebSockets == 0, 5f);
            Assert.AreEqual(0, m_Mock.OpenWebSockets, "StopBackend closes the socket");
        }

        [UnityTest]
        public IEnumerator Auto_FallsBackToGenpressoChat_WhenTheRealtimeKeyIsRejected()
        {
            yield return CheckPlainHttp();
            RequirePlainHttp();
            m_Settings.voiceBackend = VoiceBackendKind.Auto;
            m_Mock.RejectUpgradeStatus = 401;
            m_Mock.ModelProbeStatus = 401;
            LogAssert.Expect(LogType.Error, new Regex("Realtime connection failed"));

            var voice = CreateVoiceAgent();
            voice.StartBackend();
            Assert.AreEqual(VoiceBackendKind.OpenAIRealtime, voice.ActiveBackend, "Auto with an OpenAI key starts Realtime");
            yield return Pump(() => voice.ActiveBackend == VoiceBackendKind.GenpressoChat, kTimeout);
            Assert.AreEqual(VoiceBackendKind.GenpressoChat, voice.ActiveBackend, voice.LastError + "\n" + m_Mock.Describe());
            Assert.IsNotNull(voice.GenpressoBackend);
            Assert.IsNull(voice.RealtimeBackend);
            Assert.IsNotNull(voice.LastError);
            StringAssert.Contains("Realtime", voice.LastError);
            StringAssert.Contains("401", voice.LastError);
            StringAssert.DoesNotContain(kKey, voice.LastError);
            Assert.AreEqual(1, m_Mock.UpgradeAttempts);

            yield return PumpFor(2f);
            Assert.AreEqual(VoiceBackendKind.GenpressoChat, voice.ActiveBackend, "stays on GenPresso chat");
            Assert.AreEqual(1, m_Mock.UpgradeAttempts, "Realtime is not retried behind the user's back:\n" + m_Mock.Describe());
        }

        // ------------------------------------------------------------------------------------------
        // helpers

        OpenAIRealtimeBackend StartBackend(bool withPlayer = false, bool withCapture = false)
        {
            m_Backend = new OpenAIRealtimeBackend();
            m_Backend.UserTranscript += t => m_Transcripts.Add(t);
            m_Backend.AgentReply += r => m_Replies.Add(r);
            m_Backend.PlacementRequested += p => m_Placements.Add(p);
            m_Backend.CancelRequested += () => m_Cancels++;
            m_Backend.Error += e => m_Errors.Add(e);
            if (withPlayer)
            {
                var go = new GameObject("AudioStreamPlayer (test)");
                m_Objects.Add(go);
                m_Player = go.AddComponent<AudioStreamPlayer>(); // RequireComponent adds the AudioSource
            }
            m_Backend.Start(new VoiceAgentContext
            {
                settings = m_Settings,
                player = m_Player,
                captureJpeg = withCapture ? new Func<Awaitable<byte[]>>(FakeCaptureAsync) : null,
            });
            return m_Backend;
        }

        // VoiceAgent on its own GameObject. The microphone lives on an inactive object, so the tests never open a
        // real input device (no push-to-talk is needed here).
        VoiceAgent CreateVoiceAgent()
        {
            var micHost = new GameObject("MicCapture (test, inactive)");
            micHost.SetActive(false);
            m_Objects.Add(micHost);
            var mic = micHost.AddComponent<MicCapture>();

            var go = new GameObject("VoiceAgent (test)");
            go.SetActive(false);
            m_Objects.Add(go);
            m_Voice = go.AddComponent<VoiceAgent>();
            m_Voice.settings = m_Settings;
            m_Voice.mic = mic;
            go.SetActive(true);
            m_Voice.Error += e => m_Errors.Add(e);
            return m_Voice;
        }

        async Awaitable<byte[]> FakeCaptureAsync()
        {
            m_CaptureCalls++;
            await Awaitable.NextFrameAsync();
            m_CaptureDone = true;
            return kFakeJpeg;
        }

        static AudioUtterance Utterance(float seconds) => new AudioUtterance
        {
            samples = new float[(int)(RealtimeProtocol.SampleRate * seconds)],
            sampleRate = RealtimeProtocol.SampleRate,
            peak = 0.4f,
        };

        // A push-to-talk turn driven on the backend: press, one chunk per frame, release.
        IEnumerator Talk(params string[] chunks)
        {
            m_Backend.OnTalkPressed();
            foreach (string chunk in chunks)
            {
                m_Backend.OnAudioChunk(chunk);
                m_Backend.Tick();
                yield return null;
            }
            m_Backend.OnTalkReleased(Utterance(0.2f * chunks.Length), null);
        }

        // Ticks the backend under test (a VoiceAgent ticks its own) once per frame until done() or the timeout.
        IEnumerator Pump(Func<bool> done, float timeoutSec)
        {
            double until = Time.realtimeSinceStartupAsDouble + timeoutSec;
            while (true)
            {
                m_Backend?.Tick();
                if (done() || Time.realtimeSinceStartupAsDouble >= until)
                    yield break;
                yield return null;
            }
        }

        IEnumerator PumpFor(float seconds) => Pump(() => false, seconds);

        IEnumerator WaitAvailable() => Pump(() => m_Backend.IsAvailable || m_Backend.HasFailed, kTimeout);

        void AssertAvailable()
        {
            Assert.IsTrue(m_Backend.IsAvailable, "the backend is not available (" + (m_Backend.LastSocketError ?? "no socket error") + ")\n" + m_Mock.Describe());
        }

        static string[] Types(IEnumerable<RealtimeClientEvent> events) => events.Select(e => e.type).ToArray();

        static string Voice(RealtimeClientEvent sessionUpdate) => (string)sessionUpdate.json["session"]?["audio"]?["output"]?["voice"];

        static List<RealtimeClientEvent> FunctionOutputs(List<RealtimeClientEvent> events) =>
            events.FindAll(e => e.type == "conversation.item.create" && (string)e.json["item"]?["type"] == "function_call_output");

        // The models probe of a refused upgrade uses UnityWebRequest over plain http: checked once per fixture against
        // a throwaway server (so no test's request log sees it).
        IEnumerator CheckPlainHttp()
        {
            if (m_HttpChecked)
                yield break;
            m_HttpChecked = true;
            var server = new MockRealtimeServer();
            try
            {
                HttpResponse resp = null;
                var op = AsyncOp.Run("plain-http preflight", 15f, async ct =>
                {
                    resp = await HttpJson.SendAsync("GET", server.Origin + "/v1/models/" + kModel, null, null, null, 10, ct, throwOnHttpError: false);
                });
                while (op.KeepWaiting)
                    yield return null;
                if (op.Error != null && op.Error.Message.IndexOf("could not start", StringComparison.OrdinalIgnoreCase) >= 0)
                    m_HttpBlocked = "Unity refused the plain-http request to the local mock server (" + op.Error.Message +
                                    "). Set Project Settings > Player > Allow downloads over HTTP to 'Always allowed' for the test project.";
                else if (!op.Completed)
                    m_HttpPreflightError = "the plain-http preflight did not finish";
                else if (op.Error != null)
                    m_HttpPreflightError = "the plain-http preflight threw " + op.Error;
                else if (resp == null || resp.StatusCode != 200)
                    m_HttpPreflightError = $"the plain-http preflight answered {resp?.StatusCode}: {resp?.Text}";
            }
            finally
            {
                server.Dispose();
            }
        }

        void RequirePlainHttp()
        {
            if (m_HttpBlocked != null)
                Assert.Ignore(m_HttpBlocked);
            if (m_HttpPreflightError != null)
                Assert.Fail(m_HttpPreflightError);
        }
    }
}
