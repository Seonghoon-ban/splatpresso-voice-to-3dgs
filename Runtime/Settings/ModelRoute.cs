using System;
using System.Collections.Generic;

namespace SplatPresso
{
    /// <summary>
    /// Where one pipeline capability (edit, segment, image-to-splat, ...) runs.
    /// </summary>
    /// <remarks>
    /// GenPresso re-hosts fal.ai models: a fal id starting with <c>fal-ai/</c> becomes <c>gp/...</c>, other
    /// owners (<c>google/</c>, <c>tripo3d/</c>, ...) pass through unchanged, and the raw <c>fal-ai/</c> form
    /// returns 404. Because not every path is confirmed, each route lists ordered candidates: a 404 at submit
    /// is not queued or billed, so the client falls through to the next candidate and caches the first one
    /// that exists (see <c>ModelPathCache</c>).
    /// </remarks>
    [Serializable]
    public sealed class ModelRoute
    {
        /// <summary>Ordered GenPresso media paths to try (first that exists wins).</summary>
        public string[] genpressoPaths;
        /// <summary>fal.ai endpoint id used with MediaProvider.FalDirect or as the fal fallback.</summary>
        public string falEndpoint;
        /// <summary>Estimated cost per call in GenPresso credits (for the per-run cost cap).</summary>
        public float estimatedCost;
        /// <summary>Whole-job budget in seconds (submit through result).</summary>
        public int timeoutSec;

        public ModelRoute()
        {
            genpressoPaths = Array.Empty<string>();
            falEndpoint = "";
            estimatedCost = 0f;
            timeoutSec = 120;
        }

        public ModelRoute(string[] genpressoPaths, string falEndpoint, float estimatedCost, int timeoutSec)
        {
            this.genpressoPaths = genpressoPaths ?? Array.Empty<string>();
            this.falEndpoint = falEndpoint ?? "";
            this.estimatedCost = estimatedCost;
            this.timeoutSec = timeoutSec;
        }

        /// <summary>Deep copy.</summary>
        public ModelRoute Clone() => new ModelRoute(
            genpressoPaths != null ? (string[])genpressoPaths.Clone() : Array.Empty<string>(),
            falEndpoint, estimatedCost, timeoutSec);

        /// <summary>Non-empty, trimmed, de-duplicated candidate paths in order.</summary>
        public List<string> CleanPaths()
        {
            var list = new List<string>();
            if (genpressoPaths == null)
                return list;
            foreach (var p in genpressoPaths)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                string clean = p.Trim().Trim('/');
                bool dup = false;
                foreach (var existing in list)
                    if (string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup)
                    list.Add(clean);
            }
            return list;
        }

        public override string ToString() =>
            $"[{string.Join(", ", genpressoPaths ?? Array.Empty<string>())}] fal:{falEndpoint} ~{estimatedCost} cr, {timeoutSec}s";
    }

    /// <summary>Route keys (also the <c>ModelPathCache</c> keys) of the media capabilities.</summary>
    public static class MediaRouteKeys
    {
        public const string Edit = "edit";
        public const string EditFallback = "editFallback";
        public const string Enhance = "enhance";
        public const string TextToImage = "textToImage";
        public const string Segment = "segment";
        public const string RemoveBackground = "removeBackground";
        public const string Depth = "depth";
        public const string ImageToSplat = "imageToSplat";
        public const string ImageToMesh = "imageToMesh";
        public const string TextToMesh = "textToMesh";
        /// <summary>Spoken replies of the GenPresso chat voice agent (not a pipeline step).</summary>
        public const string TextToSpeech = "textToSpeech";

        /// <summary>Every key, in pipeline order (text-to-speech last).</summary>
        public static readonly string[] All =
        {
            Edit, EditFallback, Enhance, TextToImage, Segment, RemoveBackground, Depth, ImageToSplat, ImageToMesh, TextToMesh,
            TextToSpeech,
        };
    }
}
