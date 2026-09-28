using NUnit.Framework;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Bbox parsing and conversions. The lenient parser guards a real failure: Gemini sometimes returns
    /// 0-1000-scale boxes (the box_2d convention), even mixing scales per component, which used to collapse the
    /// box to zero size after Clamp01 (scale then always fell back to size_hint_m and the SAM-3 box was garbage).
    /// </summary>
    public class BboxTests
    {
        const float kEps = 1e-5f;

        static void AssertBox(Bbox b, float x, float y, float w, float h)
        {
            Assert.That(b.x, Is.EqualTo(x).Within(kEps), "x");
            Assert.That(b.y, Is.EqualTo(y).Within(kEps), "y");
            Assert.That(b.w, Is.EqualTo(w).Within(kEps), "w");
            Assert.That(b.h, Is.EqualTo(h).Within(kEps), "h");
        }

        [Test]
        public void Lenient_KeepsNormalizedValues()
        {
            AssertBox(Bbox.FromXYWHNormLenient(new[] { 0.1f, 0.2f, 0.3f, 0.4f }), 0.1f, 0.2f, 0.3f, 0.4f);
            AssertBox(Bbox.FromXYWHNormLenient(new[] { 0f, 0f, 1f, 1f }), 0f, 0f, 1f, 1f);
        }

        [Test]
        public void Lenient_Rescales0To1000Boxes()
        {
            // observed in a session log
            AssertBox(Bbox.FromXYWHNormLenient(new[] { 304f, 330f, 272f, 542f }), 0.304f, 0.330f, 0.272f, 0.542f);
        }

        [Test]
        public void Lenient_RescalesOnlyTheOutOfRangeComponents()
        {
            // mixed scales in one box, also observed: [0.407, 508, 0.147, 0.187]
            AssertBox(Bbox.FromXYWHNormLenient(new[] { 0.407f, 508f, 0.147f, 0.187f }), 0.407f, 0.508f, 0.147f, 0.187f);
        }

        [Test]
        public void Lenient_ThresholdIsOnePointFive()
        {
            // values up to 1.5 are treated as (slightly out of range) normalized values, not as 0-1000 values
            AssertBox(Bbox.FromXYWHNormLenient(new[] { 1.2f, 1.5f, 1.6f, 2f }), 1.2f, 1.5f, 0.0016f, 0.002f);
        }

        [Test]
        public void Strict_DoesNotRescale()
        {
            AssertBox(Bbox.FromXYWHNorm(new[] { 304f, 330f, 272f, 542f }), 304f, 330f, 272f, 542f);
        }

        [Test]
        public void TargetAndVerifiedBoxes_ReadThroughTheLenientParser()
        {
            var decided = new DecidedObject { targetBboxNorm = new[] { 440f, 460f, 120f, 400f } };
            AssertBox(decided.TargetBbox, 0.44f, 0.46f, 0.12f, 0.4f);

            var verified = new VerifiedObject { bboxNorm = new[] { 0.44f, 460f, 0.12f, 0.4f } };
            AssertBox(verified.Bbox, 0.44f, 0.46f, 0.12f, 0.4f);

            var placed = new PlacedObjectResult { bboxGeneratedNorm = new[] { 100f, 200f, 300f, 400f } };
            AssertBox(placed.BboxGenerated, 0.1f, 0.2f, 0.3f, 0.4f);
        }

        [Test]
        public void MissingOrShortArrays_GiveAnInvalidDefaultBox()
        {
            Assert.IsFalse(new DecidedObject { targetBboxNorm = null }.TargetBbox.IsValid);
            Assert.IsFalse(new DecidedObject { targetBboxNorm = new[] { 0.1f, 0.2f, 0.3f } }.TargetBbox.IsValid);
            Assert.IsFalse(new VerifiedObject().Bbox.IsValid);
            Assert.IsFalse(new PlacedObjectResult().BboxGenerated.IsValid);
        }

        [Test]
        public void ToXyXyPixels_ReturnsRoundedIntegerCorners()
        {
            // SAM-3 rejects fractional box prompts ("Input should be a valid integer"), so these must be integers
            var b = new Bbox(0.1f, 0.2f, 0.3f, 0.4f);
            CollectionAssert.AreEqual(new[] { 128, 144, 512, 432 }, b.ToXyXyPixels(1280, 720));

            var odd = new Bbox(0.4398f, 0.4597f, 0.1203f, 0.4f);
            CollectionAssert.AreEqual(new[] { 563, 331, 717, 619 }, odd.ToXyXyPixels(1280, 720));
        }

        [Test]
        public void FormatConversions_RoundTrip()
        {
            var b = new Bbox(0.2f, 0.3f, 0.4f, 0.5f);
            AssertBox(Bbox.FromCxCyWHNorm(b.ToCxCyWHNorm()), 0.2f, 0.3f, 0.4f, 0.5f);
            float[] xyxy = b.ToXyXyNorm();
            AssertBox(Bbox.FromXyXyNorm(xyxy[0], xyxy[1], xyxy[2], xyxy[3]), 0.2f, 0.3f, 0.4f, 0.5f);
            AssertBox(Bbox.FromXYWHNorm(b.ToXYWHNorm()), 0.2f, 0.3f, 0.4f, 0.5f);
            Assert.That(b.centerX, Is.EqualTo(0.4f).Within(kEps));
            Assert.That(b.centerY, Is.EqualTo(0.55f).Within(kEps));
        }

        [Test]
        public void Clamp01_ClipsToTheUnitSquare()
        {
            AssertBox(new Bbox(-0.2f, 0.5f, 0.6f, 0.8f).Clamp01(), 0f, 0.5f, 0.4f, 0.5f);
            Assert.IsFalse(new Bbox(1.2f, 0.1f, 0.3f, 0.3f).Clamp01().IsValid, "a box fully outside collapses");
        }

        [Test]
        public void Expand_PadsEachSideAndClamps()
        {
            AssertBox(new Bbox(0.4f, 0.4f, 0.2f, 0.2f).Expand(0.15f), 0.37f, 0.37f, 0.26f, 0.26f);
            AssertBox(new Bbox(0f, 0.9f, 0.2f, 0.1f).Expand(0.5f), 0f, 0.85f, 0.3f, 0.15f);
        }

        [Test]
        public void IsValid_RequiresPositiveSize()
        {
            Assert.IsTrue(new Bbox(0.1f, 0.1f, 0.01f, 0.01f).IsValid);
            Assert.IsFalse(new Bbox(0.1f, 0.1f, 0f, 0.5f).IsValid);
            Assert.IsFalse(default(Bbox).IsValid);
        }
    }
}
