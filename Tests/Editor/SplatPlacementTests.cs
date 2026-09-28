using NUnit.Framework;
using SplatPresso.Placement;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// SplatPlacement.Solve on a synthetic capture with analytic depth: camera at (0, 1.6, 0) looking along +Z,
    /// pitched down 20 degrees, over an infinite floor at y = 0. The object's ground contact must land on the floor,
    /// its height must come from the MASK rows (verify boxes are often loose), and the calibrated yaw offset must
    /// be applied on top of "face the camera".
    /// </summary>
    public class SplatPlacementTests
    {
        const int W = 320, H = 180;
        const float kFov = 60f;
        static readonly Vector3 kCamPos = new Vector3(0f, 1.6f, 0f);
        static readonly Quaternion kCamRot = Quaternion.Euler(20f, 0f, 0f);
        static readonly Vector3 kContentBounds = Vector3.one;

        // mask: a rectangle of rows 100..150 and columns 150..170
        const int kRow0 = 100, kRow1 = 150, kCol0 = 150, kCol1 = 170;

        static float Focal => 0.5f * H / Mathf.Tan(0.5f * kFov * Mathf.Deg2Rad);

        static Vector3 CameraRayDir(float px, float py) =>
            new Vector3((px + 0.5f - W * 0.5f) / Focal, -(py + 0.5f - H * 0.5f) / Focal, 1f);

        /// <summary>Eye depth of the floor at a pixel (0 = sky), computed independently of the solver.</summary>
        static float FloorEyeDepth(int px, int py)
        {
            Vector3 dir = kCamRot * CameraRayDir(px, py);
            if (dir.y > -1e-6f)
                return 0f;
            return kCamPos.y / -dir.y; // the camera-space ray has z = 1, so t equals the eye depth
        }

        static Vector3 FloorHit(int px, int py) => kCamPos + kCamRot * CameraRayDir(px, py) * FloorEyeDepth(px, py);

        static CaptureResult MakeCapture()
        {
            var depth = new float[W * H];
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                depth[y * W + x] = FloorEyeDepth(x, y);
            return new CaptureResult
            {
                width = W,
                height = H,
                depthMeters = depth,
                cameraPosition = kCamPos,
                cameraRotation = kCamRot,
                verticalFovDeg = kFov,
                nearPlane = 0.1f,
                farPlane = 100f,
            };
        }

        static bool[] MakeMask()
        {
            var mask = new bool[W * H];
            for (int y = kRow0; y <= kRow1; y++)
            for (int x = kCol0; x <= kCol1; x++)
                mask[y * W + x] = true;
            return mask;
        }

        static Bbox MaskBbox() => new Bbox((float)kCol0 / W, (float)kRow0 / H, (float)(kCol1 - kCol0 + 1) / W, (float)(kRow1 - kRow0 + 1) / H);

        static float ExpectedYaw(Vector3 pos, float offset) =>
            Mathf.Atan2(kCamPos.x - pos.x, kCamPos.z - pos.z) * Mathf.Rad2Deg + offset;

        [Test]
        public void SyntheticCapture_IsConsistentWithUnproject()
        {
            // sanity check of the test's own camera model against CaptureResult.UnprojectToWorld
            var cap = MakeCapture();
            Assert.That(cap.FocalPixels, Is.EqualTo(Focal).Within(1e-3f));
            Vector3 p = cap.UnprojectToWorld(160, 150, cap.DepthAt(160, 150));
            Assert.That(Vector3.Distance(p, FloorHit(160, 150)), Is.LessThan(1e-3f));
            Assert.That(Mathf.Abs(p.y), Is.LessThan(1e-3f));
            Assert.AreEqual(0f, cap.DepthAt(160, 5), "rows above the horizon see the sky");
        }

        [Test]
        public void Mask_AnchorsTheBottomRowOnTheFloor_AndSizesFromMaskRows()
        {
            var cap = MakeCapture();
            var tuning = new PlacementTuning();
            var sol = SplatPlacement.Solve(cap, MaskBbox(), 0.9f, null, MakeMask(), kContentBounds, tuning);

            Assert.IsTrue(sol.valid, sol.note);
            // ground contact = bottom mask row, median column of the bottom band
            Vector3 expected = FloorHit(160, kRow1);
            Assert.That(Mathf.Abs(sol.position.y), Is.LessThan(0.05f), "must stand on the floor: " + sol.position);
            Assert.That(Vector3.Distance(sol.position, expected), Is.LessThan(0.03f), $"expected {expected}, got {sol.position}");
            float dist = Vector3.Distance(new Vector3(sol.position.x, 0, sol.position.z), new Vector3(kCamPos.x, 0, kCamPos.z));
            Assert.That(dist, Is.EqualTo(new Vector2(expected.x, expected.z).magnitude).Within(0.03f));

            // height = mask row extent at the anchor depth; scale = height / content height * calibrated factor
            float anchorDepth = FloorEyeDepth(160, kRow1);
            float heightWorld = (kRow1 - kRow0 + 1) * anchorDepth / Focal;
            Assert.That(sol.uniformScale, Is.EqualTo(heightWorld / kContentBounds.y * tuning.uniformScaleFactor).Within(0.02f));

            // yaw: +Z faces the capture camera, then the calibrated offset (270) is added
            var expectedRot = Quaternion.Euler(0f, ExpectedYaw(sol.position, tuning.yawOffsetDeg), 0f);
            Assert.That(Quaternion.Angle(sol.rotation, expectedRot), Is.LessThan(0.5f), "rotation " + sol.rotation.eulerAngles);
        }

        [Test]
        public void YawOffset_IsAppliedOnTopOfFacingTheCamera()
        {
            var cap = MakeCapture();
            var withOffset = new PlacementTuning();
            var noOffset = new PlacementTuning { yawOffsetDeg = 0f };
            var a = SplatPlacement.Solve(cap, MaskBbox(), 0.9f, null, MakeMask(), kContentBounds, withOffset);
            var b = SplatPlacement.Solve(cap, MaskBbox(), 0.9f, null, MakeMask(), kContentBounds, noOffset);
            Assert.IsTrue(a.valid && b.valid);
            Assert.That(Vector3.Distance(a.position, b.position), Is.LessThan(1e-4f), "the offset must not move the object");
            var rotated = b.rotation * Quaternion.Euler(0f, 270f, 0f);
            Assert.That(Quaternion.Angle(a.rotation, rotated), Is.LessThan(0.5f));
            Assert.That(Quaternion.Angle(a.rotation, b.rotation), Is.EqualTo(90f).Within(0.5f));
        }

        [Test]
        public void SizeHint_WinsWhenTheMeasuredHeightIsImplausible()
        {
            var cap = MakeCapture();
            var tuning = new PlacementTuning();
            // measured height is ~0.75 m; a 3 m hint differs by more than 3x, so the hint is used
            var sol = SplatPlacement.Solve(cap, MaskBbox(), 3f, null, MakeMask(), kContentBounds, tuning);
            Assert.IsTrue(sol.valid);
            Assert.That(sol.uniformScale, Is.EqualTo(3f / kContentBounds.y * tuning.uniformScaleFactor).Within(1e-3f));

            // a plausible hint does not override the measurement
            var measured = SplatPlacement.Solve(cap, MaskBbox(), 0.6f, null, MakeMask(), kContentBounds, tuning);
            Assert.That(measured.uniformScale, Is.Not.EqualTo(0.6f * tuning.uniformScaleFactor).Within(1e-3f));
        }

        [Test]
        public void ContentBounds_ScaleTheResult()
        {
            var cap = MakeCapture();
            var tuning = new PlacementTuning();
            var unit = SplatPlacement.Solve(cap, MaskBbox(), 0.9f, null, MakeMask(), Vector3.one, tuning);
            var tall = SplatPlacement.Solve(cap, MaskBbox(), 0.9f, null, MakeMask(), new Vector3(1f, 2f, 1f), tuning);
            Assert.That(tall.uniformScale, Is.EqualTo(unit.uniformScale * 0.5f).Within(1e-4f));
        }

        [Test]
        public void MeshFlag_UsesTheMeshTuning()
        {
            var cap = MakeCapture();
            var tuning = new PlacementTuning();
            var splat = SplatPlacement.Solve(cap, MaskBbox(), 0.9f, null, MakeMask(), kContentBounds, tuning);
            var mesh = SplatPlacement.Solve(cap, MaskBbox(), 0.9f, null, MakeMask(), kContentBounds, tuning, isMesh: true);
            Assert.IsTrue(mesh.valid, mesh.note);
            Assert.That(Vector3.Distance(splat.position, mesh.position), Is.LessThan(1e-4f));
            var expectedRot = Quaternion.Euler(0f, ExpectedYaw(mesh.position, tuning.meshYawOffsetDeg), 0f);
            Assert.That(Quaternion.Angle(mesh.rotation, expectedRot), Is.LessThan(0.5f));
            Assert.That(mesh.uniformScale, Is.EqualTo(splat.uniformScale / tuning.uniformScaleFactor * tuning.meshUniformScaleFactor).Within(0.01f));
        }

        [Test]
        public void NoMask_AnchorsOnTheBboxBottomCenter()
        {
            var cap = MakeCapture();
            var box = new Bbox(0.4f, 0.5f, 0.2f, 0.3f); // pixel columns 128..191, rows 90..143
            var sol = SplatPlacement.Solve(cap, box, 0.9f, null, null, kContentBounds, new PlacementTuning());
            Assert.IsTrue(sol.valid, sol.note);
            Assert.That(Mathf.Abs(sol.position.y), Is.LessThan(0.05f));
            Assert.That(Vector3.Distance(sol.position, FloorHit(159, 143)), Is.LessThan(0.05f), "got " + sol.position);
        }

        [Test]
        public void MaskOfTheWrongSize_IsIgnored()
        {
            var cap = MakeCapture();
            var box = new Bbox(0.4f, 0.5f, 0.2f, 0.3f);
            var withBadMask = SplatPlacement.Solve(cap, box, 0.9f, new float[7], new bool[13], kContentBounds, new PlacementTuning());
            var without = SplatPlacement.Solve(cap, box, 0.9f, null, null, kContentBounds, new PlacementTuning());
            Assert.IsTrue(withBadMask.valid, withBadMask.note);
            Assert.That(Vector3.Distance(withBadMask.position, without.position), Is.LessThan(1e-4f));
        }

        [Test]
        public void SkyAnchor_FallsBackToTwoMeters()
        {
            var cap = MakeCapture();
            var box = new Bbox(0.45f, 0.02f, 0.1f, 0.08f); // entirely above the horizon: no depth anywhere in the box
            var sol = SplatPlacement.Solve(cap, box, 0.9f, null, null, kContentBounds, new PlacementTuning());
            Assert.IsTrue(sol.valid, sol.note);
            float eyeDepth = Vector3.Dot(sol.position - kCamPos, kCamRot * Vector3.forward);
            Assert.That(eyeDepth, Is.EqualTo(2f).Within(0.01f), sol.note);
        }

        [Test]
        public void InvalidInput_IsRejected()
        {
            var cap = MakeCapture();
            Assert.IsFalse(SplatPlacement.Solve(null, MaskBbox(), 1f, null, null, kContentBounds, new PlacementTuning()).valid, "no capture");
            Assert.IsFalse(SplatPlacement.Solve(cap, new Bbox(0.5f, 0.5f, 0f, 0f), 1f, null, null, kContentBounds, new PlacementTuning()).valid, "empty bbox");
        }

        [Test]
        public void DistanceIsClampedByTuning()
        {
            var cap = MakeCapture();
            var tuning = new PlacementTuning { maxDistance = 1f };
            var sol = SplatPlacement.Solve(cap, MaskBbox(), 0.9f, null, MakeMask(), kContentBounds, tuning);
            Assert.IsTrue(sol.valid);
            float eyeDepth = Vector3.Dot(sol.position - kCamPos, kCamRot * Vector3.forward);
            Assert.That(eyeDepth, Is.EqualTo(1f).Within(0.01f));
        }
    }
}
