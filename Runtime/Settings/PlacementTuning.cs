using System;
using UnityEngine;

namespace SplatPresso
{
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

        /// <summary>Copy (all fields are values).</summary>
        public PlacementTuning Clone() => (PlacementTuning)MemberwiseClone();
    }
}
