using System;

namespace SplatPresso.Api
{
    /// <summary>Classification of a GenPresso / HTTP failure.</summary>
    public enum GenpressoErrorKind
    {
        Network,             // status 0: DNS, connection, client timeout
        Unauthorized,        // 401 / 403 / missing key
        InsufficientCredits, // 402: balance below the minimum (media jobs need >= 10 credits)
        NotFound,            // 404 / no model path responded
        PayloadTooLarge,     // 413 or the client-side 4 MB guard
        Validation,          // 400 / 422: the input was rejected (deterministic; never retry)
        RateLimited,         // 429 (honor RetryAfterSec)
        Server,              // 5xx
        Timeout,             // 408
        JobFailed,           // a media job ended FAILED / ERROR / EXPIRED / CANCELED
        Parse,               // unexpected or unparsable response body
        CostCap,             // reserved (the cap uses CostCapExceededException)
        Cancelled,           // reserved (cancellation uses OperationCanceledException)
        Unknown,
    }

    /// <summary>Request limits of the GenPresso API.</summary>
    public static class GenpressoLimits
    {
        /// <summary>
        /// Request bodies are capped at 4 MB by GenPresso (a 4.5 MB+ body gets a plain-text 413 from the hosting
        /// layer). Keep JSON bodies strictly below this many bytes; base64 adds ~33%.
        /// </summary>
        public const int MaxRequestBodyBytes = 4000000;

        /// <summary>Minimum balance (credits) GenPresso requires before accepting a media job.</summary>
        public const int MinMediaCredits = 10;
    }

    /// <summary>
    /// A failed GenPresso (or fal.ai / download) request. <see cref="Retryable"/> tells callers whether trying
    /// the same step again can help; deterministic failures (validation, auth, balance, a job that already
    /// completed) are not retryable, so an upper-layer retry never pays twice for the same error.
    /// </summary>
    public class GenpressoException : Exception
    {
        /// <summary>HTTP status (0 for connection-level failures or client-side errors).</summary>
        public long StatusCode { get; }
        /// <summary>Raw response body when available (may be null).</summary>
        public string ResponseBody { get; }
        public GenpressoErrorKind Kind { get; }
        public bool Retryable { get; }
        /// <summary>Seconds requested by a Retry-After header (0 when absent).</summary>
        public float RetryAfterSec { get; }

        public GenpressoException(string message, GenpressoErrorKind kind, long statusCode = 0, string responseBody = null,
            bool? retryable = null, float retryAfterSec = 0f, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
            StatusCode = statusCode;
            ResponseBody = responseBody;
            Retryable = retryable ?? IsRetryableKind(kind);
            RetryAfterSec = retryAfterSec;
        }

        /// <summary>Maps an HTTP status to an error kind.</summary>
        public static GenpressoErrorKind KindFromStatus(long status)
        {
            if (status == 0) return GenpressoErrorKind.Network;
            if (status == 401 || status == 403) return GenpressoErrorKind.Unauthorized;
            if (status == 402) return GenpressoErrorKind.InsufficientCredits;
            if (status == 404) return GenpressoErrorKind.NotFound;
            if (status == 408) return GenpressoErrorKind.Timeout;
            if (status == 413) return GenpressoErrorKind.PayloadTooLarge;
            if (status == 400 || status == 422) return GenpressoErrorKind.Validation;
            if (status == 429) return GenpressoErrorKind.RateLimited;
            if (status >= 500 && status < 600) return GenpressoErrorKind.Server;
            return GenpressoErrorKind.Unknown;
        }

        /// <summary>True for transient kinds (network, 408, 429, 5xx).</summary>
        public static bool IsRetryableKind(GenpressoErrorKind kind) =>
            kind == GenpressoErrorKind.Network || kind == GenpressoErrorKind.Timeout ||
            kind == GenpressoErrorKind.RateLimited || kind == GenpressoErrorKind.Server;

        /// <summary>True for statuses that are worth retrying with the same request (0, 408, 429, 5xx).</summary>
        public static bool IsTransientStatus(long status) =>
            status == 0 || status == 408 || status == 429 || (status >= 500 && status < 600);

        /// <summary>
        /// Builds an exception from an HTTP outcome with a readable message:
        /// "<paramref name="context"/>: HTTP 402 - ... (hint)".
        /// </summary>
        public static GenpressoException FromHttp(string context, long status, string body, string transportError,
            float retryAfterSec = 0f, bool? retryable = null)
        {
            var kind = KindFromStatus(status);
            string detail = GenpressoError.Describe(status, transportError, body);
            string hint = HintFor(kind);
            string msg = string.IsNullOrEmpty(context) ? detail : context + ": " + detail;
            if (!string.IsNullOrEmpty(hint))
                msg += " (" + hint + ")";
            return new GenpressoException(msg, kind, status, body, retryable, retryAfterSec);
        }

        static string HintFor(GenpressoErrorKind kind)
        {
            switch (kind)
            {
                case GenpressoErrorKind.Unauthorized:
                    return "check the API key in Project Settings > SplatPresso";
                case GenpressoErrorKind.InsufficientCredits:
                    return $"GenPresso balance too low: media jobs need at least {GenpressoLimits.MinMediaCredits} credits";
                case GenpressoErrorKind.PayloadTooLarge:
                    return "request body too large; GenPresso caps bodies at 4 MB";
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Thrown before a call whose estimated cost would push the run over its cost cap. Fatal: callers treat it
    /// as a hard stop and never retry it.
    /// </summary>
    public sealed class CostCapExceededException : Exception
    {
        public CostCapExceededException(string message) : base(message) { }
    }
}
