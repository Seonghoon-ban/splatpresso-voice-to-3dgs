using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SplatPresso.Bootstrap
{
    /// <summary>Per-user, per-project bootstrap choices. UserSettings/ is normally not committed.</summary>
    [FilePath("UserSettings/SplatPresso.Bootstrap.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class BootstrapPrefs : ScriptableSingleton<BootstrapPrefs>
    {
        public bool dontAskGs;

        public void SaveNow() => Save(true);
    }

    /// <summary>
    /// Installs the dependency a package.json cannot declare: aras-p's Gaussian Splatting is hosted on GitHub, and
    /// the Package Manager only accepts git URLs in the PROJECT manifest (a git URL in package.json fails
    /// resolution for the whole project). Everything else in SplatPresso is gated on the GS package through
    /// asmdef versionDefines + defineConstraints, so this assembly (no references at all) is the only SplatPresso
    /// code that compiles before GS exists. That matters: if any assembly failed to compile, a running editor
    /// would load nothing new and this installer would never run.
    /// </summary>
    /// <remarks>
    /// Batch mode never shows dialogs and never edits the manifest unless the command line contains
    /// <c>-splatpressoInstallDeps</c>. Add <c>-splatpressoExitWhenDone</c> to quit with exit code 0/1 once the package
    /// is installed AND the resulting domain reload has happened. Do not combine with <c>-quit</c>: Unity would exit
    /// before the asynchronous install finishes.
    /// </remarks>
    [InitializeOnLoad]
    public static class DependencyInstaller
    {
        /// <summary>Package name of aras-p's UnityGaussianSplatting.</summary>
        public const string GsName = "org.nesnausk.gaussian-splatting";
        /// <summary>
        /// Pinned commit (upstream HEAD after v1.1.1). It includes the composite-blend fix the placement calibration
        /// was made with; a full SHA is required (the Package Manager rejects short SHAs). Path comes before revision.
        /// </summary>
        public const string GsGitUrl = "https://github.com/aras-p/UnityGaussianSplatting.git?path=/package#2c6fed37da67a217367261fcfcd3316d34c73e76";
        /// <summary>OpenUPM registry used when git is unavailable or the clone fails.</summary>
        public const string OpenUpmUrl = "https://package.openupm.com";
        /// <summary>GS version installed from OpenUPM (the v1.1.1 tag; predates the composite-blend fix).</summary>
        public const string OpenUpmVersion = "1.1.1";
        /// <summary>Optional: glTFast, needed only for Mesh (Rodin) representation.</summary>
        public const string GltfastName = "com.unity.cloud.gltfast";
        /// <summary>Batch-mode flag: install missing dependencies without dialogs.</summary>
        public const string InstallDepsFlag = "-splatpressoInstallDeps";
        /// <summary>Batch-mode flag: exit (0 = ok, 1 = failure) once the install and its domain reload are done.</summary>
        public const string ExitWhenDoneFlag = "-splatpressoExitWhenDone";

        const string kTitle = "SplatPresso Voice To 3DGS";
        const string kPinnedCommit = "2c6fed37da67a217367261fcfcd3316d34c73e76";
        // Supported GS range [1.1.0, 2.0.0): 1.1.0 added URP Render Graph support (GaussianSplatURPFeature before it
        // only implements Execute, so it renders nothing once Setup turns Render Graph on). The other SplatPresso
        // assemblies are gated on the minimum through asmdef versionDefines, so only this assembly can report it.
        static readonly Version kMinGsVersion = new Version(1, 1, 0);
        static readonly Version kMaxGsVersionExclusive = new Version(2, 0, 0);
        const string kCheckedKey = "SplatPresso.Bootstrap.Checked";
        const string kExitPendingKey = "SplatPresso.Bootstrap.ExitPending";
        const double kReloadTimeoutSec = 300;        // install succeeded but no domain reload -> give up
        const double kPostReloadWaitSec = 120;       // after a reload, wait this long for the package to register

        // One Package Manager request at a time: "Client methods must execute sequentially; concurrent operations
        // produce unpredictable results." These statics live for one domain (editor state, not play state).
        static Request s_Request;
        static Action<Request> s_OnRequestDone;
        static double s_WatchDeadline;

        static DependencyInstaller()
        {
            // [InitializeOnLoad] also runs inside asset import worker processes.
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            if (SessionState.GetBool(kExitPendingKey, false))
            {
                // A batch install finished and Unity reloaded the domain (SessionState survives reloads).
                RunWhenIdle(FinishExitAfterReload);
                return;
            }
            // Runs on every domain reload (including entering play mode): check once per editor session.
            if (SessionState.GetBool(kCheckedKey, false))
                return;
            RunWhenIdle(() => Check(fromMenu: false));
        }

        /// <summary>Clears "Don't ask again" and re-runs the dependency check (also offers glTFast for Mesh mode).</summary>
        [MenuItem("SplatPresso/Install or Repair Dependencies", priority = 200)]
        public static void InstallOrRepair()
        {
            BootstrapPrefs.instance.dontAskGs = false;
            BootstrapPrefs.instance.SaveNow();
            SessionState.SetBool(kCheckedKey, false);
            RunWhenIdle(() => Check(fromMenu: true));
        }

        /// <summary>True when the Gaussian Splatting package is installed (from any source).</summary>
        public static bool IsGaussianSplattingInstalled => FindPackage(GsName) != null;

        // ------------------------------------------------------------------------------------------

        static void Check(bool fromMenu)
        {
            if (s_Request != null)
            {
                if (fromMenu)
                    Debug.Log("[SplatPresso] A package request is already running; wait for it to finish.");
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (fromMenu)
                    EditorUtility.DisplayDialog(kTitle, "Exit play mode first.", "OK");
                return; // not marked as checked: the next domain reload checks again
            }
            SessionState.SetBool(kCheckedKey, true);

            bool batch = Application.isBatchMode;
            bool exitWhenDone = batch && HasArg(ExitWhenDoneFlag);

            // Never touch an existing install of ANY source: Client.Add of the git URL would replace a user's
            // local fork, an OpenUPM install or an embedded copy.
            var gs = FindPackage(GsName);
            if (gs != null)
            {
                if (!IsSupportedGsVersion(gs.version))
                {
                    // Still never replaced without explicit consent; batch mode only reports.
                    Debug.LogError(UnsupportedGsMessage(gs));
                    if (exitWhenDone)
                    {
                        Exit(1);
                        return;
                    }
                    if (!batch && (fromMenu || !BootstrapPrefs.instance.dontAskGs))
                        OfferGsReplacement(gs);
                    return;
                }
                if (exitWhenDone)
                {
                    Debug.Log("[SplatPresso] Dependencies already installed: " + Describe(gs));
                    Exit(0);
                    return;
                }
                if (fromMenu)
                    OfferOptionalPackages(gs);
                return;
            }

            if (batch)
            {
                if (!HasArg(InstallDepsFlag))
                {
                    // CI must not get a mutated manifest unless it asked for it.
                    Debug.LogError(MissingGsMessage());
                    if (exitWhenDone)
                        Exit(1);
                    return;
                }
                Debug.Log($"[SplatPresso] {InstallDepsFlag}: installing {GsName} ...");
                InstallGs(interactive: false, alsoGltf: false, exitWhenDone: exitWhenDone);
                return;
            }

            if (!fromMenu && BootstrapPrefs.instance.dontAskGs)
                return;

            int choice = EditorUtility.DisplayDialogComplex(kTitle,
                "SplatPresso renders with aras-p's Gaussian Splatting package (MIT). It is hosted on GitHub, so it " +
                "cannot be a regular package dependency and has to be added to your project manifest.\n\n" +
                "Install it now from:\n" + GsGitUrl + "\n\nThis needs Git; without Git an OpenUPM fallback is offered.",
                "Install (git)", "Not now", "Don't ask again");
            if (choice == 1)
            {
                Debug.Log("[SplatPresso] Gaussian Splatting not installed. SplatPresso stays inactive until it is: " +
                          "SplatPresso > Install or Repair Dependencies.");
                return;
            }
            if (choice == 2)
            {
                BootstrapPrefs.instance.dontAskGs = true;
                BootstrapPrefs.instance.SaveNow();
                Debug.Log("[SplatPresso] Will not ask again in this project. Install later from " +
                          "SplatPresso > Install or Repair Dependencies, or add this line to Packages/manifest.json:\n" + ManifestLine());
                return;
            }

            bool alsoGltf = fromMenu && FindPackage(GltfastName) == null && AskGltf();
            InstallGs(interactive: true, alsoGltf: alsoGltf, exitWhenDone: false);
        }

        static void InstallGs(bool interactive, bool alsoGltf, bool exitWhenDone)
        {
            if (!IsGitAvailable())
            {
                const string noGit = "Git (2.14 or newer) was not found on PATH. The Package Manager needs it for GitHub packages. " +
                                     "Install Git and restart BOTH Unity and Unity Hub, or install Gaussian Splatting " + OpenUpmVersion +
                                     " from the OpenUPM registry instead (no Git needed).";
                if (!interactive)
                {
                    Debug.LogWarning("[SplatPresso] " + noGit + " Falling back to OpenUPM.");
                    InstallViaOpenUpm(alsoGltf, exitWhenDone);
                    return;
                }
                if (EditorUtility.DisplayDialog(kTitle, noGit, "Install from OpenUPM", "Cancel"))
                    InstallViaOpenUpm(alsoGltf, false);
                else
                    Debug.LogWarning("[SplatPresso] Gaussian Splatting not installed (no Git). Manifest line:\n" + ManifestLine());
                return;
            }

            if (exitWhenDone)
                SessionState.SetBool(kExitPendingKey, true);
            string label = alsoGltf ? "Gaussian Splatting + glTFast" : "Gaussian Splatting";
            Debug.Log($"[SplatPresso] Installing {label} (git clone; progress in the Package Manager window) ...");
            // AddAndRemove resolves the dependency list once for both packages.
            Request req = alsoGltf
                ? Client.AddAndRemove(new[] { GsGitUrl, GltfastName }, null)
                : Client.Add(GsGitUrl);
            StartRequest(req, r => OnGsRequestDone(r, label, interactive, alsoGltf, exitWhenDone));
        }

        static void OnGsRequestDone(Request req, string label, bool interactive, bool alsoGltf, bool exitWhenDone)
        {
            if (req.Status == StatusCode.Success)
            {
                Debug.Log($"[SplatPresso] Installed {label}. Unity now compiles and reloads scripts; then run SplatPresso > Setup Scene.");
                if (exitWhenDone)
                    StartReloadWatchdog();
                return;
            }

            string message = req.Error?.message ?? "unknown error";
            Debug.LogError($"[SplatPresso] Installing {label} failed ({req.Error?.errorCode}): {message}");
            bool noGit = message.Contains("No 'git' executable");
            bool tooLong = message.Contains("Filename too long");
            if (noGit)
                Debug.LogError("[SplatPresso] Install Git (2.14+), then restart BOTH Unity and Unity Hub: the Package Manager looks git up only when it starts.");
            if (tooLong)
                Debug.LogError("[SplatPresso] The project path is too long for a git clone on Windows (the clone goes deep under Library/PackageCache). " +
                               "Move the project to a shorter path (under ~150 characters), or use the OpenUPM fallback.");

            if (noGit || tooLong)
            {
                if (!interactive)
                {
                    Debug.LogWarning("[SplatPresso] Falling back to OpenUPM.");
                    InstallViaOpenUpm(alsoGltf, exitWhenDone);
                    return;
                }
                if (EditorUtility.DisplayDialog(kTitle,
                        "Installing from GitHub failed:\n" + message + "\n\nInstall Gaussian Splatting " + OpenUpmVersion +
                        " from OpenUPM instead? It downloads a tarball, so it needs no Git and has no path-length problem.",
                        "Install from OpenUPM", "Cancel"))
                {
                    InstallViaOpenUpm(alsoGltf, false);
                    return;
                }
            }

            if (exitWhenDone)
            {
                SessionState.EraseBool(kExitPendingKey);
                Exit(1);
            }
        }

        // Fallback without git: a scoped registry (tarball download). Scoped registries must be in the PROJECT
        // manifest, so this edits Packages/manifest.json and lets the Package Manager resolve it.
        static void InstallViaOpenUpm(bool alsoGltf, bool exitWhenDone)
        {
            try
            {
                AddOpenUpmToManifest();
            }
            catch (Exception e)
            {
                Debug.LogError("[SplatPresso] Could not edit Packages/manifest.json for OpenUPM: " + e.Message + "\nAdd it by hand:\n" + OpenUpmManifestSnippet());
                if (exitWhenDone)
                    Exit(1);
                return;
            }
            Debug.Log($"[SplatPresso] Added the OpenUPM scoped registry and \"{GsName}\": \"{OpenUpmVersion}\" to Packages/manifest.json. " +
                      "Note: OpenUPM carries the v1.1.1 tag, which predates an upstream composite-blending fix; splat colors may differ slightly.");

            if (exitWhenDone)
                SessionState.SetBool(kExitPendingKey, true);
            if (alsoGltf)
            {
                // Client.Add resolves the whole (edited) manifest, so one resolve covers both packages.
                StartRequest(Client.Add(GltfastName), r => OnGsRequestDone(r, "Gaussian Splatting (OpenUPM) + glTFast", false, false, exitWhenDone));
            }
            else
            {
                Client.Resolve();
                if (exitWhenDone)
                    StartReloadWatchdog();
            }
        }

        static void AddOpenUpmToManifest()
        {
            string path = Path.GetFullPath("Packages/manifest.json");
            var root = JObject.Parse(File.ReadAllText(path));

            var registries = root["scopedRegistries"] as JArray;
            if (registries == null)
            {
                registries = new JArray();
                root["scopedRegistries"] = registries;
            }
            var registry = registries.OfType<JObject>().FirstOrDefault(r =>
                string.Equals(r["url"]?.ToString().TrimEnd('/'), OpenUpmUrl, StringComparison.OrdinalIgnoreCase));
            if (registry == null)
            {
                registry = new JObject { ["name"] = "package.openupm.com", ["url"] = OpenUpmUrl, ["scopes"] = new JArray() };
                registries.Add(registry);
            }
            var scopes = registry["scopes"] as JArray;
            if (scopes == null)
            {
                scopes = new JArray();
                registry["scopes"] = scopes;
            }
            if (!scopes.Any(s => s.ToString() == GsName))
                scopes.Add(GsName);

            var deps = root["dependencies"] as JObject;
            if (deps == null)
            {
                deps = new JObject();
                root["dependencies"] = deps;
            }
            deps[GsName] = OpenUpmVersion;

            File.WriteAllText(path, root.ToString(Formatting.Indented) + "\n");
        }

        static void OfferOptionalPackages(PackageInfo gs)
        {
            string gsLine = Describe(gs);
            if (gs.source == PackageSource.Git && gs.git != null && !string.IsNullOrEmpty(gs.git.hash) &&
                !gs.git.hash.StartsWith(kPinnedCommit.Substring(0, 8), StringComparison.OrdinalIgnoreCase))
                gsLine += "\n(SplatPresso was verified with commit " + kPinnedCommit.Substring(0, 8) + "; other versions usually work.)";

            var gltf = FindPackage(GltfastName);
            if (gltf != null)
            {
                EditorUtility.DisplayDialog(kTitle, "All dependencies are installed:\n\n" + gsLine + "\n" + Describe(gltf), "OK");
                return;
            }
            if (EditorUtility.DisplayDialog(kTitle,
                    "Gaussian Splatting is installed:\n" + gsLine + "\n\nAlso install glTFast for Mesh (Rodin) mode?\n" +
                    "It is only needed when Representation = Mesh; Gaussian splat mode does not use it.",
                    "Install glTFast", "Not now"))
            {
                Debug.Log("[SplatPresso] Installing glTFast ...");
                StartRequest(Client.Add(GltfastName), r =>
                {
                    if (r.Status == StatusCode.Success)
                        Debug.Log("[SplatPresso] Installed glTFast. Mesh (Rodin) mode is available after the script reload.");
                    else
                        Debug.LogError($"[SplatPresso] Installing glTFast failed ({r.Error?.errorCode}): {r.Error?.message}");
                });
            }
        }

        // Only after explicit consent: an unsupported install is otherwise left untouched (it may be a user's fork).
        static void OfferGsReplacement(PackageInfo gs)
        {
            string current = Describe(gs);
            string range = $"{kMinGsVersion} or newer, below {kMaxGsVersionExclusive}";
            if (gs.source == PackageSource.Embedded)
            {
                EditorUtility.DisplayDialog(kTitle,
                    $"{current} is embedded in the project, but SplatPresso needs Gaussian Splatting {range} (1.1.0 added URP Render Graph " +
                    $"support). Update the embedded copy at\n{gs.resolvedPath}\nor delete it and add this line to Packages/manifest.json:\n" + ManifestLine(),
                    "OK");
                return;
            }
            int choice = EditorUtility.DisplayDialogComplex(kTitle,
                $"{current} is installed, but SplatPresso needs Gaussian Splatting {range} (1.1.0 added URP Render Graph support, " +
                "which SplatPresso needs; older versions render nothing with Render Graph on).\n\n" +
                "Replace it with:\n" + GsGitUrl + "\n\nThis changes the \"" + GsName + "\" entry in Packages/manifest.json.",
                "Replace (git)", "Not now", "Don't ask again");
            if (choice == 1)
                return;
            if (choice == 2)
            {
                BootstrapPrefs.instance.dontAskGs = true;
                BootstrapPrefs.instance.SaveNow();
                Debug.Log("[SplatPresso] Will not ask again in this project. Replace Gaussian Splatting later from " +
                          "SplatPresso > Install or Repair Dependencies, or set this line in Packages/manifest.json:\n" + ManifestLine());
                return;
            }
            InstallGs(interactive: true, alsoGltf: false, exitWhenDone: false);
        }

        /// <summary>
        /// True when <paramref name="version"/> is in the supported range [1.1.0, 2.0.0). A prerelease/build suffix is
        /// ignored; an unparseable version is not blocked.
        /// </summary>
        internal static bool IsSupportedGsVersion(string version)
        {
            var v = ParseVersion(version);
            return v == null || (v >= kMinGsVersion && v < kMaxGsVersionExclusive);
        }

        static Version ParseVersion(string version)
        {
            if (string.IsNullOrEmpty(version))
                return null;
            int cut = version.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0)
                version = version.Substring(0, cut);
            if (!Version.TryParse(version.Trim(), out var v))
                return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); // "1.1" == "1.1.0"
        }

        static string UnsupportedGsMessage(PackageInfo gs)
        {
            var v = ParseVersion(gs.version);
            string why = v != null && v < kMinGsVersion
                ? $"SplatPresso needs {GsName} {kMinGsVersion} or newer (1.1.0 added URP Render Graph support), so SplatPresso is inactive"
                : $"SplatPresso supports {GsName} from {kMinGsVersion} up to (not including) {kMaxGsVersionExclusive}; this version is untested " +
                  "and may not compile or render";
            return $"[SplatPresso] Installed {Describe(gs)}: {why}. Replace it with SplatPresso > Install or Repair Dependencies, " +
                   "or set this line in \"dependencies\" in Packages/manifest.json:\n" + ManifestLine();
        }

        static bool AskGltf() => EditorUtility.DisplayDialog(kTitle,
            "Also install glTFast for Mesh (Rodin) mode?\nIt is only needed when Representation = Mesh; Gaussian splat mode does not use it.",
            "Yes", "No");

        // ------------------------------------------------------------------------------------------
        // Batch-mode exit handling

        static void StartReloadWatchdog()
        {
            // Success is only final after Unity compiled the new package and reloaded the domain; the next
            // domain's static constructor sees kExitPendingKey and exits. This watchdog only survives when that
            // reload never comes (compile errors block it) and turns that into a failure exit.
            s_WatchDeadline = EditorApplication.timeSinceStartup + kReloadTimeoutSec;
            EditorApplication.update -= WatchForReload;
            EditorApplication.update += WatchForReload;
        }

        static void WatchForReload()
        {
            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && EditorUtility.scriptCompilationFailed)
            {
                EditorApplication.update -= WatchForReload;
                SessionState.EraseBool(kExitPendingKey);
                Debug.LogError("[SplatPresso] Scripts failed to compile after installing dependencies (see the errors above).");
                Exit(1);
            }
            else if (EditorApplication.timeSinceStartup > s_WatchDeadline)
            {
                EditorApplication.update -= WatchForReload;
                SessionState.EraseBool(kExitPendingKey);
                Debug.LogError("[SplatPresso] Timed out waiting for Unity to reload scripts after installing dependencies.");
                Exit(1);
            }
        }

        static void FinishExitAfterReload()
        {
            s_WatchDeadline = EditorApplication.timeSinceStartup + kPostReloadWaitSec;
            EditorApplication.update -= WaitForPackageThenExit;
            EditorApplication.update += WaitForPackageThenExit;
        }

        static void WaitForPackageThenExit()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            var gs = FindPackage(GsName);
            bool timedOut = EditorApplication.timeSinceStartup > s_WatchDeadline;
            if (gs == null && !timedOut)
                return;
            EditorApplication.update -= WaitForPackageThenExit;
            SessionState.EraseBool(kExitPendingKey);
            if (gs == null)
            {
                Debug.LogError($"[SplatPresso] '{GsName}' is still not installed after the script reload.");
                Exit(1);
            }
            else if (EditorUtility.scriptCompilationFailed)
            {
                Debug.LogError("[SplatPresso] Dependencies installed, but scripts have compile errors (see above).");
                Exit(1);
            }
            else
            {
                Debug.Log("[SplatPresso] Dependencies installed: " + Describe(gs));
                Exit(0);
            }
        }

        static void Exit(int code)
        {
            Debug.Log($"[SplatPresso] {ExitWhenDoneFlag}: exiting with code {code}.");
            EditorApplication.Exit(code);
        }

        // ------------------------------------------------------------------------------------------
        // Helpers

        static void StartRequest(Request request, Action<Request> onDone)
        {
            s_Request = request;
            s_OnRequestDone = onDone;
            EditorApplication.update -= PollRequest;
            EditorApplication.update += PollRequest;
        }

        static void PollRequest()
        {
            if (s_Request == null)
            {
                EditorApplication.update -= PollRequest;
                return;
            }
            if (!s_Request.IsCompleted)
                return;
            EditorApplication.update -= PollRequest;
            var request = s_Request;
            var onDone = s_OnRequestDone;
            s_Request = null;
            s_OnRequestDone = null;
            try
            {
                onDone?.Invoke(request);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // Defer past the domain-reload callback, then wait until the editor is idle (not compiling, not
        // resolving packages) before touching the Package Manager.
        static void RunWhenIdle(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                EditorApplication.CallbackFunction tick = null;
                tick = () =>
                {
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                        return;
                    EditorApplication.update -= tick;
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                };
                EditorApplication.update += tick;
            };
        }

        static PackageInfo FindPackage(string name)
        {
            try
            {
                return PackageInfo.FindForPackageName(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Describe(PackageInfo p)
        {
            if (p == null)
                return "(not installed)";
            string s = $"{p.name} {p.version} ({p.source}";
            if (p.source == PackageSource.Git && p.git != null && !string.IsNullOrEmpty(p.git.hash))
                s += " " + p.git.hash.Substring(0, Math.Min(8, p.git.hash.Length));
            return s + ")";
        }

        static bool HasArg(string flag) =>
            Environment.GetCommandLineArgs().Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        // The Package Manager server runs git from the same environment, so `git --version` is a faithful pre-check.
        static bool IsGitAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null)
                        return false;
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { /* already gone */ }
                        return false;
                    }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string ManifestLine() => $"  \"{GsName}\": \"{GsGitUrl}\"";

        static string MissingGsMessage() =>
            $"[SplatPresso] Required package '{GsName}' is missing, so SplatPresso is inactive. Add this line to \"dependencies\" " +
            "in Packages/manifest.json (and commit manifest.json + packages-lock.json so CI and teammates get it):\n" + ManifestLine() +
            $"\nor run Unity with {InstallDepsFlag} (add {ExitWhenDoneFlag} to quit when finished; do not combine with -quit).";

        static string OpenUpmManifestSnippet() =>
            "  \"scopedRegistries\": [ { \"name\": \"package.openupm.com\", \"url\": \"" + OpenUpmUrl + "\", \"scopes\": [ \"" + GsName + "\" ] } ],\n" +
            "  \"dependencies\": { \"" + GsName + "\": \"" + OpenUpmVersion + "\", ... }";
    }
}
