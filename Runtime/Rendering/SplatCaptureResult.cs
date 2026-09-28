// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SplatPresso.Rendering
{
    /// <summary>
    /// Result of a one-shot RGB + depth capture of a camera view (see <see cref="SplatCaptureFeature"/>), with
    /// gaussian splats included in the depth. Pixel data row 0 = top of the image.
    /// </summary>
    public sealed class SplatCaptureResult
    {
        /// <summary>Width in pixels of both <see cref="rgba"/> and <see cref="depthEye"/> (the camera target size).</summary>
        public int width;
        /// <summary>Height in pixels.</summary>
        public int height;
        /// <summary>RGBA32, sRGB-encoded, row 0 = top. Taken before post-processing and UI.</summary>
        public byte[] rgba;
        /// <summary>Linear eye depth in world units (distance along the camera forward axis), row 0 = top; 0 = invalid/sky.</summary>
        public float[] depthEye;
        /// <summary>Camera pose recorded in the same frame as the pixels.</summary>
        public Vector3 cameraPosition;
        /// <summary>Camera rotation recorded in the same frame as the pixels.</summary>
        public Quaternion cameraRotation;
        /// <summary>Vertical field of view in degrees (perspective cameras only; meaningless when <see cref="orthographic"/>).</summary>
        public float verticalFovDeg;
        /// <summary>Near clip plane distance.</summary>
        public float nearPlane;
        /// <summary>Far clip plane distance.</summary>
        public float farPlane;
        /// <summary>
        /// True when the camera used an orthographic projection. <see cref="depthEye"/> is still correct linear depth,
        /// but <see cref="verticalFovDeg"/> does not describe the projection, so pinhole back-projection (placement) must
        /// reject such a capture. RGB-only consumers (voice snapshots) can ignore it.
        /// </summary>
        public bool orthographic;
    }
}
