using System;
using System.Collections.Generic;
using System.Threading;
using SplatPresso.Api;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>
    /// Default voice backend: needs only the GenPresso key. Each push-to-talk turn sends the utterance as a
    /// 16 kHz WAV (<c>input_audio</c>) plus a snapshot of the current view to GenPresso chat/completions and gets
    /// back one strict JSON object (transcript, reply, actions). The reply is shown as a subtitle (text output).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>One request in flight at a time (turn or narration), mirroring "only one response may be active".
    /// A turn recorded while another is in flight is queued; push-to-talk never aborts an in-flight turn (that
    /// would lose its actions).</item>
    /// <item>Audio and image are sent only for the current turn. Afterwards the turn is rewritten in the history
    /// as text (<c>User said: "..."</c> + the reply and a one-line summary per action) so the model knows what
    /// it already requested and does not create duplicates.</item>
    /// <item>[PIPELINE] notices are merged into the next user message instead of standalone system/user
    /// messages: this keeps strict user/assistant alternation (Gemini) and avoids mid-history system messages
    /// being hoisted into the system instruction.</item>
    /// <item>Request bodies must stay under GenPresso's 4 MB cap: above ~3.6 MB the image is dropped first, then
    /// the oldest history. 16 kHz mono PCM16 is ~42.7 KB/s after base64 (24 kHz would fit only ~40 s).</item>
    /// <item>If the reply cannot be parsed, a local "Sorry" is shown and NO action is guessed.</item>
    /// </list>
    /// </remarks>
    public sealed class GenpressoVoiceBackend : IVoiceBackend
    {
        const float kNarrationCoalesceSec = 1.5f;
        const float kFrameWaitSec = 2.5f;
        const int kMaxQueuedTurns = 3;
        const int kMaxPendingNotices = 12;
        const int kSoftBodyLimitBytes = 3600000;
        const int kHardBodyLimitBytes = GenpressoLimits.MaxRequestBodyBytes - 64000;
        const int kEnvelopeBytes = 8192; // model name, response_format + schema, JSON punctuation
        const float kKeyCheckIntervalSec = 1f;
        const int kMaxActionsPerTurn = 4;

        public event Action<string> UserTranscript;
        public event Action<string> AgentReply;
        public event Action<VoicePlacementRequest> PlacementRequested;
        public event Action CancelRequested;
        public event Action<string> Error;

        sealed class TurnJob
        {
            public string typedText;      // typed turn (no audio)
            public AudioUtterance audio;  // spoken turn
            public bool isNarration;
            public byte[] frame;
            public bool frameDone = true;
            public CancellationToken ct;
            public AwaitableCompletionSource<VoiceTurnResponse> done;
        }

        VoiceAgentContext m_Ctx;
        bool m_Started;
        CancellationTokenSource m_Cts;
        readonly Queue<TurnJob> m_Queue = new Queue<TurnJob>();
        TurnJob m_Current;
        bool m_Pumping;
        readonly List<ChatMessage> m_History = new List<ChatMessage>();
        readonly List<string> m_PendingNotices = new List<string>();
        bool m_NarrationPending;
        float m_NarrationDueTime = -1f;
        bool m_FrameSendUnsupported;
        bool m_HasKey;
        float m_NextKeyCheck;
        bool m_MissingKeyReported;

        /// <summary>Transcript of the last completed turn.</summary>
        public string LastTranscript { get; private set; }
        /// <summary>Reply of the last completed turn or narration.</summary>
        public string LastReply { get; private set; }
        /// <summary>Text-only view of the conversation history sent with each turn (oldest first).</summary>
        public IReadOnlyList<ChatMessage> History => m_History;
        /// <summary>[PIPELINE] notices waiting to be merged into the next request.</summary>
        public IReadOnlyList<string> PendingNotices => m_PendingNotices;
        /// <summary>True once the chat model rejected an image; snapshots are no longer sent.</summary>
        public bool FrameSendUnsupported => m_FrameSendUnsupported;

        public bool IsAvailable => m_Started && m_HasKey;
        public bool IsBusy => m_Current != null || m_Queue.Count > 0;
        public bool WantsStreamingAudio => false;

        SplatPressoSettings Settings => m_Ctx?.settings != null ? m_Ctx.settings : SplatPressoSettings.Active;

        bool TalkHeld
        {
            get
            {
                var f = m_Ctx?.isTalkHeld;
                if (f == null)
                    return false;
                try { return f(); }
                catch (Exception) { return false; }
            }
        }

        // ------------------------------------------------------------------------------------------
        // lifecycle

        public void Start(VoiceAgentContext ctx)
        {
            m_Ctx = ctx ?? new VoiceAgentContext();
            m_Cts?.Dispose();
            m_Cts = new CancellationTokenSource();
            m_Started = true;
            m_MissingKeyReported = false;
            RefreshKey(force: true);
            if (!m_HasKey)
                ReportMissingKey();
        }

        public void Stop()
        {
            m_Started = false;
            try { m_Cts?.Cancel(); } catch (ObjectDisposedException) { }
            while (m_Queue.Count > 0)
                m_Queue.Dequeue().done?.TrySetResult(null);
            m_NarrationPending = false;
            m_NarrationDueTime = -1f;
        }

        public void Dispose()
        {
            Stop();
            m_Cts?.Dispose();
            m_Cts = null;
        }

        public void Tick()
        {
            if (!m_Started)
                return;
            RefreshKey(force: false);
            if (m_NarrationDueTime >= 0f && Time.unscaledTime >= m_NarrationDueTime && !IsBusy && !TalkHeld)
            {
                m_NarrationDueTime = -1f;
                EnqueueNarration();
            }
        }

        void RefreshKey(bool force)
        {
            float now = Time.unscaledTime;
            if (!force && now < m_NextKeyCheck)
                return;
            m_NextKeyCheck = now + kKeyCheckIntervalSec;
            bool had = m_HasKey;
            m_HasKey = ApiKeys.Has(ApiKeyKind.Genpresso);
            if (m_HasKey && !had)
                m_MissingKeyReported = false;
        }

        void ReportMissingKey()
        {
            if (m_MissingKeyReported)
                return;
            m_MissingKeyReported = true;
            RaiseError("GenPresso API key is missing. Set it in Project Settings > SplatPresso or the GENPRESSO_API_KEY environment variable.");
        }

        // ------------------------------------------------------------------------------------------
        // input

        public void OnTalkPressed()
        {
            // the user's turn wins: drop pending narration (the notices stay queued for the next request)
            m_NarrationPending = false;
            m_NarrationDueTime = -1f;
            if (m_Queue.Count > 0)
            {
                var keep = new List<TurnJob>();
                while (m_Queue.Count > 0)
                {
                    var j = m_Queue.Dequeue();
                    if (j.isNarration)
                        j.done?.TrySetResult(null);
                    else
                        keep.Add(j);
                }
                foreach (var j in keep)
                    m_Queue.Enqueue(j);
            }
        }

        public void OnAudioChunk(string b64Pcm16)
        {
            // buffered backend: only the finished utterance is used
        }

        public void OnTalkReleased(AudioUtterance utt, Func<Awaitable<byte[]>> captureJpeg)
        {
            if (utt == null || utt.samples == null || utt.samples.Length == 0)
            {
                FlushNarrationIfIdle();
                return;
            }
            if (utt.DurationSeconds < Settings.minUtteranceSeconds)
            {
                Debug.Log($"[SplatPresso] Utterance too short ({utt.DurationSeconds:F2}s); ignored");
                FlushNarrationIfIdle();
                return;
            }
            RefreshKey(force: true);
            if (!m_HasKey)
            {
                m_MissingKeyReported = false;
                ReportMissingKey();
                return;
            }
            var job = new TurnJob { audio = utt };
            StartFrameCapture(job, captureJpeg); // at release time, in parallel with WAV encoding / queueing
            Enqueue(job);
        }

        public void SubmitText(string text) => SubmitTextFireAndForget(text);

        async void SubmitTextFireAndForget(string text)
        {
            try
            {
                await RunTextTurnAsync(text, CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Runs a typed turn through the same path as a spoken one (queue, history, notices, action mapping and
        /// events) and returns the model's reply, or null when the turn failed or was cancelled (see
        /// <see cref="Error"/>). Starts the backend with default settings if it was not started. The snapshot of
        /// the view is attached like for speech when <paramref name="attachFrame"/> and a capture source exist.
        /// </summary>
        public async Awaitable<VoiceTurnResponse> RunTextTurnAsync(string userText, CancellationToken ct, bool attachFrame = true)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return null;
            if (!m_Started)
                Start(m_Ctx ?? new VoiceAgentContext { settings = SplatPressoSettings.Active });
            var job = new TurnJob
            {
                typedText = userText.Trim(),
                ct = ct,
                done = new AwaitableCompletionSource<VoiceTurnResponse>(),
            };
            if (attachFrame)
                StartFrameCapture(job, m_Ctx?.captureJpeg);
            Enqueue(job);
            return await job.done.Awaitable;
        }

        public void StopSpeaking()
        {
            // text output only: nothing is playing
        }

        // ------------------------------------------------------------------------------------------
        // narration

        public void NotifyPipeline(string message, bool narrate)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            m_PendingNotices.Add(VoicePromptLibrary.PipelinePrefix + message.Trim());
            while (m_PendingNotices.Count > kMaxPendingNotices)
                m_PendingNotices.RemoveAt(0);
            if (!narrate || !m_Started || !m_HasKey)
                return;
            if (IsBusy || TalkHeld)
                m_NarrationPending = true;           // flushed when the current turn completes
            else if (m_NarrationDueTime < 0f)
                m_NarrationDueTime = Time.unscaledTime + kNarrationCoalesceSec; // coalesce bursts of notices
        }

        void FlushNarrationIfIdle()
        {
            if (m_NarrationPending && !IsBusy && !TalkHeld && m_Started)
            {
                m_NarrationPending = false;
                if (m_NarrationDueTime < 0f)
                    m_NarrationDueTime = Time.unscaledTime; // Tick runs it
            }
        }

        void EnqueueNarration()
        {
            if (m_PendingNotices.Count == 0)
                return;
            foreach (var j in m_Queue)
                if (j.isNarration)
                    return;
            Enqueue(new TurnJob { isNarration = true });
        }

        // ------------------------------------------------------------------------------------------
        // queue

        void Enqueue(TurnJob job)
        {
            if (!m_Started)
            {
                job.done?.TrySetResult(null);
                return;
            }
            // a backlog of stale turns helps nobody: keep the newest
            while (m_Queue.Count >= kMaxQueuedTurns)
            {
                var dropped = m_Queue.Dequeue();
                Debug.LogWarning("[SplatPresso] Voice turn queue full; dropping the oldest queued turn");
                dropped.done?.TrySetResult(null);
            }
            m_Queue.Enqueue(job);
            if (!m_Pumping)
                PumpAsync();
        }

        async void PumpAsync()
        {
            m_Pumping = true;
            try
            {
                while (m_Started && m_Queue.Count > 0)
                {
                    var job = m_Queue.Dequeue();
                    m_Current = job;
                    VoiceTurnResponse result = null;
                    CancellationTokenSource linked = null;
                    try
                    {
                        linked = CancellationTokenSource.CreateLinkedTokenSource(m_Cts.Token, job.ct);
                        result = job.isNarration ? await RunNarrationAsync(linked.Token) : await RunTurnAsync(job, linked.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                    finally
                    {
                        linked?.Dispose();
                        m_Current = null;
                    }
                    job.done?.TrySetResult(result);
                }
            }
            finally
            {
                m_Pumping = false;
            }
            FlushNarrationIfIdle();
        }

        void StartFrameCapture(TurnJob job, Func<Awaitable<byte[]>> captureJpeg)
        {
            if (captureJpeg == null || m_FrameSendUnsupported || !Settings.sendFrameWithSpeech)
                return;
            job.frameDone = false;
            CaptureFrameAsync(job, captureJpeg);
        }

        static async void CaptureFrameAsync(TurnJob job, Func<Awaitable<byte[]>> captureJpeg)
        {
            try
            {
                job.frame = await captureJpeg();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Voice snapshot failed: {e.Message}");
            }
            finally
            {
                job.frameDone = true;
            }
        }

        static async Awaitable<byte[]> WaitForFrameAsync(TurnJob job, CancellationToken ct)
        {
            float deadline = Time.realtimeSinceStartup + kFrameWaitSec;
            while (!job.frameDone && Time.realtimeSinceStartup < deadline)
                await Awaitable.NextFrameAsync(ct);
            return job.frameDone ? job.frame : null;
        }

        // ------------------------------------------------------------------------------------------
        // turns

        async Awaitable<VoiceTurnResponse> RunTurnAsync(TurnJob job, CancellationToken ct)
        {
            var s = Settings;
            string key = ApiKeys.Get(ApiKeyKind.Genpresso);
            if (string.IsNullOrWhiteSpace(key))
            {
                m_MissingKeyReported = false;
                ReportMissingKey();
                return null;
            }

            bool typed = job.typedText != null;
            byte[] frame = await WaitForFrameAsync(job, ct);
            if (m_FrameSendUnsupported)
                frame = null;

            var notices = TakeNotices();
            string system = VoicePromptLibrary.BuildChatSystemPrompt(s);
            var history = new List<ChatMessage>(m_History);

            ChatContentPart audioPart = null;
            if (!typed)
                audioPart = ChatContentPart.AudioWav(WavUtility.FromSamples(job.audio.samples, job.audio.sampleRate, 1));
            ChatContentPart imagePart = frame != null && frame.Length > 0 ? ChatContentPart.ImageJpeg(frame) : null;

            if (!FitBudget(system, history, notices, audioPart, ref imagePart, out string budgetError))
            {
                RestoreNotices(notices);
                RaiseError(budgetError);
                RaiseReply("Sorry, that was too long for me. Please keep it shorter.");
                return null;
            }

            VoiceTurnResponse response;
            try
            {
                try
                {
                    response = await CallAsync(s, key, system, history, BuildParts(notices, typed, job.typedText, imagePart, audioPart), ct);
                }
                catch (GenpressoException e) when (imagePart != null && IsImageRejection(e))
                {
                    // the model/provider does not take images with this request: stop sending snapshots
                    m_FrameSendUnsupported = true;
                    Debug.LogWarning($"[SplatPresso] The chat model rejected the snapshot image ({GenpressoError.Truncate(e.Message, 200)}); retrying without it and disabling snapshots");
                    imagePart = null;
                    response = await CallAsync(s, key, system, history, BuildParts(notices, typed, job.typedText, null, audioPart), ct);
                }
            }
            catch (OperationCanceledException)
            {
                RestoreNotices(notices);
                throw;
            }
            catch (Exception e)
            {
                RestoreNotices(notices);
                if (ct.IsCancellationRequested)
                    return null;
                HandleTurnFailure(e, s, typed);
                return null;
            }

            if (ct.IsCancellationRequested)
            {
                RestoreNotices(notices);
                return null;
            }
            ApplyTurn(response, notices, typed, job.typedText, s);
            return response;
        }

        async Awaitable<VoiceTurnResponse> RunNarrationAsync(CancellationToken ct)
        {
            var s = Settings;
            string key = ApiKeys.Get(ApiKeyKind.Genpresso);
            if (string.IsNullOrWhiteSpace(key) || m_PendingNotices.Count == 0)
                return null;

            var notices = TakeNotices();
            string system = VoicePromptLibrary.BuildChatSystemPrompt(s);
            var history = new List<ChatMessage>(m_History);
            var parts = new List<ChatContentPart> { ChatContentPart.Text(VoicePromptLibrary.BuildNarrationText(notices)) };

            VoiceTurnResponse response;
            try
            {
                response = await CallAsync(s, key, system, history, parts, ct);
            }
            catch (OperationCanceledException)
            {
                RestoreNotices(notices);
                throw;
            }
            catch (Exception e)
            {
                // no retry (avoids loops); the notices ride along with the next turn instead
                RestoreNotices(notices);
                if (!ct.IsCancellationRequested)
                    Debug.LogWarning($"[SplatPresso] Voice narration failed: {DescribeFailure(e)}");
                return null;
            }
            if (ct.IsCancellationRequested)
            {
                RestoreNotices(notices);
                return null;
            }

            // narration never acts, whatever the model returned
            if (response.actions != null && response.actions.Count > 0)
                Debug.Log($"[SplatPresso] Ignoring {response.actions.Count} action(s) returned by a narration call");
            string reply = response.reply?.Trim() ?? "";
            AppendHistory(VoicePromptLibrary.HistoryNoticeText(notices), VoicePromptLibrary.HistoryAssistantText(reply, null, false), s);
            if (reply.Length > 0)
            {
                LastReply = reply;
                Debug.Log($"[SplatPresso] Said: \"{reply}\" (narration)");
                RaiseReply(reply);
            }
            return response;
        }

        static async Awaitable<VoiceTurnResponse> CallAsync(SplatPressoSettings s, string key, string system, List<ChatMessage> history,
            List<ChatContentPart> parts, CancellationToken ct)
        {
            // voice turns are not part of a run's budget: no ledger
            var chat = new GenpressoChatClient(s.apiBaseUrl, key, null);
            return await chat.CompleteJsonAsync<VoiceTurnResponse>(s.chatModel, system, history, parts,
                VoicePromptLibrary.BuildChatSchema(), VoicePromptLibrary.ChatSchemaName, s.chatTimeoutSec, ct, s.chatFallbackModel);
        }

        static List<ChatContentPart> BuildParts(List<string> notices, bool typed, string typedText, ChatContentPart imagePart, ChatContentPart audioPart)
        {
            string text = typed
                ? VoicePromptLibrary.BuildTypedTurnText(notices, typedText, imagePart != null)
                : VoicePromptLibrary.BuildSpokenTurnText(notices, imagePart != null);
            var parts = new List<ChatContentPart> { ChatContentPart.Text(text) };
            if (imagePart != null)
                parts.Add(imagePart);
            if (audioPart != null)
                parts.Add(audioPart);
            return parts;
        }

        // Keeps the request under GenPresso's 4 MB body cap: drop the image first, then the oldest history.
        static bool FitBudget(string system, List<ChatMessage> history, List<string> notices, ChatContentPart audioPart,
            ref ChatContentPart imagePart, out string error)
        {
            int noticesBytes = 256;
            foreach (var n in notices)
                noticesBytes += (n?.Length ?? 0) * 2 + 2;
            int fixedBytes = kEnvelopeBytes + (system?.Length ?? 0) * 2 + noticesBytes + (audioPart?.ApproxJsonBytes ?? 0);
            int historyBytes = 0;
            foreach (var m in history)
                historyBytes += m.ApproxJsonBytes;
            int imageBytes = imagePart?.ApproxJsonBytes ?? 0;

            if (imagePart != null && fixedBytes + historyBytes + imageBytes > kSoftBodyLimitBytes)
            {
                Debug.LogWarning("[SplatPresso] Voice turn is close to the 4 MB request cap; sending it without the snapshot");
                imagePart = null;
                imageBytes = 0;
            }
            while (history.Count >= 2 && fixedBytes + historyBytes > kSoftBodyLimitBytes)
            {
                historyBytes -= history[0].ApproxJsonBytes + history[1].ApproxJsonBytes;
                history.RemoveRange(0, 2);
            }
            if (fixedBytes + historyBytes + imageBytes > kHardBodyLimitBytes)
            {
                error = $"The utterance is too long for one GenPresso request (~{(fixedBytes + historyBytes) / 1000} KB of a 4 MB cap). Lower maxUtteranceSeconds.";
                return false;
            }
            error = null;
            return true;
        }

        static bool IsImageRejection(GenpressoException e)
        {
            long c = e.StatusCode;
            if (c < 400 || c >= 500 || c == 401 || c == 402 || c == 403 || c == 404 || c == 408 || c == 413 || c == 429)
                return false;
            string text = (e.ResponseBody ?? "") + " " + e.Message;
            return text.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void ApplyTurn(VoiceTurnResponse response, List<string> notices, bool typed, string typedText, SplatPressoSettings s)
        {
            // a typed turn's utterance IS the typed text, whatever the model echoed
            string transcript = typed ? typedText : (response.transcript ?? "").Trim();
            response.transcript = transcript;
            string reply = (response.reply ?? "").Trim();

            var creates = new List<PlacementRequest>();
            bool cancel = false;
            if (response.actions != null)
            {
                foreach (var a in response.actions)
                {
                    if (a == null)
                        continue;
                    if (a.IsCancel)
                    {
                        cancel = true;
                        continue;
                    }
                    if (!a.IsCreate)
                    {
                        Debug.LogWarning($"[SplatPresso] Voice turn: ignoring unknown action type '{a.type}'");
                        continue;
                    }
                    if (creates.Count >= kMaxActionsPerTurn)
                    {
                        Debug.LogWarning("[SplatPresso] Voice turn: more than 4 create actions; ignoring the rest");
                        continue;
                    }
                    var request = a.ToPlacementRequest(out string reason);
                    if (request != null)
                        creates.Add(request);
                    else
                        Debug.LogWarning($"[SplatPresso] Voice turn: dropped a create action ({reason})");
                }
            }

            // 1. history (text-only rewrite of this turn)
            AppendHistory(VoicePromptLibrary.HistoryUserText(notices, transcript, typed),
                VoicePromptLibrary.HistoryAssistantText(reply, creates, cancel), s);

            // 2. transcript
            LastTranscript = transcript;
            if (!string.IsNullOrEmpty(transcript))
            {
                Debug.Log(typed ? $"[SplatPresso] Typed: \"{transcript}\"" : $"[SplatPresso] Heard: \"{transcript}\"");
                Raise(UserTranscript, transcript);
            }

            // 3. effects: cancel first, so "cancel that and make X instead" does not cancel the new request too
            if (cancel)
            {
                Debug.Log("[SplatPresso] Voice turn: cancel requested");
                RaiseCancel();
            }
            foreach (var request in creates)
            {
                Debug.Log($"[SplatPresso] Voice turn: create {request.objects.Count} object type(s), hint='{request.placementHint}'");
                RaisePlacement(new VoicePlacementRequest { request = request, sourceUtterance = string.IsNullOrEmpty(transcript) ? null : transcript });
            }

            // 4. reply
            if (reply.Length > 0)
            {
                LastReply = reply;
                Debug.Log($"[SplatPresso] Said: \"{reply}\"");
                RaiseReply(reply);
            }
        }

        void HandleTurnFailure(Exception e, SplatPressoSettings s, bool typed)
        {
            string detail = DescribeFailure(e);
            string local = "Sorry, I couldn't reach the voice service. Please try again.";
            if (e is GenpressoException ge)
            {
                switch (ge.Kind)
                {
                    case GenpressoErrorKind.Parse:
                        local = "Sorry, I couldn't process that. Please try again.";
                        break;
                    case GenpressoErrorKind.Unauthorized:
                        local = "My GenPresso API key was rejected.";
                        break;
                    case GenpressoErrorKind.InsufficientCredits:
                        local = "The GenPresso balance is too low.";
                        break;
                    case GenpressoErrorKind.PayloadTooLarge:
                        local = "Sorry, that was too long for me. Please keep it shorter.";
                        break;
                }
                string body = (ge.ResponseBody ?? "") + " " + ge.Message;
                if (!typed && ge.StatusCode >= 400 && ge.StatusCode < 500 &&
                    (body.IndexOf("input_audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     body.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    detail += $" - the chat model '{s.chatModel}' may not accept audio input; choose an audio-capable model in Project Settings > SplatPresso";
                }
            }
            RaiseError("Voice turn failed: " + detail);
            // never guess actions: only a local apology
            RaiseReply(local);
        }

        static string DescribeFailure(Exception e)
        {
            if (e is GenpressoException ge)
                return GenpressoError.Truncate(ge.Message, 400);
            return e.Message;
        }

        void AppendHistory(string userText, string assistantText, SplatPressoSettings s)
        {
            m_History.Add(ChatMessage.User(userText));
            m_History.Add(ChatMessage.Assistant(assistantText));
            int maxMessages = Mathf.Max(0, s.voiceHistoryTurns) * 2;
            while (m_History.Count > maxMessages && m_History.Count >= 2)
                m_History.RemoveRange(0, 2);
        }

        /// <summary>Forgets the conversation history and pending notices.</summary>
        public void ClearHistory()
        {
            m_History.Clear();
            m_PendingNotices.Clear();
        }

        List<string> TakeNotices()
        {
            var taken = new List<string>(m_PendingNotices);
            m_PendingNotices.Clear();
            return taken;
        }

        void RestoreNotices(List<string> notices)
        {
            if (notices == null || notices.Count == 0)
                return;
            m_PendingNotices.InsertRange(0, notices);
            while (m_PendingNotices.Count > kMaxPendingNotices)
                m_PendingNotices.RemoveAt(0);
        }

        // ------------------------------------------------------------------------------------------
        // events (each handler isolated)

        void RaiseError(string message)
        {
            Debug.LogWarning("[SplatPresso] " + message);
            Raise(Error, message);
        }

        void RaiseReply(string reply) => Raise(AgentReply, reply);

        void RaiseCancel()
        {
            if (CancelRequested == null)
                return;
            foreach (Action h in CancelRequested.GetInvocationList())
            {
                try { h(); }
                catch (Exception ex) { Debug.LogError($"[SplatPresso] CancelRequested handler threw: {ex}"); }
            }
        }

        void RaisePlacement(VoicePlacementRequest args)
        {
            if (PlacementRequested == null)
                return;
            foreach (Action<VoicePlacementRequest> h in PlacementRequested.GetInvocationList())
            {
                try { h(args); }
                catch (Exception ex) { Debug.LogError($"[SplatPresso] PlacementRequested handler threw: {ex}"); }
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
