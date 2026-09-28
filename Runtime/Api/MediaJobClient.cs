using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace SplatPresso.Api
{
    /// <summary>The provider + model path a media job is about to be submitted to (lets callers shape the payload per path).</summary>
    public sealed class MediaTarget
    {
        public MediaProvider provider;
        /// <summary>GenPresso media path, or the fal endpoint id for fal.</summary>
        public string path;
    }

    /// <summary>Outcome of a media job: the raw model output JSON and where it ran.</summary>
    public sealed class MediaJobResult
    {
        /// <summary>Raw model output (fal-style payload, e.g. <c>images[0].url</c>, <c>model_mesh.url</c>).</summary>
        public JObject result;
        public string resolvedPath;
        public MediaProvider provider;
        public string requestId;
    }

    /// <summary>Outcome of a media path probe.</summary>
    public enum ProbeOutcome
    {
        /// <summary>The path exists (the probe body was accepted or rejected by validation).</summary>
        Present,
        /// <summary>The path returned 404 (not hosted).</summary>
        Missing,
        /// <summary>Could not tell (auth, balance, network, rate limit...).</summary>
        Error,
    }

    /// <summary>Result of <see cref="MediaJobClient.ProbeAsync"/>.</summary>
    public sealed class ProbeResult
    {
        public string path;
        public ProbeOutcome outcome;
        public long statusCode;
        public string message;
        public bool Present => outcome == ProbeOutcome.Present;

        public override string ToString() =>
            $"[{(outcome == ProbeOutcome.Present ? "ok" : outcome == ProbeOutcome.Missing ? "missing" : "error")}] {path} (HTTP {statusCode}){(string.IsNullOrEmpty(message) ? "" : ": " + message)}";
    }

    /// <summary>
    /// Runs media (image / 3D) jobs on the GenPresso media queue, or on fal.ai directly: submit, poll, fetch.
    /// </summary>
    /// <remarks>
    /// Behaviour (merged from the proven fal queue client, the GenPresso mesh client and observed GenPresso quirks):
    /// <list type="bullet">
    /// <item>Cost cap checked before submit (<see cref="CostCapExceededException"/>); cost recorded after an accepted submit.</item>
    /// <item>A global gate limits media jobs in flight across all runs to <c>settings.maxConcurrentMediaJobs</c>.</item>
    /// <item>GenPresso paths are tried in order (cached path first); a 404 at submit is not queued or billed, so it
    ///   falls through to the next candidate. The first path that is accepted is cached.</item>
    /// <item>429 is retried on submit AND poll: giving up there abandons (or re-submits) already-billed work.</item>
    /// <item>status_url / response_url / cancel_url are used verbatim (sub-path endpoints cannot be rebuilt).</item>
    /// <item>Any status other than COMPLETED or a terminal failure keeps polling; COMPLETED is always followed by a
    ///   result fetch that tolerates a 200 "still in progress" body and reveals the real error (a 422 after COMPLETED).</item>
    /// <item>A job that was accepted is never re-submitted by this client.</item>
    /// <item>Timeout / cancellation fires a best-effort cancel of the queued job.</item>
    /// </list>
    /// All work runs on the main thread (Awaitable).
    /// </remarks>
    public sealed class MediaJobClient
    {
        const int kSubmitTimeoutSec = 60;
        const int kPollTimeoutSec = 30;
        const int kFetchTimeoutSec = 60;
        const int kMaxSubmitRetries = 3;
        const int kMaxTransientPollFailures = 8;
        const int kMaxTransientFetchFailures = 10;

        static int s_ActiveJobs;
        static System.Random s_Random;
        static HashSet<string> s_WarnedFalFallback;

        readonly SplatPressoSettings m_Settings;
        readonly CostLedger m_Ledger; // may be null

        /// <param name="settings">Settings (null = <see cref="SplatPressoSettings.Active"/>).</param>
        /// <param name="ledger">Optional run ledger for the cost cap.</param>
        public MediaJobClient(SplatPressoSettings settings, CostLedger ledger)
        {
            m_Settings = settings != null ? settings : SplatPressoSettings.Active;
            m_Ledger = ledger;
        }

        /// <summary>Media jobs currently holding a slot of the global concurrency gate.</summary>
        public static int ActiveJobs => s_ActiveJobs;

        /// <summary>The settings this client uses.</summary>
        public SplatPressoSettings Settings => m_Settings;

        /// <summary>
        /// Runs one media job for <paramref name="route"/>. <paramref name="buildInput"/> builds the request body for
        /// each target it is about to be submitted to. Throws <see cref="GenpressoException"/>,
        /// <see cref="CostCapExceededException"/>, <see cref="TimeoutException"/> or <see cref="OperationCanceledException"/>.
        /// </summary>
        public async Awaitable<MediaJobResult> RunAsync(string routeKey, ModelRoute route, Func<MediaTarget, JObject> buildInput,
            string costLabel, Action<string> onStatus, CancellationToken ct)
        {
            if (route == null)
                throw new ArgumentNullException(nameof(route));
            if (buildInput == null)
                throw new ArgumentNullException(nameof(buildInput));
            ct.ThrowIfCancellationRequested();

            if (m_Ledger != null && !m_Ledger.CanSpend(route.estimatedCost))
                throw new CostCapExceededException(
                    $"Cost cap would be exceeded by '{costLabel}' (~{route.estimatedCost:F2} credits; spent {m_Ledger.TotalCost:F2} of {m_Ledger.capCost:F2})");

            await AcquireSlotAsync(ct);
            try
            {
                if (m_Settings.mediaProvider == MediaProvider.FalDirect)
                    return await RunOnFalAsync(routeKey, route, buildInput, costLabel, onStatus, ct);
                return await RunOnGenpressoAsync(routeKey, route, buildInput, costLabel, onStatus, ct);
            }
            finally
            {
                ReleaseSlot();
            }
        }

        // ------------------------------------------------------------------------------------------
        // Providers

        async Awaitable<MediaJobResult> RunOnGenpressoAsync(string routeKey, ModelRoute route, Func<MediaTarget, JObject> buildInput,
            string costLabel, Action<string> onStatus, CancellationToken ct)
        {
            string key = ApiKeys.Get(ApiKeyKind.Genpresso);
            if (string.IsNullOrEmpty(key))
                throw new GenpressoException("GenPresso API key is missing. Set it in Project Settings > SplatPresso or the GENPRESSO_API_KEY environment variable.",
                    GenpressoErrorKind.Unauthorized, 0, null, retryable: false);
            string auth = "Bearer " + key;
            string baseUrl = m_Settings.apiBaseUrl;

            // Candidates: the cached path first (only while it is still listed, so edits in the settings win), then the list.
            var paths = route.CleanPaths();
            var candidates = new List<string>();
            string cached = ModelPathCache.Get(baseUrl, routeKey);
            if (!string.IsNullOrEmpty(cached))
                foreach (var p in paths)
                    if (string.Equals(p, cached, StringComparison.OrdinalIgnoreCase)) { candidates.Add(p); break; }
            foreach (var p in paths)
                if (!candidates.Contains(p))
                    candidates.Add(p);

            var tried = new List<string>();
            string lastMissingDetail = null;
            foreach (string path in candidates)
            {
                var target = new MediaTarget { provider = MediaProvider.Genpresso, path = path };
                string submitUrl = SplatPressoSettings.JoinUrl(baseUrl, "media/" + path);
                SafeStatus(onStatus, "submitting " + path);
                var sub = await SubmitAsync(submitUrl, BuildBody(buildInput, target, path), auth, path, true, ct);
                if (sub.rejected != null)
                {
                    // 422 at submit: the path exists (cache it) but this input is invalid.
                    ModelPathCache.Set(baseUrl, routeKey, path);
                    throw sub.rejected;
                }
                if (sub.missing)
                {
                    tried.Add(path);
                    lastMissingDetail = sub.missingDetail;
                    if (string.Equals(path, cached, StringComparison.OrdinalIgnoreCase))
                        ModelPathCache.Invalidate(baseUrl, routeKey);
                    Debug.Log($"[SplatPresso] GenPresso media path '{path}' ({routeKey}) is not available ({sub.missingDetail}); trying the next candidate");
                    continue;
                }

                ModelPathCache.Set(baseUrl, routeKey, path);
                m_Ledger?.Record(costLabel, route.estimatedCost);
                var urls = ResolveJobUrls(sub.json, path, true);
                var result = await PollAndFetchAsync(urls, auth, path, route.timeoutSec, onStatus, ct);
                return new MediaJobResult { result = result, resolvedPath = path, provider = MediaProvider.Genpresso, requestId = urls.requestId };
            }

            // No GenPresso path answered: optionally run the step on fal.ai.
            if (m_Settings.falFallbackWhenMissing && !string.IsNullOrWhiteSpace(route.falEndpoint) && ApiKeys.Has(ApiKeyKind.Fal))
            {
                s_WarnedFalFallback ??= new HashSet<string>();
                if (s_WarnedFalFallback.Add(routeKey ?? ""))
                    Debug.LogWarning($"[SplatPresso] No GenPresso path for '{routeKey}' is available (tried: {string.Join(", ", tried)}); using fal.ai '{route.falEndpoint}' instead.");
                SafeStatus(onStatus, $"GenPresso has no '{routeKey}' model; using fal.ai");
                return await RunOnFalAsync(routeKey, route, buildInput, costLabel, onStatus, ct);
            }

            string triedText = tried.Count > 0 ? string.Join(", ", tried) : "(no paths configured)";
            throw new GenpressoException(
                $"No GenPresso media path for '{routeKey}' is available (tried: {triedText}; last: {lastMissingDetail ?? "n/a"}). " +
                "Set a working path in Project Settings > SplatPresso > Media models (check apiBaseUrl too), or add a FAL_KEY to run this step on fal.ai.",
                GenpressoErrorKind.NotFound, 404, null, retryable: false);
        }

        async Awaitable<MediaJobResult> RunOnFalAsync(string routeKey, ModelRoute route, Func<MediaTarget, JObject> buildInput,
            string costLabel, Action<string> onStatus, CancellationToken ct)
        {
            string key = ApiKeys.Get(ApiKeyKind.Fal);
            if (string.IsNullOrEmpty(key))
                throw new GenpressoException("fal.ai key is missing (FAL_KEY). Set it in Project Settings > SplatPresso or switch the media provider to GenPresso.",
                    GenpressoErrorKind.Unauthorized, 0, null, retryable: false);
            string endpoint = (route.falEndpoint ?? "").Trim().Trim('/');
            if (string.IsNullOrEmpty(endpoint))
                throw new GenpressoException($"No fal.ai endpoint configured for '{routeKey}'.", GenpressoErrorKind.NotFound, 0, null, retryable: false);

            string auth = "Key " + key;
            var target = new MediaTarget { provider = MediaProvider.FalDirect, path = endpoint };
            string submitUrl = SplatPressoSettings.JoinUrl(m_Settings.falQueueBaseUrl, endpoint, "https://queue.fal.run");
            SafeStatus(onStatus, "submitting " + endpoint + " (fal.ai)");
            var sub = await SubmitAsync(submitUrl, BuildBody(buildInput, target, endpoint), auth, endpoint, false, ct);
            if (sub.rejected != null)
                throw sub.rejected;
            if (sub.missing)
                throw new GenpressoException($"fal.ai endpoint '{endpoint}' not found ({sub.missingDetail}).", GenpressoErrorKind.NotFound, 404, null, retryable: false);

            m_Ledger?.Record(costLabel, route.estimatedCost);
            var urls = ResolveJobUrls(sub.json, endpoint, false);
            var result = await PollAndFetchAsync(urls, auth, endpoint, route.timeoutSec, onStatus, ct);
            return new MediaJobResult { result = result, resolvedPath = endpoint, provider = MediaProvider.FalDirect, requestId = urls.requestId };
        }

        // ------------------------------------------------------------------------------------------
        // Submit

        struct SubmitOutcome
        {
            public JObject json;
            public bool missing;
            public string missingDetail;
            public GenpressoException rejected; // input rejected by an existing path (never retried)
        }

        static byte[] BuildBody(Func<MediaTarget, JObject> buildInput, MediaTarget target, string path)
        {
            JObject input = buildInput(target) ?? new JObject();
            byte[] bytes = Encoding.UTF8.GetBytes(input.ToString(Formatting.None));
            if (bytes.Length >= GenpressoLimits.MaxRequestBodyBytes)
                throw new GenpressoException(
                    $"Media request for {path} is {bytes.Length / 1024} KB; request bodies are capped at 4 MB (send a hosted URL or a smaller image).",
                    GenpressoErrorKind.PayloadTooLarge, 0, null, retryable: false);
            return bytes;
        }

        async Awaitable<SubmitOutcome> SubmitAsync(string url, byte[] body, string auth, string path, bool genpresso, CancellationToken ct)
        {
            var headers = new Dictionary<string, string> { { "Authorization", auth } };
            string service = genpresso ? "GenPresso" : "fal.ai";
            for (int attempt = 0; ; attempt++)
            {
                var resp = await HttpJson.SendAsync("POST", url, body, "application/json", headers, kSubmitTimeoutSec, ct, throwOnHttpError: false);
                long code = resp.StatusCode;

                if (resp.IsSuccess)
                {
                    JObject json;
                    try
                    {
                        json = JObject.Parse(resp.Text ?? "");
                    }
                    catch (JsonException e)
                    {
                        // The job may exist already; do not re-submit.
                        throw new GenpressoException($"{service} submit to {path} returned a non-JSON body: {GenpressoError.Truncate(resp.Text, 300)}",
                            GenpressoErrorKind.Parse, code, resp.Text, retryable: false, inner: e);
                    }
                    return new SubmitOutcome { json = json };
                }

                if (IsMissingPath(code, resp.Text))
                    return new SubmitOutcome { missing = true, missingDetail = GenpressoError.Describe(code, resp.Error, resp.Text) };

                // 422 at submit: the path exists but rejected this input. Deterministic: never retry.
                if (code == 422 || code == 400)
                    return new SubmitOutcome
                    {
                        rejected = GenpressoException.FromHttp($"{service} rejected the input for {path}", code, resp.Text, resp.Error, retryable: false),
                    };

                // 429 (per-minute / media concurrency limit), 408, 5xx, network: retry the SAME candidate. Before the
                // 429 retry existed, the upper-layer retry re-submitted and the same work was billed twice.
                // (A 0/5xx can still leave a duplicate job behind; that risk is inherent.)
                if (GenpressoException.IsTransientStatus(code) && attempt < kMaxSubmitRetries)
                {
                    float delay = resp.RetryAfterSec;
                    if (delay <= 0f)
                        delay = (1 << attempt) + (float)NextRandom() * 0.5f; // ~1 s, 2 s, 4 s
                    delay = Mathf.Min(delay, 60f);
                    Debug.LogWarning($"[SplatPresso] {service} submit to {path} failed ({GenpressoError.Describe(code, resp.Error, resp.Text)}); retry {attempt + 1}/{kMaxSubmitRetries} in {delay:F1}s");
                    await HttpJson.DelayAsync(delay, ct);
                    continue;
                }

                // 401/402/403/413/other: fail now (402 = balance below the media minimum).
                throw GenpressoException.FromHttp($"{service} submit to {path}", code, resp.Text, resp.Error, resp.RetryAfterSec);
            }
        }

        // A 404 (JSON or the web app's HTML page) means the path is not hosted; a 400 whose message says the model is
        // unknown/unsupported means the same. Neither is queued or billed.
        static bool IsMissingPath(long code, string body)
        {
            if (code == 404)
                return true;
            if (code != 400 || string.IsNullOrEmpty(body))
                return false;
            string msg = (GenpressoError.Parse(body) ?? body).ToLowerInvariant();
            bool aboutModel = msg.Contains("model") || msg.Contains("endpoint") || msg.Contains("route") || msg.Contains("path");
            bool missing = msg.Contains("not found") || msg.Contains("unsupported") || msg.Contains("not supported") ||
                           msg.Contains("unknown") || msg.Contains("does not exist") || msg.Contains("not available") ||
                           msg.Contains("not allowed") || msg.Contains("no such");
            return aboutModel && missing;
        }

        // ------------------------------------------------------------------------------------------
        // Poll + fetch

        struct JobUrls
        {
            public string requestId, statusUrl, responseUrl, cancelUrl;
        }

        JobUrls ResolveJobUrls(JObject submit, string path, bool genpresso)
        {
            var u = new JobUrls
            {
                requestId = submit["request_id"]?.Type == JTokenType.String ? (string)submit["request_id"] : submit["request_id"]?.ToString(),
                statusUrl = (string)submit["status_url"],
                responseUrl = (string)submit["response_url"],
                cancelUrl = (string)submit["cancel_url"],
            };
            // GenPresso: fall back to building the polling URLs from the id (they never contain the model path).
            if (genpresso && !string.IsNullOrEmpty(u.requestId))
            {
                if (string.IsNullOrEmpty(u.statusUrl)) u.statusUrl = m_Settings.ApiUrl($"media/requests/{u.requestId}/status");
                if (string.IsNullOrEmpty(u.responseUrl)) u.responseUrl = m_Settings.ApiUrl($"media/requests/{u.requestId}");
                if (string.IsNullOrEmpty(u.cancelUrl)) u.cancelUrl = m_Settings.ApiUrl($"media/requests/{u.requestId}/cancel");
            }
            if (string.IsNullOrEmpty(u.statusUrl) || string.IsNullOrEmpty(u.responseUrl))
            {
                string raw = submit.ToString(Formatting.None);
                TryCancelFireAndForget(u.cancelUrl, null);
                throw new GenpressoException(
                    $"Submit response for {path} has neither status_url/response_url nor a request_id: {GenpressoError.Truncate(raw, 500)}",
                    GenpressoErrorKind.Parse, 0, raw, retryable: false);
            }
            return u;
        }

        async Awaitable<JObject> PollAndFetchAsync(JobUrls urls, string auth, string path, int timeoutSec, Action<string> onStatus, CancellationToken ct)
        {
            var headers = new Dictionary<string, string> { { "Authorization", auth } };
            var sw = Stopwatch.StartNew();
            double budget = Mathf.Max(10, timeoutSec);
            try
            {
                // ---- poll: every 1 s for the first 10 s, then every 2 s ----
                int transient = 0;
                string lastReported = null;
                while (true)
                {
                    if (sw.Elapsed.TotalSeconds > budget)
                        throw MakeTimeout(urls, auth, path, timeoutSec, "while queued/running");
                    await HttpJson.DelayAsync(sw.Elapsed.TotalSeconds < 10.0 ? 1f : 2f, ct);

                    var resp = await HttpJson.SendAsync("GET", urls.statusUrl, null, null, headers, kPollTimeoutSec, ct, throwOnHttpError: false);
                    long code = resp.StatusCode;
                    if (!resp.IsSuccess)
                    {
                        if (GenpressoException.IsTransientStatus(code))
                        {
                            // 429 while polling means the status check was throttled, not that the job died. Giving up
                            // here would abandon an already-billed job and the upper layer would pay again.
                            if (++transient > kMaxTransientPollFailures)
                            {
                                // Media is billed at completion: cancel the job we can no longer observe.
                                TryCancelFireAndForget(urls.cancelUrl, auth);
                                throw GenpressoException.FromHttp($"Polling {path} failed {transient} times in a row", code, resp.Text, resp.Error, resp.RetryAfterSec);
                            }
                            if (code == 429)
                                await HttpJson.DelayAsync(resp.RetryAfterSec > 0 ? Mathf.Min(resp.RetryAfterSec, 30f) : 5f + (float)NextRandom(), ct);
                            continue;
                        }
                        // Hard 4xx: the job cannot be observed any more; stop it so it does not keep costing credits.
                        TryCancelFireAndForget(urls.cancelUrl, auth);
                        throw GenpressoException.FromHttp($"Status check for {path}", code, resp.Text, resp.Error, retryable: false);
                    }

                    JObject status;
                    try
                    {
                        status = JObject.Parse(resp.Text ?? "");
                    }
                    catch (JsonException)
                    {
                        if (++transient > kMaxTransientPollFailures)
                        {
                            TryCancelFireAndForget(urls.cancelUrl, auth);
                            throw new GenpressoException($"Status of {path} is not JSON: {GenpressoError.Truncate(resp.Text, 300)}",
                                GenpressoErrorKind.Parse, code, resp.Text);
                        }
                        continue;
                    }
                    transient = 0;

                    string state = (status["status"]?.ToString() ?? "").Trim().ToUpperInvariant();
                    var queuePos = status["queue_position"];
                    string report = queuePos != null && queuePos.Type != JTokenType.Null ? $"{state} (queue position {queuePos})" : state;
                    if (!string.IsNullOrEmpty(report) && report != lastReported)
                    {
                        lastReported = report;
                        SafeStatus(onStatus, report);
                    }

                    if (state == "COMPLETED")
                        break;
                    if (IsTerminalFailure(state))
                        throw await BuildJobFailureAsync(urls, headers, path, state, status, ct);
                    // IN_QUEUE / IN_PROGRESS / null / anything unknown: keep polling.
                }

                // ---- fetch result (same budget; the output is already paid for, never re-submit) ----
                int fetchFailures = 0;
                while (true)
                {
                    if (sw.Elapsed.TotalSeconds > budget)
                        throw MakeTimeout(urls, auth, path, timeoutSec, "waiting for the result body");

                    var resp = await HttpJson.SendAsync("GET", urls.responseUrl, null, null, headers, kFetchTimeoutSec, ct, throwOnHttpError: false);
                    long code = resp.StatusCode;
                    if (!resp.IsSuccess)
                    {
                        if (GenpressoException.IsTransientStatus(code))
                        {
                            if (++fetchFailures > kMaxTransientFetchFailures)
                                throw GenpressoException.FromHttp(
                                    $"Result of {path} could not be fetched after {fetchFailures} attempts (the job completed; not re-submitting)",
                                    code, resp.Text, resp.Error, retryable: false);
                            SafeStatus(onStatus, "fetching result (retrying)");
                            await HttpJson.DelayAsync(resp.RetryAfterSec > 0 ? Mathf.Min(resp.RetryAfterSec, 30f) : 2f, ct);
                            continue;
                        }
                        // A 4xx after COMPLETED is the real (validation) error: the status "lied". Deterministic.
                        throw GenpressoException.FromHttp($"{path} failed", code, resp.Text, resp.Error, retryable: false);
                    }

                    JObject json;
                    try
                    {
                        json = JObject.Parse(resp.Text ?? "");
                    }
                    catch (JsonException)
                    {
                        if (++fetchFailures > kMaxTransientFetchFailures)
                            throw new GenpressoException($"Result of {path} is not JSON: {GenpressoError.Truncate(resp.Text, 300)}",
                                GenpressoErrorKind.Parse, code, resp.Text, retryable: false);
                        await HttpJson.DelayAsync(2f, ct);
                        continue;
                    }

                    // The result can briefly lag the status with 200 {"detail":"Request is still in progress"}.
                    if (GenpressoError.IsStillInProgress(json))
                    {
                        SafeStatus(onStatus, "finishing up");
                        await HttpJson.DelayAsync(2f, ct);
                        continue;
                    }
                    // A validation error delivered with 200.
                    if (json["detail"] is JArray)
                        throw new GenpressoException($"{path} rejected the input: {GenpressoError.ParseToken(json)}",
                            GenpressoErrorKind.Validation, 422, resp.Text, retryable: false);
                    return json;
                }
            }
            catch (OperationCanceledException)
            {
                TryCancelFireAndForget(urls.cancelUrl, auth);
                throw;
            }
        }

        static TimeoutException MakeTimeout(JobUrls urls, string auth, string path, int timeoutSec, string phase)
        {
            TryCancelFireAndForget(urls.cancelUrl, auth);
            return new TimeoutException($"[SplatPresso] Media job {path} exceeded {timeoutSec}s {phase} (request {urls.requestId ?? "?"}).");
        }

        static bool IsTerminalFailure(string state)
        {
            switch (state)
            {
                // GenPresso spells it CANCELED; accept the double-L form too.
                case "FAILED":
                case "ERROR":
                case "EXPIRED":
                case "CANCELED":
                case "CANCELLED":
                    return true;
                default:
                    return false;
            }
        }

        // On FAILED the result endpoint says which parameter was invalid and why: fetch it once for the message.
        async Awaitable<GenpressoException> BuildJobFailureAsync(JobUrls urls, Dictionary<string, string> headers, string path,
            string state, JObject status, CancellationToken ct)
        {
            string reason = GenpressoError.ParseToken(status);
            long reasonStatus = 0;
            string reasonBody = null;
            try
            {
                var resp = await HttpJson.SendAsync("GET", urls.responseUrl, null, null, headers, kPollTimeoutSec, ct, throwOnHttpError: false);
                reasonStatus = resp.StatusCode;
                reasonBody = resp.Text;
                string parsed = GenpressoError.Parse(resp.Text);
                if (!string.IsNullOrEmpty(parsed))
                    reason = parsed;
                else if (!resp.IsSuccess && string.IsNullOrEmpty(reason))
                    reason = GenpressoError.Describe(resp.StatusCode, resp.Error, resp.Text);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                reason ??= e.Message;
            }

            string msg = $"Media job {path} ended with status {state}" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason);
            bool validation = reasonStatus >= 400 && reasonStatus < 500 && reasonStatus != 408 && reasonStatus != 429;
            if (validation)
                return new GenpressoException(msg, GenpressoErrorKind.Validation, reasonStatus, reasonBody, retryable: false);
            // FAILED/ERROR/EXPIRED without a validation reason may be transient (failed jobs are not billed);
            // CANCELED means someone stopped it on purpose.
            bool retryable = state != "CANCELED" && state != "CANCELLED";
            return new GenpressoException(msg, GenpressoErrorKind.JobFailed, reasonStatus, reasonBody, retryable);
        }

        // ------------------------------------------------------------------------------------------
        // Cancel + probe

        /// <summary>
        /// Best-effort cancel of a queued job; never throws or blocks. Built by hand rather than via
        /// UnityWebRequest.Put because UploadHandlerRaw rejects an empty payload and this endpoint takes no body.
        /// </summary>
        public static void TryCancelFireAndForget(string cancelUrl, string authHeaderValue)
        {
            if (string.IsNullOrEmpty(cancelUrl))
                return;
            try
            {
                var req = new UnityWebRequest(cancelUrl, UnityWebRequest.kHttpVerbPUT)
                {
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = 15,
                };
                if (!string.IsNullOrEmpty(authHeaderValue))
                    req.SetRequestHeader("Authorization", authHeaderValue);
                var op = req.SendWebRequest();
                op.completed += _ =>
                {
                    try
                    {
                        if (req.result != UnityWebRequest.Result.Success)
                            Debug.LogWarning($"[SplatPresso] Could not cancel an abandoned media request: {req.error}");
                        req.Dispose();
                    }
                    catch { /* best effort */ }
                };
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Checks whether a GenPresso media path exists WITHOUT running a job: posts a body that fails validation
        /// for every supported model. 404 = missing; 2xx or 422 = present (a 2xx job is cancelled immediately).
        /// Needs a key (and, per GenPresso, at least 10 credits of balance). Works in edit mode.
        /// </summary>
        public static async Awaitable<ProbeResult> ProbeAsync(SplatPressoSettings s, string path, CancellationToken ct)
        {
            s = s != null ? s : SplatPressoSettings.Active;
            var result = new ProbeResult { path = (path ?? "").Trim().Trim('/') };
            string key = ApiKeys.Get(ApiKeyKind.Genpresso);
            if (string.IsNullOrEmpty(key))
            {
                result.outcome = ProbeOutcome.Error;
                result.message = "no GenPresso key";
                return result;
            }
            string auth = "Bearer " + key;
            var body = Encoding.UTF8.GetBytes("{\"output_format\":\"__probe__\",\"tier\":\"__probe__\"}");
            var headers = new Dictionary<string, string> { { "Authorization", auth } };
            HttpResponse resp;
            try
            {
                resp = await HttpJson.SendAsync("POST", s.ApiUrl("media/" + result.path), body, "application/json", headers, 30, ct, throwOnHttpError: false);
            }
            catch (GenpressoException e)
            {
                result.outcome = ProbeOutcome.Error;
                result.message = e.Message;
                return result;
            }

            result.statusCode = resp.StatusCode;
            if (resp.IsSuccess)
            {
                result.outcome = ProbeOutcome.Present;
                result.message = "accepted (cancelled immediately)";
                try
                {
                    var json = JObject.Parse(resp.Text ?? "");
                    string id = json["request_id"]?.ToString();
                    string cancel = (string)json["cancel_url"];
                    if (string.IsNullOrEmpty(cancel) && !string.IsNullOrEmpty(id))
                        cancel = s.ApiUrl($"media/requests/{id}/cancel");
                    TryCancelFireAndForget(cancel, auth);
                }
                catch { /* nothing to cancel */ }
                return result;
            }

            if (IsMissingPath(resp.StatusCode, resp.Text))
            {
                result.outcome = ProbeOutcome.Missing;
                result.message = "not hosted";
                return result;
            }
            if (resp.StatusCode == 422 || resp.StatusCode == 400)
            {
                result.outcome = ProbeOutcome.Present;
                result.message = "exists (probe input rejected as expected)";
                return result;
            }

            result.outcome = ProbeOutcome.Error;
            if (resp.StatusCode == 401 || resp.StatusCode == 403)
                result.message = "the GenPresso key was rejected";
            else if (resp.StatusCode == 402)
                result.message = $"balance too low (media needs at least {GenpressoLimits.MinMediaCredits} credits)";
            else
                result.message = GenpressoError.Describe(resp.StatusCode, resp.Error, resp.Text);
            return result;
        }

        // ------------------------------------------------------------------------------------------
        // Global concurrency gate (main thread only, so a plain counter suffices)

        async Awaitable AcquireSlotAsync(CancellationToken ct)
        {
            int limit = Mathf.Max(1, m_Settings.maxConcurrentMediaJobs);
            while (s_ActiveJobs >= limit)
                await HttpJson.DelayAsync(0.02f, ct); // ~a frame in play mode; also works in edit mode
            s_ActiveJobs++;
        }

        static void ReleaseSlot()
        {
            if (s_ActiveJobs > 0)
                s_ActiveJobs--;
        }

        static double NextRandom()
        {
            s_Random ??= new System.Random();
            return s_Random.NextDouble();
        }

        static void SafeStatus(Action<string> onStatus, string message)
        {
            if (onStatus == null)
                return;
            try { onStatus(message); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // Projects often disable domain reload, so statics survive play sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_ActiveJobs = 0;
            s_Random = null;
            s_WarnedFalFallback = null;
        }
    }
}
