using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplatPresso.Placement
{
    /// <summary>
    /// Pure placement math: pixel bbox + capture depth (+ optional generated-image relative depth and object mask)
    /// -> world position / rotation / uniform scale of a generated object. No scene access; unit-testable.
    /// </summary>
    /// <remarks>
    /// The pivot of a placed object is its ground contact point (bottom of the content bounds), and the capture-time
    /// camera pose is used throughout, so it does not matter whether the user moved after the capture.
    /// </remarks>
    public static class SplatPlacement
    {
        /// <summary>Result of a placement solve: where and how to spawn a generated object.</summary>
        public struct PlacementSolution
        {
            /// <summary>World position of the ground-contact pivot.</summary>
            public Vector3 position;
            /// <summary>World rotation (yaw only), including the representation's yaw offset.</summary>
            public Quaternion rotation;
            /// <summary>Uniform scale for the placed root (content bounds are fitted to the measured height).</summary>
            public float uniformScale;
            /// <summary>False when the inputs were unusable; see <see cref="note"/>.</summary>
            public bool valid;
            /// <summary>"ok", or ";"-joined notes about fallbacks taken (or the reason for failure).</summary>
            public string note;
        }

        /// <summary>
        /// Solves the placement of one object.
        /// </summary>
        /// <param name="capture">The capture the bbox refers to (pose, intrinsics, metric eye depth).</param>
        /// <param name="bboxNorm">Normalized top-left-origin box of the object in the capture frame.</param>
        /// <param name="sizeHintM">The model's size hint in meters (0 = none).</param>
        /// <param name="genDepthRelative">Relative depth of the GENERATED image resampled to capture dims (0..1;
        /// the disparity-vs-depth sign is resolved by the affine fit); may be null.</param>
        /// <param name="objectMask">True where object pixels are, capture dims; null -> the bbox rect is used.</param>
        /// <param name="contentBoundsSize">Size of the model's bounds after the content transform (root space, unscaled).</param>
        /// <param name="tuning">Placement tuning (calibration values).</param>
        /// <param name="isMesh">Use the mesh tuning (<see cref="PlacementTuning.meshYawOffsetDeg"/>,
        /// <see cref="PlacementTuning.meshUniformScaleFactor"/>) instead of the splat tuning.</param>
        public static PlacementSolution Solve(CaptureResult capture, Bbox bboxNorm, float sizeHintM,
            float[] genDepthRelative, bool[] objectMask, Vector3 contentBoundsSize, PlacementTuning tuning, bool isMesh = false)
        {
            var sol = new PlacementSolution { rotation = Quaternion.identity, uniformScale = 1f };
            if (capture == null || capture.width <= 0 || capture.height <= 0)
            {
                sol.note = "no capture";
                return sol;
            }
            if (!bboxNorm.IsValid)
            {
                sol.note = "invalid bbox";
                return sol;
            }
            if (tuning == null)
            {
                sol.note = "no placement tuning";
                return sol;
            }

            int W = capture.width, H = capture.height;
            float f = Mathf.Max(capture.FocalPixels, 1e-3f);
            float[] D = capture.depthMeters;
            if (D != null && D.Length != W * H) D = null;
            if (genDepthRelative != null && genDepthRelative.Length != W * H) genDepthRelative = null;
            if (objectMask != null && objectMask.Length != W * H) objectMask = null;

            var notes = new List<string>();

            // bbox rect in capture pixels (inclusive)
            Bbox b = bboxNorm.Clamp01();
            int bx0 = Mathf.Clamp(Mathf.FloorToInt(b.x * W), 0, W - 1);
            int by0 = Mathf.Clamp(Mathf.FloorToInt(b.y * H), 0, H - 1);
            int bx1 = Mathf.Clamp(Mathf.CeilToInt((b.x + b.w) * W) - 1, bx0, W - 1);
            int by1 = Mathf.Clamp(Mathf.CeilToInt((b.y + b.h) * H) - 1, by0, H - 1);

            // 1) fit gen relative depth -> metric inverse depth (s*R + t ~= 1/D over background)
            bool fitUsable = false;
            float fitS = 0f, fitT = 0f;
            if (genDepthRelative != null && D != null)
            {
                fitUsable = FitGenDepth(genDepthRelative, D, objectMask, b, W, H, out fitS, out fitT);
                if (!fitUsable)
                    notes.Add("gen-depth-fit-unusable");
            }

            // 2) ground anchor pixel: bottom band of the mask (or bbox rect), median column
            bool usedMask = false;
            List<int> rowsWithPx = null;
            if (objectMask != null)
            {
                rowsWithPx = new List<int>();
                for (int v = 0; v < H; ++v)
                {
                    int rowBase = v * W;
                    for (int u = 0; u < W; ++u)
                    {
                        if (objectMask[rowBase + u])
                        {
                            rowsWithPx.Add(v);
                            break;
                        }
                    }
                }
                if (rowsWithPx.Count > 0)
                    usedMask = true;
                else
                    notes.Add("mask-empty");
            }
            if (!usedMask)
            {
                rowsWithPx = new List<int>();
                for (int v = by0; v <= by1; ++v)
                    rowsWithPx.Add(v);
            }

            int bandCount = Mathf.Max(1, Mathf.CeilToInt(rowsWithPx.Count * tuning.groundAnchorBandFraction));
            int bandStart = rowsWithPx.Count - bandCount;
            int vA = rowsWithPx[rowsWithPx.Count - 1];

            int uA;
            if (usedMask)
            {
                var cols = new List<int>();
                for (int ri = bandStart; ri < rowsWithPx.Count; ++ri)
                {
                    int rowBase = rowsWithPx[ri] * W;
                    for (int u = 0; u < W; ++u)
                        if (objectMask[rowBase + u])
                            cols.Add(u);
                }
                cols.Sort();
                uA = cols.Count > 0 ? cols[cols.Count / 2] : (bx0 + bx1) / 2;
            }
            else
            {
                uA = (bx0 + bx1) / 2; // median of a full row of bbox columns
            }

            // 3) anchor depth: ORIGINAL capture depth at the ground-contact pixel (the ground surface still
            // exists there in the original view, before the object was inserted)
            float dA = D != null ? D[vA * W + uA] : 0f;
            if (dA <= 0f)
            {
                if (fitUsable)
                {
                    var samples = new List<float>();
                    for (int ri = bandStart; ri < rowsWithPx.Count; ++ri)
                    {
                        int v = rowsWithPx[ri];
                        int rowBase = v * W;
                        int u0 = usedMask ? 0 : bx0;
                        int u1 = usedMask ? W - 1 : bx1;
                        for (int u = u0; u <= u1; ++u)
                        {
                            if (usedMask && !objectMask[rowBase + u])
                                continue;
                            float g = GenDepthMetric(genDepthRelative, rowBase + u, fitS, fitT, capture.nearPlane, capture.farPlane);
                            if (g > 0f)
                                samples.Add(g);
                        }
                    }
                    if (samples.Count > 0)
                    {
                        dA = Median(samples);
                        notes.Add("fallback:gen-depth-band");
                    }
                }
                if (dA <= 0f && D != null)
                {
                    var samples = new List<float>();
                    for (int v = by0; v <= by1; ++v)
                    {
                        int rowBase = v * W;
                        for (int u = bx0; u <= bx1; ++u)
                        {
                            float d = D[rowBase + u];
                            if (d > 0f)
                                samples.Add(d);
                        }
                    }
                    if (samples.Count > 0)
                    {
                        dA = Median(samples);
                        notes.Add("fallback:bbox-depth-median");
                    }
                }
                if (dA <= 0f)
                {
                    dA = 2f;
                    notes.Add("fallback:default-2m");
                }
            }
            dA = Mathf.Clamp(dA, tuning.minDistance, tuning.maxDistance);

            // 4) world position of the ground anchor
            Vector3 pos = capture.UnprojectToWorld(uA, vA, dA);

            // 5) world-space object height from pixel height + distance; sanity-blend with the hint.
            // Height comes from the mask's actual row extent (the inpainted silhouette) when available -
            // verify bboxes are often loose (shadows/margins) and overestimate size.
            float hPx = rowsWithPx[rowsWithPx.Count - 1] - rowsWithPx[0] + 1;
            float hWorld = hPx * dA / f;
            float chosenH;
            if (hWorld < 0.02f || hWorld > 20f)
            {
                chosenH = sizeHintM > 0f ? sizeHintM : tuning.defaultObjectSizeM;
                notes.Add("size:hint-or-default");
            }
            else if (sizeHintM > 0f && (hWorld > sizeHintM * 3f || hWorld < sizeHintM / 3f))
            {
                chosenH = sizeHintM;
                notes.Add("size:hint(ratio>3x)");
            }
            else
            {
                chosenH = hWorld;
            }
            sol.uniformScale = chosenH / Mathf.Max(contentBoundsSize.y, 1e-4f) * ScaleFactor(tuning, isMesh);

            // 6) yaw so the object's +Z faces the camera horizontally, plus the calibrated offset that turns the
            // generator's "front" toward the camera
            Vector3 camPos = capture.cameraPosition;
            float yaw = Mathf.Atan2(camPos.x - pos.x, camPos.z - pos.z) * Mathf.Rad2Deg;
            sol.rotation = Quaternion.Euler(0f, yaw + YawOffset(tuning, isMesh), 0f);

            sol.position = pos;
            sol.valid = true;
            sol.note = notes.Count > 0 ? string.Join(";", notes) : "ok";
            return sol;
        }

        /// <summary>Uniform scale factor of the representation (splat or mesh tuning).</summary>
        public static float ScaleFactor(PlacementTuning tuning, bool isMesh) =>
            tuning == null ? 1f : isMesh ? tuning.meshUniformScaleFactor : tuning.uniformScaleFactor;

        /// <summary>Yaw offset in degrees of the representation (splat or mesh tuning).</summary>
        public static float YawOffset(PlacementTuning tuning, bool isMesh) =>
            tuning == null ? 0f : isMesh ? tuning.meshYawOffsetDeg : tuning.yawOffsetDeg;

        // ---- generated-depth affine fit -------------------------------------------------------------------

        // Least-squares fit of s*R + t ~= 1/D (INVERSE metric depth) over background pixels; one robust refit
        // dropping >2 sigma residuals. Usable only when |Pearson corr| >= 0.2 (the sign of s resolves
        // disparity-vs-depth automatically).
        static bool FitGenDepth(float[] R, float[] D, bool[] mask, Bbox bbox, int W, int H, out float s, out float t)
        {
            s = 0f;
            t = 0f;

            // background = outside mask if given, else outside the bbox expanded by 10%
            Bbox ex = bbox.Expand(0.1f);
            int ex0 = Mathf.FloorToInt(ex.x * W);
            int ey0 = Mathf.FloorToInt(ex.y * H);
            int ex1 = Mathf.CeilToInt((ex.x + ex.w) * W) - 1;
            int ey1 = Mathf.CeilToInt((ex.y + ex.h) * H) - 1;

            int total = W * H;
            int stride = Mathf.Max(1, total / 20000);
            var rs = new List<float>(5000);
            var ys = new List<float>(5000);
            for (int i = 0; i < total && rs.Count < 5000; i += stride)
            {
                float d = D[i];
                if (d <= 0f)
                    continue;
                bool background;
                if (mask != null)
                {
                    background = !mask[i];
                }
                else
                {
                    int px = i % W, py = i / W;
                    background = px < ex0 || px > ex1 || py < ey0 || py > ey1;
                }
                if (!background)
                    continue;
                float r = R[i];
                if (float.IsNaN(r) || float.IsInfinity(r))
                    continue;
                rs.Add(r);
                ys.Add(1f / d);
            }
            if (rs.Count < 50)
                return false;

            if (!LinFit(rs, ys, null, out s, out t, out float corr))
                return false;

            // robust refit: drop residuals > 2 sigma
            int n = rs.Count;
            var res = new float[n];
            double sum2 = 0;
            for (int i = 0; i < n; ++i)
            {
                res[i] = s * rs[i] + t - ys[i];
                sum2 += (double)res[i] * res[i];
            }
            float sigma = (float)Math.Sqrt(sum2 / n);
            var keep = new bool[n];
            int kept = 0;
            for (int i = 0; i < n; ++i)
            {
                keep[i] = Mathf.Abs(res[i]) <= 2f * sigma + 1e-12f;
                if (keep[i])
                    kept++;
            }
            if (kept >= 50 && kept < n && LinFit(rs, ys, keep, out float s2, out float t2, out float corr2))
            {
                s = s2;
                t = t2;
                corr = corr2;
            }
            return Mathf.Abs(corr) >= 0.2f;
        }

        static bool LinFit(List<float> xs, List<float> ys, bool[] keep, out float s, out float t, out float corr)
        {
            double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < xs.Count; ++i)
            {
                if (keep != null && !keep[i])
                    continue;
                double x = xs[i], y = ys[i];
                n++;
                sx += x; sy += y;
                sxx += x * x; sxy += x * y; syy += y * y;
            }
            s = 0f;
            t = 0f;
            corr = 0f;
            if (n < 2)
                return false;
            double varX = sxx - sx * sx / n;
            double varY = syy - sy * sy / n;
            double cov = sxy - sx * sy / n;
            if (varX < 1e-12)
                return false;
            s = (float)(cov / varX);
            t = (float)((sy - s * sx) / n);
            corr = varY > 1e-12 ? (float)(cov / Math.Sqrt(varX * varY)) : 0f;
            return true;
        }

        // Metric depth from the fitted mapping; 0 = invalid.
        static float GenDepthMetric(float[] R, int idx, float s, float t, float near, float far)
        {
            float r = R[idx];
            if (float.IsNaN(r) || float.IsInfinity(r))
                return 0f;
            float inv = s * r + t;
            if (inv < 1e-6f)
                return 0f;
            return Mathf.Clamp(1f / inv, Mathf.Max(near, 1e-3f), Mathf.Max(far, 1f));
        }

        static float Median(List<float> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }
    }
}
