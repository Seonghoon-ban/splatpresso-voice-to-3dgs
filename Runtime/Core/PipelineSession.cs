using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SplatPresso
{
    /// <summary>
    /// One pipeline run = one session directory. Every stage's inputs and outputs are cached here, which
    /// enables (a) debugging by inspection and (b) replaying the pipeline from any stage without re-spending
    /// API calls. The run id is the folder name, so logs and events join with the artifacts.
    /// </summary>
    /// <remarks>
    /// Layout (root = <see cref="SplatPressoSettings.sessionsFolder"/> or &lt;persistentDataPath&gt;/SplatPresso/sessions):
    /// <code>
    /// &lt;root&gt;/&lt;yyyyMMdd_HHmmss[_n]&gt;/
    ///   capture.jpg / capture_depth.bin / capture_meta.json   (CaptureResult)
    ///   request.json  mode.txt  representation.txt
    ///   decision.json                                         (DecisionResult)
    ///   edited.jpg + edited_url.txt                           (image-edit output + hosted URL)
    ///   verification.json                                     (VerificationResult)
    ///   depth_gen.png + depth_gen_url.txt                     (relative depth of the edited image)
    ///   objects/&lt;id&gt;/cutout.png|cutout_url.txt|enhanced.png|enhanced_url.txt|generated.png|generated_url.txt|model.ply|model.glb|object.json
    ///   ledger.json                                           (CostLedger)
    ///   result.json                                           (PlacementResult)
    /// </code>
    /// </remarks>
    public sealed class PipelineSession
    {
        /// <summary>Absolute session directory.</summary>
        public string Dir { get; }
        /// <summary>Session folder name; used as the run id.</summary>
        public string RunId => Path.GetFileName(Dir);
        /// <summary>The session's cost ledger (ledger.json).</summary>
        public CostLedger Ledger { get; }

        /// <summary>Sessions root for the active settings.</summary>
        public static string SessionsRoot => ResolveSessionsRoot(SplatPressoSettings.FindActive());

        /// <summary>
        /// Sessions root for <paramref name="settings"/>: its sessionsFolder (relative paths resolve against
        /// persistentDataPath) or &lt;persistentDataPath&gt;/SplatPresso/sessions.
        /// </summary>
        public static string ResolveSessionsRoot(SplatPressoSettings settings)
        {
            string folder = settings != null ? settings.sessionsFolder : null;
            if (string.IsNullOrWhiteSpace(folder))
                return Path.Combine(Application.persistentDataPath, "SplatPresso", "sessions");
            folder = folder.Trim();
            return Path.IsPathRooted(folder)
                ? Path.GetFullPath(folder)
                : Path.GetFullPath(Path.Combine(Application.persistentDataPath, folder));
        }

        PipelineSession(string dir)
        {
            Dir = dir;
            Directory.CreateDirectory(dir);
            Ledger = CostLedger.LoadOrCreate(PathOf(LedgerJson));
        }

        /// <summary>Creates a new, uniquely named session under the active sessions root.</summary>
        public static PipelineSession CreateNew() => CreateNew(SessionsRoot);

        /// <summary>Creates a new, uniquely named session under <paramref name="sessionsRoot"/>.</summary>
        public static PipelineSession CreateNew(string sessionsRoot)
        {
            string dir = Path.Combine(sessionsRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            // avoid a collision when two runs start within the same second
            string unique = dir;
            int suffix = 1;
            while (Directory.Exists(unique))
                unique = $"{dir}_{suffix++}";
            return new PipelineSession(unique);
        }

        /// <summary>Opens an existing session (throws <see cref="DirectoryNotFoundException"/> if missing).</summary>
        public static PipelineSession Open(string dir)
        {
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException($"Session directory not found: {dir}");
            return new PipelineSession(dir);
        }

        /// <summary>Most recent session directory under the active root, or null.</summary>
        public static string LatestSessionDir() => LatestSessionDir(SessionsRoot);

        /// <summary>Most recent session directory under <paramref name="sessionsRoot"/>, or null.</summary>
        public static string LatestSessionDir(string sessionsRoot)
        {
            if (!Directory.Exists(sessionsRoot))
                return null;
            return Directory.GetDirectories(sessionsRoot).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault();
        }

        /// <summary>All session directories under the active root, newest first.</summary>
        public static string[] ListSessionDirs() => ListSessionDirs(SessionsRoot);

        /// <summary>All session directories under <paramref name="sessionsRoot"/>, newest first.</summary>
        public static string[] ListSessionDirs(string sessionsRoot)
        {
            if (!Directory.Exists(sessionsRoot))
                return Array.Empty<string>();
            return Directory.GetDirectories(sessionsRoot).OrderByDescending(d => d, StringComparer.Ordinal).ToArray();
        }

        // ---- paths ----

        /// <summary>Absolute path of a session-level file.</summary>
        public string PathOf(string fileName) => Path.Combine(Dir, fileName);

        /// <summary>Absolute path of objects/&lt;id&gt; (does not create it).</summary>
        public string ObjectDirPath(int objectId) => Path.Combine(Dir, ObjectsDir, objectId.ToString(CultureInfo.InvariantCulture));

        /// <summary>objects/&lt;id&gt;, created if missing (writers rely on this).</summary>
        public string ObjectDir(int objectId)
        {
            string dir = ObjectDirPath(objectId);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Path of a file inside objects/&lt;id&gt; (creates the folder, see <see cref="ObjectDir"/>).</summary>
        public string ObjectPath(int objectId, string fileName) => Path.Combine(ObjectDir(objectId), fileName);

        /// <summary>True when objects/&lt;id&gt;/&lt;fileName&gt; exists (never creates folders).</summary>
        public bool HasObjectFile(int objectId, string fileName) => File.Exists(Path.Combine(ObjectDirPath(objectId), fileName));

        /// <summary>Model file name for a representation: model.ply or model.glb.</summary>
        public static string ModelFileName(ObjectRepresentation representation) =>
            representation == ObjectRepresentation.Mesh ? ObjectGlb : ObjectPly;

        // ---- json / text / bytes helpers ----

        public void SaveJson<T>(string fileName, T obj) =>
            File.WriteAllText(PathOf(fileName), JsonUtil.Serialize(obj));

        /// <summary>Loads a JSON artifact; null when the file does not exist.</summary>
        public T LoadJson<T>(string fileName) where T : class
        {
            string path = PathOf(fileName);
            return File.Exists(path) ? JsonUtil.Deserialize<T>(File.ReadAllText(path)) : null;
        }

        public bool Has(string fileName) => File.Exists(PathOf(fileName));

        public void SaveText(string fileName, string text) => File.WriteAllText(PathOf(fileName), text ?? "");

        /// <summary>Trimmed file text; null when the file does not exist.</summary>
        public string LoadText(string fileName)
        {
            string path = PathOf(fileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        public void SaveBytes(string fileName, byte[] bytes) => File.WriteAllBytes(PathOf(fileName), bytes);

        /// <summary>File bytes; null when the file does not exist.</summary>
        public byte[] LoadBytes(string fileName)
        {
            string path = PathOf(fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        /// <summary>Deletes a session-level file if it exists (errors are logged, not thrown).</summary>
        public void DeleteFile(string fileName)
        {
            try
            {
                string path = PathOf(fileName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Could not delete {fileName} in {RunId}: {e.Message}");
            }
        }

        /// <summary>
        /// Deletes every per-object cache (objects/). Replays that re-run Edit/Verify must do this, otherwise
        /// cutouts made from the OLD edited image are reused (they are keyed only by object id).
        /// </summary>
        public void DeleteObjectsDir()
        {
            try
            {
                string path = PathOf(ObjectsDir);
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Could not delete objects/ in {RunId}: {e.Message}");
            }
        }

        // ---- well-known artifact names ----

        public const string CaptureJpg = CaptureResult.JpegFileName;
        public const string CaptureDepthBin = CaptureResult.DepthFileName;
        public const string CaptureMetaJson = CaptureResult.MetaFileName;
        public const string RequestJson = "request.json";
        public const string ModeTxt = "mode.txt";
        public const string RepresentationTxt = "representation.txt";
        public const string DecisionJson = "decision.json";
        public const string EditedJpg = "edited.jpg";
        public const string EditedUrlTxt = "edited_url.txt";
        public const string VerificationJson = "verification.json";
        public const string DepthGenPng = "depth_gen.png";
        public const string DepthGenUrlTxt = "depth_gen_url.txt";
        public const string ResultJson = "result.json";
        public const string LedgerJson = "ledger.json";
        public const string ObjectsDir = "objects";
        public const string ObjectCutoutPng = "cutout.png";
        public const string ObjectCutoutUrlTxt = "cutout_url.txt";
        public const string ObjectEnhancedPng = "enhanced.png";
        public const string ObjectEnhancedUrlTxt = "enhanced_url.txt";
        public const string ObjectGeneratedPng = "generated.png";      // DirectTextTo3D text-to-image output
        public const string ObjectGeneratedUrlTxt = "generated_url.txt";
        public const string ObjectPly = "model.ply";
        public const string ObjectGlb = "model.glb";
        public const string ObjectJson = "object.json";
    }
}
