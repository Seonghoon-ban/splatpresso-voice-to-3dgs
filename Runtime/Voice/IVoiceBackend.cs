using System;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>
    /// A voice front end driven by <see cref="VoiceAgent"/> on the main thread. Implementations:
    /// <see cref="GenpressoVoiceBackend"/> (push-to-talk WAV to GenPresso chat, GenPresso key only) and
    /// <see cref="OpenAIRealtimeBackend"/> (speech-to-speech over WebSocket, OpenAI key).
    /// </summary>
    public interface IVoiceBackend : IDisposable
    {
        /// <summary>Ready for turns (key present; for Realtime also socket open).</summary>
        bool IsAvailable { get; }
        /// <summary>A turn or narration is in flight, or a spoken response is active.</summary>
        bool IsBusy { get; }
        /// <summary>True: wants microphone chunks while talk is held (Realtime). False: only the finished utterance (chat).</summary>
        bool WantsStreamingAudio { get; }

        /// <summary>What the user said (or typed), once known.</summary>
        event Action<string> UserTranscript;
        /// <summary>What the agent replied (text, always raised even when the reply is also spoken).</summary>
        event Action<string> AgentReply;
        /// <summary>The user asked for object(s) to be generated and placed.</summary>
        event Action<VoicePlacementRequest> PlacementRequested;
        /// <summary>The user asked to cancel the running generation(s).</summary>
        event Action CancelRequested;
        /// <summary>A user-visible problem (missing key, connection failure, rejected request, ...).</summary>
        event Action<string> Error;

        /// <summary>Connects / validates the key. Called once before any other member.</summary>
        void Start(VoiceAgentContext ctx);
        /// <summary>Stops turns and disconnects; the backend may be started again.</summary>
        void Stop();
        /// <summary>Main-thread pump, called every frame (socket inbox, timers).</summary>
        void Tick();
        /// <summary>Push-to-talk pressed: barge in (stop playback, drop pending narration).</summary>
        void OnTalkPressed();
        /// <summary>A ~100 ms base64 PCM16 microphone chunk (only when <see cref="WantsStreamingAudio"/>).</summary>
        void OnAudioChunk(string b64Pcm16);
        /// <summary>
        /// Push-to-talk released. <paramref name="utt"/> is null when nothing usable was recorded (too short, or
        /// the capture was aborted). <paramref name="captureJpeg"/> snapshots the current view (may be null).
        /// </summary>
        void OnTalkReleased(AudioUtterance utt, Func<Awaitable<byte[]>> captureJpeg);
        /// <summary>A pipeline progress notice; <paramref name="narrate"/> asks for it to be relayed to the user.</summary>
        void NotifyPipeline(string message, bool narrate);
        /// <summary>A typed request, handled exactly like a spoken turn (for testing without a microphone).</summary>
        void SubmitText(string text);
        /// <summary>Stops any speech output now (and cancels the active spoken response, if any).</summary>
        void StopSpeaking();
    }
}
