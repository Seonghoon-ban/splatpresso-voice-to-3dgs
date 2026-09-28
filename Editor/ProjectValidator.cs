using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SplatPresso.Placement;
using SplatPresso.Rendering;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SplatPresso.EditorTools
{
    /// <summary>Severity of a <see cref="ValidationFinding"/>.</summary>
    public enum ValidationSeverity
    {
        Ok,
        Info,
        Warning,
        Error,
    }

    /// <summary>One validator result line.</summary>
    public sealed class ValidationFinding
    {
        public ValidationSeverity severity;
        public string message;

        public override string ToString() => $"{Tag(severity)} {message}";

        internal static string Tag(ValidationSeverity s) =>
            s == ValidationSeverity.Error ? "[ERROR]" : s == ValidationSeverity.Warning ? "[WARN] " : s == ValidationSeverity.Info ? "[INFO] " : "[OK]   ";
    }

    /// <summary>Result of <see cref="ProjectValidator.Validate()"/>.</summary>
    public sealed class ValidationReport
    {
        public readonly List<ValidationFinding> findings = new List<ValidationFinding>();

        public int ErrorCount => findings.Count(f => f.severity == ValidationSeverity.Error);
        public int WarningCount => findings.Count(f => f.severity == ValidationSeverity.Warning);

        internal void Add(ValidationSeverity severity, string message) =>
            findings.Add(new ValidationFinding { severity = severity, message = message });

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[SplatPresso] Project validation: ").Append(ErrorCount).Append(" error(s), ").Append(WarningCount).Append(" warning(s)");
            foreach (var f in findings.OrderByDescending(f => f.severity))
                sb.Append('\n').Append(f);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Read-only project checks (SplatPresso &gt; Validate Project, the Setup window's status panel and the build
    /// preprocessor). Nothing here modifies the project; SplatPresso &gt; Setup Scene fixes what it can.
    /// </summary>
    public static class ProjectValidator
    {
        /// <summary>Validates for the editor (active build target, editor device and API keys included).</summary>
        public static ValidationReport Validate() => Validate(false, EditorUserBuildSettings.activeBuildTarget);

        /// <summary>Validates; <paramref name="forBuild"/> limits machine-specific checks and focuses on <paramref name="target"/>.</summary>
        public static ValidationReport Validate(bool forBuild, BuildTarget target)
        {
            var r = new ValidationReport();
            var settings = SplatPressoEditorUtil.FindSettingsAsset(out bool loadable);
            try
            {
                CheckPackages(r, settings);
                bool urp = CheckPipelines(r);
                if (urp)
                {
                    CheckRenderGraph(r);
                    CheckRenderers(r, settings, forBuild);
                    CheckMsaa(r);
                }
                CheckGraphicsApis(r, forBuild, target);
                CheckSettings(r, settings, loadable);
                CheckKeys(r, settings, forBuild);
                CheckScenes(r, forBuild);
            }
            catch (Exception e)
            {
                r.Add(ValidationSeverity.Error, "Validator failed: " + e.Message);
            }
            return r;
        }

        /// <summary>Runs <see cref="Validate()"/>, logs the report and shows a summary dialog.</summary>
        public static ValidationReport RunFromMenu()
        {
            var r = Validate();
            Log(r);
            var top = r.findings.Where(f => f.severity >= ValidationSeverity.Warning).OrderByDescending(f => f.severity).Take(6).Select(f => "- " + f.message);
            string body = r.ErrorCount == 0 && r.WarningCount == 0
                ? "Everything SplatPresso needs is in place."
                : string.Join("\n", top) + (r.ErrorCount + r.WarningCount > 6 ? "\n..." : "") + "\n\nFull report in the Console. SplatPresso > Setup Scene fixes most of these.";
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog($"SplatPresso: {r.ErrorCount} error(s), {r.WarningCount} warning(s)", body, "OK");
            return r;
        }

        internal static void Log(ValidationReport r)
        {
            if (r.ErrorCount > 0)
                Debug.LogError(r.ToString());
            else if (r.WarningCount > 0)
                Debug.LogWarning(r.ToString());
            else
                Debug.Log(r.ToString());
        }

        // ------------------------------------------------------------------------------------------

        static void CheckPackages(ValidationReport r, SplatPressoSettings settings)
        {
            var gs = PackageInfo.FindForPackageName("org.nesnausk.gaussian-splatting");
            if (gs != null)
                r.Add(ValidationSeverity.Ok, $"Gaussian Splatting {gs.version} ({gs.source})");
            if (!GsInternals.Validate(out string gsError))
                r.Add(ValidationSeverity.Error, gsError);
            if (UrpRendererUtil.GsUrpFeatureType == null)
                r.Add(ValidationSeverity.Error, "GaussianSplatting.Runtime.GaussianSplatURPFeature not found. Gaussian Splatting compiles its URP support " +
                                                "only when URP is installed; reinstall it (SplatPresso > Install or Repair Dependencies).");
#if SPLATPRESSO_HAS_GLTFAST
            r.Add(ValidationSeverity.Ok, "glTFast installed (Mesh representation available)");
#else
            if (settings != null && settings.representation == ObjectRepresentation.Mesh)
                r.Add(ValidationSeverity.Warning, "Representation is Mesh but glTFast is not installed; mesh objects will be skipped " +
                                                  "(SplatPresso > Install or Repair Dependencies).");
            else
                r.Add(ValidationSeverity.Info, "glTFast not installed (only needed for Mesh representation)");
#endif
        }

        static bool CheckPipelines(ValidationReport r)
        {
            var levels = UrpRendererUtil.GetQualityLevelPipelines();
            if (levels.Count == 0)
            {
                bool ok = GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset;
                r.Add(ok ? ValidationSeverity.Ok : ValidationSeverity.Error,
                    ok ? "URP is the default render pipeline" : "The project does not use URP; SplatPresso needs URP (run Setup).");
                return ok;
            }
            int urp = 0;
            foreach (var l in levels)
            {
                if (l.effective is UniversalRenderPipelineAsset)
                    urp++;
                else if (l.effective == null)
                    r.Add(ValidationSeverity.Error, $"Quality level '{l.name}' renders with the Built-in pipeline; SplatPresso needs URP (run Setup).");
                else
                    r.Add(ValidationSeverity.Error, $"Quality level '{l.name}' uses {l.effective.GetType().Name} ({l.effective.name}); SplatPresso supports URP only.");
            }
            if (urp == levels.Count)
                r.Add(ValidationSeverity.Ok, $"URP on all {levels.Count} quality level(s)");
            return urp > 0;
        }

        static void CheckRenderGraph(ValidationReport r)
        {
            bool? compat = UrpRendererUtil.IsRenderGraphCompatibilityMode();
            if (compat == null)
                r.Add(ValidationSeverity.Warning, "URP global settings not found yet (URP creates them the first time it renders); Render Graph state unknown.");
            else if (compat.Value)
                r.Add(ValidationSeverity.Error, "URP Render Graph compatibility mode is ON: Gaussian Splatting and the SplatPresso capture only implement " +
                                                "Render Graph, so nothing renders (Project Settings > Graphics > URP > Render Graph, or run Setup).");
            else
                r.Add(ValidationSeverity.Ok, "Render Graph enabled");
        }

        static void CheckRenderers(ValidationReport r, SplatPressoSettings settings, bool forBuild)
        {
            var gsType = UrpRendererUtil.GsUrpFeatureType;
            var renderers = UrpRendererUtil.FindUrpRenderers();
            if (renderers.Count == 0)
            {
                r.Add(ValidationSeverity.Error, "The URP assets have no renderer data.");
                return;
            }
            int good = 0;
            foreach (var rd in renderers)
            {
                string label = $"Renderer '{rd.name}'";
                if (!(rd is UniversalRendererData))
                {
                    r.Add(ValidationSeverity.Warning, $"{label} is a {rd.GetType().Name}; Gaussian splats need the Universal Renderer.");
                    continue;
                }
                bool ok = true;
                var gsFeature = UrpRendererUtil.FindFeature(rd, gsType);
                if (gsType != null && gsFeature == null)
                {
                    r.Add(ValidationSeverity.Error, $"{label}: GaussianSplatURPFeature missing, so splats are invisible with this renderer (run Setup).");
                    ok = false;
                }
                else if (gsFeature != null && !gsFeature.isActive)
                {
                    r.Add(ValidationSeverity.Warning, $"{label}: GaussianSplatURPFeature is disabled.");
                    ok = false;
                }

                var capture = UrpRendererUtil.FindFeature(rd, typeof(SplatCaptureFeature)) as SplatCaptureFeature;
                if (capture == null)
                {
                    r.Add(ValidationSeverity.Error, $"{label}: SplatCaptureFeature missing, so scene captures time out (run Setup).");
                    ok = false;
                }
                else
                {
                    if (!capture.isActive)
                    {
                        r.Add(ValidationSeverity.Warning, $"{label}: SplatCaptureFeature is disabled.");
                        ok = false;
                    }
                    if (gsFeature != null && UrpRendererUtil.IndexOf(rd, gsType) > UrpRendererUtil.IndexOf(rd, typeof(SplatCaptureFeature)))
                    {
                        r.Add(ValidationSeverity.Error, $"{label}: SplatCaptureFeature must come AFTER GaussianSplatURPFeature (it reuses that pass's per-splat " +
                                                        "view data from the same frame). Run Setup to reorder.");
                        ok = false;
                    }
                    if (capture.m_SplatDepthShader == null || capture.m_SceneDepthPrimeShader == null)
                    {
                        // The feature fills them with Shader.Find in the editor only; players need the serialized references.
                        r.Add(forBuild ? ValidationSeverity.Error : ValidationSeverity.Warning,
                            $"{label}: SplatCaptureFeature shader references are empty; players cannot capture (run Setup).");
                        ok = false;
                    }
                    if (settings != null && settings.placement != null &&
                        !Mathf.Approximately(capture.m_DepthAlphaThreshold, settings.placement.depthAlphaThreshold))
                        r.Add(ValidationSeverity.Info, $"{label}: capture depth alpha threshold {capture.m_DepthAlphaThreshold:0.###} differs from the settings " +
                                                       $"({settings.placement.depthAlphaThreshold:0.###}); Setup copies the settings value.");
                }
                if (UrpRendererUtil.HasFeatureNamed(rd, UrpRendererUtil.LegacyCaptureFeatureTypeName))
                    r.Add(ValidationSeverity.Warning, $"{label}: the research fork's GaussianSplatCaptureFeature is also present; remove it (SplatCaptureFeature replaces it).");
                if (ok)
                    good++;
            }
            if (good == renderers.Count)
                r.Add(ValidationSeverity.Ok, $"Splat + capture features installed in order on all {renderers.Count} URP renderer(s)");
        }

        static void CheckMsaa(ValidationReport r)
        {
            foreach (var asset in UrpRendererUtil.FindUrpAssets())
                if (asset.msaaSampleCount > 1)
                    r.Add(ValidationSeverity.Warning, $"URP asset '{asset.name}' uses {asset.msaaSampleCount}x MSAA: the SplatPresso capture does not support MSAA (run Setup to turn it off).");
        }

        static void CheckGraphicsApis(ValidationReport r, bool forBuild, BuildTarget target)
        {
            bool windows = target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneWindows;
            if (!forBuild || windows)
            {
                var t = forBuild ? target : BuildTarget.StandaloneWindows64;
                var apis = PlayerSettings.GetGraphicsAPIs(t);
                var first = apis != null && apis.Length > 0 ? apis[0] : GraphicsDeviceType.Null;
                if (first == GraphicsDeviceType.Direct3D11 || first == GraphicsDeviceType.OpenGLCore)
                    r.Add(ValidationSeverity.Error, $"Windows players start on {first}; Gaussian splats need D3D12 or Vulkan first " +
                                                    "(Player Settings > Other Settings > Graphics APIs, or run Setup).");
                else if (first != GraphicsDeviceType.Null)
                    r.Add(ValidationSeverity.Ok, $"Windows graphics API: {first}");
            }
            if (forBuild)
            {
                if (target == BuildTarget.WebGL)
                    r.Add(ValidationSeverity.Error, "WebGL is not supported (Gaussian Splatting needs compute shaders; voice needs sockets).");
                else if (!windows && target != BuildTarget.StandaloneOSX && target != BuildTarget.StandaloneLinux64)
                    r.Add(ValidationSeverity.Warning, $"SplatPresso targets desktop players; {target} is untested.");
            }
            else
            {
                var device = SystemInfo.graphicsDeviceType;
                if (device == GraphicsDeviceType.Direct3D11 || device == GraphicsDeviceType.OpenGLCore)
                    r.Add(ValidationSeverity.Warning, $"The editor runs on {device}, where Gaussian splats do not render. Put D3D12/Vulkan first and restart the editor.");
            }
        }

        static void CheckSettings(ValidationReport r, SplatPressoSettings settings, bool loadable)
        {
            if (settings == null)
                r.Add(ValidationSeverity.Warning, "No SplatPressoSettings asset; players fall back to built-in defaults (run Setup).");
            else if (!loadable)
                r.Add(ValidationSeverity.Warning, $"Settings asset {AssetDatabase.GetAssetPath(settings)} is not '{SplatPressoSettings.ResourceName}' at the root of a " +
                                                  "Resources folder, so only scene references find it in players.");
            else
                r.Add(ValidationSeverity.Ok, "Settings: " + AssetDatabase.GetAssetPath(settings));
        }

        static void CheckKeys(ValidationReport r, SplatPressoSettings settings, bool forBuild)
        {
            if (settings != null && !string.IsNullOrWhiteSpace(settings.apiKey))
                r.Add(ValidationSeverity.Warning, $"A GenPresso key is stored in plain text in {AssetDatabase.GetAssetPath(settings)}: it is committed with the " +
                                                  "project and shipped in builds. Prefer Project Settings > SplatPresso (user profile) or GENPRESSO_API_KEY.");
            if (File.Exists(ApiKeys.StreamingAssetsKeysPath))
                r.Add(ValidationSeverity.Warning, "StreamingAssets/splatpresso.keys.json exists: its keys ship in plain text inside builds.");
            if (forBuild)
                return;
            string key = ApiKeys.Get(ApiKeyKind.Genpresso, out var source);
            if (string.IsNullOrEmpty(key))
                r.Add(ValidationSeverity.Warning, "No GenPresso API key on this machine (Project Settings > SplatPresso; get one at " + SplatPressoEditorUtil.KeyPortalUrl + ").");
            else
                r.Add(ValidationSeverity.Ok, $"GenPresso key {ApiKeys.Mask(key)} ({source})");
        }

        static void CheckScenes(ValidationReport r, bool forBuild)
        {
            var buildScenes = new HashSet<string>(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path));
            bool anyRoot = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || (forBuild && !buildScenes.Contains(scene.path)))
                    continue;
                string sceneName = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;
                foreach (var go in scene.GetRootGameObjects())
                {
                    anyRoot |= go.GetComponentInChildren<SplatPressoRoot>(true) != null;
                    foreach (var spawn in go.GetComponentsInChildren<ObjectSpawnService>(true))
                        if (spawn.rendererResources == null || !spawn.rendererResources.IsComplete)
                            r.Add(ValidationSeverity.Error, $"{sceneName}: ObjectSpawnService on '{spawn.name}' lacks Gaussian Splatting shader references; " +
                                                            "spawned splats render nothing in players (run Setup).");
                    foreach (var preview in go.GetComponentsInChildren<PlacementPreviewService>(true))
                        if (preview.previewShader == null)
                            r.Add(ValidationSeverity.Warning, $"{sceneName}: PlacementPreviewService on '{preview.name}' has no preview shader; players fall back to Sprites/Default.");
                }
            }
            if (!anyRoot && !forBuild)
                r.Add(ValidationSeverity.Info, "No SplatPressoRoot in the open scene(s) (SplatPresso > Setup Scene adds one).");
        }
    }

    /// <summary>
    /// Runs the validator before every player build and logs its findings. It never fails the build: a project may
    /// contain the package without using it in every build, and the log is explicit about what breaks.
    /// </summary>
    internal sealed class SplatPressoBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var r = ProjectValidator.Validate(true, report.summary.platform);
            if (r.ErrorCount > 0 || r.WarningCount > 0)
                ProjectValidator.Log(r);
        }
    }
}
