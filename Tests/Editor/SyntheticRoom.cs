using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Analytic room for the orientation tests: planes (point + normal, optional axis-aligned bounds and rectangular holes)
    /// rendered into eye depth with the same camera model as <see cref="CaptureResult.UnprojectToWorld"/>. The capture
    /// depth is the room WITHOUT the inserted object (as in the pipeline, where the object only exists in the edited
    /// image); objects are described by their mask (<see cref="MaskFromBox"/>).
    /// </summary>
    internal sealed class SyntheticRoom
    {
        public sealed class Plane
        {
            public Vector3 point;
            public Vector3 normal;
            /// <summary>Optional axis-aligned bounds of the plane's extent (world).</summary>
            public Bounds? bounds;
            /// <summary>Optional axis-aligned holes (world boxes) where the plane is absent.</summary>
            public List<Bounds> holes = new List<Bounds>();
        }

        public int width = 640;
        public int height = 360;
        public float verticalFovDeg = 60f;
        public Vector3 cameraPosition = new Vector3(0f, 1.6f, 0f);
        public Quaternion cameraRotation = Quaternion.Euler(12f, 40f, 0f);
        public readonly List<Plane> planes = new List<Plane>();

        public float Focal => 0.5f * height / Mathf.Tan(0.5f * verticalFovDeg * Mathf.Deg2Rad);

        public SyntheticRoom AddPlane(Vector3 point, Vector3 normal, Bounds? bounds = null, params Bounds[] holes)
        {
            var p = new Plane { point = point, normal = normal.normalized, bounds = bounds };
            if (holes != null)
                p.holes.AddRange(holes);
            planes.Add(p);
            return this;
        }

        /// <summary>Scene A: 640x360, 60 deg vertical FOV, camera (0, 1.6, 0) pitched 12 deg down and turned 40 deg right;
        /// floor y = 0, right wall x = 2.5 (normal -X, yaw -90), back wall z = 7 (normal -Z, yaw 180).</summary>
        public static SyntheticRoom SceneA(float rightWallX = 2.5f)
        {
            var room = new SyntheticRoom();
            room.AddPlane(Vector3.zero, Vector3.up);
            room.AddPlane(new Vector3(rightWallX, 0f, 0f), Vector3.left);
            room.AddPlane(new Vector3(0f, 0f, 7f), Vector3.back);
            return room;
        }

        /// <summary>World direction of the ray through a pixel centre (its camera-space z is 1, so t is eye depth).</summary>
        public Vector3 RayDir(float u, float v) =>
            cameraRotation * new Vector3((u + 0.5f - width * 0.5f) / Focal, -(v + 0.5f - height * 0.5f) / Focal, 1f);

        /// <summary>Eye depth of the nearest plane hit at a pixel; 0 = nothing hit.</summary>
        public float DepthAt(int u, int v)
        {
            Vector3 dir = RayDir(u, v);
            float best = float.PositiveInfinity;
            foreach (var p in planes)
            {
                float den = Vector3.Dot(p.normal, dir);
                if (den >= -1e-6f)
                    continue;
                float t = Vector3.Dot(p.normal, p.point - cameraPosition) / den;
                if (t <= 0f || t >= best)
                    continue;
                Vector3 hit = cameraPosition + dir * t;
                if (p.bounds.HasValue && !Contains(p.bounds.Value, hit))
                    continue;
                bool inHole = false;
                foreach (var h in p.holes)
                {
                    if (Contains(h, hit))
                    {
                        inHole = true;
                        break;
                    }
                }
                if (inHole)
                    continue;
                best = t;
            }
            return float.IsPositiveInfinity(best) ? 0f : best;
        }

        static bool Contains(Bounds b, Vector3 p)
        {
            const float e = 1e-3f;
            Vector3 min = b.min, max = b.max;
            return p.x >= min.x - e && p.x <= max.x + e && p.y >= min.y - e && p.y <= max.y + e && p.z >= min.z - e && p.z <= max.z + e;
        }

        public float[] RenderDepth()
        {
            var d = new float[width * height];
            for (int v = 0; v < height; ++v)
            for (int u = 0; u < width; ++u)
                d[v * width + u] = DepthAt(u, v);
            return d;
        }

        public CaptureResult ToCapture(float[] depth = null) => new CaptureResult
        {
            width = width,
            height = height,
            depthMeters = depth ?? RenderDepth(),
            cameraPosition = cameraPosition,
            cameraRotation = cameraRotation,
            verticalFovDeg = verticalFovDeg,
            nearPlane = 0.1f,
            farPlane = 100f,
        };

        /// <summary>Pixel coordinates (u, v; top-left origin, pixel-centre convention of the unprojection) of a world point.</summary>
        public Vector2 Project(Vector3 world)
        {
            Vector3 c = Quaternion.Inverse(cameraRotation) * (world - cameraPosition);
            float u = c.x / c.z * Focal + width * 0.5f - 0.5f;
            float v = -c.y / c.z * Focal + height * 0.5f - 0.5f;
            return new Vector2(u, v);
        }

        /// <summary>Mask of a world box: its 8 projected corners, convex fill (pixel centres inside the hull).</summary>
        public bool[] MaskFromBox(Vector3 min, Vector3 max)
        {
            var pts = new List<Vector2>(8);
            for (int i = 0; i < 8; ++i)
                pts.Add(Project(new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z)));
            var hull = ConvexHull(pts);
            var mask = new bool[width * height];
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var p in hull)
            {
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
            }
            int u0 = Mathf.Clamp(Mathf.FloorToInt(x0), 0, width - 1), u1 = Mathf.Clamp(Mathf.CeilToInt(x1), 0, width - 1);
            int v0 = Mathf.Clamp(Mathf.FloorToInt(y0), 0, height - 1), v1 = Mathf.Clamp(Mathf.CeilToInt(y1), 0, height - 1);
            for (int v = v0; v <= v1; ++v)
            for (int u = u0; u <= u1; ++u)
                if (InsideConvex(hull, new Vector2(u, v)))
                    mask[v * width + u] = true;
            return mask;
        }

        /// <summary>Tight normalized box of a mask (x, y, w, h).</summary>
        public Bbox BboxOf(bool[] mask)
        {
            int x0 = width, x1 = -1, y0 = height, y1 = -1;
            for (int v = 0; v < height; ++v)
            for (int u = 0; u < width; ++u)
            {
                if (!mask[v * width + u])
                    continue;
                x0 = Mathf.Min(x0, u); x1 = Mathf.Max(x1, u);
                y0 = Mathf.Min(y0, v); y1 = Mathf.Max(y1, v);
            }
            if (x1 < 0)
                return default;
            return new Bbox((float)x0 / width, (float)y0 / height, (float)(x1 - x0 + 1) / width, (float)(y1 - y0 + 1) / height);
        }

        /// <summary>
        /// Splat-like depth: constant depth per block (the block centre's depth), relative Gaussian noise per block and
        /// "floaters" (a block pulled toward the camera by U(0.3, 0.9)); seeded.
        /// </summary>
        public static float[] Blockify(float[] depth, int w, int h, int block, float relNoise, float floaterFrac, int seed)
        {
            var rng = new System.Random(seed);
            var outD = new float[depth.Length];
            for (int by = 0; by < h; by += block)
            for (int bx = 0; bx < w; bx += block)
            {
                int cx = Mathf.Min(bx + block / 2, w - 1), cy = Mathf.Min(by + block / 2, h - 1);
                float d0 = depth[cy * w + cx];
                double g = Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
                float factor = 1f + relNoise * (float)g;
                if (rng.NextDouble() < floaterFrac)
                    factor *= 0.3f + 0.6f * (float)rng.NextDouble();
                float d = d0 > 0f ? d0 * factor : 0f;
                for (int y = by; y < Mathf.Min(by + block, h); ++y)
                for (int x = bx; x < Mathf.Min(bx + block, w); ++x)
                    outD[y * w + x] = depth[y * w + x] > 0f ? d : 0f;
            }
            return outD;
        }

        static List<Vector2> ConvexHull(List<Vector2> pts)
        {
            pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var hull = new List<Vector2>();
            for (int pass = 0; pass < 2; ++pass)
            {
                int start = hull.Count;
                for (int k = 0; k < pts.Count; ++k)
                {
                    var p = pass == 0 ? pts[k] : pts[pts.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        static bool InsideConvex(List<Vector2> hull, Vector2 p)
        {
            if (hull.Count < 3)
                return false;
            bool pos = false, neg = false;
            for (int i = 0; i < hull.Count; ++i)
            {
                float c = Cross(hull[i], hull[(i + 1) % hull.Count], p);
                if (c > 0f) pos = true;
                if (c < 0f) neg = true;
                if (pos && neg)
                    return false;
            }
            return true;
        }
    }
}
