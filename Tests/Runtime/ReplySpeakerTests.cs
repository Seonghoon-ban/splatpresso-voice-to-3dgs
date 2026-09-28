using System.Collections;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SplatPresso.Api;
using SplatPresso.Voice;
using UnityEngine;
using UnityEngine.TestTools;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Spoken replies of the GenPresso chat backend (textToSpeech route) against the mock server, plus the helpers
    /// they rely on (request body per model, WAV decoding) and the push-to-talk silence guard.
    /// </summary>
    public class ReplySpeakerTests : MockServerFixture
    {
        [UnityTest]
        public IEnumerator Speak_RunsTheTextToSpeechRoute_AndQueuesThePcmOnThePlayer()
        {
            var go = new GameObject("ReplySpeaker (test)");
            try
            {
                var player = go.AddComponent<AudioStreamPlayer>();
                int mark = Mock.RequestCount;
                using (var speaker = new ReplySpeaker(() => Settings, player))
                {
                    double took = -1;
                    float buffered = 0f;
                    speaker.Spoken += (text, secs) =>
                    {
                        took = secs;
                        buffered = player.BufferedSeconds;
                    };
                    speaker.Speak("좋아요, 소파 옆에 의자를 만들어 드릴게요.");
                    Assert.IsTrue(speaker.IsPending);

                    float until = Time.realtimeSinceStartup + 20f;
                    while (took < 0 && Time.realtimeSinceStartup < until)
                        yield return null;
                    Assert.GreaterOrEqual(took, 0.0, "the reply was synthesized\n" + Mock.Describe(mark));
                    Assert.Greater(buffered, 0.3f, "the 0.5 s mock reply was queued on the player");
                    Assert.IsFalse(speaker.IsPending);

                    var submits = Mock.Find(r => r.kind == "submit" && r.capability == "tts", mark);
                    Assert.AreEqual(1, submits.Count, Mock.Describe(mark));
                    Assert.AreEqual("gp/minimax/speech-02-turbo", submits[0].target);
                    var body = JObject.Parse(submits[0].submitBody);
                    Assert.AreEqual("좋아요, 소파 옆에 의자를 만들어 드릴게요.", (string)body["text"]);
                    Assert.AreEqual("pcm", (string)body["audio_setting"]["format"]);
                    Assert.AreEqual(AudioStreamPlayer.SourceRate, (int)body["audio_setting"]["sample_rate"]);
                    Assert.AreEqual(1, Mock.Find(r => r.kind == "file" && r.target == "speech.pcm", mark).Count);
                }
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        [UnityTest]
        public IEnumerator Stop_DropsTheReplyBeingSynthesized_AndSilencesThePlayer()
        {
            var go = new GameObject("ReplySpeaker (test)");
            try
            {
                var player = go.AddComponent<AudioStreamPlayer>();
                using (var speaker = new ReplySpeaker(() => Settings, player))
                {
                    bool spoken = false;
                    speaker.Spoken += (text, secs) => spoken = true;
                    speaker.Speak("first");
                    speaker.Speak("second");
                    yield return null;
                    speaker.Stop(); // barge-in while the first reply is still being synthesized
                    Assert.IsFalse(speaker.IsPending);

                    float until = Time.realtimeSinceStartup + 4f;
                    while (Time.realtimeSinceStartup < until)
                        yield return null;
                    Assert.IsFalse(spoken, "a cancelled reply is never played");
                    Assert.AreEqual(0f, player.BufferedSeconds);
                }
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        [UnityTest]
        public IEnumerator TypedTurn_ReplyIsSpoken_WhenSpeakRepliesIsOn()
        {
            Settings.speakReplies = true;
            var go = new GameObject("VoiceAgent (test)");
            go.SetActive(false);
            var voice = go.AddComponent<VoiceAgent>();
            voice.settings = Settings;
            go.SetActive(true);
            try
            {
                string reply = null;
                voice.AgentReply += r => reply = r;
                voice.StartBackend();
                int mark = Mock.RequestCount;
                voice.SubmitText("put a chair next to the sofa");

                float until = Time.realtimeSinceStartup + 45f;
                while (Mock.Find(r => r.kind == "file" && r.target == "speech.pcm", mark).Count == 0 && Time.realtimeSinceStartup < until)
                    yield return null;
                Assert.IsNotNull(reply, Mock.Describe(mark));
                var submits = Mock.Find(r => r.kind == "submit" && r.capability == "tts", mark);
                Assert.AreEqual(1, submits.Count, Mock.Describe(mark));
                Assert.AreEqual(reply, (string)JObject.Parse(submits[0].submitBody)["text"], "the reply text is what gets spoken");
                Assert.NotNull(voice.player, "the agent added a player for spoken replies");
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        [Test]
        public void BuildInput_AsksMiniMaxForPcm_AndSendsOtherModelsJustTheText()
        {
            Settings.ttsVoice = "Calm_Woman";
            Settings.ttsSpeed = 1.2f;
            var minimax = ReplySpeaker.BuildInput(Settings, new MediaTarget { provider = MediaProvider.Genpresso, path = "gp/minimax/speech-02-turbo" }, "hi");
            Assert.AreEqual("hi", (string)minimax["text"]);
            Assert.AreEqual("Calm_Woman", (string)minimax["voice_setting"]["voice_id"]);
            Assert.AreEqual(1.2f, (float)minimax["voice_setting"]["speed"], 1e-4f);
            Assert.AreEqual("pcm", (string)minimax["audio_setting"]["format"]);
            Assert.AreEqual("url", (string)minimax["output_format"]);

            var eleven = ReplySpeaker.BuildInput(Settings, new MediaTarget { provider = MediaProvider.Genpresso, path = "gp/elevenlabs/tts/multilingual-v2" }, "hi");
            Assert.AreEqual(1, eleven.Count);
            Assert.AreEqual("hi", (string)eleven["text"]);
        }

        [Test]
        public void TryDecodePcm16Wav_RoundTripsWavUtility_AndDownmixesStereo()
        {
            var mono = new float[] { 0f, 0.5f, -0.5f, 0.25f };
            Assert.IsTrue(ReplySpeaker.TryDecodePcm16Wav(WavUtility.FromSamples(mono, 16000, 1), out var samples, out int rate));
            Assert.AreEqual(16000, rate);
            Assert.AreEqual(mono.Length, samples.Length);
            for (int i = 0; i < mono.Length; i++)
                Assert.AreEqual(mono[i], samples[i], 1e-3f);

            var stereo = new float[] { 0.5f, -0.5f, 0.2f, 0.4f };
            Assert.IsTrue(ReplySpeaker.TryDecodePcm16Wav(WavUtility.FromSamples(stereo, 24000, 2), out samples, out rate));
            Assert.AreEqual(2, samples.Length);
            Assert.AreEqual(0f, samples[0], 1e-3f);
            Assert.AreEqual(0.3f, samples[1], 1e-3f);

            Assert.IsFalse(ReplySpeaker.TryDecodePcm16Wav(new byte[] { 1, 2, 3 }, out _, out _));
        }

        [Test]
        public void SilenceGuard_TreatsAMutedMicAsSilence_AndCanBeTurnedOff()
        {
            Assert.IsTrue(VoiceAgent.IsSilentUtterance(0f, 0.01f), "a muted mic (peak 0.00) is not sent");
            Assert.IsTrue(VoiceAgent.IsSilentUtterance(0.004f, 0.01f));
            Assert.IsFalse(VoiceAgent.IsSilentUtterance(0.2f, 0.01f), "normal speech is sent");
            Assert.IsFalse(VoiceAgent.IsSilentUtterance(0f, 0f), "threshold 0 = always send");
        }
    }
}
