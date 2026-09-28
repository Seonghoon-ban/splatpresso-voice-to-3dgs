using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace SplatPresso
{
    // ------------------------------------------------------------------------------------------
    // Pipeline stages / state machine

    /// <summary>Run-level pipeline stage. Per-object steps are reported as sub-stage strings instead.</summary>
    public enum PlacementStage
    {
        Idle,
        Capturing,
        Deciding,
        Editing,
        Verifying,
        DepthEstimating,
        ProcessingObjects, // per-object: segmenting -> cutout -> enhancing -> generating3d -> downloading (parallel)
        Placing,
        Completed,
        Failed,
        Cancelled,
    }

    /// <summary>How each object's source image is produced.</summary>
    /// <remarks>
    /// SceneContextual edits the captured view (objects match the scene's lighting/style), verifies and
    /// segments it. DirectTextTo3D uses the capture ONLY to decide placement; the object image is generated
    /// from the text alone.
    /// </remarks>
    public enum GenerationMode
    {
        SceneContextual,
        DirectTextTo3D,
    }

    /// <summary>What kind of 3D asset a run generates.</summary>
    public enum ObjectRepresentation
    {
        GaussianSplat = 0,
        Mesh = 1,
    }

    /// <summary>
    /// Replay entry points: run the pipeline starting from a given stage, loading all earlier stage artifacts
    /// from a previous session directory.
    /// </summary>
    public enum StartStage
    {
        Capture,
        Decide,
        Edit,
        Verify,
        ProcessObjects,
        Place,
    }

    /// <summary>How a run ended (replaces the brittle "cancelled" reason-string sentinel).</summary>
    public enum PlacementEndKind
    {
        Completed,
        Cancelled,
        CostCapExceeded,
        StageFailed,
        Error,
    }

    // ------------------------------------------------------------------------------------------
    // Request (voice-agent tool payload / scripting entry point). All text should be English.

    /// <summary>A user's placement request: what to add and where.</summary>
    [Serializable]
    public class PlacementRequest
    {
        [JsonProperty("intent_summary")] public string intentSummary;
        [JsonProperty("objects")] public List<RequestedObject> objects = new List<RequestedObject>();
        [JsonProperty("placement_hint")] public string placementHint;

        /// <summary>True when the request has at least one object with a non-empty name.</summary>
        public bool IsValid(out string reason)
        {
            if (objects == null || objects.Count == 0)
            {
                reason = "the request contains no objects";
                return false;
            }
            foreach (var o in objects)
            {
                if (o == null || string.IsNullOrWhiteSpace(o.name))
                {
                    reason = "an object in the request has no name";
                    return false;
                }
            }
            reason = null;
            return true;
        }
    }

    /// <summary>One requested object. The DECIDE model expands <see cref="count"/> into separate objects.</summary>
    [Serializable]
    public class RequestedObject
    {
        [JsonProperty("name")] public string name;
        [JsonProperty("description")] public string description;
        [JsonProperty("count")] public int count = 1;
    }

    // ------------------------------------------------------------------------------------------
    // Normalized bounding box. Canonical storage: x,y = top-left corner, w,h = size, all in 0..1 normalized
    // image coordinates (y down, origin top-left). All format conversions for external APIs go through this
    // struct so a format change is a one-line fix.

    /// <summary>Normalized top-left-origin bounding box (x, y, w, h in 0..1, y down).</summary>
    [Serializable]
    public struct Bbox
    {
        public float x, y, w, h; // top-left + size, normalized 0..1

        public Bbox(float x, float y, float w, float h) { this.x = x; this.y = y; this.w = w; this.h = h; }

        public float centerX => x + w * 0.5f;
        public float centerY => y + h * 0.5f;

        /// <summary>From [x, y, w, h] normalized (strict: no rescaling).</summary>
        public static Bbox FromXYWHNorm(IReadOnlyList<float> a) => new Bbox(a[0], a[1], a[2], a[3]);
        public float[] ToXYWHNorm() => new[] { x, y, w, h };

        /// <summary>
        /// From [x, y, w, h] that SHOULD be normalized, tolerating 0-1000-scale components.
        /// </summary>
        /// <remarks>
        /// Gemini sometimes returns 0-1000-scale coordinates (the box_2d convention) instead of 0..1, and
        /// sometimes mixes the scales per component (observed in session logs: [304,330,272,542] and
        /// [0.407,508,0.147,0.187]). Only components greater than 1.5 are divided by 1000, so properly
        /// normalized values (&lt;= 1) are untouched. Before this fix Clamp01 collapsed w/h to 0, so scale
        /// always fell back to size_hint_m and the SAM-3 box was corrupted.
        /// </remarks>
        public static Bbox FromXYWHNormLenient(IReadOnlyList<float> a) =>
            new Bbox(Norm01(a[0]), Norm01(a[1]), Norm01(a[2]), Norm01(a[3]));

        static float Norm01(float v) => v > 1.5f ? v / 1000f : v;

        public static Bbox FromCxCyWHNorm(IReadOnlyList<float> a) => new Bbox(a[0] - a[2] * 0.5f, a[1] - a[3] * 0.5f, a[2], a[3]);
        public float[] ToCxCyWHNorm() => new[] { centerX, centerY, w, h };

        public static Bbox FromXyXyNorm(float x0, float y0, float x1, float y1) => new Bbox(x0, y0, x1 - x0, y1 - y0);
        public float[] ToXyXyNorm() => new[] { x, y, x + w, y + h };

        /// <summary>Pixel-space [xMin, yMin, xMax, yMax], rounded (SAM-3 box prompts need integers).</summary>
        public int[] ToXyXyPixels(int imgW, int imgH) => new[]
        {
            Mathf.RoundToInt(x * imgW), Mathf.RoundToInt(y * imgH),
            Mathf.RoundToInt((x + w) * imgW), Mathf.RoundToInt((y + h) * imgH),
        };

        /// <summary>Clips the box to the unit square.</summary>
        public Bbox Clamp01()
        {
            float x0 = Mathf.Clamp01(x), y0 = Mathf.Clamp01(y);
            float x1 = Mathf.Clamp01(x + w), y1 = Mathf.Clamp01(y + h);
            return new Bbox(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }

        /// <summary>Expands by a fraction of the box size on each side (0.15 = +15% padding), clamped.</summary>
        public Bbox Expand(float fraction)
        {
            float dx = w * fraction, dy = h * fraction;
            return new Bbox(x - dx, y - dy, w + dx * 2, h + dy * 2).Clamp01();
        }

        public bool IsValid => w > 0.001f && h > 0.001f;

        public override string ToString() => $"[x:{x:F3} y:{y:F3} w:{w:F3} h:{h:F3}]";
    }

    // ------------------------------------------------------------------------------------------
    // DECIDE result (language model plans where each object goes in the captured view)

    /// <summary>Result of the DECIDE call. JSON names match the strict schema.</summary>
    [Serializable]
    public class DecisionResult
    {
        [JsonProperty("scene_summary")] public string sceneSummary;
        [JsonProperty("feasible")] public bool feasible;
        [JsonProperty("infeasible_reason")] public string infeasibleReason;
        [JsonProperty("objects")] public List<DecidedObject> objects = new List<DecidedObject>();
        [JsonProperty("edit_prompt")] public string editPrompt;
    }

    /// <summary>One planned object of a <see cref="DecisionResult"/>.</summary>
    [Serializable]
    public class DecidedObject
    {
        [JsonProperty("id")] public int id;
        [JsonProperty("name")] public string name;
        [JsonProperty("description_for_image_edit")] public string descriptionForImageEdit;
        [JsonProperty("description_for_segmentation")] public string descriptionForSegmentation;
        [JsonProperty("target_bbox_norm")] public float[] targetBboxNorm; // [x,y,w,h] top-left origin (raw, may be 0-1000)
        [JsonProperty("size_hint_m")] public float sizeHintM;              // largest real-world dimension in meters
        [JsonProperty("resting_surface")] public string restingSurface;    // ground | table | wall | other
        [JsonProperty("against_wall")] public string againstWall;          // yes | no (null in sessions before 0.4 or with askVlmForOrientation off)

        /// <summary>Normalized target box (lenient: tolerates 0-1000 components). Always read the box through this.</summary>
        [JsonIgnore] public Bbox TargetBbox => targetBboxNorm != null && targetBboxNorm.Length == 4 ? Bbox.FromXYWHNormLenient(targetBboxNorm) : default;
    }

    // ------------------------------------------------------------------------------------------
    // VERIFY result (bboxes on the EDITED image; ground truth for segmentation and placement)

    /// <summary>Result of the VERIFY call. JSON names match the strict schema.</summary>
    [Serializable]
    public class VerificationResult
    {
        [JsonProperty("camera_unchanged")] public bool cameraUnchanged;
        [JsonProperty("unexpected_changes")] public string unexpectedChanges;
        [JsonProperty("objects")] public List<VerifiedObject> objects = new List<VerifiedObject>();
    }

    /// <summary>One object of a <see cref="VerificationResult"/>.</summary>
    [Serializable]
    public class VerifiedObject
    {
        [JsonProperty("id")] public int id;
        [JsonProperty("name")] public string name;
        [JsonProperty("found")] public bool found;
        [JsonProperty("bbox_norm")] public float[] bboxNorm; // [x,y,w,h] top-left origin (raw, may be 0-1000)
        [JsonProperty("fully_visible")] public bool fullyVisible;
        // Orientation hints (null in sessions before 0.4, with askVlmForOrientation off, or on verify-fallback)
        [JsonProperty("support")] public string support;                   // floor | table_or_furniture | wall_mounted | other
        [JsonProperty("back_against_wall")] public string backAgainstWall; // yes | no | unsure
        [JsonProperty("front_faces")] public string frontFaces;            // toward_viewer | ... | no_clear_front (logged only)
        [JsonProperty("notes")] public string notes;

        /// <summary>Normalized box on the edited image (lenient). Always read the box through this.</summary>
        [JsonIgnore] public Bbox Bbox => bboxNorm != null && bboxNorm.Length == 4 ? Bbox.FromXYWHNormLenient(bboxNorm) : default;
    }

    // ------------------------------------------------------------------------------------------
    // Per-object pipeline outcome

    /// <summary>Per-object status. Serialized as an integer: never reorder.</summary>
    public enum ObjectStatus
    {
        Pending = 0,
        Placed = 1,  // final: spawned into the scene
        Ready = 2,   // model downloaded, waiting for placement
        Skipped = 3, // failed somewhere; siblings continue
    }

    /// <summary>Everything the pipeline knows about one generated object (saved as objects/&lt;id&gt;/object.json).</summary>
    [Serializable]
    public class PlacedObjectResult
    {
        public int id;
        public string name;
        public string descriptionForSegmentation;
        public string descriptionForEdit;   // look description (color/material/style); Direct mode: the user's own words
        public float[] bboxGeneratedNorm;   // [x,y,w,h]: verified box on the edited image (Direct mode: decided box on the capture)
        public float sizeHintM;
        public string restingSurface;
        public string cutoutPath;           // local path of the single-object cutout PNG
        public string cutoutUrl;            // hosted URL of the cutout (replay without re-upload; may expire)
        public string enhancedPath;         // local path of the enhanced (re-rendered) object image
        public string enhancedUrl;          // hosted URL of the enhanced image
        public string modelPath;            // local path of the downloaded model (model.ply or model.glb)
        public ObjectRepresentation representation;
        public string sourceImagePath;      // the image actually fed to the 3D model
        public string sourceImageUrl;       // its hosted URL (or null when a data URI was sent)
        public ObjectStatus status = ObjectStatus.Pending;
        public string skipReason;
        public float segScore;
        // Orientation hints from DECIDE (againstWall) and VERIFY (the rest); null when the models were not asked.
        public string againstWall;
        public string support;
        public string backAgainstWall;
        public string frontFaces;
        /// <summary>Final pose and how its yaw was chosen (set when the object is placed; persisted in result.json).</summary>
        public PlacementRecord placement;

        /// <summary>Run id (session folder name); not persisted.</summary>
        [JsonIgnore] public string runId;

        /// <summary>Normalized placement box (lenient). Always read the box through this.</summary>
        [JsonIgnore] public Bbox BboxGenerated => bboxGeneratedNorm != null && bboxGeneratedNorm.Length == 4 ? Bbox.FromXYWHNormLenient(bboxGeneratedNorm) : default;
    }

    /// <summary>
    /// The placed pose of one object and how its yaw was chosen (camera-facing or a wall found in the capture depth), for
    /// inspection, gizmos and offline comparison. Written by <see cref="Placement.SceneOrientation.ToRecord"/>; never
    /// contains NaN. Angles are world yaw in degrees (atan2(x, z)); "front" is the direction the model's front faces.
    /// </summary>
    [Serializable]
    public class PlacementRecord
    {
        public int version = 1;
        /// <summary><see cref="OrientationMode"/> name (CameraFacing, Shadow, SceneAware).</summary>
        public string mode;
        /// <summary>Applied yaw rule: CameraFacing or Wall.</summary>
        public string rule;
        /// <summary>Wall intent: Unknown, FreeStanding, Backed or Mounted.</summary>
        public string intent;
        /// <summary>Which hint decided the intent (e.g. verify.support, decide.against_wall, category:bookshelf).</summary>
        public string intentSource;
        public float cameraYawDeg;
        public float wallYawDeg;
        public float appliedFrontYawDeg;
        public float rotationYawDeg;
        public float yawOffsetDeg;
        public bool isMesh;
        public Vector3 position;
        public Vector3 legacyPosition;
        public float uniformScale;
        public bool flushApplied;
        public string reanchor;
        public float shiftM;
        public float standoffM;
        public bool standoffClamped;
        public bool wallFound;
        public string wallReject;
        public Vector3 wallNormal;
        public Vector3 wallPoint;
        public float wallYMin;
        public float wallYMax;
        public int points;
        public int inliers;
        public float tau;
        public float frac;
        public float ext;
        public float ySpan;
        public float behind;
        public float splitDeg;
        public float anchorDistM;
        public float centerErrM;
        public bool corner;
        public bool hasVlmFrontYaw;
        public float vlmFrontYawDeg;
        public float confidence;
        public float elapsedMs;
        public string note;
    }

    // ------------------------------------------------------------------------------------------
    // Progress / result / failure events

    /// <summary>Run progress tick. Messages are English; the voice model translates.</summary>
    public class PlacementProgress
    {
        public string runId;
        public PlacementStage stage;
        public string message;
        public int objectsDone;
        public int objectsTotal;
        public double costSoFar;  // estimated credits spent by this run
        public bool narrate;      // true = milestone worth relaying to the user by voice
    }

    /// <summary>Outcome of a run (saved as result.json; the capture is stored separately).</summary>
    [Serializable]
    public class PlacementResult
    {
        public string runId;
        public string sessionDir;
        [JsonIgnore] public CaptureResult capture; // serialized separately (capture_meta.json + bin)
        public string editedImagePath;
        public string editedImageUrl;      // hosted URL; downstream calls reuse it (may expire)
        public string generatedDepthPath;  // relative depth PNG of the edited image (may be null)
        public List<PlacedObjectResult> objects = new List<PlacedObjectResult>();
    }

    /// <summary>Why and where a run ended without completing.</summary>
    public class PlacementFailure
    {
        public string runId;
        public PlacementStage stage;
        public PlacementEndKind kind;
        public string reason;
        public Exception exception;
    }
}
