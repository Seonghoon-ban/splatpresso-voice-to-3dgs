using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace SplatPresso
{
    // ------------------------------------------------------------------------------------------
    // Contract between the cloud/orchestration side and the Unity engine side.

    /// <summary>One-shot capture of the user's current view: RGB JPEG + metric eye depth + camera pose.</summary>
    [Serializable]
    public class CaptureResult
    {
        /// <summary>JPEG-encoded RGB (sent to the cloud pipeline; may be downscaled relative to width/height).</summary>
        [JsonIgnore] public byte[] rgbJpeg;
        /// <summary>Linear eye depth per pixel in meters, row 0 = top; 0 = invalid/sky.</summary>
        [JsonIgnore] public float[] depthMeters;
        public int width;   // full depth resolution; bbox work is normalized
        public int height;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public float verticalFovDeg;
        public float nearPlane;
        public float farPlane;

        /// <summary>Focal length in pixels (square pixels assumed).</summary>
        [JsonIgnore] public float FocalPixels => 0.5f * height / Mathf.Tan(0.5f * verticalFovDeg * Mathf.Deg2Rad);

        /// <summary>Eye depth at a pixel (top-left origin); 0 outside the image or when there is no depth.</summary>
        public float DepthAt(int px, int py)
        {
            if (depthMeters == null || px < 0 || py < 0 || px >= width || py >= height)
                return 0f;
            int i = py * width + px;
            return i < depthMeters.Length ? depthMeters[i] : 0f;
        }

        /// <summary>Unprojects a pixel (top-left origin, y down) at a given eye depth to world space.</summary>
        public Vector3 UnprojectToWorld(float px, float py, float eyeDepth)
        {
            float f = FocalPixels;
            float xCam = (px + 0.5f - width * 0.5f) / f * eyeDepth;
            float yCam = -(py + 0.5f - height * 0.5f) / f * eyeDepth;
            return cameraPosition + cameraRotation * new Vector3(xCam, yCam, eyeDepth);
        }

        // ---- persistence (session replay) ----

        public const string JpegFileName = "capture.jpg";
        public const string DepthFileName = "capture_depth.bin"; // raw float32, host byte order, width*height values
        public const string MetaFileName = "capture_meta.json";

        /// <summary>Writes capture.jpg, capture_depth.bin and capture_meta.json into <paramref name="dir"/>.</summary>
        public void SaveTo(string dir)
        {
            Directory.CreateDirectory(dir);
            if (rgbJpeg != null)
                File.WriteAllBytes(Path.Combine(dir, JpegFileName), rgbJpeg);
            if (depthMeters != null)
            {
                var bytes = new byte[depthMeters.Length * 4];
                Buffer.BlockCopy(depthMeters, 0, bytes, 0, bytes.Length);
                File.WriteAllBytes(Path.Combine(dir, DepthFileName), bytes);
            }
            File.WriteAllText(Path.Combine(dir, MetaFileName), JsonUtil.Serialize(this));
        }

        /// <summary>Loads a capture saved by <see cref="SaveTo"/>; null when there is no capture_meta.json.</summary>
        public static CaptureResult LoadFrom(string dir)
        {
            string metaPath = Path.Combine(dir, MetaFileName);
            if (!File.Exists(metaPath))
                return null;
            var res = JsonUtil.Deserialize<CaptureResult>(File.ReadAllText(metaPath));
            if (res == null)
                return null;
            string jpegPath = Path.Combine(dir, JpegFileName);
            if (File.Exists(jpegPath))
                res.rgbJpeg = File.ReadAllBytes(jpegPath);
            string depthPath = Path.Combine(dir, DepthFileName);
            if (File.Exists(depthPath))
            {
                var bytes = File.ReadAllBytes(depthPath);
                res.depthMeters = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, res.depthMeters, 0, res.depthMeters.Length * 4);
            }
            return res;
        }
    }

    /// <summary>Engine side: captures the current camera view (RGB + metric depth + pose).</summary>
    public interface ICaptureProvider
    {
        Awaitable<CaptureResult> CaptureAsync(CancellationToken ct);
    }

    /// <summary>
    /// Engine side: spawns the generated objects. Must set each <see cref="ObjectStatus.Ready"/> object to
    /// <see cref="ObjectStatus.Placed"/>, or to <see cref="ObjectStatus.Skipped"/> with a skipReason. Objects
    /// already spawned stay spawned when cancelled mid-way.
    /// </summary>
    public interface IObjectPlacer
    {
        Awaitable PlaceAsync(PlacementResult result, CancellationToken ct);
    }

    /// <summary>No-op placer so the cloud pipeline can run end-to-end without scene integration.</summary>
    public sealed class NullObjectPlacer : IObjectPlacer
    {
        public async Awaitable PlaceAsync(PlacementResult result, CancellationToken ct)
        {
            int ready = 0;
            if (result?.objects != null)
                foreach (var o in result.objects)
                    if (o.status == ObjectStatus.Ready) ready++;
            Debug.Log($"[SplatPresso] NullObjectPlacer: {ready} object(s) ready, models in {result?.sessionDir}");
            await Awaitable.NextFrameAsync(ct);
        }
    }
}
