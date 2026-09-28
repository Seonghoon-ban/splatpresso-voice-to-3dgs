using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SplatPresso.Api
{
    /// <summary>
    /// GenPresso chat/completions client (OpenAI-compatible) with structured output (json_schema), graceful
    /// fallback for models that reject response_format, one model fallback for unknown model slugs, and a
    /// single JSON-repair round trip when the reply fails to parse.
    /// </summary>
    public sealed class GenpressoChatClient
    {
        const int kMaxTransientRetries = 2;

        readonly string m_BaseUrl;
        readonly string m_ApiKey;
        readonly CostLedger m_Ledger; // may be null
        readonly float m_CostPerCall;

        /// <param name="baseUrl">API root, e.g. https://genpresso.ai/api/v1 (empty = default).</param>
        /// <param name="apiKey">GenPresso key (gp_...).</param>
        /// <param name="ledger">Optional cost ledger; the cap is checked before every call and the cost recorded after success.</param>
        /// <param name="costPerCall">Estimated credits per successful call.</param>
        public GenpressoChatClient(string baseUrl, string apiKey, CostLedger ledger, float costPerCall = 0.01f)
        {
            m_BaseUrl = baseUrl;
            m_ApiKey = apiKey;
            m_Ledger = ledger;
            m_CostPerCall = costPerCall;
        }

        /// <summary>The chat/completions URL this client posts to.</summary>
        public string ChatUrl => SplatPressoSettings.JoinUrl(m_BaseUrl, "chat/completions");

        /// <summary>
        /// Longest single wait honored for a 429 Retry-After (seconds). A per-minute limit asks for up to 60 s; the
        /// waits of one call are also capped by its timeoutSec. A 429 asking for longer fails right away as
        /// <see cref="GenpressoErrorKind.RateLimited"/> (with <see cref="GenpressoException.RetryAfterSec"/>).
        /// Interactive callers (voice turns) may lower it.
        /// </summary>
        public float MaxRateLimitWaitSec { get; set; } = 60f;

        sealed class CallState
        {
            public string model;
            public string fallbackModel;
            public bool fallbackUsed;
            public JObject schema;
            public string schemaName;
            public bool schemaDropped;
        }

        /// <summary>
        /// Sends system prompt + history + a user message built from <paramref name="userParts"/> and
        /// deserializes the reply into <typeparamref name="T"/> (via <see cref="JsonUtil"/>).
        /// </summary>
        /// <exception cref="GenpressoException">HTTP / parse failures (see <see cref="GenpressoException.Retryable"/>).</exception>
        /// <exception cref="CostCapExceededException">The call would exceed the ledger cap.</exception>
        public async Awaitable<T> CompleteJsonAsync<T>(string model, string systemPrompt, IList<ChatMessage> history,
            IList<ChatContentPart> userParts, JObject schema, string schemaName, int timeoutSec, CancellationToken ct,
            string fallbackModel = null) where T : class
        {
            var messages = BuildMessages(systemPrompt, history, userParts);
            var st = new CallState
            {
                model = model,
                fallbackModel = fallbackModel,
                schema = schema,
                schemaName = string.IsNullOrWhiteSpace(schemaName) ? "result" : schemaName,
            };

            string raw = await SendWithFallbacksAsync(st, messages, timeoutSec, ct);
            try
            {
                return DeserializeOrThrow<T>(StripCodeFences(raw));
            }
            catch (JsonException firstError)
            {
                // One repair round trip: show the model its own reply and the parse error.
                Debug.LogWarning($"[SplatPresso] {st.model} returned invalid JSON ({firstError.Message}); attempting one repair round trip");
                messages.Add(new JObject { ["role"] = "assistant", ["content"] = raw });
                messages.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = $"Your previous reply was not valid JSON ({firstError.Message}). Reply with ONLY the JSON object, no prose, no code fences.",
                });
                string repaired = await SendWithFallbacksAsync(st, messages, timeoutSec, ct);
                try
                {
                    return DeserializeOrThrow<T>(StripCodeFences(repaired));
                }
                catch (JsonException secondError)
                {
                    throw new GenpressoException(
                        $"{st.model} did not return valid JSON for {typeof(T).Name} after a repair round trip: {secondError.Message}. " +
                        $"Reply: {GenpressoError.Truncate(repaired, 300)}",
                        GenpressoErrorKind.Parse, 0, repaired, retryable: true, inner: secondError);
                }
            }
        }

        /// <summary>Plain-text completion (no schema). Returns the reply with code fences / wrapping quotes removed.</summary>
        public async Awaitable<string> CompleteTextAsync(string model, string systemPrompt, IList<ChatMessage> history,
            IList<ChatContentPart> userParts, int timeoutSec, CancellationToken ct)
        {
            var messages = BuildMessages(systemPrompt, history, userParts);
            var st = new CallState { model = model };
            string raw = await SendWithFallbacksAsync(st, messages, timeoutSec, ct);
            return CleanText(raw);
        }

        /// <summary>Trims, strips a surrounding code fence and wrapping quotes; optionally caps the length.</summary>
        public static string CleanText(string content, int maxChars = 0)
        {
            if (string.IsNullOrEmpty(content))
                return content;
            string s = StripCodeFences(content);
            s = s.Trim().Trim('"', '\u201C', '\u201D').Trim();
            if (maxChars > 0 && s.Length > maxChars)
                s = s.Substring(0, maxChars);
            return s;
        }

        /// <summary>Removes a leading ```lang line and the trailing ``` (models sometimes fence JSON).</summary>
        public static string StripCodeFences(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            s = s.Trim();
            if (!s.StartsWith("```", StringComparison.Ordinal))
                return s;
            int firstNewline = s.IndexOf('\n');
            if (firstNewline < 0)
                return s.Trim('`').Trim();
            s = s.Substring(firstNewline + 1); // drop "```json" (or "```") line
            int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
                s = s.Substring(0, lastFence);
            return s.Trim();
        }

        // ------------------------------------------------------------------------------------------

        static JArray BuildMessages(string systemPrompt, IList<ChatMessage> history, IList<ChatContentPart> userParts)
        {
            var messages = new JArray();
            if (!string.IsNullOrEmpty(systemPrompt))
                messages.Add(new JObject { ["role"] = "system", ["content"] = systemPrompt });
            if (history != null)
                foreach (var m in history)
                    if (m != null)
                        messages.Add(m.ToJson());
            if (userParts != null && userParts.Count > 0)
            {
                var content = new JArray();
                foreach (var part in userParts)
                    if (part != null)
                        content.Add(part.ToJson());
                messages.Add(new JObject { ["role"] = "user", ["content"] = content });
            }
            return messages;
        }

        async Awaitable<string> SendWithFallbacksAsync(CallState st, JArray messages, int timeoutSec, CancellationToken ct)
        {
            while (true)
            {
                try
                {
                    return await PostChatAsync(st.model, messages, st.schemaDropped ? null : st.schema, st.schemaName, timeoutSec, ct);
                }
                // Some models/providers reject response_format (400/404, or a FastAPI-style 422): retry without it.
                catch (GenpressoException e) when (st.schema != null && !st.schemaDropped &&
                                                   (e.StatusCode == 400 || e.StatusCode == 404 || e.StatusCode == 422) &&
                                                   MentionsResponseFormat(e.ResponseBody))
                {
                    Debug.LogWarning($"[SplatPresso] {st.model} rejected response_format; retrying without the JSON schema");
                    st.schemaDropped = true;
                }
                // Unknown / retired model slug: retry once with the fallback model.
                catch (GenpressoException e) when (!st.fallbackUsed && !string.IsNullOrWhiteSpace(st.fallbackModel) &&
                                                   !string.Equals(st.fallbackModel, st.model, StringComparison.Ordinal) &&
                                                   (e.StatusCode == 400 || e.StatusCode == 404) &&
                                                   MentionsModel(e.ResponseBody))
                {
                    Debug.LogWarning($"[SplatPresso] Chat model '{st.model}' was rejected ({GenpressoError.Describe(e.StatusCode, null, e.ResponseBody)}); retrying once with '{st.fallbackModel}'");
                    st.fallbackUsed = true;
                    st.model = st.fallbackModel;
                }
            }
        }

        async Awaitable<string> PostChatAsync(string model, JArray messages, JObject schema, string schemaName, int timeoutSec, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(m_ApiKey))
                throw new GenpressoException("GenPresso API key is missing. Set it in Project Settings > SplatPresso or the GENPRESSO_API_KEY environment variable.",
                    GenpressoErrorKind.Unauthorized, 0, null, retryable: false);

            if (m_Ledger != null && !m_Ledger.CanSpend(m_CostPerCall))
                throw new CostCapExceededException(
                    $"Cost cap would be exceeded by a chat call to {model} (~{m_CostPerCall:F2} credits; spent {m_Ledger.TotalCost:F2} of {m_Ledger.capCost:F2})");

            var body = new JObject
            {
                ["model"] = model,
                ["messages"] = messages, // Json.NET clones tokens that already have a parent, so re-posting is safe
            };
            if (schema != null)
            {
                body["response_format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JObject
                    {
                        ["name"] = schemaName ?? "result",
                        ["strict"] = true,
                        ["schema"] = schema,
                    },
                };
            }

            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            if (bytes.Length >= GenpressoLimits.MaxRequestBodyBytes)
                throw new GenpressoException(
                    $"Chat request to {model} is {bytes.Length / 1024} KB; GenPresso caps request bodies at 4 MB. Send smaller images/audio.",
                    GenpressoErrorKind.PayloadTooLarge, 0, null, retryable: false);

            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + m_ApiKey.Trim() } };
            HttpResponse resp;
            float rateLimitWaited = 0f;
            for (int attempt = 0; ; attempt++)
            {
                resp = await HttpJson.SendAsync("POST", ChatUrl, bytes, "application/json", headers, timeoutSec, ct, throwOnHttpError: false);
                if (resp.IsSuccess)
                    break;
                // Per-minute limits (429) and gateway hiccups (502/503/504) are worth a wait; the request was not
                // processed, so nothing is billed twice. Other failures go straight to the caller.
                long code = resp.StatusCode;
                bool transient = code == 429 || code == 502 || code == 503 || code == 504;
                if (!transient || attempt >= kMaxTransientRetries)
                    throw GenpressoException.FromHttp($"GenPresso chat ({model})", code, resp.Text, resp.Error, resp.RetryAfterSec);
                float delay;
                if (code == 429)
                {
                    // Honor the server's Retry-After (retrying sooner only burns attempts), within this call's budget.
                    delay = resp.RetryAfterSec > 0f ? resp.RetryAfterSec : 5f * (attempt + 1);
                    float allowed = Mathf.Min(Mathf.Max(0f, MaxRateLimitWaitSec), Mathf.Max(10f, timeoutSec) - rateLimitWaited);
                    if (delay > allowed)
                        throw GenpressoException.FromHttp($"GenPresso chat ({model}) is rate limited (asked to wait {delay:F0}s)",
                            code, resp.Text, resp.Error, resp.RetryAfterSec);
                    rateLimitWaited += delay;
                }
                else
                {
                    delay = resp.RetryAfterSec > 0f ? Mathf.Min(resp.RetryAfterSec, 10f) : 1.5f * (attempt + 1);
                }
                Debug.LogWarning($"[SplatPresso] GenPresso chat ({model}) returned HTTP {code}; retrying in {delay:F1}s");
                await HttpJson.DelayAsync(delay, ct);
            }

            JObject root;
            try
            {
                root = JObject.Parse(resp.Text ?? "");
            }
            catch (JsonException e)
            {
                throw new GenpressoException($"GenPresso chat ({model}) returned a non-JSON body: {GenpressoError.Truncate(resp.Text, 300)}",
                    GenpressoErrorKind.Parse, resp.StatusCode, resp.Text, retryable: true, inner: e);
            }

            // An error object can arrive with HTTP 200 (upstream provider errors).
            var err = root["error"];
            if (err != null && err.Type != JTokenType.Null)
            {
                long code = 0;
                if (err.Type == JTokenType.Object && err["code"] != null &&
                    (err["code"].Type == JTokenType.Integer || err["code"].Type == JTokenType.String))
                    long.TryParse(err["code"].ToString(), out code);
                var kind = code > 0 ? GenpressoException.KindFromStatus(code) : GenpressoErrorKind.Unknown;
                throw new GenpressoException($"GenPresso chat ({model}) error: {GenpressoError.Describe(0, null, resp.Text)}",
                    kind, code > 0 ? code : resp.StatusCode, resp.Text);
            }

            m_Ledger?.Record("chat:" + model, m_CostPerCall);

            JToken message = root["choices"] is JArray choices && choices.Count > 0 && choices[0] is JObject first ? first["message"] : null;
            string content = ExtractContent(message is JObject msg ? msg["content"] : null);
            if (string.IsNullOrEmpty(content))
                throw new GenpressoException($"GenPresso chat ({model}) response has no message content: {GenpressoError.Truncate(resp.Text, 500)}",
                    GenpressoErrorKind.Parse, resp.StatusCode, resp.Text, retryable: true);
            return content;
        }

        static T DeserializeOrThrow<T>(string json) where T : class
        {
            var result = JsonUtil.Deserialize<T>(json);
            if (result == null)
                throw new JsonSerializationException($"Deserialized {typeof(T).Name} is null");
            return result;
        }

        // Some models return content as an array of typed parts instead of a plain string.
        static string ExtractContent(JToken token)
        {
            if (token == null)
                return null;
            if (token.Type == JTokenType.String)
                return (string)token;
            if (token is JArray arr)
            {
                var sb = new StringBuilder();
                foreach (var item in arr)
                {
                    if (item == null)
                        continue;
                    if (item.Type == JTokenType.String)
                        sb.Append((string)item);
                    else if (item.Type == JTokenType.Object && item["text"] != null)
                        sb.Append((string)item["text"]);
                }
                return sb.ToString();
            }
            return null;
        }

        static bool MentionsResponseFormat(string body) =>
            !string.IsNullOrEmpty(body) &&
            (body.IndexOf("response_format", StringComparison.OrdinalIgnoreCase) >= 0 ||
             body.IndexOf("json_schema", StringComparison.OrdinalIgnoreCase) >= 0 ||
             body.IndexOf("structured output", StringComparison.OrdinalIgnoreCase) >= 0);

        static bool MentionsModel(string body) =>
            !string.IsNullOrEmpty(body) && body.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
