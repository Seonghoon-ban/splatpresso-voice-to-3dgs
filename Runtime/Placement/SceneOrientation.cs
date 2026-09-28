using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEngine;

namespace SplatPresso.Placement
{
    /// <summary>How an object relates to a wall, from the vision-model hints (or the object's name).</summary>
    public enum WallIntent
    {
        /// <summary>No hint: keeps the camera-facing yaw.</summary>
        Unknown = 0,
        /// <summary>Stands free in the room: keeps the camera-facing yaw.</summary>
        FreeStanding = 1,
        /// <summary>Stands with its back to a wall (bookshelf, cabinet, bed...).</summary>
        Backed = 2,
        /// <summary>Hangs on a wall (painting, clock, mounted head...).</summary>
        Mounted = 3,
    }

    /// <summary>Which direction an object's front was turned to.</summary>
    public enum YawRule
    {
        /// <summary>Toward the capture camera (the rule before 0.4).</summary>
        CameraFacing = 0,
        /// <summary>Along the normal of a wall found in the capture depth.</summary>
        Wall = 1,
    }

    /// <summary>
    /// Extent of a model along its canonical front axis and its width, in unscaled root units (the placed root before
    /// uniformScale). backExtent is the distance from the pivot to the model's back.
    /// </summary>
    public struct ObjectShape
    {
        public bool valid;
        public float backExtent;
        public float frontExtent;
        public float width;

        /// <summary>Shape of a model whose pivot is centred in X and Z (meshes): back = front = half the depth.</summary>
        /// <param name="rootSize">Bounds size in root space.</param>
        /// <param name="frontLocal">Root-local front axis (<see cref="SceneOrientation.FrontAxisLocal"/>).</param>
        public static ObjectShape FromCenteredSize(Vector3 rootSize, Vector3 frontLocal)
        {
            Vector3 side = SceneOrientation.SideAxisLocal(frontLocal);
            float depth = Mathf.Abs(Vector3.Dot(rootSize, Abs(frontLocal)));
            return new ObjectShape
            {
                valid = true,
                backExtent = 0.5f * depth,
                frontExtent = 0.5f * depth,
                width = Mathf.Abs(Vector3.Dot(rootSize, Abs(side))),
            };
        }

        /// <summary>
        /// Shape of a splat model from its positions: back = -P2, front = P98 along the canonical front, width = P98 - P2
        /// along the side axis (percentiles ignore floaters). The content child is re-centred in Y only, so the horizontal
        /// coordinates after the content transform are root coordinates.
        /// </summary>
        /// <param name="xyz">Packed float3 positions (e.g. <c>GaussianSplatAsset.posData.GetData&lt;float&gt;()</c> of a Float32 asset).</param>
        /// <param name="count">Number of splats.</param>
        /// <param name="rotEuler">Content local rotation (<see cref="PlacementTuning.contentRotationEuler"/>).</param>
        /// <param name="scale">Content local scale (<see cref="PlacementTuning.contentScale"/>).</param>
        /// <param name="yawOffsetDeg">Yaw offset of the representation (defines the canonical front).</param>
        public static ObjectShape FromPoints(NativeArray<float> xyz, int count, Vector3 rotEuler, Vector3 scale, float yawOffsetDeg)
        {
            if (!xyz.IsCreated)
                return default;
            count = Mathf.Min(count, xyz.Length / 3);
            if (count <= 0)
                return default;
            int stride = Mathf.Max(1, count / 20000);
            Quaternion rot = Quaternion.Euler(rotEuler);
            Vector3 front = SceneOrientation.FrontAxisLocal(yawOffsetDeg);
            Vector3 side = SceneOrientation.SideAxisLocal(front);
            var a = new List<float>(count / stride + 1);
            var s = new List<float>(count / stride + 1);
            for (int i = 0; i < count; i += stride)
            {
                var p = new Vector3(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                    continue;
                Vector3 q = rot * Vector3.Scale(p, scale);
                a.Add(Vector3.Dot(front, q));
                s.Add(Vector3.Dot(side, q));
            }
            if (a.Count < 100)
                return default;
            a.Sort();
            s.Sort();
            return new ObjectShape
            {
                valid = true,
                backExtent = Mathf.Max(0f, -SceneOrientation.Percentile(a, 0.02f)),
                frontExtent = Mathf.Max(0f, SceneOrientation.Percentile(a, 0.98f)),
                width = SceneOrientation.Percentile(s, 0.98f) - SceneOrientation.Percentile(s, 0.02f),
            };
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }

    /// <summary>
    /// What the placement solve needs to know about an object to orient it. The default value (enabled = false) keeps the
    /// camera-facing solve bit-identical to the behaviour before 0.4.
    /// </summary>
    public struct OrientationInputs
    {
        public bool enabled;
        public string name;
        public string restingSurface;
        public string againstWall;
        public string support;
        public string backAgainstWall;
        public string frontFaces;
        public ObjectShape shape;

        /// <summary>Inputs from a pipeline object (hints from DECIDE / VERIFY) and the model's shape.</summary>
        public static OrientationInputs From(PlacedObjectResult o, ObjectShape s)
        {
            if (o == null)
                return default;
            return new OrientationInputs
            {
                enabled = true,
                name = o.name,
                restingSurface = o.restingSurface,
                againstWall = o.againstWall,
                support = o.support,
                backAgainstWall = o.backAgainstWall,
                frontFaces = o.frontFaces,
                shape = s,
            };
        }
    }

    /// <summary>A vertical plane fitted to the capture depth behind an object, and its quality statistics.</summary>
    public struct WallFit
    {
        /// <summary>A plane was fitted (it may still have failed a gate; see <see cref="reject"/>).</summary>
        public bool found;
        /// <summary>Why the wall rule was not used ("frac 0.21", "behind 0.24", "dist 1.52", ...); null when accepted.</summary>
        public string reject;
        /// <summary>Horizontal unit normal pointing into the room (toward the camera side).</summary>
        public Vector3 normal;
        /// <summary>Mean of the inliers (y = mean inlier height).</summary>
        public Vector3 point;
        /// <summary>Horizontal unit tangent along the wall.</summary>
        public Vector3 tangent;
        public int points;
        public int inliers;
        public float tau;
        public float inlierFraction;
        public float extentM;
        public float yMin;
        public float yMax;
        public float ySpanM;
        public float behindFraction;
        public float splitDeg;
        /// <summary>Signed distance of the anchor from the wall (positive = in front of it).</summary>
        public float anchorDistanceM;
        /// <summary>Median distance of the capture surface behind the object's centre from the wall.</summary>
        public float centerErrM;
        public bool centerValid;
    }

    /// <summary>Outcome of <see cref="SceneOrientation.Evaluate"/>: the candidate yaw and position and why.</summary>
    public struct OrientationReport
    {
        public OrientationMode mode;
        public WallIntent intent;
        public string intentSource;
        /// <summary>What the scene rule proposes (Wall only when every gate passed).</summary>
        public YawRule candidate;
        /// <summary>What the solve applied (Wall only in SceneAware mode).</summary>
        public YawRule applied;
        public bool corner;
        public bool hasVlmFrontYaw;
        public bool flushApplied;
        public bool standoffClamped;
        public float cameraYawDeg;
        public float wallYawDeg;
        public float frontYawDeg;
        /// <summary>DeltaAngle(cameraYaw, candidate front yaw).</summary>
        public float deltaDeg;
        public float confidence;
        public float vlmFrontYawDeg;
        public float standoffM;
        public float shiftM;
        public float elapsedMs;
        /// <summary>none, center-ray, normal-push or footprint.</summary>
        public string reanchor;
        public Vector3 legacyPosition;
        /// <summary>Candidate position (applied only in SceneAware mode when the candidate is Wall).</summary>
        public Vector3 position;
        public WallFit wall;
        /// <summary>Note for the solve's note list (empty for Unknown / FreeStanding intents).</summary>
        public string note;
    }

    /// <summary>
    /// Wall-aware yaw for placed objects. An object turns parallel to a wall only when a wall intent (vision-model hint
    /// or name category) AND a wall plane fitted to the capture depth that passes every quality gate agree; everything
    /// else keeps the camera-facing yaw. Pure math: no scene access.
    /// </summary>
    /// <remarks>
    /// The yaw formula is unchanged (<c>rotation = Euler(0, psi + yawOffset, 0)</c>); only psi changes from "toward the
    /// camera" to the wall normal. Because the fitted normal always points toward the camera side and the grazing gate
    /// bounds the angle, a wall-snapped object never faces away from the user (|DeltaAngle(psi, cameraYaw)| &lt; 81.4).
    /// </remarks>
    public static class SceneOrientation
    {
        /// <summary>Everything <see cref="Evaluate"/> needs, as computed by <see cref="SplatPlacement.Solve"/>.</summary>
        public struct Context
        {
            public OrientationMode mode;
            public CaptureResult capture;
            /// <summary>Capture eye depth (W*H) or null.</summary>
            public float[] depth;
            public int width;
            public int height;
            public float focal;
            /// <summary>Object mask (capture dims) when the solve anchored on it; else null (box rect).</summary>
            public bool[] mask;
            public int bx0, by0, bx1, by1;
            /// <summary>The raw (lenient-parsed, unclamped) normalized box.</summary>
            public Bbox bboxRaw;
            public int uA, vA;
            public float dA;
            /// <summary>Legacy anchor (ground-contact pivot).</summary>
            public Vector3 pos;
            public float chosenH;
            public float sizeHintM;
            public float uniformScale;
            public Vector3 contentBoundsSize;
            public bool isMesh;
            public PlacementTuning tuning;
            public OrientationInputs inputs;
            /// <summary>Legacy (camera-facing) yaw of the front.</summary>
            public float camYawDeg;
        }

        // ------------------------------------------------------------------------------------------
        // Axes, intent, VLM bucket

        /// <summary>Root-local canonical front of a model: Euler(0, -offset, 0) * Z (270 -> +X splats, 180 -> -Z meshes).</summary>
        public static Vector3 FrontAxisLocal(float yawOffsetDeg) => Quaternion.Euler(0f, -yawOffsetDeg, 0f) * Vector3.forward;

        /// <summary>Horizontal axis perpendicular to <paramref name="front"/>: (front.z, 0, -front.x).</summary>
        public static Vector3 SideAxisLocal(Vector3 front) => new Vector3(front.z, 0f, -front.x);

        /// <summary>
        /// Wall intent from the hints, first match wins: VERIFY support wall_mounted; DECIDE resting_surface wall (unless
        /// VERIFY saw it on the floor or on furniture); VERIFY back_against_wall yes/no; DECIDE against_wall yes/no; the name
        /// category list (only when every hint is missing or unsure); else Unknown.
        /// </summary>
        public static WallIntent ResolveIntent(in OrientationInputs o, PlacementTuning t, out string source)
        {
            string support = Lower(o.support);
            string resting = Lower(o.restingSurface);
            string back = Lower(o.backAgainstWall);
            string against = Lower(o.againstWall);
            if (support == "wall_mounted")
            {
                source = "verify.support";
                return WallIntent.Mounted;
            }
            if (resting == "wall" && support != "floor" && support != "table_or_furniture")
            {
                source = "decide.resting_surface";
                return WallIntent.Mounted;
            }
            if (back == "yes" || back == "no")
            {
                source = "verify.back_against_wall";
                return back == "yes" ? WallIntent.Backed : WallIntent.FreeStanding;
            }
            if (against == "yes" || against == "no")
            {
                source = "decide.against_wall";
                return against == "yes" ? WallIntent.Backed : WallIntent.FreeStanding;
            }
            if (t != null && t.useCategoryWallHeuristic)
            {
                string cat = MatchCategory(o.name, t.wallBackedCategories);
                if (cat != null)
                {
                    source = "category:" + cat;
                    return WallIntent.Backed;
                }
            }
            source = "none";
            return WallIntent.Unknown;
        }

        /// <summary>
        /// The first category of the comma-separated list that the name ends with as a whole word or words, i.e. its head
        /// noun (plural -s / -es allowed; "desk lamp" is not a desk), case-insensitive; null when none matches.
        /// </summary>
        public static string MatchCategory(string name, string categories)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(categories))
                return null;
            string lower = name.ToLowerInvariant();
            foreach (var entry in CategoryPatterns(categories))
                if (entry.Value.IsMatch(lower))
                    return entry.Key;
            return null;
        }

        static readonly Dictionary<string, List<KeyValuePair<string, Regex>>> s_CategoryCache = new Dictionary<string, List<KeyValuePair<string, Regex>>>();

        static List<KeyValuePair<string, Regex>> CategoryPatterns(string categories)
        {
            lock (s_CategoryCache)
            {
                if (s_CategoryCache.TryGetValue(categories, out var list))
                    return list;
                list = new List<KeyValuePair<string, Regex>>();
                foreach (var raw in categories.Split(','))
                {
                    string cat = raw.Trim().ToLowerInvariant();
                    if (cat.Length == 0)
                        continue;
                    var re = new Regex("(^|[^a-z])" + Regex.Escape(cat) + "(s|es)?[^a-z]*$", RegexOptions.CultureInvariant);
                    list.Add(new KeyValuePair<string, Regex>(cat, re));
                }
                if (s_CategoryCache.Count > 16)
                    s_CategoryCache.Clear();
                s_CategoryCache[categories] = list;
                return list;
            }
        }

        /// <summary>
        /// World yaw of the front that VERIFY's image-direction bucket describes, given the viewing yaw
        /// (camera -> object): toward_viewer +180, toward_viewer_right +135, image_right +90, away_from_viewer 0,
        /// image_left -90, toward_viewer_left -135. Null for no_clear_front / unknown / null. Used for corner
        /// tie-breaks and logging only.
        /// </summary>
        public static float? BucketYaw(string frontFaces, float viewYawDeg)
        {
            float offset;
            switch (Lower(frontFaces))
            {
                case "toward_viewer": offset = 180f; break;
                case "toward_viewer_right": offset = 135f; break;
                case "image_right": offset = 90f; break;
                case "away_from_viewer": offset = 0f; break;
                case "image_left": offset = -90f; break;
                case "toward_viewer_left": offset = -135f; break;
                default: return null;
            }
            return Normalize180(viewYawDeg + offset);
        }

        // ------------------------------------------------------------------------------------------
        // Floor height (wall-hung objects), cached per capture

        sealed class FloorInfo
        {
            public float y;
        }

        static readonly ConditionalWeakTable<CaptureResult, FloorInfo> s_Floor = new ConditionalWeakTable<CaptureResult, FloorInfo>();

        /// <summary>
        /// Floor height seen by a capture: min(P2 of the heights of the lower image half within 15 m, camera y - 0.5);
        /// camera y - 1.6 without samples. Cached per capture.
        /// </summary>
        public static float FloorHeight(CaptureResult capture)
        {
            if (capture == null)
                return 0f;
            return s_Floor.GetValue(capture, ComputeFloor).y;
        }

        static FloorInfo ComputeFloor(CaptureResult cap)
        {
            float camY = cap.cameraPosition.y;
            int W = cap.width, H = cap.height;
            float[] D = cap.depthMeters;
            if (D == null || W <= 0 || H <= 0 || D.Length != W * H)
                return new FloorInfo { y = camY - 1.6f };
            int stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(W * (float)H / 20000f)));
            var ys = new List<float>();
            for (int v = H / 2; v < H; v += stride)
            {
                int rowBase = v * W;
                for (int u = 0; u < W; u += stride)
                {
                    float d = D[rowBase + u];
                    if (d > 0f && d <= 15f)
                        ys.Add(cap.UnprojectToWorld(u, v, d).y);
                }
            }
            if (ys.Count == 0)
                return new FloorInfo { y = camY - 1.6f };
            ys.Sort();
            return new FloorInfo { y = Mathf.Min(Percentile(ys, 0.02f), camY - 0.5f) };
        }

        // ------------------------------------------------------------------------------------------
        // Evaluate

        struct Plane2
        {
            public float nx, nz, px, pz;
            public float C => nx * px + nz * pz;
            public float Dist(float x, float z) => nx * x + nz * z - C;
        }

        struct PlaneStats
        {
            public int inliers;
            public float frac, ext, yMin, yMax, ySpan, behind, split, yMean;
        }

        /// <summary>
        /// Runs the wall rule for one object: intent, preconditions, a vertical-plane RANSAC fit on the capture depth
        /// around the object, the quality gates, corner handling and the flush position. Never throws for bad data (the
        /// caller still wraps it); returns candidate = CameraFacing with <see cref="WallFit.reject"/> when anything fails.
        /// </summary>
        public static OrientationReport Evaluate(in Context c)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new OrientationReport
            {
                mode = c.mode,
                cameraYawDeg = c.camYawDeg,
                frontYawDeg = c.camYawDeg,
                legacyPosition = c.pos,
                position = c.pos,
                reanchor = "none",
                note = "",
            };
            var t = c.tuning ?? new PlacementTuning();
            r.intent = ResolveIntent(c.inputs, t, out r.intentSource);

            CaptureResult cap = c.capture;
            Vector3 C = cap.cameraPosition;
            Vector3 toObj = c.pos - C;
            float viewYaw = Atan2Deg(toObj.x, toObj.z);
            float? bucket = BucketYaw(c.inputs.frontFaces, viewYaw);
            if (bucket.HasValue)
            {
                r.hasVlmFrontYaw = true;
                r.vlmFrontYawDeg = bucket.Value;
            }

            if (r.intent != WallIntent.Mounted && r.intent != WallIntent.Backed)
            {
                r.elapsedMs = (float)sw.Elapsed.TotalMilliseconds;
                return r; // Unknown / FreeStanding: camera-facing, no note
            }

            string reject = EvaluateWall(c, t, bucket, ref r);
            r.wall.reject = reject;
            if (reject != null)
            {
                r.candidate = YawRule.CameraFacing;
                r.position = c.pos;
                r.frontYawDeg = c.camYawDeg;
                r.deltaDeg = 0f;
                r.reanchor = "none";
                r.flushApplied = false;
                r.shiftM = 0f;
                r.note = string.Format(CultureInfo.InvariantCulture, "yaw:camera({0}/{1},reject={2})", r.intent, r.intentSource, reject);
            }
            r.elapsedMs = (float)sw.Elapsed.TotalMilliseconds;
            return r;
        }

        // Returns the reject reason, or null when the wall rule applies (then r holds the candidate).
        static string EvaluateWall(in Context c, PlacementTuning t, float? bucket, ref OrientationReport r)
        {
            bool mounted = r.intent == WallIntent.Mounted;
            CaptureResult cap = c.capture;
            int W = c.width, H = c.height;
            float f = c.focal;
            float[] D = c.depth;
            Vector3 C = cap.cameraPosition;

            // E1: preconditions
            if (D == null || D.Length != W * H)
                return "no-depth";
            if (c.mask == null)
            {
                Bbox b = c.bboxRaw;
                if (b.x < -0.02f || b.y < -0.02f || b.x + b.w > 1.02f || b.y + b.h > 1.02f)
                    return "bbox-suspect";
            }
            Vector3 cb = c.contentBoundsSize;
            if (!mounted && cb.y < 0.25f * Mathf.Max(cb.x, cb.z))
                return "flat";

            // E2: object rectangle
            int mx0, mx1, my0, my1;
            if (c.mask != null)
            {
                if (!MaskRect(c.mask, W, H, out mx0, out mx1, out my0, out my1))
                    return "mask-empty";
                my1 = Mathf.Min(my1, c.vA);
                if (my1 < my0)
                    my1 = my0;
            }
            else
            {
                mx0 = c.bx0; mx1 = c.bx1; my0 = c.by0; my1 = c.by1;
            }
            float uc = 0.5f * (mx0 + mx1), vc = 0.5f * (my0 + my1);
            int wPx = mx1 - mx0 + 1, hPx = my1 - my0 + 1;
            float dA = Mathf.Max(c.dA, 1e-3f);
            float widthM = wPx * dA / f;
            float hM = c.chosenH;
            float hModel = c.contentBoundsSize.y * c.uniformScale;

            // E3: sampled region (at least 1 m wide; wall-hung: at least 0.6 m tall)
            int ex = Mathf.Max(Mathf.RoundToInt(0.25f * wPx), Mathf.RoundToInt(0.5f * (1.0f * f / dA - wPx)));
            int rx0 = Mathf.Clamp(mx0 - ex, 0, W - 1), rx1 = Mathf.Clamp(mx1 + ex, 0, W - 1);
            int ry0, ry1;
            if (mounted)
            {
                int ey = Mathf.Max(Mathf.RoundToInt(0.25f * hPx), Mathf.RoundToInt(0.5f * (0.6f * f / dA - hPx)));
                ry0 = my0 - ey;
                ry1 = my1 + ey;
            }
            else
            {
                ry0 = my0 - hPx;
                ry1 = my1;
            }
            ry0 = Mathf.Clamp(ry0, 0, H - 1);
            ry1 = Mathf.Clamp(ry1, ry0, H - 1);
            long area = (long)(rx1 - rx0 + 1) * (ry1 - ry0 + 1);
            int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(area / (float)Mathf.Max(1, t.wallMaxPoints))));

            // E4: points
            float floorY = mounted ? FloorHeight(cap) : 0f;
            Vector2 hv = new Vector2(c.pos.x - C.x, c.pos.z - C.z);
            float L = hv.magnitude;
            if (L < 1e-3f)
                return "no-direction";
            Vector2 hHat = hv / L;
            int cap0 = ((rx1 - rx0) / step + 1) * ((ry1 - ry0) / step + 1);
            var px = new float[cap0];
            var py = new float[cap0];
            var pz = new float[cap0];
            int n = 0;
            for (int v = ry0; v <= ry1; v += step)
            {
                int rowBase = v * W;
                for (int u = rx0; u <= rx1; u += step)
                {
                    float d = D[rowBase + u];
                    if (!(d > 0f) || float.IsInfinity(d))
                        continue;
                    Vector3 P = cap.UnprojectToWorld(u, v, d);
                    if (hHat.x * (P.x - C.x) + hHat.y * (P.z - C.z) <= L - 0.25f)
                        continue; // in front of the object
                    if (mounted)
                    {
                        if (P.y <= floorY + 0.10f)
                            continue;
                    }
                    else if (!(P.y > c.pos.y + 0.10f && P.y < c.pos.y + 3.0f))
                    {
                        continue;
                    }
                    px[n] = P.x;
                    py[n] = P.y;
                    pz[n] = P.z;
                    n++;
                }
            }
            r.wall.points = n;
            if (n < Mathf.Max(3, t.wallMinPoints))
                return "few-points " + n.ToString(CultureInfo.InvariantCulture);

            // E5: vertical-plane RANSAC on XZ
            float tau = Mathf.Max(t.wallTauMinM, t.wallTauPerMeter * L);
            r.wall.tau = tau;
            var all = new int[n];
            for (int i = 0; i < n; ++i)
                all[i] = i;
            int iterations = Mathf.Max(1, t.wallRansacIterations);
            if (!FitPlane(px, pz, all, n, iterations, tau, 17, out Plane2 pl1))
                return "no-plane";
            OrientToCamera(ref pl1, C);

            // Second surface among the points off plane 1 (E7 corner candidate). Near a corner the tau band of plane 1 also
            // holds a strip of the adjacent wall, which tilts the PCA refit and the split statistic: when a roughly
            // perpendicular second surface exists, its band is excluded from plane 1's refit and statistics.
            var rest = new int[n];
            int m2 = 0;
            for (int i = 0; i < n; ++i)
                if (Mathf.Abs(pl1.Dist(px[i], pz[i])) >= tau)
                    rest[m2++] = i;
            Plane2 pl2 = default;
            bool has2 = m2 >= 50 && FitPlane(px, pz, rest, m2, iterations, tau, 29, out pl2);
            bool[] excl = null;
            int in2 = 0;
            if (has2)
            {
                OrientToCamera(ref pl2, C);
                in2 = CountInliers(pl2, px, pz, rest, m2, tau, null);
                if (Mathf.Abs(pl1.nx * pl2.nx + pl1.nz * pl2.nz) < 0.3f &&
                    in2 >= Mathf.Max(50f, 0.1f * CountInliers(pl1, px, pz, all, n, tau, null)))
                {
                    excl = new bool[n];
                    for (int i = 0; i < n; ++i)
                        excl[i] = Mathf.Abs(pl2.Dist(px[i], pz[i])) < tau;
                    for (int pass = 0; pass < 2; ++pass)
                    {
                        if (!PcaRefine(pl1, px, pz, all, n, tau, excl, out Plane2 refined))
                            break;
                        pl1 = refined;
                    }
                    OrientToCamera(ref pl1, C);
                }
            }
            var st1 = Stats(pl1, px, py, pz, all, n, n, tau, excl);
            float d1 = pl1.Dist(c.pos.x, c.pos.z);
            bool cv1 = CenterErr(c, pl1, mx0, mx1, my0, my1, out float ce1);
            FillWall(ref r.wall, pl1, st1, d1, cv1, ce1);
            r.confidence = Mathf.Clamp01(Mathf.Min(Mathf.Clamp01((st1.frac - t.wallMinInlierFraction) / 0.45f),
                Mathf.Min(1f - st1.behind / Mathf.Max(t.wallMaxBehindFraction, 1e-4f), 1f - st1.split / Mathf.Max(t.wallMaxSplitDeg, 1e-4f))));

            // E6: gates
            float extMin = Mathf.Max(0.25f, Mathf.Min(0.5f, widthM));
            float ySpanMin = mounted ? Mathf.Min(1.0f, 1.2f * hM) : Mathf.Min(1.2f, hM + 0.3f);
            string g = PlaneGates(t, st1, pl1, hHat, extMin, ySpanMin);
            if (g != null)
                return g;
            float sizeS = c.sizeHintM > 0f ? c.sizeHintM : Mathf.Max(hM, widthM);
            g = DistanceGate(t, mounted, d1, cv1, ce1, tau, sizeS);
            if (g != null)
                return g;

            // E7: corners (the second surface found above)
            Plane2 pl = pl1;
            float dSel = d1;
            if (has2 && m2 >= Mathf.Max(3, t.wallMinPoints))
            {
                var st2 = Stats(pl2, px, py, pz, rest, m2, n, tau, null);
                float dot = Mathf.Abs(pl1.nx * pl2.nx + pl1.nz * pl2.nz);
                if (PlaneGates(t, st2, pl2, hHat, extMin, ySpanMin) == null && st2.inliers >= 0.5f * st1.inliers && dot < 0.3f)
                {
                    r.corner = true;
                    float d2 = pl2.Dist(c.pos.x, c.pos.z);
                    bool cv2 = CenterErr(c, pl2, mx0, mx1, my0, my1, out float ce2);
                    int choice; // 1 or 2; 0 = tie
                    if (mounted)
                    {
                        float k1, k2;
                        if (cv1 || cv2)
                        {
                            k1 = cv1 ? Mathf.Abs(ce1) : float.PositiveInfinity;
                            k2 = cv2 ? Mathf.Abs(ce2) : float.PositiveInfinity;
                        }
                        else
                        {
                            k1 = Mathf.Abs(d1);
                            k2 = Mathf.Abs(d2);
                        }
                        choice = Mathf.Abs(k1 - k2) < 0.10f ? 0 : (k1 < k2 ? 1 : 2);
                    }
                    else
                    {
                        bool ok1 = d1 >= -0.15f, ok2 = d2 >= -0.15f;
                        if (ok1 && ok2)
                            choice = Mathf.Abs(d1 - d2) < 0.10f ? 0 : (d1 < d2 ? 1 : 2);
                        else
                            choice = ok2 ? 2 : 1;
                    }
                    if (choice == 0)
                    {
                        // a tie only goes to plane 2 when plane 2 also passes the distance gate (plane 1 already did)
                        float target = bucket ?? c.camYawDeg;
                        float y1 = Atan2Deg(pl1.nx, pl1.nz), y2 = Atan2Deg(pl2.nx, pl2.nz);
                        bool pass2 = DistanceGate(t, mounted, d2, cv2, ce2, tau, sizeS) == null;
                        choice = pass2 && Mathf.Abs(Mathf.DeltaAngle(y2, target)) < Mathf.Abs(Mathf.DeltaAngle(y1, target)) ? 2 : 1;
                    }
                    if (choice == 2)
                    {
                        pl = pl2;
                        dSel = d2;
                        FillWall(ref r.wall, pl2, st2, d2, cv2, ce2);
                        g = DistanceGate(t, mounted, d2, cv2, ce2, tau, sizeS);
                        if (g != null)
                            return g;
                    }
                }
            }

            // A roughly perpendicular second surface that was not accepted as a corner (it failed a gate or has too few
            // inliers) but is the nearer wall: the object may stand against it rather than against plane 1.
            if (has2 && !r.corner && Mathf.Abs(pl1.nx * pl2.nx + pl1.nz * pl2.nz) < 0.3f && in2 >= Mathf.Max(50f, 0.1f * st1.inliers))
            {
                if (!mounted)
                {
                    float d2n = pl2.Dist(c.pos.x, c.pos.z);
                    if (d2n >= -0.15f && d2n < dSel - 0.10f)
                        return "corner-ambiguous " + F2(d2n);
                }
                else if (cv1 && CenterErr(c, pl2, mx0, mx1, my0, my1, out float ce2n) && Mathf.Abs(ce2n) < Mathf.Abs(ce1) - 0.05f)
                {
                    return "corner-ambiguous " + F2(ce2n);
                }
            }

            // E8/E9: yaw
            float psi = Atan2Deg(pl.nx, pl.nz);
            r.candidate = YawRule.Wall;
            r.wallYawDeg = psi;
            r.frontYawDeg = psi;
            r.deltaDeg = Mathf.DeltaAngle(c.camYawDeg, psi);
            var extra = new StringBuilder();
            if (r.corner)
                extra.Append(",corner");
            if (bucket.HasValue)
            {
                float dv = Mathf.DeltaAngle(psi, bucket.Value);
                if (Mathf.Abs(dv) > 90f)
                    extra.Append(",vlm-disagree:").Append(dv.ToString("F0", CultureInfo.InvariantCulture));
            }

            // E10: flush
            Vector3 n3 = new Vector3(pl.nx, 0f, pl.nz);
            Vector3 t3 = new Vector3(pl.nz, 0f, -pl.nx);
            Vector3 front = FrontAxisLocal(SplatPlacement.YawOffset(t, c.isMesh));
            Vector3 side = SideAxisLocal(front);
            ObjectShape shape = c.inputs.shape;
            float backW = (shape.valid ? shape.backExtent : 0.5f * Mathf.Abs(Vector3.Dot(cb, Abs3(front)))) * c.uniformScale;
            float modelWidth = (shape.valid ? shape.width : Mathf.Abs(Vector3.Dot(cb, Abs3(side)))) * c.uniformScale;
            Vector3 pos = c.pos;
            Vector3 newPos = pos;
            string skip = null;
            if (t.wallFlush)
            {
                float plC = pl.C;
                if (mounted)
                {
                    float o = Mathf.Min(backW, t.wallMaxStandoffM) + t.wallGapM;
                    r.standoffM = o;
                    r.standoffClamped = backW > t.wallMaxStandoffM;
                    bool ok = false;
                    Vector3 ray = cap.UnprojectToWorld(uc, vc, 1f) - C;
                    float den = Vector3.Dot(n3, ray);
                    float rxz = new Vector2(ray.x, ray.z).magnitude;
                    if (den < -0.17f * rxz)
                    {
                        float tt = (plC + o - (pl.nx * C.x + pl.nz * C.z)) / den;
                        if (tt > 0f)
                        {
                            Vector3 I = C + tt * ray;
                            Vector3 cand = new Vector3(I.x, Mathf.Max(I.y - 0.5f * hModel, floorY), I.z);
                            if (FlushGuards(t, cap, pos, cand, true, n3, out _))
                            {
                                newPos = cand;
                                r.reanchor = "center-ray";
                                ok = true;
                            }
                        }
                    }
                    if (!ok)
                    {
                        Vector3 cand = pos + n3 * (o - dSel);
                        if (FlushGuards(t, cap, pos, cand, true, n3, out string why))
                        {
                            newPos = cand;
                            r.reanchor = "normal-push";
                        }
                        else
                        {
                            skip = why;
                        }
                    }
                }
                else
                {
                    float o = Mathf.Min(backW, 1.0f) + t.wallGapM;
                    r.standoffM = o;
                    r.standoffClamped = backW > 1.0f;
                    float sPos = t3.x * (pos.x - pl.px) + t3.z * (pos.z - pl.pz);
                    float sc = sPos;
                    bool foot = false;
                    if (t.wallBackedCenterOnFootprint && FootprintCenter(c, mx0, mx1, my0, pl, t3, out float sFoot))
                    {
                        sc = sFoot;
                        foot = true;
                    }
                    Vector3 basePt = new Vector3(pl.px, pos.y, pl.pz);
                    Vector3 cand = basePt + t3 * sc + n3 * o;
                    cand.y = pos.y;
                    r.reanchor = foot ? "footprint" : "normal-push";
                    float lateralMax = Mathf.Max(0.5f, t.wallMaxLateralFrac * modelWidth);
                    if (Mathf.Abs(Vector3.Dot(t3, cand - pos)) > lateralMax)
                    {
                        cand = basePt + t3 * sPos + n3 * o;
                        cand.y = pos.y;
                        r.reanchor = "normal-push";
                    }
                    if (FlushGuards(t, cap, pos, cand, false, n3, out string why))
                    {
                        newPos = cand;
                    }
                    else
                    {
                        skip = why;
                        r.reanchor = "none";
                    }
                }
            }
            r.position = newPos;
            r.flushApplied = newPos != pos;
            r.shiftM = (newPos - pos).magnitude;
            if (skip != null)
                extra.Append(",flush-skipped:").Append(skip);

            string kind = c.mode == OrientationMode.Shadow ? "yaw:shadow-wall" : "yaw:wall";
            r.note = string.Format(CultureInfo.InvariantCulture,
                "{0}({1}/{2},psi={3:F1},dcam={4:F1},d={5:F2},shift={6:F2},reanchor={7},standoff={8:F2}{9}{10})",
                kind, r.intent, r.intentSource, psi, r.deltaDeg, dSel, r.shiftM, r.reanchor, r.standoffM,
                r.standoffClamped ? "c" : "", extra.ToString());
            return null;
        }

        // frac, ext, ySpan, behind, split, grazing
        static string PlaneGates(PlacementTuning t, in PlaneStats st, in Plane2 pl, Vector2 hHat, float extMin, float ySpanMin)
        {
            if (st.frac < t.wallMinInlierFraction)
                return "frac " + F2(st.frac);
            if (st.ext < extMin)
                return "ext " + F2(st.ext);
            if (st.ySpan < ySpanMin)
                return "yspan " + F2(st.ySpan);
            if (st.behind >= t.wallMaxBehindFraction)
                return "behind " + F2(st.behind);
            if (st.split >= t.wallMaxSplitDeg)
                return "split " + st.split.ToString("F1", CultureInfo.InvariantCulture);
            float cos = -(pl.nx * hHat.x + pl.nz * hHat.y);
            if (cos < t.wallMinGrazingCos)
                return "grazing " + F2(cos);
            return null;
        }

        static string DistanceGate(PlacementTuning t, bool mounted, float d, bool centerValid, float centerErr, float tau, float sizeS)
        {
            if (mounted)
            {
                if (centerValid)
                    return Mathf.Abs(centerErr) <= Mathf.Max(t.wallMountedCenterTolM, 3f * tau) ? null : "center " + F2(centerErr);
                return Mathf.Abs(d) <= t.wallMountedMaxAnchorDistM ? null : "dist " + F2(d);
            }
            return d >= -0.15f && d <= 0.5f * sizeS + t.wallBackedMaxDistSlackM ? null : "dist " + F2(d);
        }

        static bool FlushGuards(PlacementTuning t, CaptureResult cap, Vector3 pos, Vector3 cand, bool mounted, Vector3 n3, out string why)
        {
            float shift = mounted ? (cand - pos).magnitude : Mathf.Abs(Vector3.Dot(n3, cand - pos));
            if (shift > t.wallMaxShiftM)
            {
                why = "shift " + F2(shift);
                return false;
            }
            float eye = Vector3.Dot(cand - cap.cameraPosition, cap.cameraRotation * Vector3.forward);
            if (eye < t.minDistance || eye > t.maxDistance)
            {
                why = "depth " + F2(eye);
                return false;
            }
            why = null;
            return true;
        }

        static void FillWall(ref WallFit w, in Plane2 pl, in PlaneStats st, float d, bool centerValid, float centerErr)
        {
            w.found = true;
            w.normal = new Vector3(pl.nx, 0f, pl.nz);
            w.tangent = new Vector3(pl.nz, 0f, -pl.nx);
            w.point = new Vector3(pl.px, st.yMean, pl.pz);
            w.inliers = st.inliers;
            w.inlierFraction = st.frac;
            w.extentM = st.ext;
            w.yMin = st.yMin;
            w.yMax = st.yMax;
            w.ySpanM = st.ySpan;
            w.behindFraction = st.behind;
            w.splitDeg = st.split;
            w.anchorDistanceM = d;
            w.centerValid = centerValid;
            w.centerErrM = centerValid ? centerErr : 0f;
        }

        // 0.5% / 99.5% cumulative columns and rows of the mask (ignores stray pixels).
        static bool MaskRect(bool[] mask, int W, int H, out int x0, out int x1, out int y0, out int y1)
        {
            var cols = new int[W];
            var rows = new int[H];
            long total = 0;
            for (int v = 0; v < H; ++v)
            {
                int rowBase = v * W, cnt = 0;
                for (int u = 0; u < W; ++u)
                {
                    if (mask[rowBase + u])
                    {
                        cols[u]++;
                        cnt++;
                    }
                }
                rows[v] = cnt;
                total += cnt;
            }
            x0 = x1 = y0 = y1 = 0;
            if (total == 0)
                return false;
            x0 = CumIndex(cols, total, 0.005);
            x1 = CumIndex(cols, total, 0.995);
            y0 = CumIndex(rows, total, 0.005);
            y1 = CumIndex(rows, total, 0.995);
            return true;
        }

        static int CumIndex(int[] hist, long total, double q)
        {
            double target = Math.Max(1.0, q * total);
            long acc = 0;
            for (int i = 0; i < hist.Length; ++i)
            {
                acc += hist[i];
                if (acc >= target)
                    return i;
            }
            return hist.Length - 1;
        }

        // Median signed distance from the wall of the ORIGINAL capture surface behind the central 40% x 40% of the object
        // (mask pixels only with a mask); valid with at least 20 samples.
        static bool CenterErr(in Context c, in Plane2 pl, int mx0, int mx1, int my0, int my1, out float err)
        {
            err = 0f;
            int W = c.width;
            float uc = 0.5f * (mx0 + mx1), vc = 0.5f * (my0 + my1);
            float hw = 0.2f * (mx1 - mx0 + 1), hh = 0.2f * (my1 - my0 + 1);
            int u0 = Mathf.Clamp(Mathf.RoundToInt(uc - hw), 0, W - 1), u1 = Mathf.Clamp(Mathf.RoundToInt(uc + hw), u0, W - 1);
            int v0 = Mathf.Clamp(Mathf.RoundToInt(vc - hh), 0, c.height - 1), v1 = Mathf.Clamp(Mathf.RoundToInt(vc + hh), v0, c.height - 1);
            long area = (long)(u1 - u0 + 1) * (v1 - v0 + 1);
            int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(area / 400f)));
            var vals = new List<float>(420);
            for (int v = v0; v <= v1; v += step)
            {
                int rowBase = v * W;
                for (int u = u0; u <= u1; u += step)
                {
                    if (c.mask != null && !c.mask[rowBase + u])
                        continue;
                    float d = c.depth[rowBase + u];
                    if (!(d > 0f) || float.IsInfinity(d))
                        continue;
                    Vector3 Q = c.capture.UnprojectToWorld(u, v, d);
                    vals.Add(pl.Dist(Q.x, Q.z));
                }
            }
            if (vals.Count < 20)
                return false;
            vals.Sort();
            err = vals[vals.Count / 2];
            return true;
        }

        // Centre of the object's footprint along the wall: P5..P95 of the wall coordinate of the floor points under the
        // bottom silhouette (bottom-most mask pixel per column, or the box bottom row).
        static bool FootprintCenter(in Context c, int mx0, int mx1, int my0, in Plane2 pl, Vector3 t3, out float center)
        {
            center = 0f;
            int W = c.width;
            int wPx = mx1 - mx0 + 1;
            int step = Mathf.Max(1, wPx / 400);
            var s = new List<float>();
            int vBottom = c.mask != null ? Mathf.Clamp(c.vA, 0, c.height - 1) : c.by1;
            for (int u = mx0; u <= mx1; u += step)
            {
                int vHit = -1;
                if (c.mask != null)
                {
                    for (int v = vBottom; v >= 0; --v)
                    {
                        if (c.mask[v * W + u])
                        {
                            vHit = v;
                            break;
                        }
                    }
                }
                else
                {
                    vHit = c.by1;
                }
                if (vHit < 0)
                    continue;
                float d = c.depth[vHit * W + u];
                if (!(d > 0f) || float.IsInfinity(d))
                    continue;
                Vector3 P = c.capture.UnprojectToWorld(u, vHit, d);
                if (Mathf.Abs(P.y - c.pos.y) >= 0.10f)
                    continue;
                s.Add(t3.x * (P.x - pl.px) + t3.z * (P.z - pl.pz));
            }
            if (s.Count < 5)
                return false;
            s.Sort();
            center = 0.5f * (Percentile(s, 0.05f) + Percentile(s, 0.95f));
            return true;
        }

        // ---- RANSAC -----------------------------------------------------------------------------------------

        static bool FitPlane(float[] x, float[] z, int[] idx, int m, int iterations, float tau, int seed, out Plane2 best)
        {
            best = default;
            if (m < 2)
                return false;
            var rng = new System.Random(seed);
            int bestCount = 0;
            for (int it = 0; it < iterations; ++it)
            {
                int i = -1;
                float dx = 0f, dz = 0f, len = 0f;
                for (int draw = 0; draw < 4; ++draw)
                {
                    int a = idx[rng.Next(m)], b = idx[rng.Next(m)];
                    dx = x[b] - x[a];
                    dz = z[b] - z[a];
                    len = Mathf.Sqrt(dx * dx + dz * dz);
                    if (len >= 0.2f)
                    {
                        i = a;
                        break;
                    }
                }
                if (i < 0)
                    continue;
                var h = new Plane2 { nx = -dz / len, nz = dx / len, px = x[i], pz = z[i] };
                int count = CountInliers(h, x, z, idx, m, tau, null);
                if (count > bestCount)
                {
                    bestCount = count;
                    best = h;
                }
            }
            if (bestCount < 2)
                return false;
            for (int pass = 0; pass < 2; ++pass)
            {
                if (!PcaRefine(best, x, z, idx, m, tau, null, out Plane2 refined))
                    break;
                best = refined;
            }
            return true;
        }

        // Points of idx[0..m) within tau of the plane (excluding excl[p] when given).
        static int CountInliers(in Plane2 h, float[] x, float[] z, int[] idx, int m, float tau, bool[] excl)
        {
            float nx = h.nx, nz = h.nz, c0 = h.C;
            int count = 0;
            for (int k = 0; k < m; ++k)
            {
                int p = idx[k];
                if (excl != null && excl[p])
                    continue;
                float dd = nx * x[p] + nz * z[p] - c0;
                if (dd < tau && dd > -tau)
                    count++;
            }
            return count;
        }

        // Closed-form 2x2 PCA of the current inliers: theta = 0.5 atan2(2b, a - c), tangent (cos, sin), normal perpendicular.
        static bool PcaRefine(in Plane2 h, float[] x, float[] z, int[] idx, int m, float tau, bool[] excl, out Plane2 refined)
        {
            refined = h;
            double sx = 0, sz = 0;
            int cnt = 0;
            for (int k = 0; k < m; ++k)
            {
                int p = idx[k];
                if ((excl == null || !excl[p]) && Mathf.Abs(h.Dist(x[p], z[p])) < tau)
                {
                    sx += x[p];
                    sz += z[p];
                    cnt++;
                }
            }
            if (cnt < 2)
                return false;
            double mx = sx / cnt, mz = sz / cnt;
            double a = 0, b = 0, cc = 0;
            for (int k = 0; k < m; ++k)
            {
                int p = idx[k];
                if ((excl == null || !excl[p]) && Mathf.Abs(h.Dist(x[p], z[p])) < tau)
                {
                    double ddx = x[p] - mx, ddz = z[p] - mz;
                    a += ddx * ddx;
                    b += ddx * ddz;
                    cc += ddz * ddz;
                }
            }
            double theta = 0.5 * Math.Atan2(2 * b, a - cc);
            float tx = (float)Math.Cos(theta), tz = (float)Math.Sin(theta);
            refined = new Plane2 { nx = -tz, nz = tx, px = (float)mx, pz = (float)mz };
            return true;
        }

        static void OrientToCamera(ref Plane2 pl, Vector3 C)
        {
            if (pl.nx * (C.x - pl.px) + pl.nz * (C.z - pl.pz) < 0f)
            {
                pl.nx = -pl.nx;
                pl.nz = -pl.nz;
            }
        }

        // Inliers among idx[0..m) (minus excl); behind over ALL n points.
        static PlaneStats Stats(in Plane2 pl, float[] x, float[] y, float[] z, int[] idx, int m, int nAll, float tau, bool[] excl)
        {
            var st = new PlaneStats();
            float tx = pl.nz, tz = -pl.nx;
            var sList = new List<float>();
            var yList = new List<float>();
            double ySum = 0;
            for (int k = 0; k < m; ++k)
            {
                int p = idx[k];
                if ((excl == null || !excl[p]) && Mathf.Abs(pl.Dist(x[p], z[p])) < tau)
                {
                    sList.Add(tx * (x[p] - pl.px) + tz * (z[p] - pl.pz));
                    yList.Add(y[p]);
                    ySum += y[p];
                }
            }
            st.inliers = sList.Count;
            st.frac = m > 0 ? st.inliers / (float)m : 0f;
            int behind = 0;
            for (int p = 0; p < nAll; ++p)
                if (pl.Dist(x[p], z[p]) < -0.20f)
                    behind++;
            st.behind = nAll > 0 ? behind / (float)nAll : 0f;
            if (st.inliers == 0)
                return st;
            st.yMean = (float)(ySum / st.inliers);

            // split: tangents of the two halves along the wall
            var order = new int[st.inliers];
            for (int i = 0; i < order.Length; ++i)
                order[i] = i;
            var sArr = sList.ToArray();
            Array.Sort(sArr, order);
            sList.Sort();
            yList.Sort();
            st.ext = Percentile(sList, 0.97f) - Percentile(sList, 0.03f);
            st.yMin = Percentile(yList, 0.02f);
            st.yMax = Percentile(yList, 0.98f);
            st.ySpan = st.yMax - st.yMin;

            int half = st.inliers / 2;
            if (half >= 50 && st.inliers - half >= 50)
            {
                // re-collect inlier coordinates in index order to map the sorted order back to points
                var ix = new float[st.inliers];
                var iz = new float[st.inliers];
                int j = 0;
                for (int k = 0; k < m; ++k)
                {
                    int p = idx[k];
                    if ((excl == null || !excl[p]) && Mathf.Abs(pl.Dist(x[p], z[p])) < tau)
                    {
                        ix[j] = x[p];
                        iz[j] = z[p];
                        j++;
                    }
                }
                Vector2 t1 = Tangent(ix, iz, order, 0, half);
                Vector2 t2 = Tangent(ix, iz, order, half, st.inliers);
                float dot = Mathf.Clamp01(Mathf.Abs(t1.x * t2.x + t1.y * t2.y));
                st.split = Mathf.Acos(dot) * Mathf.Rad2Deg;
            }
            return st;
        }

        static Vector2 Tangent(float[] x, float[] z, int[] order, int from, int to)
        {
            double sx = 0, sz = 0;
            int cnt = to - from;
            for (int i = from; i < to; ++i)
            {
                sx += x[order[i]];
                sz += z[order[i]];
            }
            double mx = sx / cnt, mz = sz / cnt;
            double a = 0, b = 0, cc = 0;
            for (int i = from; i < to; ++i)
            {
                double dx = x[order[i]] - mx, dz = z[order[i]] - mz;
                a += dx * dx;
                b += dx * dz;
                cc += dz * dz;
            }
            double theta = 0.5 * Math.Atan2(2 * b, a - cc);
            return new Vector2((float)Math.Cos(theta), (float)Math.Sin(theta));
        }

        // ------------------------------------------------------------------------------------------
        // Record (persisted in result.json)

        /// <summary>The persisted pose record of a solve (no NaN).</summary>
        public static PlacementRecord ToRecord(SplatPlacement.PlacementSolution sol, PlacementTuning tuning, bool isMesh)
        {
            var o = sol.orientation;
            var w = o.wall;
            bool wall = o.applied == YawRule.Wall;
            return new PlacementRecord
            {
                version = 1,
                mode = o.mode.ToString(),
                rule = o.applied.ToString(),
                intent = o.intent.ToString(),
                intentSource = o.intentSource ?? "",
                cameraYawDeg = Finite(o.cameraYawDeg),
                wallYawDeg = o.candidate == YawRule.Wall ? Finite(o.wallYawDeg) : (w.found ? Finite(Atan2Deg(w.normal.x, w.normal.z)) : 0f),
                appliedFrontYawDeg = Finite(wall ? o.wallYawDeg : o.cameraYawDeg),
                rotationYawDeg = Finite(sol.rotation.eulerAngles.y),
                yawOffsetDeg = Finite(SplatPlacement.YawOffset(tuning, isMesh)),
                isMesh = isMesh,
                position = Finite(sol.position),
                legacyPosition = Finite(o.legacyPosition),
                uniformScale = Finite(sol.uniformScale),
                flushApplied = wall && o.flushApplied,
                reanchor = o.reanchor ?? "none",
                shiftM = Finite(o.shiftM),
                standoffM = Finite(o.standoffM),
                standoffClamped = o.standoffClamped,
                wallFound = w.found,
                wallReject = w.reject,
                wallNormal = Finite(w.normal),
                wallPoint = Finite(w.point),
                wallYMin = Finite(w.yMin),
                wallYMax = Finite(w.yMax),
                points = w.points,
                inliers = w.inliers,
                tau = Finite(w.tau),
                frac = Finite(w.inlierFraction),
                ext = Finite(w.extentM),
                ySpan = Finite(w.ySpanM),
                behind = Finite(w.behindFraction),
                splitDeg = Finite(w.splitDeg),
                anchorDistM = Finite(w.anchorDistanceM),
                centerErrM = Finite(w.centerErrM),
                corner = o.corner,
                hasVlmFrontYaw = o.hasVlmFrontYaw,
                vlmFrontYawDeg = o.hasVlmFrontYaw ? Finite(o.vlmFrontYawDeg) : 0f,
                confidence = Finite(o.confidence),
                elapsedMs = Finite(o.elapsedMs),
                note = sol.note,
            };
        }

        /// <summary>One-line log summary of a report (null when the intent is Unknown).</summary>
        public static string Describe(string name, in OrientationReport o)
        {
            if (o.intent == WallIntent.Unknown)
                return null;
            var w = o.wall;
            return string.Format(CultureInfo.InvariantCulture,
                "[SplatPresso] Orient '{0}' rule={1} cand={2} intent={3}({4}) cam={5:F1} wall={6:F1} dcam={7:F1} frac={8:F2} ext={9:F2} " +
                "ysp={10:F2} behind={11:F2} split={12:F1} d={13:F2} cerr={14} reanchor={15} shift={16:F2} standoff={17:F2}{18} {19:F1}ms reject={20}",
                name, o.applied, o.candidate, o.intent, o.intentSource, o.cameraYawDeg,
                o.candidate == YawRule.Wall ? o.wallYawDeg : (w.found ? Atan2Deg(w.normal.x, w.normal.z) : 0f), o.deltaDeg,
                w.inlierFraction, w.extentM, w.ySpanM, w.behindFraction, w.splitDeg, w.anchorDistanceM,
                w.centerValid ? w.centerErrM.ToString("F2", CultureInfo.InvariantCulture) : "n/a",
                o.reanchor ?? "none", o.shiftM, o.standoffM, o.standoffClamped ? "c" : "", o.elapsedMs,
                string.IsNullOrEmpty(w.reject) ? "-" : w.reject);
        }

        // ------------------------------------------------------------------------------------------
        // Helpers

        /// <summary>Nearest-rank percentile of an ascending list.</summary>
        public static float Percentile(List<float> sorted, float q)
        {
            if (sorted == null || sorted.Count == 0)
                return 0f;
            int i = Mathf.Clamp((int)Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[i];
        }

        static float Atan2Deg(float x, float z) => Mathf.Atan2(x, z) * Mathf.Rad2Deg;

        static float Normalize180(float deg) => Mathf.DeltaAngle(0f, deg);

        static string Lower(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant();

        static string F2(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

        static Vector3 Abs3(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        static Vector3 Finite(Vector3 v) => new Vector3(Finite(v.x), Finite(v.y), Finite(v.z));
    }
}
