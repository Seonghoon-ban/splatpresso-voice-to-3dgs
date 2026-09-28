using System;
using System.Threading;
using SplatPresso.Rendering;
using UnityEngine;

namespace SplatPresso.Placement
{
    /// <summary>
    /// <see cref="ICaptureProvider"/> backed by <see cref="SplatCaptureFeature"/> (URP renderer feature): one-shot
    /// capture of the current camera view as RGB JPEG + metric eye depth + camera pose.
    /// </summary>
    /// <remarks>
    /// Needs <see cref="SplatCaptureFeature"/> on the URP renderer used by the target camera (SplatPresso > Setup
    /// Scene adds it). Fails fast with an actionable error when it is missing instead of hanging.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Capture Service")]
    public sealed class CaptureService : MonoBehaviour, ICaptureProvider
    {
        /// <summary>Camera to capture; null uses <see cref="Camera.main"/>. In XR assign a mono capture camera.</summary>
        [Tooltip("Camera to capture. Empty = Camera.main. In XR, assign a mono (non-stereo) capture camera that follows the HMD.")]
        public Camera targetCamera;

        /// <summary>Settings for JPEG size/quality; null uses <see cref="SplatPressoSettings.Active"/>.</summary>
        [Tooltip("Empty = the active SplatPresso settings.")]
        public SplatPressoSettings settings;

        /// <summary>Seconds to wait for a requested capture before giving up (at least 3 rendered frames always pass).</summary>
        [Tooltip("Seconds to wait for a requested capture before failing with a setup hint.")]
        [Min(1f)] public float timeoutSeconds = 5f;

        // Waiting for a previous capture (the voice snapshot) to drain.
        const float kDrainSeconds = 2f;
        // Frames to wait for the feature to report activity before failing fast.
        const int kFeatureProbeFrames = 10;
        const int kMinTimeoutFrames = 3;

        SplatPressoSettings Settings => settings != null ? settings : SplatPressoSettings.Active;

        /// <summary>The camera a capture would use now (<see cref="targetCamera"/>, else <see cref="Camera.main"/>); may be null.</summary>
        public Camera ResolveCamera() => targetCamera != null ? targetCamera : Camera.main;

        /// <summary>
        /// Captures the current view. Throws <see cref="InvalidOperationException"/> when capturing is impossible
        /// (no camera, feature missing, capture failed), <see cref="TimeoutException"/> when the capture never
        /// completed, and <see cref="OperationCanceledException"/> on cancel.
        /// </summary>
        public async Awaitable<CaptureResult> CaptureAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var cam = ResolveCamera();
            if (cam == null)
                throw new InvalidOperationException("[SplatPresso] CaptureService: no target camera and no Camera.main.");
            // Placement back-projects depth through a pinhole model (vertical FOV), and the splat renderer assumes a
            // perspective projection too, so an orthographic capture would place objects at meaningless positions.
            if (cam.orthographic)
                throw new InvalidOperationException($"[SplatPresso] CaptureService: camera '{cam.name}' is orthographic; placement needs a perspective camera.");
            var s = Settings;

            // Wait for a previous capture to drain (up to ~2 s). The voice agent's speech-turn snapshot uses the
            // same single capture slot and may still be in flight when the pipeline starts.
            float deadline = Time.realtimeSinceStartup + kDrainSeconds;
            while (SplatCaptureFeature.HasPendingCapture)
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new InvalidOperationException("[SplatPresso] CaptureService: a previous capture is still pending.");
                await Awaitable.NextFrameAsync(ct);
            }

            // Fail fast when no renderer runs the feature: the request would never be served.
            for (int i = 0; i < kFeatureProbeFrames && !SplatCaptureFeature.IsActiveOnAnyRenderer; ++i)
                await Awaitable.NextFrameAsync(ct);
            if (!SplatCaptureFeature.IsActiveOnAnyRenderer)
                throw new InvalidOperationException("[SplatPresso] " + FeatureMissingMessage(cam));

            SplatCaptureResult raw = null;
            bool done = false;
            if (!SplatCaptureFeature.RequestCapture(cam, r => { raw = r; done = true; }))
                throw new InvalidOperationException("[SplatPresso] CaptureService: capture busy (another capture is pending).");

            float timeoutAt = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
            int startFrame = Time.frameCount;
            while (!done)
            {
                if (ct.IsCancellationRequested)
                {
                    // free the single capture slot; our callback receives null (done = true)
                    SplatCaptureFeature.CancelPending("capture cancelled by caller");
                    ct.ThrowIfCancellationRequested();
                }
                if (Time.realtimeSinceStartup > timeoutAt && Time.frameCount - startFrame >= kMinTimeoutFrames)
                {
                    SplatCaptureFeature.CancelPending("capture timed out");
                    throw new TimeoutException("[SplatPresso] Capture timed out: is SplatCaptureFeature on the URP renderer used by camera '" +
                                               cam.name + "'? Run SplatPresso > Setup Scene.");
                }
                await Awaitable.NextFrameAsync();
            }

            if (raw == null)
            {
                string why = SplatCaptureFeature.LastError;
                throw new InvalidOperationException("[SplatPresso] Capture failed: " + (string.IsNullOrEmpty(why) ? "see previous errors" : why));
            }
            if (raw.orthographic)
                throw new InvalidOperationException($"[SplatPresso] CaptureService: camera '{cam.name}' switched to orthographic before the capture rendered; placement needs a perspective camera.");

            var result = new CaptureResult
            {
                // width/height stay the FULL depth resolution; the jpeg may be smaller (bbox work is normalized)
                width = raw.width,
                height = raw.height,
                cameraPosition = raw.cameraPosition,
                cameraRotation = raw.cameraRotation,
                verticalFovDeg = raw.verticalFovDeg,
                nearPlane = raw.nearPlane,
                farPlane = raw.farPlane,
                depthMeters = raw.depthEye,
                rgbJpeg = ImageUtil.EncodeJpeg(raw.rgba, raw.width, raw.height, s.maxImageLongSide, s.jpegQuality),
            };
            Debug.Log($"[SplatPresso] Captured {raw.width}x{raw.height} (jpeg {result.rgbJpeg.Length / 1024} KB)");
            return result;
        }

        /// <summary>
        /// RGB snapshot of the current view for voice turns. Returns null when the capture slot is busy (it never
        /// steals the pipeline's capture), the feature is not active, on timeout or cancel. Never throws.
        /// </summary>
        /// <param name="maxLongSide">Downscale so the long side is at most this (0 = keep).</param>
        /// <param name="quality">JPEG quality 1..100.</param>
        /// <param name="timeoutSec">Give up after this many seconds (the pending request is cancelled so the slot frees up).</param>
        /// <param name="ct">Cancellation.</param>
        public async Awaitable<byte[]> CaptureSnapshotJpegAsync(int maxLongSide, int quality, float timeoutSec, CancellationToken ct)
        {
            try
            {
                if (ct.IsCancellationRequested)
                    return null;
                var cam = ResolveCamera();
                if (cam == null || SplatCaptureFeature.HasPendingCapture || !SplatCaptureFeature.IsActiveOnAnyRenderer)
                    return null;

                SplatCaptureResult raw = null;
                bool done = false;
                if (!SplatCaptureFeature.RequestCapture(cam, r => { raw = r; done = true; }))
                    return null;

                float timeoutAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, timeoutSec);
                while (!done)
                {
                    if (ct.IsCancellationRequested || Time.realtimeSinceStartup > timeoutAt)
                    {
                        // Give the slot back right away: a snapshot left in flight used to block the pipeline's
                        // own capture for up to 2 s.
                        SplatCaptureFeature.CancelPending(ct.IsCancellationRequested ? "snapshot cancelled" : "snapshot timed out");
                        return null;
                    }
                    await Awaitable.NextFrameAsync();
                }
                if (raw == null || raw.rgba == null)
                    return null;
                return ImageUtil.EncodeJpeg(raw.rgba, raw.width, raw.height, maxLongSide, quality);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Snapshot capture failed: {e.Message}");
                return null;
            }
        }

        /// <summary>Encodes top-down RGBA32 (row 0 = top) as JPEG; see <see cref="ImageUtil.EncodeJpeg"/>.</summary>
        public static byte[] EncodeJpeg(byte[] rgbaTopDown, int w, int h, int maxLongSide, int quality) =>
            ImageUtil.EncodeJpeg(rgbaTopDown, w, h, maxLongSide, quality);

        static string FeatureMissingMessage(Camera cam) =>
            "SplatCaptureFeature is not running on any URP renderer (camera '" + cam.name + "'). " +
            "Run SplatPresso > Setup Scene to add it to the renderer used by the camera, and make sure the camera renders.";
    }
}
