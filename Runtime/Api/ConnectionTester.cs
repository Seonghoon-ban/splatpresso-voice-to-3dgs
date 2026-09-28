using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SplatPresso.Api
{
    /// <summary>Human-readable outcome of <see cref="ConnectionTester.TestAsync"/>.</summary>
    public sealed class ConnectionReport
    {
        /// <summary>True when the API answered and the key + chat model work.</summary>
        public bool ok;
        public bool hasKey;
        public KeySource keySource;
        public bool reachable;
        public bool keyAccepted;
        public bool chatModelListed;
        public bool chatOk;
        public bool hasFalKey;
        public bool hasOpenAIKey;
        /// <summary>Report lines (never contain key values).</summary>
        public List<string> lines = new List<string>();
        /// <summary>Media path probes (empty unless requested).</summary>
        public List<ProbeResult> probes = new List<ProbeResult>();

        public override string ToString() => string.Join("\n", lines);
    }

    /// <summary>
    /// Checks the GenPresso setup: reachability + model list, a 1-token chat ping (key, balance and model slug),
    /// optional media-path probes, and which optional keys are present. Uses only HTTP awaits, so it also runs
    /// in edit mode. Never logs or returns key values.
    /// </summary>
    public static class ConnectionTester
    {
        /// <summary>Runs the checks. <paramref name="probeMediaModels"/> also probes every media route candidate (not billed; needs >= 10 credits).</summary>
        public static async Awaitable<ConnectionReport> TestAsync(SplatPressoSettings s, bool probeMediaModels, CancellationToken ct)
        {
            s = s != null ? s : SplatPressoSettings.Active;
            var r = new ConnectionReport();
            string key = ApiKeys.Get(ApiKeyKind.Genpresso, out r.keySource);
            r.hasKey = !string.IsNullOrEmpty(key);
            r.hasFalKey = ApiKeys.Has(ApiKeyKind.Fal);
            r.hasOpenAIKey = ApiKeys.Has(ApiKeyKind.OpenAI);

            string baseUrl = string.IsNullOrWhiteSpace(s.apiBaseUrl) ? SplatPressoSettings.DefaultApiBaseUrl : s.apiBaseUrl.Trim();
            r.lines.Add($"API: {baseUrl}");
            if (r.hasKey)
            {
                r.lines.Add($"GenPresso key: {ApiKeys.Mask(key)} (source: {r.keySource})");
                if (!key.StartsWith("gp_", StringComparison.Ordinal))
                    r.lines.Add("  warning: GenPresso keys normally start with 'gp_'.");
                if (r.keySource == KeySource.SettingsAsset || r.keySource == KeySource.StreamingAssets)
                    r.lines.Add("  warning: this key is stored in plain text inside the project/build.");
            }
            else
            {
                r.lines.Add("GenPresso key: MISSING. Get one at https://genpresso.ai/ko/developers and save it in Project Settings > SplatPresso " +
                            "(or set GENPRESSO_API_KEY).");
            }
            var headers = new Dictionary<string, string>();
            if (r.hasKey)
                headers["Authorization"] = "Bearer " + key;

            // 1. GET /models: reachability, key, and whether the chat model is listed (free).
            var sw = Stopwatch.StartNew();
            HttpResponse models = null;
            try
            {
                models = await HttpJson.SendAsync("GET", s.ApiUrl("models"), null, null, headers, 20, ct, throwOnHttpError: false);
            }
            catch (GenpressoException e)
            {
                r.lines.Add("GET /models: " + e.Message);
            }
            if (models != null)
            {
                long ms = sw.ElapsedMilliseconds;
                r.reachable = models.StatusCode != 0;
                if (models.IsSuccess)
                {
                    r.keyAccepted = r.hasKey;
                    var ids = ParseModelIds(models.Text);
                    r.chatModelListed = ids.Contains(s.chatModel ?? "");
                    r.lines.Add($"GET /models: OK ({ms} ms, {ids.Count} models)");
                    r.lines.Add(r.chatModelListed
                        ? $"  chat model '{s.chatModel}' is listed"
                        : $"  chat model '{s.chatModel}' is NOT listed (the chat ping below tells whether it still works)");
                    if (!string.IsNullOrWhiteSpace(s.chatFallbackModel) && ids.Count > 0)
                        r.lines.Add(ids.Contains(s.chatFallbackModel)
                            ? $"  fallback model '{s.chatFallbackModel}' is listed"
                            : $"  fallback model '{s.chatFallbackModel}' is NOT listed");
                }
                else if (models.StatusCode == 0)
                {
                    r.lines.Add($"GET /models: unreachable ({models.Error}). Check the network / apiBaseUrl.");
                }
                else
                {
                    r.lines.Add($"GET /models: {GenpressoError.Describe(models.StatusCode, models.Error, models.Text)} ({ms} ms)");
                }
            }

            // 2. Tiny chat ping (max_tokens 1): confirms key (401), balance (402) and model slug (400/404).
            if (r.hasKey && r.reachable)
            {
                sw.Restart();
                var body = new JObject
                {
                    ["model"] = s.chatModel,
                    ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "ping" }),
                    ["max_tokens"] = 1,
                };
                try
                {
                    var chat = await HttpJson.SendAsync("POST", s.ApiUrl("chat/completions"), Encoding.UTF8.GetBytes(body.ToString(Formatting.None)),
                        "application/json", headers, Mathf.Max(10, s.chatTimeoutSec), ct, throwOnHttpError: false);
                    long ms = sw.ElapsedMilliseconds;
                    string errorOn200 = null;
                    if (chat.IsSuccess)
                    {
                        try
                        {
                            var json = JObject.Parse(chat.Text ?? "");
                            if (json["error"] != null && json["error"].Type != JTokenType.Null)
                                errorOn200 = GenpressoError.ParseToken(json) ?? json["error"].ToString(Formatting.None);
                        }
                        catch (JsonException) { errorOn200 = "response is not JSON"; }
                    }

                    if (chat.IsSuccess && errorOn200 == null)
                    {
                        r.chatOk = true;
                        r.keyAccepted = true;
                        r.lines.Add($"Chat ping ({s.chatModel}): OK ({ms} ms)");
                    }
                    else if (errorOn200 != null)
                    {
                        r.lines.Add($"Chat ping ({s.chatModel}): error: {errorOn200}");
                    }
                    else if (chat.StatusCode == 401 || chat.StatusCode == 403)
                    {
                        r.keyAccepted = false;
                        r.lines.Add("Chat ping: the GenPresso key was rejected (HTTP " + chat.StatusCode + ").");
                    }
                    else if (chat.StatusCode == 402)
                    {
                        r.keyAccepted = true;
                        r.lines.Add("Chat ping: balance too low (HTTP 402). Top up your GenPresso credits.");
                    }
                    else
                    {
                        r.lines.Add($"Chat ping ({s.chatModel}): {GenpressoError.Describe(chat.StatusCode, chat.Error, chat.Text)}");
                    }
                }
                catch (GenpressoException e)
                {
                    r.lines.Add("Chat ping: " + e.Message);
                }
                r.lines.Add($"Note: media jobs need at least {GenpressoLimits.MinMediaCredits} credits of balance.");
            }

            // 3. Optional keys.
            r.lines.Add("Media provider: " + s.mediaProvider + (s.mediaProvider == MediaProvider.FalDirect && !r.hasFalKey ? " (FAL_KEY MISSING)" : ""));
            r.lines.Add("fal.ai key: " + (r.hasFalKey ? "present" : "not set") +
                        (s.falFallbackWhenMissing ? " (used only as a fallback when a GenPresso path is missing)" : ""));
            if (s.voiceBackend == VoiceBackendKind.OpenAIRealtime)
                r.lines.Add("OpenAI key (Realtime voice): " + (r.hasOpenAIKey ? "present" : "MISSING"));

            // 4. Optional media path probes (the first present candidate per route is cached, unless a candidate
            //    listed before it could not be checked).
            if (probeMediaModels && r.hasKey && r.reachable)
            {
                r.lines.Add("Media model paths:");
                // GenPresso validates media jobs asynchronously, so a probe waits for its job to fail (possibly after
                // queueing). Start all probes at once (Awaitables run as soon as they are created), await in order.
                var started = new Dictionary<string, Awaitable<ProbeResult>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in s.EnumerateRoutes())
                    if (kv.Value != null)
                        foreach (var path in kv.Value.CleanPaths())
                            if (!started.ContainsKey(path))
                                started[path] = MediaJobClient.ProbeAsync(s, path, ct);
                var finished = new Dictionary<string, ProbeResult>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in s.EnumerateRoutes())
                {
                    if (kv.Value == null)
                        continue;
                    bool resolved = false;
                    bool earlierUnknown = false; // a higher-priority candidate could not be checked (429, 5xx, network...)
                    var paths = kv.Value.CleanPaths();
                    foreach (var path in paths)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!finished.TryGetValue(path, out var probe))
                            finished[path] = probe = await started[path];
                        r.probes.Add(probe);
                        r.lines.Add($"  {kv.Key}: {probe}");
                        if (probe.Present && !resolved)
                        {
                            resolved = true;
                            // Caching a lower-priority path because the preferred one was momentarily unreachable would
                            // pin every later run to it; leave the route to be resolved by real use instead.
                            if (earlierUnknown)
                                r.lines.Add($"  {kv.Key}: not cached ('{probe.path}' answered, but a preferred candidate could not be checked)");
                            else
                                ModelPathCache.Set(s.apiBaseUrl, kv.Key, probe.path, paths);
                        }
                        else if (probe.outcome == ProbeOutcome.Error && !resolved)
                        {
                            earlierUnknown = true;
                        }
                    }
                    if (!resolved)
                        r.lines.Add($"  {kv.Key}: no GenPresso path available" +
                                    (r.hasFalKey && s.falFallbackWhenMissing && !string.IsNullOrWhiteSpace(kv.Value.falEndpoint)
                                        ? $" -> fal.ai '{kv.Value.falEndpoint}' will be used"
                                        : ""));
                }
            }

            r.ok = r.reachable && r.hasKey && r.chatOk;
            r.lines.Insert(0, r.ok ? "Connection OK." : "Connection has problems (see below).");
            return r;
        }

        static HashSet<string> ParseModelIds(string text)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text))
                return ids;
            try
            {
                var token = JToken.Parse(text);
                JArray arr = token as JArray;
                if (arr == null && token is JObject obj)
                    arr = obj["data"] as JArray ?? obj["models"] as JArray;
                if (arr == null)
                    return ids;
                foreach (var item in arr)
                {
                    if (item == null)
                        continue;
                    string id = item.Type == JTokenType.String ? item.ToString() : item["id"]?.ToString() ?? item["slug"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }
            catch (JsonException) { /* not a model list */ }
            return ids;
        }
    }
}
