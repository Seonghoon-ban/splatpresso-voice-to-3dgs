using NUnit.Framework;
using SplatPresso.Placement;
using Unity.Collections;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Pure helpers of <see cref="SceneOrientation"/>: wall-intent precedence, the VERIFY direction buckets, the model's
    /// canonical front axis and the splat shape measured from positions.
    /// </summary>
    public class OrientationIntentTests
    {
        static WallIntent Intent(out string source, string name = "object", string resting = null, string against = null,
            string support = null, string back = null, PlacementTuning t = null)
        {
            var o = new OrientationInputs
            {
                enabled = true, name = name, restingSurface = resting, againstWall = against, support = support, backAgainstWall = back,
            };
            return SceneOrientation.ResolveIntent(o, t ?? new PlacementTuning(), out source);
        }

        [Test]
        public void Intent_FollowsThePrecedenceTable()
        {
            Assert.AreEqual(WallIntent.Mounted, Intent(out var s, support: "wall_mounted", resting: "ground", back: "no"));
            Assert.AreEqual("verify.support", s);

            Assert.AreEqual(WallIntent.Mounted, Intent(out s, resting: "wall"));
            Assert.AreEqual("decide.resting_surface", s);
            Assert.AreEqual(WallIntent.Mounted, Intent(out s, resting: "Wall", support: "other"));

            // VERIFY saw it on the floor: the DECIDE 'wall' is overridden
            Assert.AreEqual(WallIntent.Backed, Intent(out s, resting: "wall", support: "floor", back: "yes"));
            Assert.AreEqual("verify.back_against_wall", s);
            Assert.AreEqual(WallIntent.FreeStanding, Intent(out s, resting: "wall", support: "table_or_furniture", back: "no"));

            Assert.AreEqual(WallIntent.FreeStanding, Intent(out s, back: "no", against: "yes"));
            Assert.AreEqual("verify.back_against_wall", s);

            // unsure falls through to DECIDE
            Assert.AreEqual(WallIntent.Backed, Intent(out s, back: "unsure", against: "yes"));
            Assert.AreEqual("decide.against_wall", s);
            Assert.AreEqual(WallIntent.FreeStanding, Intent(out s, back: "unsure", against: "no"));

            // an explicit "no" beats the category list
            Assert.AreEqual(WallIntent.FreeStanding, Intent(out s, name: "bookshelf", against: "no"));
            Assert.AreEqual(WallIntent.FreeStanding, Intent(out s, name: "wooden bookshelf", back: "no"));

            // categories only without hints (older sessions, mock runs)
            Assert.AreEqual(WallIntent.Backed, Intent(out s, name: "Tall Wooden Bookshelf"));
            Assert.AreEqual("category:bookshelf", s);
            Assert.AreEqual(WallIntent.Backed, Intent(out s, name: "two cabinets", back: "unsure"));
            Assert.AreEqual("category:cabinet", s);
            Assert.AreEqual(WallIntent.Backed, Intent(out s, name: "white tv stand"));
            Assert.AreEqual("category:tv stand", s);
            Assert.AreEqual(WallIntent.Unknown, Intent(out s, name: "bedside lamp"), "whole words only");
            // the category must be the head noun (the name's last word or words), not a modifier
            Assert.AreEqual(WallIntent.Unknown, Intent(out s, name: "desk lamp"));
            Assert.AreEqual(WallIntent.Unknown, Intent(out s, name: "piano bench"));
            Assert.AreEqual(WallIntent.Unknown, Intent(out s, name: "desk chair"));
            Assert.AreEqual(WallIntent.Backed, Intent(out s, name: "oak bookshelf"));
            Assert.AreEqual("category:bookshelf", s);
            Assert.AreEqual(WallIntent.Backed, Intent(out s, name: "chest of drawers"));
            Assert.AreEqual("category:chest of drawers", s);
            Assert.AreEqual(WallIntent.Unknown, Intent(out s, name: "potted plant"));
            Assert.AreEqual("none", s);
            Assert.AreEqual(WallIntent.Unknown, Intent(out s, name: null));

            var off = new PlacementTuning { useCategoryWallHeuristic = false };
            Assert.AreEqual(WallIntent.Unknown, Intent(out s, name: "bookshelf", t: off));
        }

        [Test]
        public void BucketYaw_MapsImageDirectionsAroundTheViewYaw()
        {
            Assert.AreEqual(180f, SceneOrientation.BucketYaw("toward_viewer", 0f).Value, 1e-4f);
            Assert.AreEqual(135f, SceneOrientation.BucketYaw("toward_viewer_right", 0f).Value, 1e-4f);
            Assert.AreEqual(90f, SceneOrientation.BucketYaw("image_right", 0f).Value, 1e-4f);
            Assert.AreEqual(0f, SceneOrientation.BucketYaw("away_from_viewer", 0f).Value, 1e-4f);
            Assert.AreEqual(-90f, SceneOrientation.BucketYaw("image_left", 0f).Value, 1e-4f);
            Assert.AreEqual(-135f, SceneOrientation.BucketYaw("toward_viewer_left", 0f).Value, 1e-4f);
            Assert.IsNull(SceneOrientation.BucketYaw("no_clear_front", 0f));
            Assert.IsNull(SceneOrientation.BucketYaw(null, 0f));
            Assert.IsNull(SceneOrientation.BucketYaw("sideways", 0f));
            // wraps into (-180, 180]
            Assert.AreEqual(-140f, SceneOrientation.BucketYaw("toward_viewer", 40f).Value, 1e-4f);
        }

        [Test]
        public void FrontAxisLocal_MatchesTheYawOffsets()
        {
            Vector3 splat = SceneOrientation.FrontAxisLocal(270f);
            Vector3 mesh = SceneOrientation.FrontAxisLocal(180f);
            Assert.That(Vector3.Distance(splat, new Vector3(1f, 0f, 0f)), Is.LessThan(1e-5f), splat.ToString("F4"));
            Assert.That(Vector3.Distance(mesh, new Vector3(0f, 0f, -1f)), Is.LessThan(1e-5f), mesh.ToString("F4"));
            // the offset turns the canonical front onto root +Z (what Euler(0, psi, 0) points at psi)
            Assert.That(Vector3.Distance(Quaternion.Euler(0f, 270f, 0f) * splat, Vector3.forward), Is.LessThan(1e-5f));
        }

        [Test]
        public void ObjectShape_FromPoints_UsesPercentilesAlongTheFront()
        {
            const int n = 4001;
            var xyz = new NativeArray<float>(n * 3, Allocator.Temp);
            try
            {
                for (int i = 0; i < n; ++i)
                {
                    xyz[3 * i] = -0.1f + 0.4f * i / (n - 1); // x uniform in [-0.1, 0.3]
                    xyz[3 * i + 1] = 0.5f * ((i * 7) % 11) / 10f;
                    xyz[3 * i + 2] = -0.25f + 0.5f * ((i * 13) % 17) / 16f;
                }
                var s = ObjectShape.FromPoints(xyz, n, new Vector3(180f, 0f, 0f), new Vector3(1f, 1f, -1f), 270f);
                Assert.IsTrue(s.valid);
                Assert.That(s.backExtent, Is.EqualTo(0.092f).Within(0.005f));
                Assert.That(s.frontExtent, Is.EqualTo(0.292f).Within(0.005f));
                Assert.That(s.width, Is.EqualTo(0.5f).Within(0.05f));

                var few = ObjectShape.FromPoints(xyz, 50, new Vector3(180f, 0f, 0f), new Vector3(1f, 1f, -1f), 270f);
                Assert.IsFalse(few.valid, "fewer than 100 samples");
            }
            finally
            {
                xyz.Dispose();
            }
        }

        [Test]
        public void ObjectShape_FromCenteredSize_IsHalfTheDepth()
        {
            var s = ObjectShape.FromCenteredSize(new Vector3(1f, 1f, 0.4f), SceneOrientation.FrontAxisLocal(180f));
            Assert.IsTrue(s.valid);
            Assert.That(s.backExtent, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(s.frontExtent, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(s.width, Is.EqualTo(1f).Within(1e-5f));
        }
    }
}
