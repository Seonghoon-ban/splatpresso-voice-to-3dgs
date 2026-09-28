using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SplatPresso.EditorTools;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Offline replay of real saved sessions against <c>Tests/Editor/Data/orientation_golden.json</c>. Explicit: needs
    /// the session folders of the machine that recorded them, listed (';'-separated) in the SPLATPRESSO_REPLAY_ROOTS
    /// environment variable; ignored otherwise. Sessions are only read.
    /// </summary>
    [Explicit, Category("Replay")]
    public class SessionReplayOrientationTests
    {
        [Test]
        public void SavedSessions_MatchTheGoldenOrientation()
        {
            string rootsVar = Environment.GetEnvironmentVariable(OrientationReplay.RootsEnvVar);
            if (string.IsNullOrWhiteSpace(rootsVar))
                Assert.Ignore("Set " + OrientationReplay.RootsEnvVar + " to the session roots (';'-separated) to run the replay.");
            var roots = rootsVar.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            string fixtures = EditorTestUtil.FixturesDir; // <package>/Tests/Runtime/Fixtures
            string goldenPath = fixtures == null ? null : Path.GetFullPath(Path.Combine(fixtures, "..", "..", "Editor", "Data", "orientation_golden.json"));
            if (goldenPath == null || !File.Exists(goldenPath))
                Assert.Inconclusive("orientation_golden.json not found next to the package tests");
            var golden = JObject.Parse(File.ReadAllText(goldenPath));
            float tol = (float)golden["tolDeg"];

            var rows = OrientationReplay.Run(roots);
            Debug.Log("[SplatPresso] Replay summary\n" + OrientationReplay.Summary(rows));
            var errors = new StringBuilder();
            var ok = rows.Where(r => r.error == null).ToList();
            Assert.Greater(ok.Count, 0, "no session replayed under " + rootsVar);

            var entries = new Dictionary<string, JObject>();
            foreach (JObject e in (JArray)golden["entries"])
                entries[(string)e["session"] + "/" + (int)e["id"]] = e;

            foreach (var r in ok)
            {
                string key = r.session + "/" + r.id;
                entries.TryGetValue(key, out var e);
                string expect = e != null ? (string)e["expect"] : "CameraFacing";
                bool wall = r.rule == "Wall";

                // the legacy rule is untouched wherever the wall rule does not apply
                if (!wall && !r.identicalToLegacy)
                    errors.AppendLine($"{key}: camera-facing but not identical to the legacy solve");
                if (wall && r.intent != "Mounted" && r.intent != "Backed")
                    errors.AppendLine($"{key}: snapped with intent {r.intent}");
                if (wall && Mathf.Abs(Mathf.DeltaAngle(r.appliedYaw, r.cameraYaw)) >= 81.5f)
                    errors.AppendLine($"{key}: faces away ({r.appliedYaw:F1} vs camera {r.cameraYaw:F1})");
                if (wall && r.intent == "Mounted" && !r.standoffClamped && !float.IsNaN(r.backGapM) &&
                    (r.backGapM < (float)golden["minBackGapM"] || r.backGapM > (float)golden["maxBackGapM"]))
                    errors.AppendLine($"{key}: back gap {r.backGapM:F3} m");

                switch (expect)
                {
                    case "visual-check":
                        break;
                    case "CameraFacing":
                        if (wall)
                            errors.AppendLine($"{key} '{r.name}': expected CameraFacing, got Wall {r.wallYaw:F1} ({r.intentSource})");
                        break;
                    case "Wall":
                    case "WallIfSnapped":
                        if (!wall)
                        {
                            if (expect == "Wall")
                                errors.AppendLine($"{key} '{r.name}': expected Wall, got CameraFacing (reject {r.reject})");
                            break;
                        }
                        if (e["wallYaw"] != null && Mathf.Abs(Mathf.DeltaAngle(r.wallYaw, (float)e["wallYaw"])) > tol)
                            errors.AppendLine($"{key} '{r.name}': wall yaw {r.wallYaw:F1}, golden {(float)e["wallYaw"]:F1}");
                        if (e["roomAxis"] != null)
                        {
                            float m = Mathf.Repeat(r.wallYaw - (float)e["roomAxis"] + 45f, 90f) - 45f;
                            if (Mathf.Abs(m) > tol)
                                errors.AppendLine($"{key} '{r.name}': wall yaw {r.wallYaw:F1} is {m:F1} deg off the room axis {(float)e["roomAxis"]:F1} (mod 90)");
                        }
                        if (e["cameraYaw"] != null && Mathf.Abs(Mathf.DeltaAngle(r.cameraYaw, (float)e["cameraYaw"])) > tol)
                            errors.AppendLine($"{key} '{r.name}': camera yaw {r.cameraYaw:F1}, golden {(float)e["cameraYaw"]:F1}");
                        if (e["intentSource"] != null && r.intentSource != (string)e["intentSource"])
                            errors.AppendLine($"{key} '{r.name}': intent source {r.intentSource}, golden {(string)e["intentSource"]}");
                        if (e["maxShiftM"] != null && r.shiftM > (float)e["maxShiftM"])
                            errors.AppendLine($"{key} '{r.name}': shift {r.shiftM:F2} m > {(float)e["maxShiftM"]:F2}");
                        break;
                }
            }

            var ms = ok.Where(r => r.intent == "Mounted" || r.intent == "Backed").Select(r => r.elapsedMs).OrderBy(x => x).ToList();
            if (ms.Count > 0)
            {
                float p95 = ms[Mathf.Clamp((int)Math.Round(0.95 * (ms.Count - 1)), 0, ms.Count - 1)];
                if (p95 >= (float)golden["maxP95Ms"])
                    errors.AppendLine($"p95 evaluate time {p95:F1} ms");
            }

            if (errors.Length > 0)
                Assert.Fail(errors.ToString());
        }
    }
}
