using System;
using SplatPresso.Placement;
using SplatPresso.Rendering;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>
    /// The voice agent facade: the only voice type <see cref="SplatPressoRoot"/> and <see cref="VoiceHud"/> talk
    /// to. Owns push-to-talk (keyboard via <see cref="InputCompat"/>, or an external source such as an XR button),
    /// the shared <see cref="MicCapture"/>, the per-turn snapshot of the view and the selected
    /// <see cref="IVoiceBackend"/> (<see cref="VoiceBackendKind"/> in the settings).
    /// </summary>
    /// <remarks>
    /// Backend selection: GenpressoChat (default, GenPresso key only), OpenAIRealtime (needs an OpenAI key; falls
    /// back to GenpressoChat with a warning when the key is missing or WebSockets are unavailable), None (no
    /// microphone; typed requests still go through a text-only GenPresso chat backend).
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Voice Agent")]
    public sealed class VoiceAgent : MonoBehaviour
    {
        /// <summary>PlayerPrefs key of the in-game microphone choice (wins over the settings default).</summary>
        public const string MicDevicePrefKey = "SplatPresso.MicDevice";

        const int kChatSampleRate = 16000;       // 4 MB request cap: 16 kHz fits ~90 s, 24 kHz only ~40 s
        const int kRealtimeSampleRate = RealtimeProtocol.SampleRate;
        const float kRealtimeSnapshotTimeoutSec = 0.6f;
        const float kChatSnapshotTimeoutSec = 1.0f;
        const int kSnapshotJpegQuality = 70;
        const float kCaptureServiceLookupIntervalSec = 2f;

        [Tooltip("Settings to use (empty = SplatPressoSettings.Active).")]
        public SplatPressoSettings settings;
        [Tooltip("Camera whose view is sent with each turn. Empty = the CaptureService camera, else Camera.main. " +
                 "In XR assign a mono camera at the HMD pose (the capture path needs a mono camera).")]
        public Camera snapshotCamera;
        [Tooltip("Microphone (added automatically when empty).")]
        public MicCapture mic;
        [Tooltip("Speech output for the OpenAI Realtime backend (added automatically when that backend starts).")]
        public AudioStreamPlayer player;

        /// <summary>What the user said (or typed).</summary>
        public event Action<string> UserTranscript;
        /// <summary>What the agent replied (always text; also spoken with the Realtime backend).</summary>
        public event Action<string> AgentReply;
        /// <summary>The user asked for object(s); carries the utterance that produced the request.</summary>
        public event Action<VoicePlacementRequest> PlacementRequested;
        /// <summary>The user asked to cancel the running generations.</summary>
        public event Action CancelRequested;
        /// <summary>A user-visible problem (missing key, connection failure, rejected request, ...).</summary>
        public event Action<string> Error;

        /// <summary>
        /// Optional external push-to-talk source polled every frame (e.g. an XR controller button); OR-ed with the
        /// keyboard key and <see cref="BeginTalk"/>.
        /// </summary>
        public Func<bool> ExternalTalkHeld;

        IVoiceBackend m_Backend;
        bool m_Talking;
        bool m_HeldPrev;
        bool m_ApiTalkHeld;
        float m_TalkStart;
        bool m_ExplicitlyStopped;
        bool m_ResumeOnEnable;
        bool m_WarnedExternalTalk;
        bool m_WarnedVoiceOff;
        CaptureService m_CaptureService;
        float m_NextCaptureServiceLookup;
        Func<Awaitable<byte[]>> m_CaptureFunc;

        // ------------------------------------------------------------------------------------------
        // state

        /// <summary>Settings in use.</summary>
        public SplatPressoSettings Settings => settings != null ? settings : SplatPressoSettings.Active;
        /// <summary>The running backend kind (None when stopped or voice is off).</summary>
        public VoiceBackendKind ActiveBackend { get; private set; } = VoiceBackendKind.None;
        /// <summary>The running backend (null when stopped).</summary>
        public IVoiceBackend Backend => m_Backend;
        /// <summary>The GenPresso chat backend when it is running (also for VoiceBackendKind.None, text only), else null.</summary>
        public GenpressoVoiceBackend GenpressoBackend => m_Backend as GenpressoVoiceBackend;
        /// <summary>The OpenAI Realtime backend when it is running, else null.</summary>
        public OpenAIRealtimeBackend RealtimeBackend => m_Backend as OpenAIRealtimeBackend;
        /// <summary>True while a backend is running.</summary>
        public bool IsRunning => m_Backend != null;
        /// <summary>The backend can take turns (key present; for Realtime also connected).</summary>
        public bool IsReady => m_Backend != null && m_Backend.IsAvailable;
        /// <summary>The microphone is capturing a turn (or streaming in hands-free mode).</summary>
        public bool IsRecording => mic != null && mic.IsCapturing;
        /// <summary>A turn or narration is in flight ("Thinking...").</summary>
        public bool IsBusy => m_Backend != null && m_Backend.IsBusy;
        /// <summary>Spoken audio is playing (Realtime backend).</summary>
        public bool IsSpeaking => player != null && player.isActiveAndEnabled && player.IsPlaying;
        /// <summary>Realtime with semantic VAD: no push-to-talk, the microphone streams continuously.</summary>
        public bool IsHandsFree => ActiveBackend == VoiceBackendKind.OpenAIRealtime && Settings.useSemanticVad;
        /// <summary>Push-to-talk is possible (a voice backend and microphone support exist).</summary>
        public bool CanTalk => ActiveBackend != VoiceBackendKind.None && mic != null && MicCapture.IsSupported;
        /// <summary>Recent microphone peak level 0..1 (for meters).</summary>
        public float MicLevel => mic != null ? mic.CurrentLevel : 0f;
        /// <summary>Label of the microphone in use.</summary>
        public string ActiveMicDevice => mic != null ? mic.ActiveDevice : "(off)";
        /// <summary>Available microphone device names.</summary>
        public string[] MicDevices => MicCapture.Devices;
        /// <summary>Seconds the current push-to-talk turn has been held.</summary>
        public float RecordingSeconds => m_Talking ? Time.unscaledTime - m_TalkStart : 0f;
        /// <summary>The configured push-to-talk key.</summary>
        public KeyCode PushToTalkKey => Settings.pushToTalkKey;

        /// <summary>Last transcript of the user's speech (or typed text).</summary>
        public string LastHeardTranscript { get; private set; }
        /// <summary>Time.unscaledTime of <see cref="LastHeardTranscript"/>.</summary>
        public float LastHeardTime { get; private set; } = -999f;
        /// <summary>Last agent reply (or narrated milestone in NarrationMode.Subtitle).</summary>
        public string LastAgentReply { get; private set; }
        /// <summary>Time.unscaledTime of <see cref="LastAgentReply"/>.</summary>
        public float LastAgentReplyTime { get; private set; } = -999f;
        /// <summary>Last error message.</summary>
        public string LastError { get; private set; }
        /// <summary>Time.unscaledTime of <see cref="LastError"/>.</summary>
        public float LastErrorTime { get; private set; } = -999f;

        // ------------------------------------------------------------------------------------------
        // lifecycle

        void Awake()
        {
            if (mic == null)
                mic = GetComponent<MicCapture>();
            if (mic == null)
                mic = gameObject.AddComponent<MicCapture>();
            if (player == null)
                player = GetComponent<AudioStreamPlayer>();
            mic.OnAudioChunk += HandleMicChunk;
            m_CaptureFunc = CaptureSnapshotAsync;
            // configure before the mic's first Update warms it up (device from PlayerPrefs, rate for the backend)
            ConfigureMic(ResolveBackendKind(log: false));
        }

        void OnEnable()
        {
            if (m_ResumeOnEnable)
            {
                m_ResumeOnEnable = false;
                StartBackend();
            }
        }

        void OnDisable()
        {
            m_ResumeOnEnable = m_Backend != null;
            TearDown(abort: true);
        }

        void OnDestroy()
        {
            TearDown(abort: true);
            if (mic != null)
                mic.OnAudioChunk -= HandleMicChunk;
        }

        void Update()
        {
            var backend = m_Backend;
            if (backend != null)
            {
                try { backend.Tick(); }
                catch (Exception e) { Debug.LogException(e); }
            }
            UpdatePushToTalk();
            UpdateHandsFreeCapture();
        }

        // ------------------------------------------------------------------------------------------
        // public API

        /// <summary>Starts the backend selected in the settings (idempotent). SplatPressoRoot calls this on Start.</summary>
        public void StartBackend()
        {
            if (m_Backend != null)
                return;
            m_ExplicitlyStopped = false;
            var s = Settings;
            var kind = ResolveBackendKind(log: true);

            IVoiceBackend backend = kind == VoiceBackendKind.OpenAIRealtime
                ? new OpenAIRealtimeBackend()
                : new GenpressoVoiceBackend(); // None: text-only chat for typed requests

            ConfigureMic(kind);
            if (kind == VoiceBackendKind.OpenAIRealtime && player == null)
            {
                player = GetComponent<AudioStreamPlayer>();
                if (player == null)
                    player = gameObject.AddComponent<AudioStreamPlayer>(); // RequireComponent adds the AudioSource
            }

            ActiveBackend = kind;
            m_Backend = backend;
            backend.UserTranscript += HandleUserTranscript;
            backend.AgentReply += HandleAgentReply;
            backend.PlacementRequested += HandlePlacementRequested;
            backend.CancelRequested += HandleCancelRequested;
            backend.Error += HandleError;

            Debug.Log($"[SplatPresso] Voice backend: {(kind == VoiceBackendKind.None ? "None (typed requests only)" : kind.ToString())}");
            try
            {
                backend.Start(new VoiceAgentContext
                {
                    settings = s,
                    mic = kind != VoiceBackendKind.None ? mic : null,
                    player = kind == VoiceBackendKind.OpenAIRealtime ? player : null,
                    captureJpeg = m_CaptureFunc ?? CaptureSnapshotAsync,
                    isTalkHeld = () => m_Talking,
                });
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>Stops the backend (disconnects Realtime). Push-to-talk and typed input stay off until <see cref="StartBackend"/>.</summary>
        public void StopBackend()
        {
            m_ExplicitlyStopped = true;
            TearDown(abort: false);
        }

        /// <summary>Stops and starts again, e.g. after changing <see cref="SplatPressoSettings.voiceBackend"/>.</summary>
        public void RestartBackend()
        {
            TearDown(abort: false);
            StartBackend();
        }

        /// <summary>Starts a push-to-talk turn from code (e.g. an XR button press). Pair with <see cref="EndTalk"/>.</summary>
        public void BeginTalk()
        {
            m_ApiTalkHeld = true;
            m_HeldPrev = true;
            if (!m_Talking)
                BeginTalkInternal();
        }

        /// <summary>Ends a push-to-talk turn started with <see cref="BeginTalk"/>.</summary>
        public void EndTalk()
        {
            m_ApiTalkHeld = false;
            if (m_Talking && !PollTalkHeld())
                EndTalkInternal();
        }

        /// <summary>Sends a typed request through the same LLM turn as speech (works without a microphone).</summary>
        public void SubmitText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            EnsureStarted();
            if (m_Backend == null)
            {
                Debug.LogWarning("[SplatPresso] Voice agent is stopped; typed request ignored. Call StartBackend() first.");
                return;
            }
            m_Backend.SubmitText(text.Trim());
        }

        /// <summary>
        /// A pipeline progress notice. <paramref name="narrate"/> asks for it to be relayed to the user, according
        /// to <see cref="SplatPressoSettings.narrationMode"/> (Llm: the model phrases it; Subtitle: shown as-is;
        /// Off: context only). Send milestones only: every progress tick pollutes the model's context and makes it
        /// keep bringing up pipeline history.
        /// </summary>
        public void NotifyPipeline(string message, bool narrate)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            var mode = Settings.narrationMode;
            if (narrate && mode == NarrationMode.Subtitle)
            {
                LastAgentReply = message.Trim();
                LastAgentReplyTime = Time.unscaledTime;
                narrate = false;
            }
            else if (mode == NarrationMode.Off)
            {
                narrate = false;
            }
            m_Backend?.NotifyPipeline(message, narrate);
        }

        /// <summary>Stops speech output now (Realtime: cancels the active response).</summary>
        public void StopSpeaking()
        {
            m_Backend?.StopSpeaking();
            if (player != null)
                player.Flush();
        }

        /// <summary>Selects a microphone (empty/null = system default) and remembers the choice in PlayerPrefs.</summary>
        public void SetMicDevice(string device)
        {
            PlayerPrefs.SetString(MicDevicePrefKey, device ?? "");
            PlayerPrefs.Save();
            if (mic != null)
                mic.SwitchDevice(device);
        }

        // ------------------------------------------------------------------------------------------
        // push-to-talk

        void UpdatePushToTalk()
        {
            if (IsHandsFree)
            {
                m_HeldPrev = false;
                return;
            }
            bool held = PollTalkHeld();
            if (held && !m_HeldPrev)
                BeginTalkInternal(); // press edge: a failed start is not retried every frame
            else if (!held && m_Talking)
                EndTalkInternal();
            m_HeldPrev = held;

            // the buffered chat backend has a hard request-size cap: end the turn at the maximum length
            if (m_Talking && m_Backend != null && !m_Backend.WantsStreamingAudio && mic != null && mic.UtteranceFull)
            {
                Debug.Log($"[SplatPresso] Maximum utterance length ({Settings.maxUtteranceSeconds:F0}s) reached; sending the turn");
                EndTalkInternal();
            }
        }

        // Hands-free: the microphone must stream for the whole connected session. A device switch or a
        // reconfigure aborts the capture (MicCapture.Restart), and a start at connect time fails while no device
        // exists yet; re-arm once the warm mic runs again. Gated on IsMicRunning so a missing device is not
        // retried every frame (MicCapture retries in the background). While the socket is down IsAvailable is
        // false, so this does not fight the backend's own CancelCapture on reconnect/close.
        void UpdateHandsFreeCapture()
        {
            if (!IsHandsFree || m_Backend == null || !m_Backend.IsAvailable)
                return;
            if (mic != null && mic.isActiveAndEnabled && mic.IsMicRunning && !mic.IsCapturing)
                mic.StartCapture();
        }

        bool PollTalkHeld()
        {
            bool held = m_ApiTalkHeld;
            if (!VoiceHud.TextInputFocused)
            {
                var key = Settings.pushToTalkKey;
                if (key != KeyCode.None && InputCompat.GetKey(key))
                    held = true;
            }
            var external = ExternalTalkHeld;
            if (external != null)
            {
                try
                {
                    if (external())
                        held = true;
                }
                catch (Exception e)
                {
                    if (!m_WarnedExternalTalk)
                    {
                        m_WarnedExternalTalk = true;
                        Debug.LogException(e);
                    }
                }
            }
            return held;
        }

        void BeginTalkInternal()
        {
            if (m_Talking)
                return;
            EnsureStarted();
            if (m_Backend == null || IsHandsFree)
                return;
            if (ActiveBackend == VoiceBackendKind.None)
            {
                if (!m_WarnedVoiceOff)
                {
                    m_WarnedVoiceOff = true;
                    Debug.Log("[SplatPresso] Voice input is off (voiceBackend = None); press Enter to type a request instead");
                }
                return;
            }
            if (!m_Backend.IsAvailable)
            {
                ReportNotReady();
                return;
            }
            if (mic == null || !MicCapture.IsSupported)
            {
                ReportError("Microphone input is not available on this platform.");
                return;
            }

            m_Backend.OnTalkPressed(); // barge-in first (clear, cancel, flush), then capture incl. the warm pre-roll
            if (!mic.StartCapture())
            {
                ReportError("No microphone is available (no device found, or it failed to start).");
                m_Backend.OnTalkReleased(null, null);
                return;
            }
            m_Talking = true;
            m_TalkStart = Time.unscaledTime;
        }

        void EndTalkInternal()
        {
            if (!m_Talking)
                return;
            m_Talking = false;
            float held = Time.unscaledTime - m_TalkStart;
            // StopCapture emits the final chunks synchronously, so a streaming backend appends them before its commit
            AudioUtterance utt = mic != null && mic.IsCapturing ? mic.StopCapture() : null;
            if (utt != null && held < Settings.minUtteranceSeconds)
            {
                Debug.Log($"[SplatPresso] Push-to-talk held {held:F2}s (< minUtteranceSeconds); ignored");
                utt = null;
            }
            try { m_Backend?.OnTalkReleased(utt, m_CaptureFunc); }
            catch (Exception e) { Debug.LogException(e); }
        }

        void ReportNotReady()
        {
            if (ActiveBackend == VoiceBackendKind.OpenAIRealtime)
            {
                string why = RealtimeBackend?.LastSocketError;
                ReportError(string.IsNullOrEmpty(why)
                    ? "The OpenAI Realtime voice agent is still connecting."
                    : "The OpenAI Realtime voice agent is not connected: " + why);
            }
            else
            {
                ReportError("GenPresso API key is missing. Set it in Project Settings > SplatPresso or the GENPRESSO_API_KEY environment variable.");
            }
        }

        // OpenAIRealtime falls back to GenpressoChat when it cannot run (no OpenAI key, no WebSockets on WebGL).
        VoiceBackendKind ResolveBackendKind(bool log)
        {
            var kind = Settings.voiceBackend;
            if (kind != VoiceBackendKind.OpenAIRealtime)
                return kind;
            if (!RealtimeSocket.IsSupported)
            {
                if (log)
                    Debug.LogWarning("[SplatPresso] OpenAI Realtime is not supported on this platform; using the GenPresso chat voice backend");
                return VoiceBackendKind.GenpressoChat;
            }
            if (!ApiKeys.Has(ApiKeyKind.OpenAI))
            {
                if (log)
                    Debug.LogWarning("[SplatPresso] Voice backend is OpenAIRealtime but no OpenAI key is configured (OPENAI_API_KEY); using the GenPresso chat voice backend");
                return VoiceBackendKind.GenpressoChat;
            }
            return kind;
        }

        void EnsureStarted()
        {
            if (m_Backend == null && !m_ExplicitlyStopped && isActiveAndEnabled)
                StartBackend();
        }

        // abort: OnDisable/OnDestroy drop the Realtime socket at once (a background receive task must not outlive
        // the component, e.g. when play mode ends with domain reload disabled); otherwise it closes gracefully.
        void TearDown(bool abort)
        {
            if (m_Talking)
            {
                m_Talking = false;
                if (mic != null)
                    mic.CancelCapture();
            }
            m_ApiTalkHeld = false;
            var backend = m_Backend;
            m_Backend = null;
            ActiveBackend = VoiceBackendKind.None;
            if (backend == null)
                return;
            if (!abort)
            {
                try { backend.Stop(); } catch (Exception e) { Debug.LogException(e); }
            }
            try { backend.Dispose(); } catch (Exception e) { Debug.LogException(e); }
            backend.UserTranscript -= HandleUserTranscript;
            backend.AgentReply -= HandleAgentReply;
            backend.PlacementRequested -= HandlePlacementRequested;
            backend.CancelRequested -= HandleCancelRequested;
            backend.Error -= HandleError;
        }

        void ConfigureMic(VoiceBackendKind kind)
        {
            if (mic == null)
                return;
            var s = Settings;
            bool streaming = kind == VoiceBackendKind.OpenAIRealtime;
            mic.gain = s.micGain;
            mic.saveDebugWav = s.debugSaveMicWav;
            mic.maxUtteranceSeconds = Mathf.Clamp(s.maxUtteranceSeconds, 1f, 85f);
            mic.streamChunks = streaming;
            mic.keepUtterance = !streaming || s.debugSaveMicWav;
            // the in-game device choice (V panel) wins over the settings default
            string device = PlayerPrefs.HasKey(MicDevicePrefKey) ? PlayerPrefs.GetString(MicDevicePrefKey, "") : s.micDeviceName;
            mic.Configure(device, streaming ? kRealtimeSampleRate : kChatSampleRate);
            // with voice off, do not keep the microphone open
            mic.enabled = kind != VoiceBackendKind.None && MicCapture.IsSupported;
        }

        void HandleMicChunk(string b64)
        {
            var backend = m_Backend;
            if (backend != null && backend.WantsStreamingAudio)
                backend.OnAudioChunk(b64);
        }

        // ------------------------------------------------------------------------------------------
        // snapshot of the current view

        async Awaitable<byte[]> CaptureSnapshotAsync()
        {
            var s = Settings;
            if (!s.sendFrameWithSpeech || this == null)
                return null;
            float timeout = ActiveBackend == VoiceBackendKind.OpenAIRealtime ? kRealtimeSnapshotTimeoutSec : kChatSnapshotTimeoutSec;
            int longSide = Mathf.Max(64, s.voiceFrameMaxLongSide);
            try
            {
                if (snapshotCamera == null)
                {
                    var service = ResolveCaptureService();
                    if (service != null)
                        return await service.CaptureSnapshotJpegAsync(longSide, kSnapshotJpegQuality, timeout, destroyCancellationToken);
                }
                return await CaptureDirectAsync(snapshotCamera != null ? snapshotCamera : Camera.main, longSide, timeout);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Voice snapshot failed: {e.Message}");
                return null;
            }
        }

        // Direct capture for an explicit snapshot camera (or when there is no CaptureService). Only one capture can
        // be pending, so the voice snapshot never steals the pipeline's slot; it gives up after the deadline.
        async Awaitable<byte[]> CaptureDirectAsync(Camera cam, int longSide, float timeout)
        {
            if (cam == null || SplatCaptureFeature.HasPendingCapture)
                return null;
            SplatCaptureResult raw = null;
            bool done = false;
            if (!SplatCaptureFeature.RequestCapture(cam, r => { raw = r; done = true; }))
                return null;
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!done && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync(destroyCancellationToken);
            if (!done || raw == null || raw.rgba == null || raw.width <= 0 || raw.height <= 0)
                return null;
            return ImageUtil.EncodeJpeg(raw.rgba, raw.width, raw.height, longSide, kSnapshotJpegQuality);
        }

        CaptureService ResolveCaptureService()
        {
            if (m_CaptureService != null)
                return m_CaptureService;
            if (Time.unscaledTime < m_NextCaptureServiceLookup)
                return null;
            m_NextCaptureServiceLookup = Time.unscaledTime + kCaptureServiceLookupIntervalSec;
            m_CaptureService = GetComponent<CaptureService>();
            if (m_CaptureService == null)
                m_CaptureService = FindFirstObjectByType<CaptureService>();
            return m_CaptureService;
        }

        // ------------------------------------------------------------------------------------------
        // backend events -> public events (each handler isolated)

        void HandleUserTranscript(string transcript)
        {
            LastHeardTranscript = transcript;
            LastHeardTime = Time.unscaledTime;
            Raise(UserTranscript, transcript);
        }

        void HandleAgentReply(string reply)
        {
            LastAgentReply = reply;
            LastAgentReplyTime = Time.unscaledTime;
            Raise(AgentReply, reply);
        }

        void HandlePlacementRequested(VoicePlacementRequest request)
        {
            if (PlacementRequested == null)
            {
                Debug.LogWarning("[SplatPresso] Voice placement request ignored: nothing handles VoiceAgent.PlacementRequested (add a SplatPressoRoot)");
                return;
            }
            foreach (Action<VoicePlacementRequest> h in PlacementRequested.GetInvocationList())
            {
                try { h(request); }
                catch (Exception e) { Debug.LogError($"[SplatPresso] PlacementRequested handler threw: {e}"); }
            }
        }

        void HandleCancelRequested()
        {
            if (CancelRequested == null)
                return;
            foreach (Action h in CancelRequested.GetInvocationList())
            {
                try { h(); }
                catch (Exception e) { Debug.LogError($"[SplatPresso] CancelRequested handler threw: {e}"); }
            }
        }

        // errors raised by the agent itself (backends log their own)
        void ReportError(string message)
        {
            Debug.LogWarning("[SplatPresso] " + message);
            HandleError(message);
        }

        void HandleError(string message)
        {
            LastError = message;
            LastErrorTime = Time.unscaledTime;
            Raise(Error, message);
        }

        static void Raise(Action<string> handler, string arg)
        {
            if (handler == null)
                return;
            foreach (Action<string> h in handler.GetInvocationList())
            {
                try { h(arg); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
