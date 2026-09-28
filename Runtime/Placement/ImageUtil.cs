using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SplatPresso.Placement
{
    /// <summary>
    /// Image helpers shared by capture, the pipeline and voice turns. Methods that touch textures must run on the
    /// main thread; they work in edit mode and without a GPU (CPU resize fallback).
    /// </summary>
    public static class ImageUtil
    {
        /// <summary>
        /// Encodes top-down RGBA32 pixels (row 0 = TOP, sRGB) as JPEG, downscaling so the long side is at most
        /// <paramref name="maxLongSide"/> (0 = keep size).
        /// </summary>
        public static byte[] EncodeJpeg(byte[] rgbaTopDown, int w, int h, int maxLongSide, int quality)
        {
            if (rgbaTopDown == null || w <= 0 || h <= 0 || rgbaTopDown.Length < w * h * 4)
                throw new ArgumentException("[SplatPresso] EncodeJpeg: pixel buffer does not match the size");
            quality = Mathf.Clamp(quality, 1, 100);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                // Texture2D raw data wants row 0 = BOTTOM: flip while copying.
                int rowBytes = w * 4;
                var flipped = new byte[rowBytes * h];
                for (int y = 0; y < h; ++y)
                    Buffer.BlockCopy(rgbaTopDown, y * rowBytes, flipped, (h - 1 - y) * rowBytes, rowBytes);
                tex.LoadRawTextureData(flipped);
                tex.Apply(false, false);
                return EncodeScaled(tex, maxLongSide, quality);
            }
            finally
            {
                DestroySafe(tex);
            }
        }

        /// <summary>Decodes an encoded image and re-encodes it as JPEG with the long side at most <paramref name="maxLongSide"/> (0 = keep). Null if it cannot be decoded.</summary>
        public static byte[] ReencodeJpeg(byte[] image, int maxLongSide, int quality)
        {
            if (image == null || image.Length == 0)
                return null;
            quality = Mathf.Clamp(quality, 1, 100);
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(image))
                    return null;
                return EncodeScaled(tex, maxLongSide, quality);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Re-encoding an image failed: {e.Message}");
                return null;
            }
            finally
            {
                DestroySafe(tex);
            }
        }

        /// <summary>
        /// Pixel size of an encoded PNG/JPEG. Reads the header when possible (no decode), otherwise decodes.
        /// (0, 0) when unknown. Segmentation needs pixel-space boxes, hence the size of the exact image sent.
        /// </summary>
        public static (int w, int h) ImageDims(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length < 16)
                return (0, 0);
            if (TryReadPngSize(imageBytes, out int w, out int h) || TryReadJpegSize(imageBytes, out w, out h))
                return (w, h);

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                return tex.LoadImage(imageBytes) ? (tex.width, tex.height) : (0, 0);
            }
            catch
            {
                return (0, 0);
            }
            finally
            {
                DestroySafe(tex);
            }
        }

        /// <summary>Crops the (top-left-origin, normalized) box out of an encoded image; PNG bytes or null.</summary>
        public static byte[] CropToPng(byte[] imageBytes, Bbox boxNorm)
        {
            if (imageBytes == null)
                return null;
            Texture2D src = null, dst = null;
            try
            {
                src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!src.LoadImage(imageBytes))
                    return null;
                var b = boxNorm.Clamp01();
                if (!b.IsValid)
                    return null;
                int px = Mathf.Clamp(Mathf.RoundToInt(b.x * src.width), 0, src.width - 1);
                int pyTop = Mathf.Clamp(Mathf.RoundToInt(b.y * src.height), 0, src.height - 1);
                int pw = Mathf.Clamp(Mathf.RoundToInt(b.w * src.width), 1, src.width - px);
                int ph = Mathf.Clamp(Mathf.RoundToInt(b.h * src.height), 1, src.height - pyTop);
                int pyBottom = src.height - pyTop - ph; // the bbox is y-down, GetPixels is y-up
                var pixels = src.GetPixels(px, pyBottom, pw, ph);
                dst = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
                dst.SetPixels(pixels);
                dst.Apply();
                return dst.EncodeToPNG();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Local crop failed: {e.Message}");
                return null;
            }
            finally
            {
                DestroySafe(src);
                DestroySafe(dst);
            }
        }

        /// <summary>True when the bytes start with the PNG signature.</summary>
        public static bool IsPng(byte[] b) =>
            b != null && b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

        /// <summary>True when the bytes start with a JPEG SOI marker.</summary>
        public static bool IsJpeg(byte[] b) => b != null && b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

        /// <summary>Destroys a Unity object in play mode or edit mode (Destroy throws outside play mode).</summary>
        public static void DestroySafe(Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(obj);
            else
                Object.DestroyImmediate(obj);
        }

        // ------------------------------------------------------------------------------------------

        static byte[] EncodeScaled(Texture2D tex, int maxLongSide, int quality)
        {
            int w = tex.width, h = tex.height;
            int longSide = Mathf.Max(w, h);
            if (maxLongSide <= 0 || longSide <= maxLongSide)
                return tex.EncodeToJPG(quality);

            float k = (float)maxLongSide / longSide;
            int nw = Mathf.Max(1, Mathf.RoundToInt(w * k));
            int nh = Mathf.Max(1, Mathf.RoundToInt(h * k));

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return EncodeCpuResized(tex, nw, nh, quality);

            // downscale via GPU blit, then read back
            var rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prevActive = RenderTexture.active;
            Texture2D small = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                small = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
                small.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
                small.Apply(false, false);
                return small.EncodeToJPG(quality);
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                DestroySafe(small);
            }
        }

        // Bilinear resize on the CPU (batch mode / -nographics, where blits are unavailable).
        static byte[] EncodeCpuResized(Texture2D tex, int nw, int nh, int quality)
        {
            Color32[] src = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            var dst = new Color32[nw * nh];
            float sx = (float)w / nw, sy = (float)h / nh;
            for (int y = 0; y < nh; y++)
            {
                float fy = Mathf.Clamp((y + 0.5f) * sy - 0.5f, 0, h - 1);
                int y0 = (int)fy, y1 = Mathf.Min(y0 + 1, h - 1);
                float ty = fy - y0;
                for (int x = 0; x < nw; x++)
                {
                    float fx = Mathf.Clamp((x + 0.5f) * sx - 0.5f, 0, w - 1);
                    int x0 = (int)fx, x1 = Mathf.Min(x0 + 1, w - 1);
                    float tx = fx - x0;
                    Color32 a = src[y0 * w + x0], b = src[y0 * w + x1], c = src[y1 * w + x0], d = src[y1 * w + x1];
                    dst[y * nw + x] = Color32.Lerp(Color32.Lerp(a, b, tx), Color32.Lerp(c, d, tx), ty);
                }
            }
            var small = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
            try
            {
                small.SetPixels32(dst);
                small.Apply(false, false);
                return small.EncodeToJPG(quality);
            }
            finally
            {
                DestroySafe(small);
            }
        }

        static bool TryReadPngSize(byte[] b, out int w, out int h)
        {
            w = h = 0;
            // signature (8) + IHDR length (4) + "IHDR" (4) + width (4, BE) + height (4, BE)
            if (!IsPng(b) || b.Length < 24 || b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R')
                return false;
            w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return w > 0 && h > 0;
        }

        static bool TryReadJpegSize(byte[] b, out int w, out int h)
        {
            w = h = 0;
            if (!IsJpeg(b))
                return false;
            int i = 2;
            while (i + 3 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }                  // fill byte
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; } // no length
                if (marker == 0xD9 || marker == 0xDA) return false;     // end of image / start of scan before a SOF
                int len = (b[i + 2] << 8) | b[i + 3];
                bool isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof)
                {
                    if (i + 8 >= b.Length)
                        return false;
                    h = (b[i + 5] << 8) | b[i + 6];
                    w = (b[i + 7] << 8) | b[i + 8];
                    return w > 0 && h > 0;
                }
                if (len < 2)
                    return false;
                i += 2 + len;
            }
            return false;
        }
    }
}
