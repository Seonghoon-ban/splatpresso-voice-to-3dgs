using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>
    /// Optional speech-to-speech backend over the OpenAI Realtime API (needs an OpenAI key; not available on WebGL).
    /// Push-to-talk streams 24 kHz PCM16 while held, then commit (+ a snapshot of the view) and response.create;
    /// the model answers with audio (played by <see cref="AudioStreamPlayer"/>) and calls the request_placement /
    /// cancel_generation tools. With <see cref="SplatPressoSettings.useSemanticVad"/> the mic streams continuously
    /// and the server detects turns (use headphones: on speakers the agent hears itself).
    /// </summary>
    public sealed class OpenAIRealtimeBackend : IVoiceBackend
    {
        const string kRealtimeUrl = "wss://api.openai.com/v1/realtime?model=";
        const float kResponseRequestTimeoutSec = 15f;
        // session.updated normally follows session.created within ~0.3 s; if it never comes (rejected update) the
        // backend still opens for turns after this long instead of locking the user out.
        const float kSessionReadyFailOpenSec = 3f;
        const int kMaxRememberedTranscripts = 16;
        // Harmless races between our requests and the server's own responses: logged, never shown on the HUD.
        static readonly HashSet<string> s_BenignErrorCodes = new HashSet<string>
        {
            "response_cancel_not_active", "conversation_already_has_active_response", "input_audio_buffer_commit_empty",
        };
        // The key, model or account cannot be used: retrying cannot help.
        static readonly HashSet<string> s_FatalErrorCodes = new HashSet<string>
        {
            "invalid_api_key", "model_not_found", "invalid_model", "insufficient_quota", "account_deactivated",
        };
        // Semantic VAD: a response deferred while the user spoke is normally covered by the response the server
        // creates for the user's turn; if none has started this long after speech_stopped, it is sent after all.
        const float kVadResponseGraceSec = 1.5f;
        // Safety net: speech_stopped never arrives when the mic stops streaming mid-utterance (device lost); do not
        // keep deferring responses / muting the agent forever.
        const float kMaxVadSpeechSec = 30f;

        /// <summary>
        /// Realtime WebSocket endpoint the model name is appended to (<c>...?model=</c>); null = OpenAI. For tests
        /// (a local mock server) and proxies. Read when the backend starts.
        /// </summary>
        public static string EndpointOverride { get; set; }

        public event Action<string> UserTranscript;
        public event Action<string> AgentReply;
        public event Action<VoicePlacementRequest> PlacementRequested;
        public event Action CancelRequested;
        public event Action<string> Error;

        VoiceAgentContext m_Ctx;
        RealtimeSocket m_Socket;
        string m_StartError;
        int m_Epoch; // bumped by Stop; async continuations from an older epoch do nothing

        // Response state. L1 fix: the old code only knew a response was active once the server sent
        // response.created, so anything that sent response.create in between (narration, tool follow-ups,
        // the capacity notice raised synchronously from a tool call) created a second concurrent response,
        // which the server rejects. Now a locally sent response.create counts as in flight immediately.
        bool m_ResponseActive;       // response.created received, response.done not yet
        bool m_ResponseRequested;    // response.create sent, response.created not yet received
        string m_RequestedEventId;
        float m_RequestedAt;
        int m_EventCounter;
        bool m_TurnFinishing;        // committed; waiting for the snapshot before response.create
        bool m_CancelOnCreated;      // barge-in arrived before the requested response was created
        bool m_TurnResponsePending;  // a user turn needs a response once the current one ends (never dropped)
        bool m_NarrationPending;     // a narration needs a response once possible (dropped on barge-in)
        bool m_PttHeld;              // push-to-talk held; NOT reset on reconnect (the hold is still in progress)
        bool m_UserSpeaking;         // semantic VAD: speech_started received, speech_stopped not yet
        float m_SpeechStartedAt;
        float m_SpeechStoppedAt = -999f;

        // per session (L3: reset on every session.created, including after a reconnect)
        bool m_FrameSendUnsupported;
        bool m_SessionLogged;
        bool m_TranscriptionFallbackTried;
        bool m_SessionReady;          // session.updated received: instructions, tools and push-to-talk are in effect
        float m_SessionCreatedAt = -1f;
        string m_SessionEventId;
        bool m_SessionSafeRetried;    // a rejected session.update was re-sent once with the default voice

        // the view snapshot is taken while push-to-talk is held, so releasing it does not wait for the capture
        int m_FrameSeq;
        bool m_FrameCapturing;
        byte[] m_HeldFrame;
        bool m_ReleaseWaitsForFrame;

        // source utterance of a placement (for logs and RunStarted); never delays the placement itself
        string m_TurnItemId;         // input item of the current turn (from input_audio_buffer.committed)
        string m_TurnTypedText;      // typed text of the current turn (SubmitText)
        readonly Dictionary<string, string> m_Transcripts = new Dictionary<string, string>();
        readonly Queue<string> m_TranscriptOrder = new Queue<string>();
        readonly HashSet<string> m_UnknownEventTypes = new HashSet<string>();

        /// <summary>
        /// Base64 characters of audio received for the current response. 0 with a transcript means the model
        /// spoke but NO AUDIO arrived; non-zero but inaudible means the Unity output device is wrong.
        /// </summary>
        public int AudioBytesThisResponse { get; private set; }

        string m_ActiveResponseId;
        readonly HashSet<string> m_CancelledResponseIds = new HashSet<string>();

        // Cancels the active response; its audio still in flight is dropped when it arrives.
        void CancelActiveResponse()
        {
            Send(RealtimeProtocol.ResponseCancel());
            if (!string.IsNullOrEmpty(m_ActiveResponseId))
            {
                if (m_CancelledResponseIds.Count > 32)
                    m_CancelledResponseIds.Clear();
                m_CancelledResponseIds.Add(m_ActiveResponseId);
            }
        }

        /// <summary>True while the server is producing a response.</summary>
        public bool IsResponseActive => m_ResponseActive;

        /// <summary>Last reason the socket closed or failed.</summary>
        public string LastSocketError => m_Socket != null ? m_Socket.LastError : m_StartError;

        /// <summary>True when the backend gave up: no key, unsupported platform, rejected key / model, or a build without WebSocket support.</summary>
        public bool HasFailed => m_StartError != null || (m_Socket != null && m_Socket.State == RealtimeSocket.SocketState.Failed);

        /// <summary>Why the backend failed (<see cref="RealtimeSocket.FailureKind.None"/> while it has not).</summary>
        public RealtimeSocket.FailureKind FailureKind =>
            m_StartError != null ? RealtimeSocket.FailureKind.Auth
            : m_Socket != null && m_Socket.State == RealtimeSocket.SocketState.Failed ? m_Socket.LastFailureKind
            : RealtimeSocket.FailureKind.None;

        /// <summary>True while the socket is down and being reconnected.</summary>
        public bool IsReconnecting => m_Socket != null && (m_Socket.State == RealtimeSocket.SocketState.Reconnecting ||
                                                           m_Socket.State == RealtimeSocket.SocketState.Connecting);

        /// <summary>Connected and the session configuration (instructions, tools, push-to-talk) is in effect.</summary>
        public bool IsAvailable => m_Socket != null && m_Socket.IsConnected && m_SessionReady;
        public bool IsBusy => m_ResponseActive || m_ResponseRequested || m_TurnFinishing;
        public bool WantsStreamingAudio => true;

        SplatPressoSettings Settings => m_Ctx?.settings != null ? m_Ctx.settings : SplatPressoSettings.Active;
        bool VadMode => Settings.useSemanticVad;
        bool IsConnected => m_Socket != null && m_Socket.IsConnected;
        // the user is talking (push-to-talk held, or semantic VAD heard speech): do not start or play a response
        // (m_PttHeld is ignored in VAD mode, where push-to-talk is off and nothing would release it)
        bool UserTalking => (m_PttHeld && !VadMode) || m_UserSpeaking;

        // ------------------------------------------------------------------------------------------
        // lifecycle

        public void Start(VoiceAgentContext ctx)
        {
            m_Ctx = ctx ?? new VoiceAgentContext();
            m_Epoch++;
            m_StartError = null;
            ResetResponseState();
            m_PttHeld = false;

            string key = ApiKeys.Get(ApiKeyKind.OpenAI);
            if (string.IsNullOrWhiteSpace(key))
            {
                m_StartError = "OpenAI API key not found. Set the OPENAI_API_KEY environment variable or add \"openai\" to the keys file (Project Settings > SplatPresso).";
                RaiseError(m_StartError);
                return;
            }
            if (!RealtimeSocket.IsSupported)
            {
                m_StartError = "The OpenAI Realtime voice backend is not supported on this platform (no WebSockets).";
                RaiseError(m_StartError);
                return;
            }

            if (m_Socket == null)
            {
                m_Socket = new RealtimeSocket();
                m_Socket.OnServerEvent += HandleServerEvent;
                m_Socket.OnStateChanged += HandleSocketState;
            }
            string model = string.IsNullOrWhiteSpace(Settings.realtimeModel) ? "gpt-realtime-2.1" : Settings.realtimeModel.Trim();
            Debug.Log($"[SplatPresso] Connecting OpenAI Realtime ({model}, key {ApiKeys.Mask(key)}, {(VadMode ? "semantic VAD" : "push-to-talk")})");
            m_Socket.Connect((string.IsNullOrWhiteSpace(EndpointOverride) ? kRealtimeUrl : EndpointOverride.Trim()) + Uri.EscapeDataString(model), key);
        }

        public void Stop()
        {
            m_Epoch++;
            m_FrameSeq++;
            m_FrameCapturing = false;
            m_ReleaseWaitsForFrame = false;
            m_HeldFrame = null;
            m_SessionReady = false;
            if (m_Socket != null)
            {
                m_Socket.OnServerEvent -= HandleServerEvent;
                m_Socket.OnStateChanged -= HandleSocketState;
                m_Socket.Close();
                m_Socket = null;
            }
            if (m_Ctx?.mic != null && m_Ctx.mic.IsCapturing && VadMode)
                m_Ctx.mic.CancelCapture();
            m_Ctx?.player?.Flush();
            ResetResponseState();
            m_PttHeld = false;
        }

        /// <summary>Aborts the socket immediately (no close handshake), then stops like <see cref="Stop"/>.</summary>
        public void Dispose()
        {
            if (m_Socket != null)
            {
                m_Socket.OnServerEvent -= HandleServerEvent;
                m_Socket.OnStateChanged -= HandleSocketState;
                m_Socket.Abort();
                m_Socket = null;
            }
            Stop();
        }

        public void Tick()
        {
            m_Socket?.Tick();

            float now = Time.unscaledTime;
            if (m_UserSpeaking && now - m_SpeechStartedAt > kMaxVadSpeechSec)
            {
                Debug.LogWarning("[SplatPresso] Realtime: no speech_stopped after a long time; no longer holding responses back");
                EndUserSpeech();
            }
            if (!m_SessionReady && IsConnected && m_SessionCreatedAt >= 0f && now - m_SessionCreatedAt > kSessionReadyFailOpenSec)
            {
                Debug.LogWarning("[SplatPresso] Realtime: no session.updated after session.created; accepting turns anyway");
                m_SessionReady = true;
            }
            if (m_ResponseRequested && now - m_RequestedAt > kResponseRequestTimeoutSec)
            {
                Debug.LogWarning("[SplatPresso] Realtime: no response.created for a requested response; clearing the in-flight flag");
                m_ResponseRequested = false;
                m_CancelOnCreated = false; // it was meant for that response, not for the user's next one
                // a half-open TCP connection never reports itself: drop it (the socket reconnects), but only when
                // nothing at all has moved on it for as long; a slow uplink or a slow model is not a dead connection
                if (m_Socket != null && m_Socket.SecondsSinceActivity > kResponseRequestTimeoutSec)
                    m_Socket.DropConnection();
                MaybeSendPendingResponse();
            }
            else if (VadMode && !m_UserSpeaking && (m_TurnResponsePending || m_NarrationPending) && !IsBusy &&
                     now - m_SpeechStoppedAt > kVadResponseGraceSec)
            {
                // deferred while the user spoke, and the server started no response for the turn
                MaybeSendPendingResponse();
            }
        }

        void ResetResponseState()
        {
            m_ResponseActive = false;
            m_ResponseRequested = false;
            m_RequestedEventId = null;
            m_TurnFinishing = false;
            m_CancelOnCreated = false;
            m_TurnResponsePending = false;
            m_NarrationPending = false;
            m_UserSpeaking = false;
            // m_PttHeld is not reset here: after a reconnect the user may still hold the key (OnTalkReleased,
            // Start and Stop clear it)
        }

        void EndUserSpeech()
        {
            if (!m_UserSpeaking)
                return;
            m_UserSpeaking = false;
            m_SpeechStoppedAt = Time.unscaledTime;
        }

        // ------------------------------------------------------------------------------------------
        // push-to-talk

        public void OnTalkPressed()
        {
            if (VadMode)
                return;
            m_PttHeld = true;
            m_NarrationPending = false; // the user's turn wins: queued narration is discarded
            m_FrameSeqStartedAtPress = -1;
            if (!IsConnected)
                return;
            StartFrameCapture(); // runs while the user talks; releasing push-to-talk then sends at once
            Send(RealtimeProtocol.InputAudioClear());
            if (m_ResponseActive)
                CancelActiveResponse();
            else if (m_ResponseRequested)
                m_CancelOnCreated = true; // it has not started yet: cancel it the moment it does
            m_Ctx?.player?.Flush();
        }

        public void OnAudioChunk(string b64Pcm16)
        {
            if (IsConnected && !string.IsNullOrEmpty(b64Pcm16))
                Send(RealtimeProtocol.InputAudioAppend(b64Pcm16));
        }

        public void OnTalkReleased(AudioUtterance utt, Func<Awaitable<byte[]>> captureJpeg)
        {
            if (VadMode)
                return;
            m_PttHeld = false;
            if (!IsConnected)
                return;
            if (utt == null)
            {
                // too short, silent or aborted: never commit a (possibly empty) buffer
                m_FrameSeq++;
                m_FrameCapturing = false;
                m_HeldFrame = null;
                Send(RealtimeProtocol.InputAudioClear());
                MaybeSendPendingResponse();
                return;
            }
            // MicCapture.StopCapture already emitted the final chunk synchronously, so it is appended before this commit
            Send(RealtimeProtocol.InputAudioCommit());
            m_TurnTypedText = null;
            if (m_FrameCapturing)
            {
                // the snapshot started at press is still encoding (rare): finish the turn when it lands
                m_ReleaseWaitsForFrame = true;
                m_TurnFinishing = true;
            }
            else if (m_HeldFrame != null)
            {
                SendHeldFrameAndRespond();
            }
            else if (Settings.sendFrameWithSpeech && !m_FrameSendUnsupported && captureJpeg != null && m_FrameSeqStartedAtPress != m_FrameSeq)
            {
                FinishTurnWithFrame(captureJpeg); // no snapshot was started at press (e.g. not connected then)
            }
            else
            {
                RequestResponse(isUserTurn: true);
            }
        }

        int m_FrameSeqStartedAtPress = -1;

        // Push-to-talk press: snapshot the view now (the user looks at what they talk about); it is sent with the turn.
        void StartFrameCapture()
        {
            if (m_ReleaseWaitsForFrame)
            {
                // the previous turn was committed but still waited for its snapshot: its response is now covered by
                // this turn's (it must not stay "finishing" forever)
                m_TurnFinishing = false;
                m_TurnResponsePending = true;
            }
            m_FrameSeq++;
            m_HeldFrame = null;
            m_ReleaseWaitsForFrame = false;
            m_FrameCapturing = false;
            var capture = m_Ctx?.captureJpeg;
            if (!Settings.sendFrameWithSpeech || m_FrameSendUnsupported || capture == null)
                return;
            m_FrameSeqStartedAtPress = m_FrameSeq;
            CaptureFrameAsync(m_FrameSeq, m_Epoch, capture);
        }

        async void CaptureFrameAsync(int seq, int epoch, Func<Awaitable<byte[]>> capture)
        {
            m_FrameCapturing = true;
            byte[] jpeg = null;
            try
            {
                jpeg = await capture();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Speech-turn snapshot failed: {e.Message}");
            }
            if (seq != m_FrameSeq || epoch != m_Epoch)
                return; // superseded by a newer press, a cancelled turn or Stop
            m_FrameCapturing = false;
            m_HeldFrame = jpeg != null && jpeg.Length > 0 ? jpeg : Array.Empty<byte>();
            if (m_ReleaseWaitsForFrame)
            {
                m_ReleaseWaitsForFrame = false;
                m_TurnFinishing = false;
                SendHeldFrameAndRespond();
            }
        }

        void SendHeldFrameAndRespond()
        {
            byte[] jpeg = m_HeldFrame;
            m_HeldFrame = null;
            if (!IsConnected)
                return;
            if (jpeg != null && jpeg.Length > 0 && !m_FrameSendUnsupported)
                Send(RealtimeProtocol.ConversationItemCreateUserImage("data:image/jpeg;base64," + Convert.ToBase64String(jpeg)));
            // if push-to-talk was pressed again meanwhile this is deferred: the next turn's response covers it
            RequestResponse(isUserTurn: true);
        }

        public void SubmitText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (!IsConnected)
            {
                RaiseError("The OpenAI Realtime voice agent is not connected yet.");
                return;
            }
            text = text.Trim();
            m_NarrationPending = false;
            if (m_ResponseActive)
            {
                CancelActiveResponse();
                m_Ctx?.player?.Flush();
            }
            else if (m_ResponseRequested)
            {
                m_CancelOnCreated = true;
            }
            Send(RealtimeProtocol.ConversationItemCreateUserText(text));
            m_TurnTypedText = text;
            m_TurnItemId = null;
            Debug.Log($"[SplatPresso] Typed: \"{text}\"");
            Raise(UserTranscript, text);
            if (Settings.sendFrameWithSpeech && !m_FrameSendUnsupported && m_Ctx?.captureJpeg != null)
                FinishTurnWithFrame(m_Ctx.captureJpeg);
            else
                RequestResponse(isUserTurn: true);
        }

        // Attaches a snapshot of the current view to the turn (so the model can resolve "this/here"), then
        // requests the response. The capture callback has its own short deadline (0.6 s) and yields null when
        // the capture is unavailable or slow; the response is requested either way.
        async void FinishTurnWithFrame(Func<Awaitable<byte[]>> captureJpeg)
        {
            int epoch = m_Epoch;
            m_TurnFinishing = true;
            byte[] jpeg = null;
            try
            {
                jpeg = await captureJpeg();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Speech-turn snapshot failed: {e.Message}");
            }
            if (epoch != m_Epoch)
                return;
            m_TurnFinishing = false;
            if (!IsConnected)
                return;
            if (jpeg != null && jpeg.Length > 0)
                Send(RealtimeProtocol.ConversationItemCreateUserImage("data:image/jpeg;base64," + Convert.ToBase64String(jpeg)));
            // if push-to-talk was pressed again meanwhile this is deferred: the next turn's response covers it
            RequestResponse(isUserTurn: true);
        }

        // ------------------------------------------------------------------------------------------
        // narration / output

        public void NotifyPipeline(string message, bool narrate)
        {
            if (string.IsNullOrWhiteSpace(message) || !IsConnected)
                return;
            Send(RealtimeProtocol.ConversationItemCreateSystemMessage(VoicePromptLibrary.PipelinePrefix + message.Trim()));
            if (narrate)
                RequestResponse(isUserTurn: false);
        }

        public void StopSpeaking()
        {
            m_NarrationPending = false;
            if (IsConnected)
            {
                if (m_ResponseActive)
                    CancelActiveResponse();
                else if (m_ResponseRequested)
                    m_CancelOnCreated = true;
            }
            m_Ctx?.player?.Flush();
        }

        // Sends response.create unless one is already in flight or the user is talking (push-to-talk held, or
        // semantic VAD heard speech); then it is deferred and sent when possible (only one response may be active
        // at a time; the release of a held push-to-talk, or the server's response to a VAD turn, covers a
        // deferred turn).
        void RequestResponse(bool isUserTurn)
        {
            if (!IsConnected)
                return;
            if (IsBusy || UserTalking)
            {
                if (isUserTurn)
                    m_TurnResponsePending = true;
                else
                    m_NarrationPending = true;
                return;
            }
            SendResponseCreate();
        }

        void SendResponseCreate()
        {
            // only called when nothing is in flight: a leftover cancel-on-created flag is stale and would cancel
            // this new response (e.g. the user's own turn) the moment it starts
            m_CancelOnCreated = false;
            string id = "sp_resp_" + (++m_EventCounter);
            Send(RealtimeProtocol.ResponseCreate(id));
            m_ResponseRequested = true;
            m_RequestedEventId = id;
            m_RequestedAt = Time.unscaledTime;
            m_TurnResponsePending = false; // this response answers the pending turn as well
        }

        // Called when nothing is in flight any more. Never flushes while the user is talking (push-to-talk held or
        // VAD speech): the done may belong to the response the user just cancelled, and responding then talks over
        // the user's turn (responses pile up, the conversation tangles, tools get re-called).
        void MaybeSendPendingResponse()
        {
            if (!IsConnected || IsBusy || UserTalking)
                return;
            if (m_TurnResponsePending || m_NarrationPending)
            {
                m_TurnResponsePending = false;
                m_NarrationPending = false; // one response covers both; the [PIPELINE] message is in the history
                SendResponseCreate();
            }
        }

        void Send(JObject clientEvent) => m_Socket?.Send(clientEvent);

        void SendSessionUpdate(string voiceOverride = null)
        {
            var update = RealtimeProtocol.SessionUpdate(Settings, VoicePromptLibrary.BuildRealtimeInstructions(Settings),
                VoicePromptLibrary.BuildRealtimeTools(), voiceOverride);
            m_SessionEventId = "sp_session_" + (++m_EventCounter);
            update["event_id"] = m_SessionEventId;
            Send(update);
        }

        // ------------------------------------------------------------------------------------------
        // socket state

        void HandleSocketState(string state)
        {
            switch (state)
            {
                case RealtimeSocket.StateConnected:
                    // session.update is sent on session.created; turns open on session.updated
                    m_SessionReady = false;
                    m_SessionCreatedAt = -1f;
                    break;

                case RealtimeSocket.StateReconnected:
                    m_SessionReady = false;
                    m_SessionCreatedAt = -1f;
                    ResetResponseState();
                    // a new session has no memory of the old conversation
                    Send(RealtimeProtocol.ConversationItemCreateSystemMessage(VoicePromptLibrary.PipelinePrefix + "reconnected; continue the conversation naturally"));
                    break;

                case RealtimeSocket.StateReconnecting:
                case RealtimeSocket.StateClosed:
                    m_SessionReady = false;
                    ResetResponseState();
                    m_Ctx?.player?.Flush();
                    if (m_Ctx?.mic != null && m_Ctx.mic.IsCapturing && VadMode)
                        m_Ctx.mic.CancelCapture();
                    break;

                case RealtimeSocket.StateFailed:
                {
                    m_SessionReady = false;
                    ResetResponseState();
                    if (m_Ctx?.mic != null && m_Ctx.mic.IsCapturing && VadMode)
                        m_Ctx.mic.CancelCapture();
                    var kind = m_Socket != null ? m_Socket.LastFailureKind : RealtimeSocket.FailureKind.Other;
                    RaiseError("OpenAI Realtime connection failed: " + (m_Socket?.LastError ?? "unknown error") +
                               (kind == RealtimeSocket.FailureKind.Auth ? ". Check the OpenAI key and the realtime model name." : "."));
                    break;
                }
            }
        }

        // ------------------------------------------------------------------------------------------
        // server events

        void HandleServerEvent(JObject e)
        {
            string type = (string)e["type"];
            switch (type)
            {
                case RealtimeEventNames.SessionCreated:
                    m_SessionLogged = false;
                    m_TranscriptionFallbackTried = false;
                    m_FrameSendUnsupported = false;
                    m_SessionSafeRetried = false;
                    m_SessionReady = false;
                    m_SessionCreatedAt = Time.unscaledTime;
                    SendSessionUpdate();
                    break;

                case RealtimeEventNames.SessionUpdated:
                {
                    string payload = e["session"]?.ToString(Formatting.None) ?? "";
                    if (!m_SessionReady)
                    {
                        m_SessionReady = true;
                        // hands-free: stream the mic only once push-to-talk / VAD settings are in effect
                        if (VadMode && m_Ctx?.mic != null && !m_Ctx.mic.IsCapturing)
                            m_Ctx.mic.StartCapture();
                    }
                    if (!m_SessionLogged)
                    {
                        m_SessionLogged = true;
                        Debug.Log($"[SplatPresso] Effective realtime session: {(payload.Length > 900 ? payload.Substring(0, 900) + "..." : payload)}");
                    }
                    // GA nested transcription field ignored by the server? Retry once with the legacy top-level
                    // field so input transcripts ("Heard: ...") start flowing.
                    if (!m_TranscriptionFallbackTried && !payload.Contains("transcription"))
                    {
                        m_TranscriptionFallbackTried = true;
                        Debug.Log("[SplatPresso] Session shows no transcription config; retrying with the legacy field");
                        Send(RealtimeProtocol.SessionUpdateLegacyTranscription());
                    }
                    break;
                }

                case RealtimeEventNames.ResponseCreated:
                {
                    bool requestedByUs = m_ResponseRequested;
                    m_ResponseActive = true;
                    m_ResponseRequested = false;
                    m_ActiveResponseId = (string)e["response"]?["id"];
                    AudioBytesThisResponse = 0;
                    if (m_CancelOnCreated || UserTalking)
                    {
                        // the user started talking before this response began: do not talk over them
                        m_CancelOnCreated = false;
                        CancelActiveResponse();
                        m_Ctx?.player?.Flush();
                    }
                    else if (!requestedByUs && VadMode)
                    {
                        // semantic VAD created this response for the user's turn; the items of a deferred
                        // narration / tool follow-up are already in the conversation, so it covers them too
                        m_TurnResponsePending = false;
                        m_NarrationPending = false;
                    }
                    break;
                }

                case RealtimeEventNames.ResponseDone:
                {
                    m_ResponseActive = false;
                    bool startedFollowUp = HandleFunctionCalls(e);
                    // don't flush queued narration when a function-call follow-up response was just requested
                    // (only one response may be active); it flushes on that response's done
                    if (!startedFollowUp)
                        MaybeSendPendingResponse();
                    break;
                }

                case RealtimeEventNames.ResponseOutputAudioDelta:
                case RealtimeEventNames.ResponseAudioDeltaLegacy:
                {
                    string delta = (string)e["delta"];
                    string responseId = (string)e["response_id"];
                    if (responseId != null && m_CancelledResponseIds.Contains(responseId))
                        break; // a cancelled reply's audio still in flight
                    if (!string.IsNullOrEmpty(delta) && !UserTalking)
                    {
                        m_Ctx?.player?.EnqueueBase64Pcm16(delta);
                        AudioBytesThisResponse += delta.Length;
                    }
                    break;
                }

                case RealtimeEventNames.InputAudioBufferSpeechStarted:
                    // semantic VAD barge-in: the server cancels its response; kill local playback. Until
                    // speech_stopped, narration and tool follow-ups are deferred (not spoken over the user).
                    if (VadMode)
                    {
                        m_UserSpeaking = true;
                        m_SpeechStartedAt = Time.unscaledTime;
                    }
                    m_Ctx?.player?.Flush();
                    break;

                case RealtimeEventNames.InputAudioBufferSpeechStopped:
                case RealtimeEventNames.InputAudioBufferCleared:
                    EndUserSpeech();
                    break;

                case RealtimeEventNames.InputAudioBufferCommitted:
                    EndUserSpeech();
                    m_TurnItemId = (string)e["item_id"];
                    m_TurnTypedText = null;
                    break;

                case RealtimeEventNames.InputAudioTranscriptionCompleted:
                    HandleTranscript(e);
                    break;

                case RealtimeEventNames.InputAudioTranscriptionFailed:
                    Debug.LogWarning($"[SplatPresso] Input transcription FAILED: {e.ToString(Formatting.None)}");
                    break;

                case RealtimeEventNames.Error:
                    HandleErrorEvent(e);
                    break;

                case RealtimeEventNames.ResponseOutputAudioTranscriptDone:
                case RealtimeEventNames.ResponseAudioTranscriptDoneLegacy:
                {
                    string said = (string)e["transcript"];
                    // always logged: without it there is no way to tell from the console whether the agent spoke
                    Debug.Log($"[SplatPresso] Said: \"{said}\"  (audio {AudioBytesThisResponse}B)");
                    if (!string.IsNullOrWhiteSpace(said))
                        Raise(AgentReply, said.Trim());
                    break;
                }

                // known but unused
                case RealtimeEventNames.RateLimitsUpdated:
                case RealtimeEventNames.ResponseOutputItemAdded:
                case RealtimeEventNames.ResponseOutputItemDone:
                case RealtimeEventNames.ResponseContentPartAdded:
                case RealtimeEventNames.ResponseContentPartDone:
                case RealtimeEventNames.ResponseOutputAudioDone:
                case RealtimeEventNames.ResponseAudioDoneLegacy:
                case RealtimeEventNames.ResponseOutputAudioTranscriptDelta:
                case RealtimeEventNames.ResponseAudioTranscriptDeltaLegacy:
                case RealtimeEventNames.ResponseFunctionCallArgumentsDelta:
                case RealtimeEventNames.ResponseFunctionCallArgumentsDone:
                case RealtimeEventNames.ConversationItemCreated:
                case RealtimeEventNames.ConversationItemAdded:
                case RealtimeEventNames.ConversationItemDone:
                case RealtimeEventNames.InputAudioTranscriptionDelta:
                    break;

                default:
                    // schema-drift tolerance: any event carrying a user transcript counts as "Heard"
                    if (type != null && type.Contains("transcription") && e["transcript"] != null)
                    {
                        HandleTranscript(e);
                        break;
                    }
                    if (m_UnknownEventTypes.Add(type ?? "<null>"))
                        Debug.Log($"[SplatPresso] Unhandled realtime event type: {type}");
                    break;
            }
        }

        void HandleErrorEvent(JObject e)
        {
            var err = e["error"] as JObject;
            string errText = err?.ToString(Formatting.None) ?? e.ToString(Formatting.None);
            string code = (string)err?["code"];
            string eventId = (string)err?["event_id"];

            string message = (string)err?["message"];
            bool handled = false;
            if (code != null && s_FatalErrorCodes.Contains(code))
            {
                // the server accepted the socket but rejects the key / model / account: reconnecting would loop
                Debug.LogWarning($"[SplatPresso] Realtime error: {errText}");
                m_Socket?.Fail($"OpenAI rejected the session: {(string.IsNullOrEmpty(message) ? code : message)}", RealtimeSocket.FailureKind.Auth);
                return; // the Failed state change reports it
            }
            if (!m_FrameSendUnsupported && errText.Contains("input_image"))
            {
                m_FrameSendUnsupported = true;
                handled = true;
                Debug.LogWarning("[SplatPresso] Server rejected input_image; disabling speech-turn snapshots for this session");
            }
            if (m_ResponseRequested && eventId != null && eventId == m_RequestedEventId)
            {
                // our response.create was rejected: it is not in flight any more (and a cancel queued for it
                // must not hit the user's next response)
                m_ResponseRequested = false;
                m_CancelOnCreated = false;
                MaybeSendPendingResponse();
            }
            if (eventId != null && eventId == m_SessionEventId)
            {
                // The session configuration was rejected (typically an unknown realtimeVoice): without it the session
                // runs on server defaults (no tools, server VAD). Retry once with the default voice, and tell the user.
                if (!m_SessionSafeRetried)
                {
                    m_SessionSafeRetried = true;
                    Debug.LogWarning($"[SplatPresso] Realtime session.update rejected ({errText}); retrying with voice 'cedar'");
                    SendSessionUpdate("cedar");
                }
                RaiseError("OpenAI Realtime rejected the session settings: " + (string.IsNullOrEmpty(message) ? errText : message) +
                           " (check realtimeVoice / realtimeModel in the settings)");
                return;
            }
            if (code != null && s_BenignErrorCodes.Contains(code))
            {
                Debug.Log($"[SplatPresso] Realtime: {code} (harmless race, ignored)");
                return;
            }
            if (handled)
                return;
            Debug.LogWarning($"[SplatPresso] Realtime error: {errText}");
            RaiseError("OpenAI Realtime: " + (string.IsNullOrEmpty(message) ? errText : message));
        }

        void HandleTranscript(JObject e)
        {
            string transcript = ((string)e["transcript"])?.Trim();
            if (string.IsNullOrEmpty(transcript))
                return;
            string itemId = (string)e["item_id"];
            if (!string.IsNullOrEmpty(itemId))
            {
                if (!m_Transcripts.ContainsKey(itemId))
                    m_TranscriptOrder.Enqueue(itemId);
                m_Transcripts[itemId] = transcript;
                while (m_TranscriptOrder.Count > kMaxRememberedTranscripts)
                    m_Transcripts.Remove(m_TranscriptOrder.Dequeue());
            }
            Debug.Log($"[SplatPresso] Heard: \"{transcript}\"");
            Raise(UserTranscript, transcript);
        }

        // Sends a function_call_output per tool call, then ONE follow-up response.create (two concurrent
        // responses would error), then raises the C# events. Returns true when a follow-up was requested.
        bool HandleFunctionCalls(JObject responseDone)
        {
            var calls = RealtimeProtocol.TryGetFunctionCalls(responseDone);
            if (calls.Count == 0)
                return false;

            var placementRequests = new List<PlacementRequest>();
            bool cancelRequested = false;

            foreach (var (name, callId, argumentsJson) in calls)
            {
                switch (name)
                {
                    case VoicePromptLibrary.ToolRequestPlacement:
                    {
                        PlacementRequest request = null;
                        string reason = null;
                        try
                        {
                            request = VoiceRequestSanitizer.Sanitize(JsonUtil.Deserialize<PlacementRequest>(argumentsJson), out reason);
                        }
                        catch (Exception ex)
                        {
                            reason = ex.Message;
                        }

                        if (request == null)
                        {
                            Debug.LogWarning($"[SplatPresso] request_placement arguments rejected: {reason}");
                            Send(RealtimeProtocol.ConversationItemCreateFunctionOutput(callId, "{\"status\":\"error\",\"reason\":\"invalid arguments\"}"));
                            break;
                        }

                        Send(RealtimeProtocol.ConversationItemCreateFunctionOutput(callId, "{\"status\":\"started\",\"estimated_seconds\":90}"));
                        Debug.Log($"[SplatPresso] request_placement: {request.objects.Count} object type(s), hint='{request.placementHint}'");
                        placementRequests.Add(request);
                        break;
                    }

                    case VoicePromptLibrary.ToolCancelGeneration:
                        Send(RealtimeProtocol.ConversationItemCreateFunctionOutput(callId, "{\"status\":\"cancelling\"}"));
                        Debug.Log("[SplatPresso] cancel_generation requested by voice");
                        cancelRequested = true;
                        break;

                    default:
                        Debug.LogWarning($"[SplatPresso] Unknown tool call: {name}");
                        Send(RealtimeProtocol.ConversationItemCreateFunctionOutput(callId, "{\"status\":\"error\",\"reason\":\"unknown tool\"}"));
                        break;
                }
            }

            // exactly one follow-up so the model speaks its acknowledgement
            RequestResponse(isUserTurn: true);

            // cancel first, so "cancel that and make X instead" does not cancel the new request as well
            if (cancelRequested)
                Raise(CancelRequested);

            foreach (var request in placementRequests)
                QueuePlacement(request);
            return true;
        }

        // Starts the generation at once. The turn's input transcript often arrives after the tool call; it only labels
        // the run (sourceUtterance), so it is attached when already known and never waited for (the original also
        // started immediately).
        void QueuePlacement(PlacementRequest request)
        {
            string source = m_TurnTypedText;
            if (source == null && m_TurnItemId != null)
                m_Transcripts.TryGetValue(m_TurnItemId, out source);
            RaisePlacement(request, source);
        }

        void RaisePlacement(PlacementRequest request, string sourceUtterance)
        {
            var args = new VoicePlacementRequest { request = request, sourceUtterance = sourceUtterance };
            if (PlacementRequested == null)
                return;
            foreach (Action<VoicePlacementRequest> h in PlacementRequested.GetInvocationList())
            {
                try { h(args); }
                catch (Exception ex) { Debug.LogError($"[SplatPresso] PlacementRequested handler threw: {ex}"); }
            }
        }

        void RaiseError(string message)
        {
            Debug.LogWarning("[SplatPresso] " + message);
            Raise(Error, message);
        }

        static void Raise(Action handler)
        {
            if (handler == null)
                return;
            foreach (Action h in handler.GetInvocationList())
            {
                try { h(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        static void Raise(Action<string> handler, string arg)
        {
            if (handler == null)
                return;
            foreach (Action<string> h in handler.GetInvocationList())
            {
                try { h(arg); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
