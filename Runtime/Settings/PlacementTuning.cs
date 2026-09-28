using System;
using UnityEngine;

namespace SplatPresso
{
    /// <summary>How a placed object's yaw is chosen (<see cref="PlacementTuning.orientationMode"/>).</summary>
    public enum OrientationMode
    {
        /// <summary>Every object faces the capture camera (the behaviour before 0.4; bit-identical).</summary>
        CameraFacing = 0,
        /// <summary>The wall rule is computed and logged, but the camera-facing pose is applied.</summary>
        Shadow = 1,
        /// <summary>Wall-hung and wall-backed objects turn parallel to a wall found in the capture depth and move flush
        /// to it; everything else (and every failed check) faces the camera as before.</summary>
        SceneAware = 2,
    }

    /// <summary>
    /// Tunables for converting generated models into scene-space placements. The splat defaults are the values
    /// calibrated for TripoSplat output (baked from the debug nudge controller), not the older code defaults.
    /// </summary>
    [Serializable]
    public sealed class PlacementTuning
    {
        [Header("Splat content transform (TripoSplat -> Unity, 3DGS convention)")]
        [Tooltip("Local rotation of the splat content under the placed root.")]
        public Vector3 contentRotationEuler = new Vector3(180f, 0f, 0f);
        [Tooltip("Local scale of the splat content under the placed root.")]
        public Vector3 contentScale = new Vector3(1f, 1f, -1f);
        [Tooltip("Extra uniform scale factor applied after fitting the object to its measured size.")]
        public float uniformScaleFactor = 0.9f;
        [Tooltip("Yaw added so the generated object's front (the enhance prompt asks for a front view) faces the camera.")]
        public float yawOffsetDeg = 270f;

        [Header("Placement solve")]
        [Tooltip("Fallback object height (meters) when depth/bbox are unusable.")]
        public float defaultObjectSizeM = 0.5f;
        [Tooltip("Placement distance clamp: minimum (meters).")]
        public float minDistance = 0.3f;
        [Tooltip("Placement distance clamp: maximum (meters).")]
        public float maxDistance = 50f;
        [Tooltip("Fraction of the object's pixel rows treated as the ground-contact band.")]
        [Range(0.01f, 0.3f)] public float groundAnchorBandFraction = 0.05f;

        [Header("Capture")]
        [Tooltip("Forwarded to SplatCaptureFeature: splat alpha above which depth is written.")]
        [Range(0.01f, 1f)] public float depthAlphaThreshold = 0.3f;

        [Header("Mesh content transform (Rodin via glTFast)")]
        [Tooltip("Local rotation of mesh content under the placed root.")]
        public Vector3 meshContentRotationEuler = Vector3.zero;
        [Tooltip("Yaw added so a generated mesh's front faces the camera.")]
        public float meshYawOffsetDeg = 180f;
        [Tooltip("Extra uniform scale factor for meshes.")]
        public float meshUniformScaleFactor = 1f;

        [Header("Scene-aware orientation (walls)")]
        [Tooltip("CameraFacing = every object faces the capture camera (previous behaviour). Shadow = compute and log the wall " +
                 "rule only. SceneAware = objects that hang on or stand against a wall turn parallel to it (only when the wall " +
                 "is found reliably in the capture depth; otherwise they face the camera).")]
        public OrientationMode orientationMode = OrientationMode.SceneAware;
        [Tooltip("Move a wall-snapped object so its back sits against the wall.")]
        public bool wallFlush = true;
        [Tooltip("Gap left between a wall-snapped object's back and the wall (meters).")]
        public float wallGapM = 0.01f;
        [Tooltip("Wall-hung objects: cap on the distance from the wall to the pivot (thick generated models may sink into the wall beyond it).")]
        public float wallMaxStandoffM = 0.30f;
        [Tooltip("Largest position change of a wall snap (wall-hung: total move; wall-backed: move along the wall normal).")]
        public float wallMaxShiftM = 1.5f;
        [Tooltip("Wall-backed furniture: centre it on its footprint along the wall.")]
        public bool wallBackedCenterOnFootprint = true;
        [Tooltip("Wall-backed furniture: largest sideways move = max(0.5 m, this fraction of the model width).")]
        public float wallMaxLateralFrac = 0.75f;
        [Tooltip("Minimum capture-depth points for a wall fit.")]
        public int wallMinPoints = 300;
        [Tooltip("Maximum capture-depth points sampled for a wall fit.")]
        public int wallMaxPoints = 12000;
        [Tooltip("RANSAC iterations of the wall fit.")]
        public int wallRansacIterations = 256;
        [Tooltip("Wall-fit inlier distance: minimum (meters).")]
        public float wallTauMinM = 0.03f;
        [Tooltip("Wall-fit inlier distance growth per meter of object distance.")]
        public float wallTauPerMeter = 0.01f;
        [Tooltip("Gate: minimum share of sampled points on the wall plane.")]
        public float wallMinInlierFraction = 0.35f;
        [Tooltip("Gate: maximum share of points more than 0.2 m behind the wall (windows, mirrors, openings).")]
        public float wallMaxBehindFraction = 0.15f;
        [Tooltip("Gate: maximum angle between the left and right halves of the wall (degrees).")]
        public float wallMaxSplitDeg = 8f;
        [Tooltip("Gate: minimum cosine between the wall normal and the direction to the camera (0.15 = at most ~81 degrees).")]
        public float wallMinGrazingCos = 0.15f;
        [Tooltip("Wall-hung objects: maximum distance of the wall behind the object's centre (meters; at least 3x the inlier distance).")]
        public float wallMountedCenterTolM = 0.15f;
        [Tooltip("Wall-hung objects without a usable centre sample: maximum anchor-to-wall distance (meters).")]
        public float wallMountedMaxAnchorDistM = 1.0f;
        [Tooltip("Wall-backed furniture: anchor-to-wall distance allowed = half the object size + this slack (meters).")]
        public float wallBackedMaxDistSlackM = 0.5f;
        [Tooltip("Treat objects whose name matches wallBackedCategories as wall-backed when the vision model gave no hint.")]
        public bool useCategoryWallHeuristic = true;
        [Tooltip("Comma-separated names of furniture that normally stands with its back to a wall (plural -s/-es also match).")]
        public string wallBackedCategories = DefaultWallBackedCategories;
        [Tooltip("Draw the wall, the camera-facing / applied / vision-model fronts and the snap move of a selected object.")]
        public bool orientationGizmos = true;

        /// <summary>Default of <see cref="wallBackedCategories"/>.</summary>
        public const string DefaultWallBackedCategories =
            "bookshelf,bookcase,shelving unit,cabinet,cupboard,wardrobe,armoire,dresser,chest of drawers,sideboard,credenza," +
            "buffet,hutch,tv stand,tv console,media console,console table,entertainment center,desk,headboard,bed,piano," +
            "fireplace,radiator,refrigerator,fridge";

        /// <summary>Copy (all fields are values).</summary>
        public PlacementTuning Clone() => (PlacementTuning)MemberwiseClone();
    }
}
