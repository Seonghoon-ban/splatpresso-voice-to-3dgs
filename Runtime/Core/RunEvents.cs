using System;

namespace SplatPresso
{
    /// <summary>Raised when a run (new request or replay) starts.</summary>
    public sealed class RunStartedInfo
    {
        public string runId;        // == session folder name, so logs join with session artifacts
        public string sessionDir;
        public PlacementRequest request;
        public GenerationMode mode;
        public ObjectRepresentation representation;
        public string sourceUtterance; // the voice transcript that produced the request, if recent
    }

    /// <summary>Raised whenever a pipeline step is retried.</summary>
    public sealed class RetryInfo
    {
        public string runId;
        public int? objectId;   // null for run-level stages
        public string stage;    // e.g. "Deciding", "Editing", "segmenting", "generating3d"
        public int attempt;     // 1-based number of the attempt that is about to start
        public Exception error; // the error that triggered the retry (may be null for policy retries)
    }

    /// <summary>Why a placement request was refused before a run started.</summary>
    public enum RequestRejectReason
    {
        Capacity,
        Gated,
        InvalidRequest,
        MissingKey,
    }
}
