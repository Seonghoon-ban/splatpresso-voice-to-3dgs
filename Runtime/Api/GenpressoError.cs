using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SplatPresso.Api
{
    /// <summary>
    /// Turns a GenPresso error body into one readable line. Two shapes show up: OpenAI-style
    /// <c>{"error":{message,code,type}}</c> and FastAPI-style <c>{"detail":[{loc,msg,...}]}</c> (or a string
    /// detail) for request-validation failures. Non-JSON bodies (e.g. an HTML 404 page or the hosting layer's
    /// plain-text 413) are shown truncated.
    /// </summary>
    public static class GenpressoError
    {
        /// <summary>"HTTP 422 - image_url: field required" style description of a failed response.</summary>
        public static string Describe(long statusCode, string transportError, string body)
        {
            string detail = Parse(body);
            string prefix = statusCode > 0 ? "HTTP " + statusCode : transportError;

            if (!string.IsNullOrEmpty(detail))
                return string.IsNullOrEmpty(prefix) ? detail : prefix + " - " + detail;

            string raw = IsHtml(body) ? "(HTML error page)" : Truncate(body, 300);
            if (!string.IsNullOrEmpty(transportError) && statusCode > 0)
                return (prefix + " " + transportError + " " + raw).TrimEnd();

            return ((prefix ?? "request failed") + " " + raw).TrimEnd();
        }

        /// <summary>The human-readable error inside a JSON body, or null when there is none.</summary>
        public static string Parse(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;
            string trimmed = body.TrimStart();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
                return null;
            try
            {
                return ParseToken(JObject.Parse(body));
            }
            catch
            {
                // Not JSON: the caller shows the raw body.
                return null;
            }
        }

        /// <summary>The human-readable error inside an already-parsed JSON object, or null.</summary>
        public static string ParseToken(JObject json)
        {
            if (json == null)
                return null;

            var error = json["error"];
            if (error != null && error.Type == JTokenType.Object)
            {
                string message = error["message"]?.ToString();
                string code = error["code"]?.ToString();
                if (!string.IsNullOrEmpty(message))
                    return string.IsNullOrEmpty(code) ? message : $"{message} ({code})";
            }
            else if (error != null && error.Type == JTokenType.String)
            {
                string message = error.ToString();
                if (!string.IsNullOrEmpty(message))
                    return message;
            }

            // FastAPI validation errors: which field was wrong and why.
            if (json["detail"] is JArray details && details.Count > 0)
            {
                var parts = new List<string>();
                foreach (var item in details)
                {
                    if (item == null)
                        continue;
                    if (item.Type != JTokenType.Object)
                    {
                        parts.Add(item.ToString());
                        continue;
                    }
                    string field = item["loc"] is JArray loc ? string.Join(".", loc) : null;
                    string message = item["msg"]?.ToString();
                    parts.Add(string.IsNullOrEmpty(field) ? message : $"{field}: {message}");
                }
                return string.Join("; ", parts);
            }

            var detail = json["detail"];
            if (detail != null && detail.Type != JTokenType.Null)
                return detail.ToString();

            string msg = json["message"]?.Type == JTokenType.String ? json["message"].ToString() : null;
            return string.IsNullOrEmpty(msg) ? null : msg;
        }

        /// <summary>
        /// True for the result body GenPresso returns for a short window after the status flips to COMPLETED:
        /// HTTP 200 with <c>{"detail":"Request is still in progress"}</c>. Treat it as "keep polling".
        /// </summary>
        public static bool IsStillInProgress(JObject json)
        {
            if (json == null)
                return false;
            var detail = json["detail"];
            if (detail == null || detail.Type != JTokenType.String)
                return false;
            string s = detail.ToString();
            return s.IndexOf("in progress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("in_progress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("in queue", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when a body looks like an HTML page (e.g. the web app's 404 page).</summary>
        public static bool IsHtml(string body)
        {
            if (string.IsNullOrEmpty(body))
                return false;
            string t = body.TrimStart();
            return t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>First <paramref name="max"/> characters plus "..." (empty string for null).</summary>
        public static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
