using System.IO;
using NUnit.Framework;
using SplatPresso.Placement;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Image helpers. Two row-order lessons are pinned here: capture pixels are TOP-down while Texture2D raw data
    /// is BOTTOM-up (EncodeJpeg must flip), and bboxes are y-down while GetPixels is y-up (CropToPng must convert).
    /// </summary>
    public class ImageUtilTests
    {
        static readonly Color32 kRed = new Color32(230, 20, 20, 255);
        static readonly Color32 kBlue = new Color32(20, 20, 230, 255);

        static Texture2D Decode(byte[] bytes)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Assert.IsTrue(tex.LoadImage(bytes), "decode");
            return tex;
        }

        static bool IsRed(Color c) => c.r > 0.6f && c.b < 0.4f;
        static bool IsBlue(Color c) => c.b > 0.6f && c.r < 0.4f;

        [Test]
        public void EncodeJpeg_TreatsRowZeroAsTheTop()
        {
            const int w = 32, h = 32;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = y < h / 2 ? kRed : kBlue; // top half red (row 0 = top)
                int i = (y * w + x) * 4;
                rgba[i] = c.r; rgba[i + 1] = c.g; rgba[i + 2] = c.b; rgba[i + 3] = 255;
            }

            byte[] jpeg = ImageUtil.EncodeJpeg(rgba, w, h, 0, 95);
            Assert.IsTrue(ImageUtil.IsJpeg(jpeg));
            var tex = Decode(jpeg);
            try
            {
                // Texture2D row h-1 is the top of the image
                Assert.IsTrue(IsRed(tex.GetPixel(w / 2, h - 4)), "top of the image must be red");
                Assert.IsTrue(IsBlue(tex.GetPixel(w / 2, 3)), "bottom of the image must be blue");
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void EncodeJpeg_DownscalesToTheLongSide()
        {
            var rgba = new byte[64 * 32 * 4];
            byte[] jpeg = ImageUtil.EncodeJpeg(rgba, 64, 32, 32, 80);
            var (w, h) = ImageUtil.ImageDims(jpeg);
            Assert.AreEqual(32, w);
            Assert.AreEqual(16, h);
        }

        [Test]
        public void CropToPng_UsesATopLeftOriginBox()
        {
            // 8x8 image: top-left quadrant red, everything else blue
            var src = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            byte[] png;
            try
            {
                for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    src.SetPixel(x, y, x < 4 && y >= 4 ? kRed : kBlue); // GetPixel/SetPixel y is bottom-up
                src.Apply();
                png = src.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(src);
            }

            byte[] crop = ImageUtil.CropToPng(png, new Bbox(0f, 0f, 0.5f, 0.5f));
            Assert.NotNull(crop);
            Assert.IsTrue(ImageUtil.IsPng(crop));
            var tex = Decode(crop);
            try
            {
                Assert.AreEqual(4, tex.width);
                Assert.AreEqual(4, tex.height);
                foreach (var c in tex.GetPixels())
                    Assert.IsTrue(IsRed(c), "the top-left box must crop the top-left (red) quadrant");
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void CropToPng_RejectsInvalidInput()
        {
            Assert.IsNull(ImageUtil.CropToPng(null, new Bbox(0f, 0f, 0.5f, 0.5f)));
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsNull(ImageUtil.CropToPng(tex.EncodeToPNG(), new Bbox(0.5f, 0.5f, 0f, 0f)));
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void ImageDims_ReadsTheFixtureHeaders()
        {
            var jpg = File.ReadAllBytes(EditorTestUtil.RequireFixture("edited.jpg"));
            var png = File.ReadAllBytes(EditorTestUtil.RequireFixture("cutout.png"));
            Assert.AreEqual((1280, 720), ImageUtil.ImageDims(jpg));
            Assert.AreEqual((1280, 720), ImageUtil.ImageDims(png));
            Assert.AreEqual((512, 512), ImageUtil.ImageDims(File.ReadAllBytes(EditorTestUtil.RequireFixture("enhanced.png"))));
            Assert.AreEqual((0, 0), ImageUtil.ImageDims(new byte[] { 1, 2, 3 }));
            Assert.IsTrue(ImageUtil.IsJpeg(jpg));
            Assert.IsTrue(ImageUtil.IsPng(png));
            Assert.IsFalse(ImageUtil.IsPng(jpg));
        }

        [Test]
        public void ReencodeJpeg_KeepsTheAspect()
        {
            var png = File.ReadAllBytes(EditorTestUtil.RequireFixture("cutout.png"));
            byte[] jpeg = ImageUtil.ReencodeJpeg(png, 640, 80);
            Assert.IsTrue(ImageUtil.IsJpeg(jpeg));
            Assert.AreEqual((640, 360), ImageUtil.ImageDims(jpeg));
            Assert.IsNull(ImageUtil.ReencodeJpeg(new byte[] { 1, 2, 3, 4 }, 640, 80));
        }
    }
}
