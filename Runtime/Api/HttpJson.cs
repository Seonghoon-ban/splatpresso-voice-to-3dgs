using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace SplatPresso.Api
{
    /// <summary>A completed HTTP exchange (also returned for non-2xx statuses when not throwing).</summary>
    public sealed class HttpResponse
    {
        /// <summary>HTTP status; 0 for connection-level failures (DNS, refused, client timeout, abort).</summary>
        public long StatusCode { get; set; }
        /// <summary>Response body as text (null for file downloads).</summary>
        public string Text { get; set; }
        /// <summary>Response body bytes (null for file downloads).</summary>
        public byte[] Bytes { get; set; }
        /// <summary>Response headers (case-insensitive keys; never null).</summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>UnityWebRequest error text when the request did not succeed.</summary>
        public string Error { get; set; }
        /// <summary>True when UnityWebRequest reported success (2xx and no transport error).</summary>
        public bool IsSuccess { get; set; }

        /// <summary>A header value or null (case-insensitive).</summary>
        public string GetHeader(string name) =>
            Headers != null && name != null && Headers.TryGetValue(name, out var v) ? v : null;

        /// <summary>Seconds from the Retry-After header (0 when absent or unparsable).</summary>
        public float RetryAfterSec => HttpJson.ParseRetryAfter(GetHeader("Retry-After"));
    }

    /// <summary>
    /// Minimal UnityWebRequest wrapper used by every client in the package. All continuations run on the main
    /// thread. When the CancellationToken fires, the in-flight request is aborted on the main thread
    /// (UnityWebRequest is not thread-safe) and allowed to settle before OperationCanceledException is thrown.
    /// Outside play mode completion is awaited on the request itself, so editor tools can use it too.
    /// </summary>
    public static class HttpJson
    {
        /// <summary>
        /// Sends a request. With <paramref name="throwOnHttpError"/> a non-success result throws
        /// <see cref="GenpressoException"/> (kind classified from the status); otherwise the response is returned.
        /// </summary>
        public static async Awaitable<HttpResponse> SendAsync(string method, string url, byte[] body, string contentType,
            IDictionary<string, string> headers, int timeoutSec, CancellationToken ct, bool throwOnHttpError = true)
        {
            ct.ThrowIfCancellationRequested();
            HttpResponse resp;
            using (var req = new UnityWebRequest(url, method))
            {
                // UploadHandlerRaw rejects an empty payload, so body-less requests get no upload handler.
                if (body != null && body.Length > 0)
                {
                    req.uploadHandler = new UploadHandlerRaw(body);
                    if (!string.IsNullOrEmpty(contentType))
                        req.uploadHandler.contentType = contentType;
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                if (!string.IsNullOrEmpty(contentType) && body != null && body.Length > 0)
                    req.SetRequestHeader("Content-Type", contentType);
                ApplyHeaders(req, headers);
                req.timeout = Mathf.Max(1, timeoutSec);

                await SendCoreAsync(req, ct);
                resp = BuildResponse(req, true);
            }
            if (throwOnHttpError && !resp.IsSuccess)
                throw GenpressoException.FromHttp($"{method} {SanitizeUrl(url)}", resp.StatusCode, resp.Text, resp.Error, resp.RetryAfterSec);
            return resp;
        }

        /// <summary>POSTs a JSON string and returns the response text (throws on non-2xx).</summary>
        public static async Awaitable<string> PostJsonAsync(string url, string json, IDictionary<string, string> headers, int timeoutSec, CancellationToken ct)
        {
            var resp = await SendAsync(UnityWebRequest.kHttpVerbPOST, url, Encoding.UTF8.GetBytes(json ?? ""), "application/json", headers, timeoutSec, ct);
            return resp.Text;
        }

        /// <summary>GETs a URL as text (throws on non-2xx).</summary>
        public static async Awaitable<string> GetStringAsync(string url, IDictionary<string, string> headers, int timeoutSec, CancellationToken ct)
        {
            var resp = await SendAsync(UnityWebRequest.kHttpVerbGET, url, null, null, headers, timeoutSec, ct);
            return resp.Text;
        }

        /// <summary>GETs a URL as bytes (throws on non-2xx).</summary>
        public static async Awaitable<byte[]> GetBytesAsync(string url, IDictionary<string, string> headers, int timeoutSec, CancellationToken ct)
        {
            var resp = await SendAsync(UnityWebRequest.kHttpVerbGET, url, null, null, headers, timeoutSec, ct);
            return resp.Bytes;
        }

        /// <summary>
        /// Streams a URL straight to disk (no large in-memory buffer). Writes <c>destPath + ".tmp"</c> first and
        /// moves it into place afterwards, so a partially written file never masquerades as a finished download.
        /// </summary>
        public static async Awaitable DownloadFileAsync(string url, string destPath, IDictionary<string, string> headers, int timeoutSec, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string tmpPath = destPath + ".tmp";
            try
            {
                HttpResponse resp;
                using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(tmpPath) { removeFileOnAbort = true };
                    ApplyHeaders(req, headers);
                    req.timeout = Mathf.Max(1, timeoutSec);
                    await SendCoreAsync(req, ct);
                    resp = BuildResponse(req, false);
                } // dispose closes the file handle before the move

                if (!resp.IsSuccess)
                    throw GenpressoException.FromHttp($"download {SanitizeUrl(url)}", resp.StatusCode, null, resp.Error, resp.RetryAfterSec);

                if (File.Exists(destPath))
                    File.Delete(destPath);
                File.Move(tmpPath, destPath);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
                throw;
            }
        }

        /// <summary>
        /// Waits <paramref name="seconds"/> of REAL time (unaffected by Time.timeScale). In play mode it checks the
        /// token every frame; in edit mode it uses a timer whose continuation returns through Unity's
        /// synchronization context (main thread).
        /// </summary>
        public static async Awaitable DelayAsync(float seconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (seconds <= 0f)
                return;
            if (!Application.isPlaying)
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                return;
            }
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
                await Awaitable.NextFrameAsync(ct);
        }

        /// <summary>First <paramref name="maxChars"/> characters plus "..." (null/short input returned as is).</summary>
        public static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxChars)
                return s;
            return s.Substring(0, maxChars) + "...";
        }

        /// <summary>Parses a Retry-After header (delta seconds or HTTP date) into seconds, clamped to 0..300.</summary>
        public static float ParseRetryAfter(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0f;
            value = value.Trim();
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float sec))
                return Mathf.Clamp(sec, 0f, 300f);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                return Mathf.Clamp((float)(when - DateTimeOffset.UtcNow).TotalSeconds, 0f, 300f);
            return 0f;
        }

        /// <summary>URL without its query string (signed CDN URLs carry tokens that should not end up in logs).</summary>
        public static string SanitizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return "data:...";
            int q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) + "?..." : url;
        }

        // ------------------------------------------------------------------------------------------

        static void ApplyHeaders(UnityWebRequest req, IDictionary<string, string> headers)
        {
            if (headers == null)
                return;
            foreach (var kv in headers)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    req.SetRequestHeader(kv.Key, kv.Value);
        }

        static async Awaitable SendCoreAsync(UnityWebRequest req, CancellationToken ct)
        {
            UnityWebRequestAsyncOperation op;
            try
            {
                op = req.SendWebRequest();
            }
            catch (InvalidOperationException e)
            {
                // e.g. "Insecure connection not allowed" for http:// URLs under the default player setting
                string hint = req.url != null && req.url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? " (plain http is blocked by default: Project Settings > Player > Allow downloads over HTTP)"
                    : "";
                throw new GenpressoException($"Request to {SanitizeUrl(req.url)} could not start: {e.Message}{hint}",
                    GenpressoErrorKind.Network, 0, null, retryable: false, inner: e);
            }

            if (Application.isPlaying)
            {
                // Play mode: the proven per-frame loop. Abort from the main thread (UnityWebRequest is not
                // thread-safe), then let the operation settle before throwing.
                bool aborted = false;
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested && !aborted)
                    {
                        aborted = true;
                        SafeAbort(req);
                    }
                    await Awaitable.NextFrameAsync();
                }
            }
            else if (!op.isDone)
            {
                // Edit mode (editor tools, EditMode tests): there may be no frame loop, so await the request itself.
                if (ct.CanBeCanceled)
                {
                    var ctx = SynchronizationContext.Current;
                    using (ct.Register(() => AbortOnMainThread(req, ctx)))
                        await op;
                }
                else
                {
                    await op;
                }
            }

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
        }

        // Abort from the main thread (UnityWebRequest is not thread-safe). A token cancelled from another
        // thread (e.g. CancelAfter) is marshalled through Unity's synchronization context.
        static void AbortOnMainThread(UnityWebRequest req, SynchronizationContext mainCtx)
        {
            if (mainCtx == null || SynchronizationContext.Current == mainCtx)
                SafeAbort(req);
            else
                mainCtx.Post(_ => SafeAbort(req), null);
        }

        static void SafeAbort(UnityWebRequest req)
        {
            try
            {
                if (!req.isDone)
                    req.Abort();
            }
            catch { /* already completed or disposed */ }
        }

        static HttpResponse BuildResponse(UnityWebRequest req, bool readBody)
        {
            var resp = new HttpResponse
            {
                StatusCode = req.responseCode,
                IsSuccess = req.result == UnityWebRequest.Result.Success,
            };
            if (!resp.IsSuccess)
                resp.Error = string.IsNullOrEmpty(req.error) ? "unknown error" : req.error;
            try
            {
                var headers = req.GetResponseHeaders();
                if (headers != null)
                    foreach (var kv in headers)
                        resp.Headers[kv.Key] = kv.Value;
            }
            catch { /* no headers (connection error) */ }

            // DownloadHandlerFile throws on .text access; only buffer handlers carry a body.
            if (readBody && req.downloadHandler is DownloadHandlerBuffer buf)
            {
                try
                {
                    resp.Bytes = buf.data;
                    resp.Text = resp.Bytes != null ? Encoding.UTF8.GetString(resp.Bytes) : null;
                }
                catch { /* body unavailable */ }
            }
            return resp;
        }
    }
}
