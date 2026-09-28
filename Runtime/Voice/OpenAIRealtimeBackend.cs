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
        // Input transcription completes asynchronously and can arrive AFTER the response.done carrying the tool
        // call; placement events wait this long for the turn's own transcript (L10: the old code joined the
        // PREVIOUS utterance instead).
        const float kTranscriptWaitSec = 2f;
        const int kMaxRememberedTranscripts = 16;

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
        bool m_PttHeld;

        // per session (L3: reset on every session.created, including after a reconnect)
        bool m_FrameSendUnsupported;
        bool m_SessionLogged;
        bool m_TranscriptionFallbackTried;

        // source-utterance join (L10)
        string m_TurnItemId;         // input item of the current turn (from input_audio_buffer.committed)
        string m_TurnTypedText;      // typed text of the current turn (SubmitText)
        readonly Dictionary<string, string> m_Transcripts = new Dictionary<string, string>();
        readonly Queue<string> m_TranscriptOrder = new Queue<string>();
        readonly List<PendingPlacement> m_PendingPlacements = new List<PendingPlacement>();
        readonly HashSet<string> m_UnknownEventTypes = new HashSet<string>();

        sealed class PendingPlacement
        {
            public PlacementRequest request;
            public string itemId;
            public float deadline;
        }

        /// <summary>
        /// Base64 characters of audio received for the current response. 0 with a transcript means the model
        /// spoke but NO AUDIO arrived; non-zero but inaudible means the Unity output device is wrong.
        /// </summary>
        public int AudioBytesThisResponse { get; private set; }

        /// <summary>True while the server is producing a response.</summary>
        public bool IsResponseActive => m_ResponseActive;

        /// <summary>Last reason the socket closed or failed.</summary>
        public string LastSocketError => m_Socket != null ? m_Socket.LastError : m_StartError;

        /// <summary>True when the backend gave up: no key, unsupported platform, rejected handshake or too many reconnects.</summary>
        public bool HasFailed => m_StartError != null || (m_Socket != null && m_Socket.State == RealtimeSocket.SocketState.Failed);

        public bool IsAvailable => m_Socket != null && m_Socket.IsConnected;
        public bool IsBusy => m_ResponseActive || m_ResponseRequested || m_TurnFinishing;
        public bool WantsStreamingAudio => true;

        SplatPressoSettings Settings => m_Ctx?.settings != null ? m_Ctx.settings : SplatPressoSettings.Active;
        bool VadMode => Settings.useSemanticVad;
        bool IsConnected => m_Socket != null && m_Socket.IsConnected;

        // ------------------------------------------------------------------------------------------
        // lifecycle

        public void Start(VoiceAgentContext ctx)
        {
            m_Ctx = ctx ?? new VoiceAgentContext();
            m_Epoch++;
            m_StartError = null;
            ResetResponseState();

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
            m_Socket.Connect(kRealtimeUrl + Uri.EscapeDataString(model), key);
        }

        public void Stop()
        {
            m_Epoch++;
            FlushPendingPlacements(); // they were acknowledged to the model as "started": still deliver them
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
            if (m_ResponseRequested && now - m_RequestedAt > kResponseRequestTimeoutSec)
            {
                Debug.LogWarning("[SplatPresso] Realtime: no response.created for a requested response; clearing the in-flight flag");
                m_ResponseRequested = false;
                MaybeSendPendingResponse();
            }

            for (int i = m_PendingPlacements.Count - 1; i >= 0; i--)
            {
                var p = m_PendingPlacements[i];
                if (now < p.deadline)
                    continue;
                m_PendingPlacements.RemoveAt(i);
                string transcript = p.itemId != null && m_Transcripts.TryGetValue(p.itemId, out var t) ? t : null;
                RaisePlacement(p.request, transcript);
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
            m_PttHeld = false;
        }

        // ------------------------------------------------------------------------------------------
        // push-to-talk

        public void OnTalkPressed()
        {
            if (VadMode)
                return;
            m_PttHeld = true;
            m_NarrationPending = false; // the user's turn wins: queued narration is discarded
            if (!IsConnected)
                return;
            Send(RealtimeProtocol.InputAudioClear());
            if (m_ResponseActive)
                Send(RealtimeProtocol.ResponseCancel());
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
                // too short or aborted: never commit a (possibly empty) buffer
                Send(RealtimeProtocol.InputAudioClear());
                MaybeSendPendingResponse();
                return;
            }
            // MicCapture.StopCapture already emitted the final chunk synchronously, so it is appended before this commit
            Send(RealtimeProtocol.InputAudioCommit());
            m_TurnTypedText = null;
            if (Settings.sendFrameWithSpeech && !m_FrameSendUnsupported && captureJpeg != null)
                FinishTurnWithFrame(captureJpeg);
            else
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
                Send(RealtimeProtocol.ResponseCancel());
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
                    Send(RealtimeProtocol.ResponseCancel());
                else if (m_ResponseRequested)
                    m_CancelOnCreated = true;
            }
            m_Ctx?.player?.Flush();
        }

        // Sends response.create unless one is already in flight or the user holds push-to-talk; then it is
        // deferred and sent when possible (only one response may be active at a time; the release of a held
        // push-to-talk creates the response that covers a deferred turn).
        void RequestResponse(bool isUserTurn)
        {
            if (!IsConnected)
                return;
            if (IsBusy || m_PttHeld)
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
            string id = "sp_resp_" + (++m_EventCounter);
            Send(RealtimeProtocol.ResponseCreate(id));
            m_ResponseRequested = true;
            m_RequestedEventId = id;
            m_RequestedAt = Time.unscaledTime;
            m_TurnResponsePending = false; // this response answers the pending turn as well
        }

        // Called when nothing is in flight any more. Never flushes while push-to-talk is held: the done may belong
        // to the response the user just cancelled, and responding then talks over the user's turn (responses pile
        // up, the conversation tangles, tools get re-called).
        void MaybeSendPendingResponse()
        {
            if (!IsConnected || IsBusy || m_PttHeld)
                return;
            if (m_TurnResponsePending || m_NarrationPending)
            {
                m_TurnResponsePending = false;
                m_NarrationPending = false; // one response covers both; the [PIPELINE] message is in the history
                SendResponseCreate();
            }
        }

        void Send(JObject clientEvent) => m_Socket?.Send(clientEvent);

        void SendSessionUpdate() =>
            Send(RealtimeProtocol.SessionUpdate(Settings, VoicePromptLibrary.BuildRealtimeInstructions(Settings), VoicePromptLibrary.BuildRealtimeTools()));

        // ------------------------------------------------------------------------------------------
        // socket state

        void HandleSocketState(string state)
        {
            switch (state)
            {
                case RealtimeSocket.StateConnected:
                    // session.update is sent on session.created
                    if (VadMode && m_Ctx?.mic != null && !m_Ctx.mic.IsCapturing)
                        m_Ctx.mic.StartCapture();
                    break;

                case RealtimeSocket.StateReconnected:
                    ResetResponseState();
                    // a new session has no memory of the old conversation
                    Send(RealtimeProtocol.ConversationItemCreateSystemMessage(VoicePromptLibrary.PipelinePrefix + "reconnected; continue the conversation naturally"));
                    if (VadMode && m_Ctx?.mic != null && !m_Ctx.mic.IsCapturing)
                        m_Ctx.mic.StartCapture();
                    break;

                case RealtimeSocket.StateReconnecting:
                case RealtimeSocket.StateClosed:
                    ResetResponseState();
                    m_Ctx?.player?.Flush();
                    if (m_Ctx?.mic != null && m_Ctx.mic.IsCapturing && VadMode)
                        m_Ctx.mic.CancelCapture();
                    break;

                case RealtimeSocket.StateFailed:
                    ResetResponseState();
                    if (m_Ctx?.mic != null && m_Ctx.mic.IsCapturing && VadMode)
                        m_Ctx.mic.CancelCapture();
                    RaiseError("OpenAI Realtime connection failed: " + (m_Socket?.LastError ?? "unknown error") +
                               ". Check the OpenAI key and the realtime model name.");
                    break;
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
                    SendSessionUpdate();
                    break;

                case RealtimeEventNames.SessionUpdated:
                {
                    string payload = e["session"]?.ToString(Formatting.None) ?? "";
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
                    m_ResponseActive = true;
                    m_ResponseRequested = false;
                    AudioBytesThisResponse = 0;
                    if (m_CancelOnCreated || m_PttHeld)
                    {
                        // the user started talking before this response began: do not talk over them
                        m_CancelOnCreated = false;
                        Send(RealtimeProtocol.ResponseCancel());
                        m_Ctx?.player?.Flush();
                    }
                    break;

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
                    if (!string.IsNullOrEmpty(delta) && !m_PttHeld)
                    {
                        m_Ctx?.player?.EnqueueBase64Pcm16(delta);
                        AudioBytesThisResponse += delta.Length;
                    }
                    break;
                }

                case RealtimeEventNames.InputAudioBufferSpeechStarted:
                    // semantic VAD barge-in: the server cancels its response; kill local playback
                    m_Ctx?.player?.Flush();
                    break;

                case RealtimeEventNames.InputAudioBufferCommitted:
                    m_TurnItemId = (string)e["item_id"];
                    m_TurnTypedText = null;
                    break;

                case RealtimeEventNames.InputAudioTranscriptionCompleted:
                    HandleTranscript(e);
                    break;

                case RealtimeEventNames.InputAudioTranscriptionFailed:
                    Debug.LogWarning($"[SplatPresso] Input transcription FAILED: {e.ToString(Formatting.None)}");
                    ResolvePendingPlacements((string)e["item_id"], null);
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
                case RealtimeEventNames.InputAudioBufferSpeechStopped:
                case RealtimeEventNames.InputAudioBufferCleared:
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

            if (!m_FrameSendUnsupported && errText.Contains("input_image"))
            {
                m_FrameSendUnsupported = true;
                Debug.LogWarning("[SplatPresso] Server rejected input_image; disabling speech-turn snapshots for this session");
            }
            if (m_ResponseRequested && eventId != null && eventId == m_RequestedEventId)
            {
                // our response.create was rejected: it is not in flight any more
                m_ResponseRequested = false;
                MaybeSendPendingResponse();
            }
            if (code == "response_cancel_not_active")
            {
                Debug.Log("[SplatPresso] Realtime: nothing to cancel (the response had already finished)");
                return;
            }
            Debug.LogWarning($"[SplatPresso] Realtime error: {errText}");
            string message = (string)err?["message"];
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
            ResolvePendingPlacements(itemId, transcript);
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

        void QueuePlacement(PlacementRequest request)
        {
            if (m_TurnTypedText != null)
            {
                RaisePlacement(request, m_TurnTypedText);
                return;
            }
            string itemId = m_TurnItemId;
            if (itemId == null)
            {
                RaisePlacement(request, null);
                return;
            }
            if (m_Transcripts.TryGetValue(itemId, out var transcript))
            {
                RaisePlacement(request, transcript);
                return;
            }
            m_PendingPlacements.Add(new PendingPlacement { request = request, itemId = itemId, deadline = Time.unscaledTime + kTranscriptWaitSec });
        }

        void ResolvePendingPlacements(string itemId, string transcript)
        {
            if (string.IsNullOrEmpty(itemId))
                return;
            for (int i = 0; i < m_PendingPlacements.Count; i++)
            {
                var p = m_PendingPlacements[i];
                if (p.itemId != itemId)
                    continue;
                m_PendingPlacements.RemoveAt(i--);
                RaisePlacement(p.request, transcript);
            }
        }

        void FlushPendingPlacements()
        {
            if (m_PendingPlacements.Count == 0)
                return;
            var pending = new List<PendingPlacement>(m_PendingPlacements);
            m_PendingPlacements.Clear();
            foreach (var p in pending)
                RaisePlacement(p.request, p.itemId != null && m_Transcripts.TryGetValue(p.itemId, out var t) ? t : null);
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
