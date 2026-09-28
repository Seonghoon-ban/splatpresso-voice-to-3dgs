using NUnit.Framework;
using SplatPresso.Placement;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Wall-aware orientation in <see cref="SplatPlacement.Solve"/> on <see cref="SyntheticRoom"/> scenes. Scene A: camera
    /// (0, 1.6, 0) pitched 12 deg down, turned 40 deg right; right wall x = 2.5 (yaw -90), back wall z = 7 (yaw 180). A
    /// painting on the right wall is seen about 50 deg off its normal, so camera-facing is visibly wrong there.
    /// </summary>
    public class SceneOrientationTests
    {
        static readonly Vector3 kPaintingMin = new Vector3(2.47f, 1.2f, 2.8f);
        static readonly Vector3 kPaintingMax = new Vector3(2.5f, 1.8f, 3.6f);
        static readonly Vector3 kPaintingBounds = new Vector3(0.06f, 1f, 0.8f);

        static OrientationInputs Inputs(string name, string resting = "ground", string against = null, string support = null,
            string back = null, string front = null, ObjectShape shape = default) => new OrientationInputs
        {
            enabled = true,
            name = name,
            restingSurface = resting,
            againstWall = against,
            support = support,
            backAgainstWall = back,
            frontFaces = front,
            shape = shape,
        };

        static ObjectShape Shape(float back, float width) => new ObjectShape { valid = true, backExtent = back, frontExtent = back, width = width };

        static OrientationInputs PaintingInputs(float back = 0.03f) => Inputs("painting", resting: "wall", shape: Shape(back, 0.8f));

        static PlacementTuning Tuning(OrientationMode mode = OrientationMode.SceneAware) =>
            new PlacementTuning { orientationMode = mode };

        struct Case
        {
            public SyntheticRoom room;
            public CaptureResult capture;
            public bool[] mask;
            public Bbox box;
        }

        static Case Make(SyntheticRoom room, Vector3 min, Vector3 max, float[] depth = null)
        {
            var c = new Case { room = room, capture = room.ToCapture(depth) };
            c.mask = room.MaskFromBox(min, max);
            c.box = room.BboxOf(c.mask);
            return c;
        }

        static SplatPlacement.PlacementSolution Solve(Case c, OrientationInputs inputs, Vector3 bounds, PlacementTuning tuning,
            float sizeHint = 0f, bool useMask = true, bool isMesh = false) =>
            SplatPlacement.Solve(c.capture, c.box, sizeHint, null, useMask ? c.mask : null, bounds, tuning, isMesh, inputs);

        static void AssertSameAsLegacy(SplatPlacement.PlacementSolution a, SplatPlacement.PlacementSolution legacy)
        {
            Assert.AreEqual(legacy.valid, a.valid);
            Assert.AreEqual(legacy.position, a.position, "position must be bit-identical");
            Assert.AreEqual(legacy.rotation.x, a.rotation.x);
            Assert.AreEqual(legacy.rotation.y, a.rotation.y);
            Assert.AreEqual(legacy.rotation.z, a.rotation.z);
            Assert.AreEqual(legacy.rotation.w, a.rotation.w);
            Assert.AreEqual(legacy.uniformScale, a.uniformScale);
            Assert.AreEqual(legacy.note, a.note);
        }

        static float FrontYaw(SplatPlacement.PlacementSolution s, PlacementTuning t, bool isMesh = false) =>
            Mathf.DeltaAngle(0f, s.rotation.eulerAngles.y - SplatPlacement.YawOffset(t, isMesh));

        static string Diag(SplatPlacement.PlacementSolution s) =>
            s.note + " | " + (SceneOrientation.Describe("obj", s.orientation) ?? "-") + " | pos " + s.position.ToString("F3");

        static void AssertWall(SplatPlacement.PlacementSolution s, float expectedPsi, float tol, string what)
        {
            what = what + " [" + Diag(s) + "]";
            Assert.IsTrue(s.valid, Diag(s));
            Assert.AreEqual(YawRule.Wall, s.orientation.candidate, what + ": " + s.note);
            Assert.AreEqual(YawRule.Wall, s.orientation.applied, what + ": " + s.note);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(s.orientation.wallYawDeg, expectedPsi)), Is.LessThan(tol), what + ": " + s.note);
            // never facing away (test 18)
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(s.orientation.wallYawDeg, s.orientation.cameraYawDeg)), Is.LessThan(81.5f), what);
        }

        static void AssertCameraFacing(SplatPlacement.PlacementSolution s, SplatPlacement.PlacementSolution legacy, string what)
        {
            what = what + " [" + Diag(s) + "]";
            Assert.IsTrue(s.valid, Diag(s));
            Assert.AreEqual(YawRule.CameraFacing, s.orientation.applied, what + ": " + s.note);
            Assert.AreEqual(legacy.position, s.position, what + ": position unchanged");
            Assert.AreEqual(legacy.rotation, s.rotation, what + ": rotation unchanged");
            Assert.AreEqual(legacy.uniformScale, s.uniformScale, what + ": scale unchanged");
        }

        // ---- 1, 2: legacy identity ----------------------------------------------------------------------------

        [Test]
        public void DefaultInputs_AreBitIdenticalToTheLegacyCall()
        {
            // floor-only capture (the SplatPlacementTests camera) and Scene A
            var floorRoom = new SyntheticRoom { width = 320, height = 180, cameraRotation = Quaternion.Euler(20f, 0f, 0f) };
            floorRoom.AddPlane(Vector3.zero, Vector3.up);
            var floorCase = Make(floorRoom, new Vector3(-0.2f, 0f, 3f), new Vector3(0.2f, 0.8f, 3.3f));
            var a = SplatPlacement.Solve(floorCase.capture, floorCase.box, 0.9f, null, floorCase.mask, Vector3.one, new PlacementTuning());
            var b = SplatPlacement.Solve(floorCase.capture, floorCase.box, 0.9f, null, floorCase.mask, Vector3.one, new PlacementTuning(), false, default);
            AssertSameAsLegacy(b, a);

            var sceneA = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var c = SplatPlacement.Solve(sceneA.capture, sceneA.box, 0.8f, null, sceneA.mask, kPaintingBounds, new PlacementTuning());
            var d = SplatPlacement.Solve(sceneA.capture, sceneA.box, 0.8f, null, sceneA.mask, kPaintingBounds, new PlacementTuning(), false, default);
            AssertSameAsLegacy(d, c);
            Assert.AreEqual(YawRule.CameraFacing, d.orientation.applied);
        }

        [Test]
        public void CameraFacingMode_WithMountedIntent_IsBitIdentical()
        {
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var legacy = Solve(c, default, kPaintingBounds, new PlacementTuning(), 0.8f);
            var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(OrientationMode.CameraFacing), 0.8f);
            AssertSameAsLegacy(s, legacy);
        }

        // ---- 3, 4, 5: wall-hung ----------------------------------------------------------------------------------

        [Test]
        public void Mounted_ObliquePainting_TurnsToTheWallAndHangsFlush()
        {
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var t = Tuning();
            var s = Solve(c, PaintingInputs(), kPaintingBounds, t, 0.8f);
            AssertWall(s, -90f, 1.5f, "painting");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(s.orientation.cameraYawDeg, -90f)), Is.GreaterThan(35f), "the view is oblique");
            Assert.That(Quaternion.Angle(s.rotation, Quaternion.Euler(0f, 180f, 0f)), Is.LessThan(1.5f));
            float scale = s.uniformScale;
            Assert.That(s.position.x, Is.EqualTo(2.5f - (0.03f * scale + 0.01f)).Within(0.03f), Diag(s));
            float hModel = kPaintingBounds.y * scale;
            Assert.That(s.position.y, Is.EqualTo(1.5f - 0.5f * hModel).Within(0.05f), Diag(s));
            Assert.That(s.position.z, Is.EqualTo(3.2f).Within(0.1f), Diag(s));
            Assert.AreEqual("center-ray", s.orientation.reanchor);
            StringAssert.StartsWith("yaw:wall(Mounted/decide.resting_surface", Diag(s));
            Assert.AreEqual(WallIntent.Mounted, s.orientation.intent);
        }

        [Test]
        public void Mounted_ThickModel_StandoffIsCapped()
        {
            // a mounted head really sticks out of the wall: it keeps its depth, only the standoff is capped
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var s = Solve(c, Inputs("deer head", resting: "wall", shape: Shape(0.8f, 0.8f)), kPaintingBounds, Tuning(), 0.8f);
            AssertWall(s, -90f, 1.5f, "thick deer head");
            Assert.IsTrue(s.orientation.standoffClamped);
            Assert.That(s.orientation.standoffM, Is.EqualTo(0.31f).Within(1e-4f));
            Assert.That(s.position.x, Is.EqualTo(2.5f - 0.31f).Within(0.03f));
        }

        [Test]
        public void Mounted_AnchorOnFurniture_IsAcceptedThroughTheCentreCheck()
        {
            // a sofa back (x = 2.0, up to 1.1 m) in front of the wall hides the painting's bottom edge: the legacy anchor
            // lands on the sofa, 0.5 m in front of the wall
            var room = SyntheticRoom.SceneA();
            room.AddPlane(new Vector3(2.0f, 0f, 0f), Vector3.left, new Bounds(new Vector3(2.0f, 0.55f, 3.25f), new Vector3(0.001f, 1.1f, 2.5f)));
            var c = Make(room, new Vector3(2.47f, 0.9f, 2.8f), new Vector3(2.5f, 1.5f, 3.6f));
            var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f);
            AssertWall(s, -90f, 1.5f, "painting over a sofa");
            Assert.That(s.orientation.wall.anchorDistanceM, Is.EqualTo(0.5f).Within(0.1f), Diag(s));
            Assert.IsTrue(s.orientation.wall.centerValid);
            Assert.AreEqual("center-ray", s.orientation.reanchor);
            Assert.That(s.position.x, Is.EqualTo(2.5f - s.orientation.standoffM).Within(0.03f), Diag(s));
        }

        // ---- 6, 7, 8, 9: wall-backed and rejections -------------------------------------------------------------

        [Test]
        public void Backed_Bookshelf_TurnsToTheWallAndMovesBackOntoIt()
        {
            var c = Make(SyntheticRoom.SceneA(), new Vector3(1.4f, 0f, 3.0f), new Vector3(1.7f, 1.6f, 4.2f));
            var bounds = new Vector3(0.3f, 1.6f, 1.2f);
            var s = Solve(c, Inputs("bookshelf", against: "yes", shape: Shape(0.15f, 1.2f)), bounds, Tuning(), 1.6f);
            AssertWall(s, -90f, 2f, "bookshelf");
            Assert.AreEqual("decide.against_wall", s.orientation.intentSource);
            float back = 0.15f * s.uniformScale;
            Assert.That(2.5f - (s.position.x + back), Is.EqualTo(0.01f).Within(0.05f), Diag(s));
            Assert.That(s.position.y, Is.EqualTo(0f).Within(0.03f), Diag(s));
            Assert.That(s.position.z, Is.EqualTo(3.6f).Within(0.3f), Diag(s));
            Assert.AreEqual("footprint", s.orientation.reanchor, Diag(s));
        }

        [Test]
        public void FreeStandingPlant_KeepsFacingTheCamera()
        {
            // a tall (1.2 m) plant whose back is 1.5 m in front of the right wall; the wall behind it is visible above it
            // (a short object's sampled region shows too little wall, so the height gate would reject it first)
            var c = Make(SyntheticRoom.SceneA(), new Vector3(0.8f, 0f, 1.67f), new Vector3(1.0f, 1.2f, 1.87f));
            var bounds = new Vector3(0.3f, 1.2f, 0.3f);
            var legacy = Solve(c, default, bounds, Tuning(), 0.6f);

            var unknown = Solve(c, Inputs("potted plant"), bounds, Tuning(), 0.6f);
            AssertCameraFacing(unknown, legacy, "unknown intent");
            Assert.AreEqual(legacy.note, unknown.note, "Unknown adds no note");

            var no = Solve(c, Inputs("potted plant", against: "no"), bounds, Tuning(), 0.6f);
            AssertCameraFacing(no, legacy, "against_wall no");
            Assert.AreEqual(WallIntent.FreeStanding, no.orientation.intent);

            var backed = Solve(c, Inputs("potted plant", against: "yes"), bounds, Tuning(), 0.6f);
            AssertCameraFacing(backed, legacy, "backed but 1.5 m from the wall");
            Assert.AreEqual(YawRule.CameraFacing, backed.orientation.candidate);
            StringAssert.StartsWith("dist", backed.orientation.wall.reject, Diag(backed));
            StringAssert.Contains("yaw:camera(Backed/decide.against_wall,reject=dist", Diag(backed));
        }

        [Test]
        public void CabinetWithOnlyFarWallsBehindIt_IsRejectedByDistance()
        {
            // right wall at x = 6: the wall fitted behind the cabinet (the back wall) is about 4 m away
            var c = Make(SyntheticRoom.SceneA(rightWallX: 6f), new Vector3(1.5f, 0f, 3.0f), new Vector3(1.9f, 0.9f, 4.0f));
            var bounds = new Vector3(0.4f, 0.9f, 1.0f);
            var legacy = Solve(c, default, bounds, Tuning(), 1.0f);
            var s = Solve(c, Inputs("cabinet"), bounds, Tuning(), 1.0f);
            Assert.AreEqual("category:cabinet", s.orientation.intentSource);
            AssertCameraFacing(s, legacy, "cabinet");
            Assert.AreEqual(YawRule.CameraFacing, s.orientation.candidate, Diag(s));
            StringAssert.StartsWith("dist", s.orientation.wall.reject, Diag(s));
        }

        [Test]
        public void CabinetInFrontOfALowFurnitureFront_IsRejectedByTheHeightGate()
        {
            // no walls: the only vertical surface behind the cabinet is a 0.5 m high furniture front at x = 2.0
            var room = new SyntheticRoom();
            room.AddPlane(Vector3.zero, Vector3.up);
            room.AddPlane(new Vector3(2.0f, 0f, 0f), Vector3.left, new Bounds(new Vector3(2.0f, 0.25f, 3.5f), new Vector3(0.001f, 0.5f, 2.5f)));
            var c = Make(room, new Vector3(1.5f, 0f, 3.0f), new Vector3(1.9f, 0.9f, 4.0f));
            var bounds = new Vector3(0.4f, 0.9f, 1.0f);
            var legacy = Solve(c, default, bounds, Tuning(), 1.0f);
            var s = Solve(c, Inputs("cabinet", against: "yes"), bounds, Tuning(), 1.0f);
            AssertCameraFacing(s, legacy, "cabinet");
            StringAssert.StartsWith("yspan", s.orientation.wall.reject, Diag(s));
        }

        [Test]
        public void PaintingSeenAlongItsWall_IsRejectedAsGrazing()
        {
            // camera 0.3 m from the right wall looking along it (no back wall): the wall is seen about 85 deg off its normal
            var room = new SyntheticRoom { cameraPosition = new Vector3(2.2f, 1.6f, 0f), cameraRotation = Quaternion.Euler(10f, 0f, 0f) };
            room.AddPlane(Vector3.zero, Vector3.up);
            room.AddPlane(new Vector3(2.5f, 0f, 0f), Vector3.left);
            var c = Make(room, new Vector3(2.47f, 1.2f, 3.0f), new Vector3(2.5f, 1.8f, 3.8f));
            var legacy = Solve(c, default, kPaintingBounds, Tuning(), 0.8f);
            var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f);
            AssertCameraFacing(s, legacy, "grazing painting");
            StringAssert.StartsWith("grazing", s.orientation.wall.reject, Diag(s));
        }

        [Test]
        public void Window_BehindTheWall_IsRejected()
        {
            var room = new SyntheticRoom();
            room.AddPlane(Vector3.zero, Vector3.up);
            room.AddPlane(new Vector3(2.5f, 0f, 0f), Vector3.left, null, new Bounds(new Vector3(2.5f, 1.45f, 3.95f), new Vector3(0.2f, 1.3f, 0.7f)));
            room.AddPlane(new Vector3(5.5f, 0f, 0f), Vector3.left);
            room.AddPlane(new Vector3(0f, 0f, 7f), Vector3.back);
            var c = Make(room, kPaintingMin, kPaintingMax);
            var legacy = Solve(c, default, kPaintingBounds, Tuning(), 0.8f);
            var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f);
            AssertCameraFacing(s, legacy, "window");
            StringAssert.StartsWith("behind", s.orientation.wall.reject, Diag(s));
        }

        // ---- 10: corners --------------------------------------------------------------------------------------

        [Test]
        public void PaintingsNearACorner_UseTheSinglePlaneBehindThem()
        {
            // the adjacent wall is too small a part of the sampled region to count as a corner
            var onBack = Make(SyntheticRoom.SceneA(), new Vector3(1.6f, 1.2f, 6.97f), new Vector3(2.3f, 1.8f, 7.0f));
            var s1 = Solve(onBack, Inputs("painting", resting: "wall", shape: Shape(0.03f, 0.7f)), new Vector3(0.06f, 1f, 0.7f), Tuning(), 0.7f);
            AssertWall(s1, 180f, 1.5f, "painting on the back wall");
            Assert.IsFalse(s1.orientation.corner, Diag(s1));

            var onRight = Make(SyntheticRoom.SceneA(), new Vector3(2.47f, 1.2f, 6.2f), new Vector3(2.5f, 1.8f, 6.9f));
            var s2 = Solve(onRight, Inputs("painting", resting: "wall", shape: Shape(0.03f, 0.7f)), new Vector3(0.06f, 1f, 0.7f), Tuning(), 0.7f);
            AssertWall(s2, -90f, 1.5f, "painting on the right wall");
            Assert.IsFalse(s2.orientation.corner, Diag(s2));
        }

        static SyntheticRoom SceneAFrom(Vector3 cameraPosition, Quaternion cameraRotation)
        {
            var room = SyntheticRoom.SceneA();
            room.cameraPosition = cameraPosition;
            room.cameraRotation = cameraRotation;
            return room;
        }

        static readonly Vector3 kCornerShelfBounds = new Vector3(0.4f, 1.8f, 1.2f);

        static OrientationInputs CornerShelfInputs() => Inputs("bookshelf", against: "yes", shape: Shape(0.2f, 1.2f));

        [Test]
        public void Corner_Backed_KeepsTheNearerFirstPlane()
        {
            // plane 1 is the right wall (0.2 m behind the anchor), plane 2 the back wall (farther): plane 1 is kept
            var room = SceneAFrom(new Vector3(0f, 1.6f, 0f), Quaternion.Euler(10f, 0f, 0f));
            var c = Make(room, new Vector3(2.1f, 0f, 5.6f), new Vector3(2.5f, 1.8f, 6.8f));
            var s = Solve(c, CornerShelfInputs(), kCornerShelfBounds, Tuning(), 1.8f);
            Assert.IsTrue(s.orientation.corner, Diag(s));
            AssertWall(s, -90f, 2f, "bookshelf in the corner (plane 1)");
        }

        [Test]
        public void Corner_Backed_SwitchesToTheNearerSecondPlane()
        {
            // plane 1 is the back wall (1.2 m in front of it), plane 2 the right wall the bookshelf stands against
            var room = SceneAFrom(new Vector3(1f, 1.6f, 0f), Quaternion.Euler(10f, 0f, 0f));
            var c = Make(room, new Vector3(2.05f, 0f, 5.8f), new Vector3(2.5f, 1.8f, 6.95f));
            var s = Solve(c, CornerShelfInputs(), kCornerShelfBounds, Tuning(), 1.8f);
            Assert.IsTrue(s.orientation.corner, Diag(s));
            AssertWall(s, -90f, 2f, "bookshelf in the corner (plane 2)");
            Assert.That(s.orientation.wall.anchorDistanceM, Is.LessThan(0.5f), Diag(s));
        }

        [Test]
        public void Corner_Mounted_TieFollowsTheFrontFacesDirection()
        {
            // a small clock right in the corner: the centre is on both walls (a tie), so VERIFY's front_faces decides
            var room = SceneAFrom(new Vector3(-1f, 1.6f, 4f), Quaternion.Euler(10f, 40f, 0f));
            var c = Make(room, new Vector3(2.35f, 1.4f, 6.85f), new Vector3(2.5f, 1.6f, 7.0f));
            var bounds = new Vector3(0.06f, 0.2f, 0.15f);

            var left = Solve(c, Inputs("clock", resting: "wall", front: "toward_viewer_left", shape: Shape(0.03f, 0.15f)), bounds, Tuning(), 0.2f);
            Assert.IsTrue(left.orientation.corner, Diag(left));
            AssertWall(left, -90f, 2f, "front_faces toward_viewer_left: the right wall");

            var right = Solve(c, Inputs("clock", resting: "wall", front: "toward_viewer_right", shape: Shape(0.03f, 0.15f)), bounds, Tuning(), 0.2f);
            Assert.IsTrue(right.orientation.corner, Diag(right));
            AssertWall(right, 180f, 2f, "front_faces toward_viewer_right: the back wall");
        }

        [Test]
        public void Backed_NearerPerpendicularWallThatIsNoCorner_IsAmbiguous()
        {
            // the right wall (0.2 m behind the bookshelf) is seen too obliquely to pass the grazing gate, so it is not a
            // corner; plane 1 is the back wall 1.2 m away. Snapping to it would move the bookshelf against the wrong wall.
            var room = SceneAFrom(new Vector3(1.5f, 1.6f, 0f), Quaternion.Euler(10f, 0f, 0f));
            var c = Make(room, new Vector3(2.05f, 0f, 5.8f), new Vector3(2.5f, 1.8f, 6.95f));
            var legacy = Solve(c, default, kCornerShelfBounds, Tuning(), 1.8f);
            var s = Solve(c, CornerShelfInputs(), kCornerShelfBounds, Tuning(), 1.8f);
            AssertCameraFacing(s, legacy, "bookshelf by the nearer wall");
            StringAssert.StartsWith("corner-ambiguous", s.orientation.wall.reject, Diag(s));
        }

        // ---- 11: splat-like depth -----------------------------------------------------------------------------

        [Test]
        public void SplatNoise_StaysWithinThreeDegreesOrFallsBack()
        {
            var room = SyntheticRoom.SceneA();
            float[] clean = room.RenderDepth();
            int accepted = 0;
            for (int seed = 1; seed <= 10; ++seed)
            {
                float[] noisy = SyntheticRoom.Blockify(clean, room.width, room.height, 8, 0.01f, 0.01f, seed);
                var c = Make(room, kPaintingMin, kPaintingMax, noisy);
                var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f);
                Assert.IsTrue(s.valid, Diag(s));
                if (s.orientation.applied != YawRule.Wall)
                    continue;
                float err = Mathf.Abs(Mathf.DeltaAngle(s.orientation.wallYawDeg, -90f));
                Assert.That(err, Is.LessThan(5f), $"seed {seed}: {s.note}");
                Assert.That(err, Is.LessThan(3f), $"seed {seed}: {s.note}");
                accepted++;
            }
            Assert.That(accepted, Is.GreaterThanOrEqualTo(7), "accepted " + accepted + "/10");
        }

        // ---- 12, 13, 14: shadow, mesh, no mask ---------------------------------------------------------------

        [Test]
        public void ShadowMode_ComputesTheWallButKeepsTheLegacyPose()
        {
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var legacy = Solve(c, default, kPaintingBounds, Tuning(), 0.8f);
            var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(OrientationMode.Shadow), 0.8f);
            Assert.AreEqual(YawRule.Wall, s.orientation.candidate);
            Assert.AreEqual(YawRule.CameraFacing, s.orientation.applied);
            Assert.AreEqual(legacy.position, s.position);
            Assert.AreEqual(legacy.rotation, s.rotation);
            Assert.AreEqual(legacy.uniformScale, s.uniformScale);
            StringAssert.StartsWith("yaw:shadow-wall(", Diag(s));
        }

        [Test]
        public void Mesh_UsesTheMeshFrontAndCentredDepth()
        {
            var shape = ObjectShape.FromCenteredSize(new Vector3(1f, 1f, 0.4f), new Vector3(0f, 0f, -1f));
            Assert.That(shape.backExtent, Is.EqualTo(0.2f).Within(1e-5f));
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var t = Tuning();
            var s = Solve(c, Inputs("painting", resting: "wall", shape: shape), new Vector3(1f, 1f, 0.4f), t, 0.8f, isMesh: true);
            AssertWall(s, -90f, 1.5f, "mesh painting");
            Assert.That(Quaternion.Angle(s.rotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(1.5f));
        }

        [Test]
        public void NoMask_DirectMode_StillFindsTheWall()
        {
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f, useMask: false);
            AssertWall(s, -90f, 1.5f, "no mask");
        }

        // ---- 15, 16, 17: preconditions ------------------------------------------------------------------------

        [Test]
        public void CornerFormatBoxWithoutMask_IsSuspect()
        {
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            c.box = Bbox.FromXYWHNorm(new[] { 0.62f, 0.338f, 0.72f, 0.468f });
            var legacy = Solve(c, default, kPaintingBounds, Tuning(), 0.8f, useMask: false);
            var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f, useMask: false);
            AssertCameraFacing(s, legacy, "suspect box");
            Assert.AreEqual("bbox-suspect", s.orientation.wall.reject);
            StringAssert.Contains("reject=bbox-suspect", Diag(s));
        }

        [Test]
        public void NoDepth_FallsBackWithoutAnException()
        {
            var room = SyntheticRoom.SceneA();
            var c = Make(room, kPaintingMin, kPaintingMax);
            c.capture.depthMeters = null;
            var legacy = Solve(c, default, kPaintingBounds, Tuning(), 0.8f);
            var s = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f);
            AssertCameraFacing(s, legacy, "no depth");
            Assert.AreEqual("no-depth", s.orientation.wall.reject);
            StringAssert.DoesNotContain("orient-error", Diag(s));
        }

        [Test]
        public void FlatContent_IsNotSnapped()
        {
            var c = Make(SyntheticRoom.SceneA(), new Vector3(1.4f, 0f, 3.0f), new Vector3(1.7f, 1.6f, 4.2f));
            var bounds = new Vector3(1f, 0.04f, 1f);
            var legacy = Solve(c, default, bounds, Tuning(), 1.6f);
            var s = Solve(c, Inputs("bookshelf", against: "yes"), bounds, Tuning(), 1.6f);
            AssertCameraFacing(s, legacy, "flat");
            Assert.AreEqual("flat", s.orientation.wall.reject);
        }

        // ---- 19: determinism ----------------------------------------------------------------------------------

        [Test]
        public void Evaluate_IsDeterministic()
        {
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var a = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f);
            var b = Solve(c, PaintingInputs(), kPaintingBounds, Tuning(), 0.8f);
            Assert.AreEqual(a.position, b.position);
            Assert.AreEqual(a.rotation, b.rotation);
            Assert.AreEqual(a.orientation.wall.inliers, b.orientation.wall.inliers);
            Assert.AreEqual(a.orientation.wall.normal, b.orientation.wall.normal);
            Assert.AreEqual(a.note, b.note);
        }

        // ---- record -------------------------------------------------------------------------------------------

        [Test]
        public void Record_IsFiniteAndDescribesTheSnap()
        {
            var c = Make(SyntheticRoom.SceneA(), kPaintingMin, kPaintingMax);
            var t = Tuning();
            var s = Solve(c, PaintingInputs(), kPaintingBounds, t, 0.8f);
            var rec = SceneOrientation.ToRecord(s, t, false);
            Assert.AreEqual("Wall", rec.rule);
            Assert.AreEqual("Mounted", rec.intent);
            Assert.AreEqual("SceneAware", rec.mode);
            Assert.IsTrue(rec.wallFound);
            Assert.IsTrue(rec.flushApplied);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(rec.appliedFrontYawDeg, -90f)), Is.LessThan(1.5f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(rec.rotationYawDeg, 180f)), Is.LessThan(1.5f));
            Assert.AreEqual(270f, rec.yawOffsetDeg);
            string json = JsonUtil.Serialize(rec);
            StringAssert.DoesNotContain("NaN", json);
            StringAssert.DoesNotContain("Infinity", json);

            var legacy = SceneOrientation.ToRecord(Solve(c, default, kPaintingBounds, t, 0.8f), t, false);
            Assert.AreEqual("CameraFacing", legacy.rule);
            Assert.AreEqual("CameraFacing", legacy.mode);
            Assert.IsFalse(legacy.wallFound);
        }
    }
}
