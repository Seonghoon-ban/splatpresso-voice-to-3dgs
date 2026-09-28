using NUnit.Framework;
using SplatPresso.Voice;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Which voice backend runs: Auto (the default) is OpenAI Realtime as soon as an OpenAI key is saved, and settings
    /// assets written before Auto existed (GenpressoChat was the default) move to Auto once.
    /// </summary>
    public class VoiceBackendSelectionTests
    {
        [Test]
        public void Auto_IsRealtimeWithAnOpenAIKey_AndGenpressoChatWithout()
        {
            Assert.AreEqual(VoiceBackendKind.OpenAIRealtime, VoiceAgent.ResolveBackend(VoiceBackendKind.Auto, true, true));
            Assert.AreEqual(VoiceBackendKind.GenpressoChat, VoiceAgent.ResolveBackend(VoiceBackendKind.Auto, false, true));
            Assert.AreEqual(VoiceBackendKind.GenpressoChat, VoiceAgent.ResolveBackend(VoiceBackendKind.Auto, true, false), "no WebSockets (WebGL)");
        }

        [Test]
        public void ExplicitChoices_AreKept_ExceptRealtimeThatCannotRun()
        {
            Assert.AreEqual(VoiceBackendKind.GenpressoChat, VoiceAgent.ResolveBackend(VoiceBackendKind.GenpressoChat, true, true));
            Assert.AreEqual(VoiceBackendKind.None, VoiceAgent.ResolveBackend(VoiceBackendKind.None, true, true));
            Assert.AreEqual(VoiceBackendKind.OpenAIRealtime, VoiceAgent.ResolveBackend(VoiceBackendKind.OpenAIRealtime, true, true));
            Assert.AreEqual(VoiceBackendKind.GenpressoChat, VoiceAgent.ResolveBackend(VoiceBackendKind.OpenAIRealtime, false, true));
        }

        [Test]
        public void NewSettings_DefaultToAuto()
        {
            var s = ScriptableObject.CreateInstance<SplatPressoSettings>();
            try { Assert.AreEqual(VoiceBackendKind.Auto, s.voiceBackend); }
            finally { Object.DestroyImmediate(s); }
        }

        [Test]
        public void OldAssetsOnTheOldDefault_MoveToAutoOnce_ButLaterExplicitChoicesStay()
        {
            var s = ScriptableObject.CreateInstance<SplatPressoSettings>();
            try
            {
                // written by 0.2.0 or older: no settingsVersion, voiceBackend GenpressoChat (0) = the old default
                JsonUtility.FromJsonOverwrite("{\"voiceBackend\":0,\"settingsVersion\":0}", s);
                Assert.AreEqual(VoiceBackendKind.Auto, s.voiceBackend);

                // an old asset that chose Realtime or None keeps it
                var t = ScriptableObject.CreateInstance<SplatPressoSettings>();
                try
                {
                    JsonUtility.FromJsonOverwrite("{\"voiceBackend\":2,\"settingsVersion\":0}", t);
                    Assert.AreEqual(VoiceBackendKind.None, t.voiceBackend);
                }
                finally { Object.DestroyImmediate(t); }

                // written by this version with an explicit GenpressoChat: kept
                var u = ScriptableObject.CreateInstance<SplatPressoSettings>();
                try
                {
                    JsonUtility.FromJsonOverwrite("{\"voiceBackend\":0,\"settingsVersion\":1}", u);
                    Assert.AreEqual(VoiceBackendKind.GenpressoChat, u.voiceBackend);
                }
                finally { Object.DestroyImmediate(u); }
            }
            finally { Object.DestroyImmediate(s); }
        }
    }
}
