using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GaussianSplatting.Runtime;
using SplatPresso.Placement;
using SplatPresso.Rendering;
using UnityEditor;
using UnityEngine;

namespace SplatPresso.EditorTools
{
    /// <summary>
    /// Offline replay of the placement solve over saved sessions (no API calls): for every object with a model file it
    /// solves the pose with the camera-facing rule and with the scene-aware (wall) rule and reports both. Sessions are
    /// only read. Output: <c>Temp/SplatPresso/orientation_replay_&lt;timestamp&gt;.csv</c> plus a summary in the console.
    /// </summary>
    /// <remarks>
    /// Batch: <c>Unity -batchmode -quit -projectPath &lt;project&gt; -executeMethod SplatPresso.EditorTools.OrientationReplay.RunBatch
    /// -replayRoots "&lt;root1&gt;;&lt;root2&gt;" [-replayOut &lt;file.csv&gt;]</c> (roots may also come from the
    /// SPLATPRESSO_REPLAY_ROOTS environment variable or the saved roots). The tuning is the package default with only
    /// <see cref="PlacementTuning.orientationMode"/> switched, so results do not depend on the project's settings asset.
    /// </remarks>
    public static class OrientationReplay
    {
        /// <summary>EditorPrefs key of the saved session roots (';'-separated).</summary>
        public const string RootsPrefKey = "SplatPresso.ReplayRoots";
        /// <summary>Environment variable with session roots (';'-separated).</summary>
        public const string RootsEnvVar = "SPLATPRESSO_REPLAY_ROOTS";

        /// <summary>One replayed object.</summary>
        public sealed class Row
        {
            public string root, session, name, mode, representation, error;
            public int id;
            public string restingSurface, againstWall, support, backAgainstWall, frontFaces;
            public string intent, intentSource, rule, candidate, reject, reanchor, note, legacyNote;
            public float cameraYaw, wallYaw, appliedYaw, deltaDeg, shiftM, standoffM, confidence, elapsedMs;
            public bool standoffClamped, corner, flushApplied, hasMask, identicalToLegacy, wallFound;
            public float frac, ext, ySpan, behind, split, anchorDist, centerErr;
            public bool centerValid;
            /// <summary>2nd percentile of the splats' distance from the wall after the snap (NaN when not measured).</summary>
            public float backGapM = float.NaN;
            public Vector3 legacyPosition, position;
            public float legacyRotationYaw, rotationYaw, legacyScale, scale;
        }

        [MenuItem("SplatPresso/Diagnostics/Orientation Replay…", priority = 80)]
        static void RunFromFolderPicker()
        {
            string start = EditorPrefs.GetString(RootsPrefKey, "").Split(';').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            string dir = EditorUtility.OpenFolderPanel("Sessions folder to replay", start, "");
            if (string.IsNullOrEmpty(dir))
                return;
            var saved = SavedRoots();
            if (!saved.Contains(dir))
            {
                saved.Add(dir);
                EditorPrefs.SetString(RootsPrefKey, string.Join(";", saved));
            }
            RunAndReport(new[] { dir }, null, interactive: true);
        }

        [MenuItem("SplatPresso/Diagnostics/Orientation Replay (saved roots)", priority = 81)]
        static void RunSavedRoots()
        {
            var roots = SavedRoots();
            if (roots.Count == 0)
            {
                EditorUtility.DisplayDialog("Orientation Replay", "No saved session roots yet. Use \"Orientation Replay…\" to pick one.", "OK");
                return;
            }
            RunAndReport(roots, null, interactive: true);
        }

        /// <summary>Batch entry point (-executeMethod); see the class remarks for the arguments.</summary>
        public static void RunBatch()
        {
            string[] args = Environment.GetCommandLineArgs();
            string rootsArg = ArgValue(args, "-replayRoots");
            string outArg = ArgValue(args, "-replayOut");
            var roots = SplitRoots(rootsArg);
            if (roots.Count == 0)
                roots = SplitRoots(Environment.GetEnvironmentVariable(RootsEnvVar));
            if (roots.Count == 0)
                roots = SavedRoots();
            int code = 0;
            try
            {
                if (roots.Count == 0)
                {
                    Debug.LogError("[SplatPresso] Orientation replay: no roots (-replayRoots \"a;b\" or " + RootsEnvVar + ")");
                    code = 2;
                }
                else
                {
                    RunAndReport(roots, outArg, interactive: false);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 1;
            }
            if (Application.isBatchMode)
                EditorApplication.Exit(code);
        }

        /// <summary>Replays every session under the roots; writes the CSV and logs the summary. Returns the rows.</summary>
        public static List<Row> RunAndReport(IEnumerable<string> roots, string csvPath, bool interactive)
        {
            List<Row> rows;
            try
            {
                rows = Run(roots, interactive ? (msg, p) => EditorUtility.DisplayProgressBar("Orientation Replay", msg, p) : (Action<string, float>)null);
            }
            finally
            {
                if (interactive)
                    EditorUtility.ClearProgressBar();
            }
            if (string.IsNullOrEmpty(csvPath))
            {
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "SplatPresso");
                Directory.CreateDirectory(dir);
                csvPath = Path.Combine(dir, "orientation_replay_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
            }
            else
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(csvPath));
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
            }
            File.WriteAllText(csvPath, ToCsv(rows), new UTF8Encoding(false));
            string summary = Summary(rows);
            Debug.Log("[SplatPresso] Orientation replay: " + rows.Count + " object(s) -> " + csvPath + "\n" + summary);
            if (interactive)
                EditorUtility.RevealInFinder(csvPath);
            return rows;
        }

        /// <summary>
        /// Replays the sessions under <paramref name="roots"/> (folders holding capture_meta.json, capture_depth.bin and
        /// result.json). Nothing is written into the sessions.
        /// </summary>
        public static List<Row> Run(IEnumerable<string> roots, Action<string, float> progress = null)
        {
            var rows = new List<Row>();
            var sessions = new List<(string root, string dir)>();
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;
                foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
                {
                    if (File.Exists(Path.Combine(dir, CaptureResult.MetaFileName)) && File.Exists(Path.Combine(dir, CaptureResult.DepthFileName)) &&
                        File.Exists(Path.Combine(dir, PipelineSession.ResultJson)))
                        sessions.Add((root, dir));
                }
            }

            var baseTuning = new PlacementTuning();
            var legacyTuning = baseTuning.Clone();
            legacyTuning.orientationMode = OrientationMode.CameraFacing;
            var sceneTuning = baseTuning.Clone();
            sceneTuning.orientationMode = OrientationMode.SceneAware;

            for (int i = 0; i < sessions.Count; ++i)
            {
                var (root, dir) = sessions[i];
                progress?.Invoke(Path.GetFileName(dir), i / (float)Math.Max(1, sessions.Count));
                try
                {
                    ReplaySession(root, dir, legacyTuning, sceneTuning, rows);
                }
                catch (Exception e)
                {
                    rows.Add(new Row { root = root, session = Path.GetFileName(dir), id = -1, error = e.GetType().Name + ": " + e.Message });
                }
            }
            return rows;
        }

        static void ReplaySession(string root, string dir, PlacementTuning legacyTuning, PlacementTuning sceneTuning, List<Row> rows)
        {
            string session = Path.GetFileName(dir);
            var capture = CaptureResult.LoadFrom(dir);
            if (capture == null)
                return;
            var result = JsonUtil.Deserialize<PlacementResult>(File.ReadAllText(Path.Combine(dir, PipelineSession.ResultJson)));
            if (result?.objects == null)
                return;
            var decision = ReadJson<DecisionResult>(Path.Combine(dir, PipelineSession.DecisionJson));
            var verification = ReadJson<VerificationResult>(Path.Combine(dir, PipelineSession.VerificationJson));
            string modeTxt = ReadText(Path.Combine(dir, PipelineSession.ModeTxt));
            bool direct = modeTxt != null ? modeTxt.Trim() == GenerationMode.DirectTextTo3D.ToString() : !File.Exists(Path.Combine(dir, PipelineSession.EditedJpg));

            // as ObjectSpawnService.PlaceAsync: the generated depth of the edited image (scene mode only)
            string depthPath = Path.Combine(dir, PipelineSession.DepthGenPng);
            float[] genDepth = !direct && File.Exists(depthPath) ? ObjectSpawnService.LoadGenDepth(depthPath, capture.width, capture.height) : null;

            foreach (var obj in result.objects)
            {
                if (obj == null)
                    continue;
                var row = new Row
                {
                    root = root, session = session, id = obj.id, name = obj.name, mode = direct ? "direct" : "scene",
                    restingSurface = obj.restingSurface,
                };
                rows.Add(row);
                try
                {
                    ReplayObject(dir, obj, decision, verification, direct, capture, genDepth, legacyTuning, sceneTuning, row);
                }
                catch (Exception e)
                {
                    row.error = e.GetType().Name + ": " + e.Message;
                }
            }
        }

        static void ReplayObject(string dir, PlacedObjectResult obj, DecisionResult decision, VerificationResult verification, bool direct,
            CaptureResult capture, float[] genDepth, PlacementTuning legacyTuning, PlacementTuning sceneTuning, Row row)
        {
            // hints by id (older sessions have none; result.json of newer ones already carries them)
            var d = decision?.objects?.Find(o => o != null && o.id == obj.id);
            var v = verification?.objects?.Find(o => o != null && o.id == obj.id);
            if (string.IsNullOrEmpty(obj.restingSurface) && d != null)
                obj.restingSurface = d.restingSurface;
            obj.againstWall = d?.againstWall ?? obj.againstWall;
            obj.support = v?.support ?? obj.support;
            obj.backAgainstWall = v?.backAgainstWall ?? obj.backAgainstWall;
            obj.frontFaces = v?.frontFaces ?? obj.frontFaces;
            row.restingSurface = obj.restingSurface;
            row.againstWall = obj.againstWall;
            row.support = obj.support;
            row.backAgainstWall = obj.backAgainstWall;
            row.frontFaces = obj.frontFaces;

            // paths are rebuilt from the session folder (result.json paths are absolute; the older format used plyPath)
            string objDir = Path.Combine(dir, PipelineSession.ObjectsDir, obj.id.ToString(CultureInfo.InvariantCulture));
            string ply = Path.Combine(objDir, PipelineSession.ObjectPly);
            string glb = Path.Combine(objDir, PipelineSession.ObjectGlb);
            string cutout = Path.Combine(objDir, PipelineSession.ObjectCutoutPng);
            bool isMesh = !File.Exists(ply) && File.Exists(glb);
            row.representation = isMesh ? "mesh" : "splat";
            if (!File.Exists(ply) && !File.Exists(glb))
            {
                row.error = "no model";
                return;
            }

            bool[] mask = !direct && File.Exists(cutout) ? ObjectSpawnService.LoadObjectMask(cutout, capture.width, capture.height) : null;
            row.hasMask = mask != null;

            GaussianSplatAsset asset = null;
            try
            {
                Vector3 contentSize;
                ObjectShape shape;
                if (isMesh)
                {
                    // no glTF import here: unit bounds, the shape falls back to them (yaw and wall fit are still exact)
                    contentSize = Vector3.one;
                    shape = default;
                }
                else
                {
                    asset = RuntimeSplatAssetFactory.CreateFromFile(ply, "replay_" + obj.id, out _);
                    contentSize = ObjectSpawnService.ContentBoundsSize(asset.boundsMax - asset.boundsMin, sceneTuning.contentRotationEuler, sceneTuning.contentScale);
                    shape = ObjectSpawnService.SplatShape(asset, sceneTuning);
                }

                var legacy = SplatPlacement.Solve(capture, obj.BboxGenerated, obj.sizeHintM, genDepth, mask, contentSize, legacyTuning, isMesh);
                var scene = SplatPlacement.Solve(capture, obj.BboxGenerated, obj.sizeHintM, genDepth, mask, contentSize, sceneTuning, isMesh,
                    OrientationInputs.From(obj, shape));
                if (!scene.valid)
                {
                    row.error = "solve failed: " + scene.note;
                    return;
                }
                var o = scene.orientation;
                var w = o.wall;
                row.intent = o.intent.ToString();
                row.intentSource = o.intentSource;
                row.rule = o.applied.ToString();
                row.candidate = o.candidate.ToString();
                row.cameraYaw = o.cameraYawDeg;
                row.wallFound = w.found;
                row.wallYaw = o.candidate == YawRule.Wall ? o.wallYawDeg : (w.found ? Mathf.Atan2(w.normal.x, w.normal.z) * Mathf.Rad2Deg : 0f);
                row.appliedYaw = o.applied == YawRule.Wall ? o.wallYawDeg : o.cameraYawDeg;
                row.deltaDeg = Mathf.DeltaAngle(o.cameraYawDeg, row.appliedYaw);
                row.reject = w.reject;
                row.reanchor = o.reanchor;
                row.shiftM = o.applied == YawRule.Wall ? (scene.position - legacy.position).magnitude : 0f;
                row.standoffM = o.standoffM;
                row.standoffClamped = o.standoffClamped;
                row.flushApplied = o.applied == YawRule.Wall && o.flushApplied;
                row.corner = o.corner;
                row.frac = w.inlierFraction;
                row.ext = w.extentM;
                row.ySpan = w.ySpanM;
                row.behind = w.behindFraction;
                row.split = w.splitDeg;
                row.anchorDist = w.anchorDistanceM;
                row.centerErr = w.centerErrM;
                row.centerValid = w.centerValid;
                row.confidence = o.confidence;
                row.elapsedMs = o.elapsedMs;
                row.note = scene.note;
                row.legacyNote = legacy.note;
                row.legacyPosition = legacy.position;
                row.position = scene.position;
                row.legacyRotationYaw = legacy.rotation.eulerAngles.y;
                row.rotationYaw = scene.rotation.eulerAngles.y;
                row.legacyScale = legacy.uniformScale;
                row.scale = scene.uniformScale;
                row.identicalToLegacy = scene.position.Equals(legacy.position) && scene.rotation.Equals(legacy.rotation) &&
                                        scene.uniformScale.Equals(legacy.uniformScale);
                if (asset != null && o.applied == YawRule.Wall)
                    row.backGapM = BackGap(asset, sceneTuning, scene, w);
            }
            finally
            {
                if (asset != null)
                    RuntimeSplatAssetFactory.DestroyRuntimeAsset(asset);
            }
        }

        // 2nd percentile of the splat centres' distance in front of the wall plane, in the snapped pose.
        static float BackGap(GaussianSplatAsset asset, PlacementTuning tuning, SplatPlacement.PlacementSolution sol, WallFit wall)
        {
            if (asset.posFormat != GaussianSplatAsset.VectorFormat.Float32 || asset.posData == null)
                return float.NaN;
            var xyz = asset.posData.GetData<float>();
            int count = Mathf.Min(asset.splatCount, xyz.Length / 3);
            int stride = Mathf.Max(1, count / 20000);
            Quaternion content = Quaternion.Euler(tuning.contentRotationEuler);
            var dist = new List<float>(count / stride + 1);
            for (int i = 0; i < count; i += stride)
            {
                Vector3 q = content * Vector3.Scale(new Vector3(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]), tuning.contentScale);
                Vector3 world = sol.position + sol.rotation * (q * sol.uniformScale);
                float dd = wall.normal.x * (world.x - wall.point.x) + wall.normal.z * (world.z - wall.point.z);
                if (!float.IsNaN(dd))
                    dist.Add(dd);
            }
            if (dist.Count == 0)
                return float.NaN;
            dist.Sort();
            return SceneOrientation.Percentile(dist, 0.02f);
        }

        // ---- output -------------------------------------------------------------------------------------------

        static readonly string[] kColumns =
        {
            "root", "session", "id", "name", "mode", "rep", "mask", "resting", "against", "support", "back", "front",
            "intent", "source", "rule", "candidate", "camYaw", "wallYaw", "appliedYaw", "dcam", "reject", "reanchor", "shift", "standoff",
            "clamped", "corner", "frac", "ext", "ySpan", "behind", "split", "d", "cerr", "conf", "backGap", "identical",
            "legacyPos", "pos", "legacyRotY", "rotY", "scale", "ms", "note", "error",
        };

        /// <summary>CSV of the rows (invariant culture, one row per object).</summary>
        public static string ToCsv(List<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", kColumns));
            foreach (var r in rows)
            {
                var f = new List<string>
                {
                    Shorten(r.root), r.session, r.id.ToString(CultureInfo.InvariantCulture), r.name, r.mode, r.representation, r.hasMask ? "1" : "0",
                    r.restingSurface, r.againstWall, r.support, r.backAgainstWall, r.frontFaces,
                    r.intent, r.intentSource, r.rule, r.candidate, F(r.cameraYaw, 1), F(r.wallYaw, 1), F(r.appliedYaw, 1), F(r.deltaDeg, 1),
                    r.reject, r.reanchor, F(r.shiftM, 3), F(r.standoffM, 3), r.standoffClamped ? "1" : "0", r.corner ? "1" : "0",
                    F(r.frac, 3), F(r.ext, 2), F(r.ySpan, 2), F(r.behind, 3), F(r.split, 1), F(r.anchorDist, 3), r.centerValid ? F(r.centerErr, 3) : "",
                    F(r.confidence, 2), float.IsNaN(r.backGapM) ? "" : F(r.backGapM, 3), r.identicalToLegacy ? "1" : "0",
                    V(r.legacyPosition), V(r.position), F(r.legacyRotationYaw, 1), F(r.rotationYaw, 1), F(r.scale, 3), F(r.elapsedMs, 1),
                    r.note, r.error,
                };
                sb.AppendLine(string.Join(",", f.Select(Csv)));
            }
            return sb.ToString();
        }

        /// <summary>Counts by intent, rule and reject reason, and the solve-time percentiles.</summary>
        public static string Summary(List<Row> rows)
        {
            var ok = rows.Where(r => r.error == null).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"objects {rows.Count}, replayed {ok.Count}, errors {rows.Count - ok.Count}");
            foreach (var g in ok.GroupBy(r => r.intent).OrderBy(g => g.Key))
                sb.AppendLine($"  intent {g.Key}: {g.Count()}");
            foreach (var g in ok.GroupBy(r => r.rule).OrderBy(g => g.Key))
                sb.AppendLine($"  rule {g.Key}: {g.Count()}");
            foreach (var g in ok.Where(r => !string.IsNullOrEmpty(r.reject)).GroupBy(r => r.reject.Split(' ')[0]).OrderBy(g => g.Key))
                sb.AppendLine($"  reject {g.Key}: {g.Count()} ({string.Join(" ", g.Select(r => r.session + "/" + r.id))})");
            var ms = ok.Where(r => r.intent == WallIntent.Mounted.ToString() || r.intent == WallIntent.Backed.ToString()).Select(r => r.elapsedMs).OrderBy(x => x).ToList();
            if (ms.Count > 0)
                sb.AppendLine($"  evaluate ms (wall intents): p50 {F(ms[ms.Count / 2], 1)}, p95 {F(ms[Mathf.Clamp((int)Math.Round(0.95 * (ms.Count - 1)), 0, ms.Count - 1)], 1)}, max {F(ms[ms.Count - 1], 1)}");
            foreach (var r in ok.Where(r => r.rule == YawRule.Wall.ToString()))
                sb.AppendLine($"  WALL {r.session}/{r.id} '{r.name}' {r.intent}({r.intentSource}) cam {F(r.cameraYaw, 1)} -> wall {F(r.wallYaw, 1)} shift {F(r.shiftM, 2)} {r.reanchor}" +
                              (float.IsNaN(r.backGapM) ? "" : " gap " + F(r.backGapM, 3)));
            foreach (var r in rows.Where(r => r.error != null))
                sb.AppendLine($"  ERROR {r.session}/{r.id} '{r.name}': {r.error}");
            return sb.ToString();
        }

        // ---- helpers ------------------------------------------------------------------------------------------

        static List<string> SavedRoots() => SplitRoots(EditorPrefs.GetString(RootsPrefKey, ""));

        static List<string> SplitRoots(string s) =>
            string.IsNullOrWhiteSpace(s) ? new List<string>() : s.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; ++i)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        static T ReadJson<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path) ? JsonUtil.Deserialize<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        static string Shorten(string root)
        {
            if (string.IsNullOrEmpty(root))
                return root;
            var parts = root.Replace('\\', '/').Split('/');
            return parts.Length >= 3 ? string.Join("/", parts.Skip(Math.Max(0, parts.Length - 4)).Take(2)) : root;
        }

        static string F(float v, int digits) => float.IsNaN(v) ? "" : v.ToString("F" + digits, CultureInfo.InvariantCulture);

        static string V(Vector3 v) => $"({F(v.x, 3)} {F(v.y, 3)} {F(v.z, 3)})";

        static string Csv(string s)
        {
            if (s == null)
                return "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
