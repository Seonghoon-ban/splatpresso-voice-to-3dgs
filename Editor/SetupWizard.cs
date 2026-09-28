using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SplatPresso.Extras;
using SplatPresso.Placement;
using SplatPresso.Rendering;
using SplatPresso.Voice;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SplatPresso.EditorTools
{
    /// <summary>What <see cref="SetupWizard.Run"/> may do. Defaults = the recommended full setup.</summary>
    [Serializable]
    public sealed class SetupOptions
    {
        [Tooltip("Show confirmation dialogs (ignored in batch mode, which never shows dialogs).")]
        public bool interactive = true;

        [Tooltip("If the project renders with the Built-in pipeline, create a URP asset and make it the default (asks first).")]
        public bool configureRenderPipeline = true;
        [Tooltip("Turn URP's Render Graph compatibility mode off (Gaussian Splatting renders only with Render Graph).")]
        public bool enableRenderGraph = true;
        [Tooltip("Add GaussianSplatURPFeature and SplatCaptureFeature (in that order) to every URP renderer.")]
        public bool installRendererFeatures = true;
        [Tooltip("Turn MSAA off on URP assets that use it (asks first); the capture does not support MSAA.")]
        public bool disableMsaa = true;
        [Tooltip("Put D3D12 first in the Windows player's graphics APIs when D3D11/OpenGL is first (D3D11 cannot render splats).")]
        public bool fixGraphicsApis = true;

        [Tooltip("Add and wire the SplatPresso components in the active scene.")]
        public bool setupScene = true;
        [Tooltip("VoiceAgent + MicCapture + AudioSource + AudioStreamPlayer.")]
        public bool addVoice = true;
        [Tooltip("VoiceHud (IMGUI push-to-talk pill, subtitles, text box, run list).")]
        public bool addHud = true;
        [Tooltip("Create a 'Main Camera' when the scene has no camera.")]
        public bool createCameraIfMissing = true;
        [Tooltip("Add FirstPersonCamera to the capture camera - only when it has no scripts and no parent (never replaces a controller).")]
        public bool addCameraController = true;
        [Tooltip("DebugHotkeys (F5 canned run, F6/F7 replay, F8 cancel, F9 load a .ply, Tab overlay) and PlacementNudgeController " +
                 "(arrows/PgUp/PgDn/[ ]/, . nudge the last object to calibrate placement). Both read keys every frame, so they are opt-in.")]
        public bool addDebugHotkeys = false;
        [Tooltip("Add the scene to Build Settings.")]
        public bool addSceneToBuildSettings = false;
        [Tooltip("Offer to save modified scenes at the end (interactive only).")]
        public bool saveScenes = true;

        /// <summary>A copy of these options.</summary>
        public SetupOptions Clone() => (SetupOptions)MemberwiseClone();
    }

    /// <summary>What <see cref="SetupWizard.Run"/> did.</summary>
    public sealed class SetupReport
    {
        public readonly List<string> created = new List<string>();
        public readonly List<string> present = new List<string>();
        public readonly List<string> warnings = new List<string>();
        public readonly List<string> errors = new List<string>();
        /// <summary>The editor must restart before a change takes effect (graphics API).</summary>
        public bool restartRequired;
        /// <summary>Path of the scene that was set up (empty while unsaved).</summary>
        public string scenePath;

        /// <summary>True when nothing failed.</summary>
        public bool Success => errors.Count == 0;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[SplatPresso] Setup ").Append(Success ? "complete." : "finished with errors.");
            sb.Append("\nCreated / changed: ").Append(created.Count > 0 ? string.Join("; ", created) : "(nothing)");
            sb.Append("\nAlready present: ").Append(present.Count > 0 ? string.Join("; ", present) : "(nothing)");
            foreach (var w in warnings)
                sb.Append("\nWARNING: ").Append(w);
            foreach (var e in errors)
                sb.Append("\nERROR: ").Append(e);
            if (restartRequired)
                sb.Append("\nRestart the editor so it runs on the new graphics API.");
            return sb.ToString();
        }

        internal void Log()
        {
            if (errors.Count > 0)
                Debug.LogError(ToString());
            else if (warnings.Count > 0)
                Debug.LogWarning(ToString());
            else
                Debug.Log(ToString());
        }
    }

    /// <summary>
    /// SplatPresso &gt; Setup Scene. Idempotent, Undo-aware project + scene setup that only adds what is missing and
    /// never replaces a user's assignments. Also callable headless: <see cref="Run"/> (no dialogs when
    /// <c>options.interactive</c> is false or in batch mode) and <see cref="RunBatch"/> for <c>-executeMethod</c>.
    /// </summary>
    public sealed class SetupWizard : EditorWindow
    {
        const string kTitle = "SplatPresso Setup";
        const string kUrpPostProcessDataPath = "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";
        const string kPreviewShaderName = "SplatPresso/Preview";
        const string kInstallDepsMenu = "SplatPresso/Install or Repair Dependencies";

        [SerializeField] SetupOptions m_Options = new SetupOptions();
        [SerializeField] string m_LastReport;
        Vector2 m_Scroll;
        ValidationReport m_Status;
        double m_NextStatusTime;

        /// <summary>Opens the Setup window.</summary>
        public static void Open()
        {
            var w = GetWindow<SetupWizard>(false, kTitle, true);
            w.minSize = new Vector2(460, 520);
            w.RefreshStatus();
            w.Show();
        }

        /// <summary>
        /// Batch entry point (<c>-executeMethod SplatPresso.EditorTools.SetupWizard.RunBatch</c>): runs the full setup
        /// without dialogs on the active scene (a new one if none is loaded) and saves that scene - to
        /// <c>Assets/SplatPresso/Demo/SplatPressoTest.unity</c> when it was never saved. Does not exit the editor.
        /// </summary>
        public static void RunBatch()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var report = Run(new SetupOptions { interactive = false });

            string path = scene.path;
            if (string.IsNullOrEmpty(path))
            {
                SplatPressoEditorUtil.EnsureFolder(SplatPressoEditorUtil.DemoFolder);
                path = SplatPressoEditorUtil.BatchScenePath;
            }
            if (EditorSceneManager.SaveScene(scene, path))
            {
                report.scenePath = path;
                Debug.Log("[SplatPresso] Setup (batch): saved scene " + path);
            }
            else
            {
                Debug.LogError("[SplatPresso] Setup (batch): could not save scene " + path);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>Runs the setup and logs the report. Never throws (errors are reported).</summary>
        public static SetupReport Run(SetupOptions options)
        {
            options = options ?? new SetupOptions();
            bool interactive = options.interactive && !Application.isBatchMode;
            var report = new SetupReport();
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                report.errors.Add("Exit play mode before running Setup.");
                report.Log();
                return report;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("SplatPresso Setup");
            int undoGroup = Undo.GetCurrentGroup();
            try
            {
                var settings = EnsureSettingsAsset(report);
                if (EnsureUrpPipeline(options, interactive, report))
                {
                    EnsureRenderGraph(options, report);
                    if (options.installRendererFeatures)
                        EnsureRendererFeatures(settings, report);
                    CheckMsaa(options, interactive, report);
                }
                if (options.fixGraphicsApis)
                    EnsureWindowsGraphicsApis(report);
                CheckEditorGraphicsDevice(report);
                if (options.setupScene)
                    SetupActiveScene(settings, options, report);
                Finish(options, interactive, report);
            }
            catch (Exception e)
            {
                report.errors.Add("Unexpected error: " + e.Message);
                Debug.LogException(e);
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
            report.Log();
            if (interactive)
                OfferRestart(report);
            return report;
        }

        // ------------------------------------------------------------------------------------------
        // Window

        void OnEnable() => RefreshStatus();

        void OnFocus() => RefreshStatus();

        void OnInspectorUpdate()
        {
            if (EditorApplication.timeSinceStartup < m_NextStatusTime)
                return;
            RefreshStatus();
            Repaint();
        }

        void RefreshStatus()
        {
            m_Status = ProjectValidator.Validate();
            m_NextStatusTime = EditorApplication.timeSinceStartup + 3.0;
        }

        void OnGUI()
        {
            m_Options ??= new SetupOptions();
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.LabelField("Makes this project and the active scene ready for SplatPresso. Safe to run again: it only adds " +
                                       "what is missing and never replaces your own assignments (Undo works for scene changes).",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            if (m_Status != null)
            {
                foreach (var f in m_Status.findings)
                    DrawFinding(f);
            }
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Project", EditorStyles.boldLabel);
            Toggle(ref m_Options.configureRenderPipeline, "Switch a Built-in project to URP", nameof(SetupOptions.configureRenderPipeline));
            Toggle(ref m_Options.enableRenderGraph, "Turn Render Graph on", nameof(SetupOptions.enableRenderGraph));
            Toggle(ref m_Options.installRendererFeatures, "Install splat + capture renderer features", nameof(SetupOptions.installRendererFeatures));
            Toggle(ref m_Options.disableMsaa, "Turn MSAA off (asks first)", nameof(SetupOptions.disableMsaa));
            Toggle(ref m_Options.fixGraphicsApis, "Put D3D12 first for Windows players", nameof(SetupOptions.fixGraphicsApis));
#if !SPLATPRESSO_HAS_GLTFAST
            // glTFast is optional (Mesh / Rodin representation only); the bootstrap assembly owns package installs.
            if (GUILayout.Button("Install glTFast for Mesh mode...", EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                EditorApplication.ExecuteMenuItem(kInstallDepsMenu);
#endif
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Scene: " + SceneLabel(SceneManager.GetActiveScene()), EditorStyles.boldLabel);
            Toggle(ref m_Options.setupScene, "Set up the active scene", nameof(SetupOptions.setupScene));
            using (new EditorGUI.DisabledScope(!m_Options.setupScene))
            using (new EditorGUI.IndentLevelScope())
            {
                Toggle(ref m_Options.addVoice, "Voice agent (mic + speech playback)", nameof(SetupOptions.addVoice));
                Toggle(ref m_Options.addHud, "On-screen HUD", nameof(SetupOptions.addHud));
                Toggle(ref m_Options.createCameraIfMissing, "Create a camera if there is none", nameof(SetupOptions.createCameraIfMissing));
                Toggle(ref m_Options.addCameraController, "First-person controls on a bare camera", nameof(SetupOptions.addCameraController));
                Toggle(ref m_Options.addDebugHotkeys, "Debug tools (F5-F9 + Tab hotkeys, placement nudge keys)", nameof(SetupOptions.addDebugHotkeys));
                Toggle(ref m_Options.addSceneToBuildSettings, "Add the scene to Build Settings", nameof(SetupOptions.addSceneToBuildSettings));
                Toggle(ref m_Options.saveScenes, "Offer to save the scene afterwards", nameof(SetupOptions.saveScenes));
            }
            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Run Setup", GUILayout.Height(32)))
                {
                    var options = m_Options.Clone();
                    options.interactive = true;
                    m_LastReport = Run(options).ToString();
                    RefreshStatus();
                    GUIUtility.ExitGUI();
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("Create Demo Scene"))
                    {
                        // Keeps this window's project opt-outs (Render Graph, graphics APIs, ...); the builder sets the scene fields.
                        DemoSceneBuilder.Build(true, m_Options.Clone());
                        RefreshStatus();
                        GUIUtility.ExitGUI();
                    }
                }
                if (GUILayout.Button("Validate"))
                {
                    ProjectValidator.RunFromMenu();
                    RefreshStatus();
                }
                if (GUILayout.Button("Settings & Keys"))
                    SplatPressoEditorUtil.OpenSettings();
                if (GUILayout.Button("Debug Window"))
                    SplatPressoDebugWindow.Open();
            }

            if (!string.IsNullOrEmpty(m_LastReport))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Last run", EditorStyles.boldLabel);
                var style = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
                float h = style.CalcHeight(new GUIContent(m_LastReport), Mathf.Max(200f, position.width - 30f));
                EditorGUILayout.SelectableLabel(m_LastReport, style, GUILayout.Height(h));
            }
            EditorGUILayout.EndScrollView();
        }

        static void Toggle(ref bool value, string label, string field)
        {
            var fi = typeof(SetupOptions).GetField(field);
            var tip = fi != null ? (TooltipAttribute)Attribute.GetCustomAttribute(fi, typeof(TooltipAttribute)) : null;
            value = EditorGUILayout.ToggleLeft(new GUIContent(label, tip?.tooltip), value);
        }

        static void DrawFinding(ValidationFinding f)
        {
            string icon = f.severity == ValidationSeverity.Error ? "console.erroricon.sml"
                : f.severity == ValidationSeverity.Warning ? "console.warnicon.sml"
                : f.severity == ValidationSeverity.Info ? "console.infoicon.sml"
                : "TestPassed";
            var content = new GUIContent(f.message, EditorGUIUtility.IconContent(icon).image);
            EditorGUILayout.LabelField(content, EditorStyles.wordWrappedLabel);
        }

        static string SceneLabel(Scene s) => !s.IsValid() ? "(none)" : string.IsNullOrEmpty(s.path) ? "Untitled (unsaved)" : s.path;

        // ------------------------------------------------------------------------------------------
        // Project steps

        static SplatPressoSettings EnsureSettingsAsset(SetupReport report)
        {
            var existing = SplatPressoEditorUtil.FindSettingsAsset(out bool loadable);
            if (existing != null)
            {
                string path = AssetDatabase.GetAssetPath(existing);
                report.present.Add("settings " + path);
                if (!loadable)
                    report.warnings.Add($"Settings asset {path} is not '{SplatPressoSettings.ResourceName}' at the root of a Resources folder, so players find " +
                                        $"it only through scene references (Setup wires them). Move it to {SplatPressoEditorUtil.SettingsAssetPath} to make it the default.");
                return existing;
            }
            var created = SplatPressoEditorUtil.CreateSettingsAsset();
            report.created.Add("settings " + SplatPressoEditorUtil.SettingsAssetPath);
            return created;
        }

        // Returns true when at least one URP asset is in use afterwards.
        static bool EnsureUrpPipeline(SetupOptions options, bool interactive, SetupReport report)
        {
            var levels = UrpRendererUtil.GetQualityLevelPipelines();
            foreach (var l in levels)
                if (l.overrideAsset != null && !(l.overrideAsset is UniversalRenderPipelineAsset))
                    report.errors.Add($"Quality level '{l.name}' uses {l.overrideAsset.GetType().Name} ({l.overrideAsset.name}); SplatPresso supports URP only. Not changed.");

            var def = GraphicsSettings.defaultRenderPipeline;
            bool someLevelInheritsDefault = levels.Count == 0 || levels.Any(l => l.overrideAsset == null);
            if (def is UniversalRenderPipelineAsset || !someLevelInheritsDefault)
            {
                if (def is UniversalRenderPipelineAsset)
                    report.present.Add("URP pipeline " + AssetDatabase.GetAssetPath(def));
                else
                    report.present.Add("URP via quality-level pipeline assets");
                return UrpRendererUtil.FindUrpAssets().Count > 0;
            }
            if (def != null)
            {
                report.errors.Add($"The default render pipeline is {def.GetType().Name} ({def.name}); SplatPresso supports URP only. Not changed.");
                return UrpRendererUtil.FindUrpAssets().Count > 0;
            }

            // Built-in pipeline (fresh project that merely has the URP package installed).
            if (!options.configureRenderPipeline)
            {
                report.errors.Add("The project renders with the Built-in pipeline; SplatPresso needs URP (enable 'Switch a Built-in project to URP' " +
                                  "or assign a URP asset in Project Settings > Graphics).");
                return UrpRendererUtil.FindUrpAssets().Count > 0;
            }
            if (interactive && !EditorUtility.DisplayDialog(kTitle,
                    "This project renders with the Built-in render pipeline. SplatPresso needs URP.\n\n" +
                    $"Create {SplatPressoEditorUtil.UrpAssetPath} (+ renderer) and make it the default render pipeline?\n\n" +
                    "Materials using Built-in shaders render pink until converted (Window > Rendering > Render Pipeline Converter).",
                    "Switch to URP", "Cancel"))
            {
                report.errors.Add("Kept the Built-in render pipeline (cancelled); SplatPresso needs URP.");
                return false;
            }

            var urp = CreateOrLoadUrpAsset(report);
            if (urp == null)
                return false;
            var graphicsSettings = GraphicsSettings.GetGraphicsSettings();
            if (graphicsSettings != null)
                Undo.RecordObject(graphicsSettings, "Assign URP");
            GraphicsSettings.defaultRenderPipeline = urp;
            if (graphicsSettings != null)
                EditorUtility.SetDirty(graphicsSettings);
            int inheriting = levels.Count(l => l.overrideAsset == null);
            report.created.Add($"default render pipeline = {SplatPressoEditorUtil.UrpAssetPath} (used by {inheriting} quality level(s) without their own pipeline)");
            AssetDatabase.SaveAssets();
            return true;
        }

        static UniversalRenderPipelineAsset CreateOrLoadUrpAsset(SetupReport report)
        {
            SplatPressoEditorUtil.EnsureFolder(SplatPressoEditorUtil.RenderingFolder);
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(SplatPressoEditorUtil.UrpAssetPath);
            if (urp != null)
            {
                report.present.Add(SplatPressoEditorUtil.UrpAssetPath);
                return urp;
            }

            // Same steps as URP's own "Create > Rendering > URP Asset (with Universal Renderer)".
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SplatPressoEditorUtil.UrpRendererPath);
            if (rendererData == null)
            {
                rendererData = CreateInstance<UniversalRendererData>();
                rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(kUrpPostProcessDataPath);
                AssetDatabase.CreateAsset(rendererData, SplatPressoEditorUtil.UrpRendererPath);
                try
                {
                    ResourceReloader.TryReloadAllNullIn(rendererData, UniversalRenderPipelineAsset.packagePath);
                }
                catch (Exception) { /* URP reloads null resources itself in OnEnable */ }
                report.created.Add(SplatPressoEditorUtil.UrpRendererPath);
            }
            urp = UniversalRenderPipelineAsset.Create(rendererData);
            urp.msaaSampleCount = 1; // the capture does not support MSAA
            AssetDatabase.CreateAsset(urp, SplatPressoEditorUtil.UrpAssetPath);
            AssetDatabase.SaveAssets();
            report.created.Add(SplatPressoEditorUtil.UrpAssetPath);
            return urp;
        }

        static void EnsureRenderGraph(SetupOptions options, SetupReport report)
        {
            if (!UrpRendererUtil.TryGetRenderGraphSettings(out var rg))
            {
                // A project that just switched to URP has no URP global settings until URP first renders.
                UrpRendererUtil.TryEnsureUrpGlobalSettings();
                UrpRendererUtil.TryGetRenderGraphSettings(out rg);
            }
            if (rg == null)
            {
                report.warnings.Add("URP global settings are not available yet (URP creates them the first time it renders; new ones have Render " +
                                    "Graph on). Run SplatPresso > Validate Project later to confirm.");
                return;
            }
            if (UrpRendererUtil.GsUrpFeatureSupportsRenderGraph() == false)
            {
                // Gaussian Splatting 1.0.x: its URP feature only implements Execute, so turning Render Graph on would hide
                // every splat in the project (the user's own too) and starve the capture of per-splat view data.
                report.errors.Add("The installed Gaussian Splatting renders only in URP compatibility mode (its GaussianSplatURPFeature has no " +
                                  "Render Graph path), but SplatPresso needs Render Graph. Update it to 1.1.0 or newer (SplatPresso > Install or " +
                                  "Repair Dependencies)." + (rg.enableRenderCompatibilityMode ? " Render Graph was left off." : ""));
                return;
            }
            if (!rg.enableRenderCompatibilityMode)
            {
                report.present.Add("Render Graph enabled");
                return;
            }
            if (!options.enableRenderGraph)
            {
                report.errors.Add("URP Render Graph compatibility mode is ON: Gaussian Splatting and the SplatPresso capture render nothing in it. " +
                                  "Turn it off in Project Settings > Graphics > URP > Render Graph.");
                return;
            }
            UrpRendererUtil.EnableRenderGraph();
            report.created.Add("Render Graph enabled (URP compatibility mode turned off)");
        }

        static void EnsureRendererFeatures(SplatPressoSettings settings, SetupReport report)
        {
            var gsType = UrpRendererUtil.GsUrpFeatureType;
            if (gsType == null)
                report.errors.Add($"{UrpRendererUtil.GsUrpFeatureTypeName} was not found. Gaussian Splatting compiles its URP support only when URP " +
                                  "is installed; reinstall it (SplatPresso > Install or Repair Dependencies).");

            var renderers = UrpRendererUtil.FindUrpRenderers();
            if (renderers.Count == 0)
            {
                report.errors.Add("The URP assets have no renderer data to install the features on.");
                return;
            }
            foreach (var rd in renderers)
            {
                string label = $"'{rd.name}'";
                if (!(rd is UniversalRendererData))
                {
                    report.warnings.Add($"Renderer {label} is a {rd.GetType().Name}; Gaussian splats need the Universal Renderer. Skipped.");
                    continue;
                }
                if (!UrpRendererUtil.IsWritable(rd, out string why))
                {
                    report.warnings.Add($"Renderer {label} is read-only ({why}). Add GaussianSplatURPFeature, then SplatCaptureFeature, to it by hand " +
                                        "or use a renderer under Assets/.");
                    continue;
                }

                bool changed = false;
                if (gsType != null)
                {
                    var gsFeature = UrpRendererUtil.FindFeature(rd, gsType);
                    if (gsFeature == null)
                    {
                        UrpRendererUtil.AddFeature(rd, gsType, null);
                        report.created.Add("GaussianSplatURPFeature -> " + label);
                        changed = true;
                    }
                    else
                    {
                        report.present.Add("GaussianSplatURPFeature on " + label);
                        if (!gsFeature.isActive)
                            report.warnings.Add($"GaussianSplatURPFeature on {label} is disabled; splats do not render with this renderer.");
                    }
                }

                if (UrpRendererUtil.FindFeature(rd, typeof(SplatCaptureFeature)) is SplatCaptureFeature capture)
                {
                    report.present.Add("SplatCaptureFeature on " + label);
                    if (!capture.isActive)
                        report.warnings.Add($"SplatCaptureFeature on {label} is disabled; scene captures time out.");
                    changed |= SyncCaptureFeature(capture, settings, label, report);
                }
                else
                {
                    UrpRendererUtil.AddFeature(rd, typeof(SplatCaptureFeature), f => InitCaptureFeature((SplatCaptureFeature)f, settings, report));
                    report.created.Add("SplatCaptureFeature -> " + label);
                    changed = true;
                }

                if (gsType != null && UrpRendererUtil.EnsureOrder(rd, gsType, typeof(SplatCaptureFeature)))
                {
                    report.created.Add($"moved GaussianSplatURPFeature before SplatCaptureFeature on {label} (the capture reuses the splat pass's " +
                                       "per-splat view data from the same frame)");
                    changed = true;
                }
                if (UrpRendererUtil.HasFeatureNamed(rd, UrpRendererUtil.LegacyCaptureFeatureTypeName))
                    report.warnings.Add($"Renderer {label} also has the research fork's GaussianSplatCaptureFeature; remove it (SplatCaptureFeature replaces it).");
                if (changed)
                    UrpRendererUtil.SaveRendererData(rd);
            }
        }

        static void InitCaptureFeature(SplatCaptureFeature feature, SplatPressoSettings settings, SetupReport report)
        {
            // Create() fills null shaders with Shader.Find only in the editor and does not persist them; players need
            // the serialized references (they are also what makes the shaders ship in builds).
            feature.m_SplatDepthShader = FindShader(SplatCaptureFeature.SplatDepthShaderName, report);
            feature.m_SceneDepthPrimeShader = FindShader(SplatCaptureFeature.SceneDepthPrimeShaderName, report);
            if (settings != null && settings.placement != null)
                feature.m_DepthAlphaThreshold = settings.placement.depthAlphaThreshold;
        }

        static bool SyncCaptureFeature(SplatCaptureFeature capture, SplatPressoSettings settings, string label, SetupReport report)
        {
            var so = new SerializedObject(capture);
            bool dirty = false;
            dirty |= FillShader(so.FindProperty("m_SplatDepthShader"), SplatCaptureFeature.SplatDepthShaderName, report);
            dirty |= FillShader(so.FindProperty("m_SceneDepthPrimeShader"), SplatCaptureFeature.SceneDepthPrimeShaderName, report);
            var threshold = so.FindProperty("m_DepthAlphaThreshold");
            if (threshold != null && settings != null && settings.placement != null &&
                !Mathf.Approximately(threshold.floatValue, settings.placement.depthAlphaThreshold))
            {
                // The settings asset is the single source of truth; the feature keeps a serialized copy.
                threshold.floatValue = settings.placement.depthAlphaThreshold;
                report.created.Add($"depth alpha threshold {settings.placement.depthAlphaThreshold:0.###} (from settings) -> SplatCaptureFeature on {label}");
                dirty = true;
            }
            if (dirty)
                so.ApplyModifiedProperties();
            return dirty;
        }

        static bool FillShader(SerializedProperty p, string shaderName, SetupReport report)
        {
            if (p == null || p.objectReferenceValue != null)
                return false;
            var shader = FindShader(shaderName, report);
            if (shader == null)
                return false;
            p.objectReferenceValue = shader;
            return true;
        }

        static Shader FindShader(string shaderName, SetupReport report)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null)
                report.errors.Add($"Shader '{shaderName}' not found; the SplatPresso package import looks incomplete (reimport the package).");
            return shader;
        }

        static void CheckMsaa(SetupOptions options, bool interactive, SetupReport report)
        {
            var withMsaa = UrpRendererUtil.FindUrpAssets().Where(a => a.msaaSampleCount > 1).ToList();
            if (withMsaa.Count == 0)
                return;
            var fixable = options.disableMsaa ? withMsaa.Where(a => UrpRendererUtil.IsWritable(a, out _)).ToList() : new List<UniversalRenderPipelineAsset>();
            if (fixable.Count > 0 && interactive &&
                !EditorUtility.DisplayDialog(kTitle,
                    "These URP assets use MSAA: " + string.Join(", ", fixable.Select(a => $"'{a.name}' ({a.msaaSampleCount}x)")) +
                    ".\n\nThe SplatPresso capture (the RGB + depth image used for placement) does not support MSAA. Turn MSAA off on them?",
                    "Turn MSAA off", "Keep MSAA"))
                fixable.Clear();
            foreach (var asset in withMsaa)
            {
                string path = AssetDatabase.GetAssetPath(asset);
                if (fixable.Contains(asset))
                {
                    Undo.RecordObject(asset, "Disable MSAA");
                    asset.msaaSampleCount = 1;
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssetIfDirty(asset);
                    report.created.Add("MSAA off on " + path);
                }
                else
                {
                    report.warnings.Add($"URP asset {path} uses {asset.msaaSampleCount}x MSAA; captures fail while MSAA is on.");
                }
            }
        }

        /// <summary>
        /// The project-wide changes <see cref="Run"/> with <paramref name="options"/> would make WITHOUT a dialog of its own
        /// (Render Graph on, Windows graphics API order), described for a confirmation prompt. Empty when none are pending.
        /// </summary>
        internal static List<string> PendingUnaskedProjectChanges(SetupOptions options)
        {
            var list = new List<string>();
            if (options == null)
                return list;
            if (options.enableRenderGraph && UrpRendererUtil.IsRenderGraphCompatibilityMode() == true)
                list.Add("turn URP Render Graph on (compatibility mode off, in the URP global settings)");
            if (options.fixGraphicsApis && !StartsWithModernApi(PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64)))
                list.Add("put D3D12 first in the Windows player's graphics APIs");
            return list;
        }

        static bool StartsWithModernApi(GraphicsDeviceType[] apis)
        {
            var first = apis != null && apis.Length > 0 ? apis[0] : GraphicsDeviceType.Null;
            return first == GraphicsDeviceType.Direct3D12 || first == GraphicsDeviceType.Vulkan;
        }

        // DX11 cannot render splats (upstream: "DX12 or Vulkan on Windows, i.e. DX11 will not work"). Only fixes the
        // case where D3D11/OpenGL is first; a user's [Vulkan, D3D12] order is kept. Other platforms are left alone.
        static void EnsureWindowsGraphicsApis(SetupReport report)
        {
            const BuildTarget target = BuildTarget.StandaloneWindows64;
            GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(target) ?? new GraphicsDeviceType[0];
            var first = apis.Length > 0 ? apis[0] : GraphicsDeviceType.Null;
            if (first == GraphicsDeviceType.Direct3D12 || first == GraphicsDeviceType.Vulkan)
            {
                report.present.Add($"Windows graphics API {first} first");
                return;
            }
            var list = apis.Where(a => a != GraphicsDeviceType.Direct3D12).ToList();
            list.Insert(0, GraphicsDeviceType.Direct3D12);
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, list.ToArray());
            report.created.Add("Windows graphics APIs = " + string.Join(", ", list) + " (Gaussian splats need D3D12 or Vulkan)");
        }

        // The Windows editor itself starts on the active build target's first graphics API, so an editor on D3D11
        // (or OpenGL) shows no splats in play mode until it restarts. A restart is only offered when it would help:
        // the active target's list now starts with D3D12/Vulkan and no -force-* flag pins the current device.
        static void CheckEditorGraphicsDevice(SetupReport report)
        {
            var device = SystemInfo.graphicsDeviceType;
            if (device != GraphicsDeviceType.Direct3D11 && device != GraphicsDeviceType.OpenGLCore)
                return;
            var target = EditorUserBuildSettings.activeBuildTarget;
            string forced = Environment.GetCommandLineArgs().FirstOrDefault(a =>
                a.StartsWith("-force-d3d11", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-force-gl", StringComparison.OrdinalIgnoreCase));
            if (forced != null)
            {
                report.warnings.Add($"The editor is running on {device}, where Gaussian splats do not render, because it was started with {forced}. " +
                                    "Start it without that flag (or with -force-d3d12).");
                return;
            }
            if (!StartsWithModernApi(PlayerSettings.GetGraphicsAPIs(target)))
            {
                report.warnings.Add($"The editor is running on {device}, where Gaussian splats do not render. It starts on the first graphics API of " +
                                    $"the active build target ({target}); put D3D12 or Vulkan first in Player Settings > Other Settings > Graphics APIs " +
                                    "for that target and restart the editor, or start it with -force-d3d12.");
                return;
            }
            report.warnings.Add($"The editor is running on {device}, where Gaussian splats do not render. Restart the editor after the graphics " +
                                "API change (or start it with -force-d3d12).");
            report.restartRequired = true;
        }

        // ------------------------------------------------------------------------------------------
        // Scene steps

        static void SetupActiveScene(SplatPressoSettings settings, SetupOptions options, SetupReport report)
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                report.errors.Add("Close Prefab Mode first; Setup wires the active scene.");
                return;
            }
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                report.errors.Add("There is no active scene to set up.");
                return;
            }
            report.scenePath = scene.path;
            int changesBefore = report.created.Count;

            var root = SplatPressoEditorUtil.FindInScene<SplatPressoRoot>(scene);
            GameObject go;
            if (root == null)
            {
                go = new GameObject("SplatPresso");
                if (go.scene != scene)
                    SceneManager.MoveGameObjectToScene(go, scene);
                Undo.RegisterCreatedObjectUndo(go, "Create SplatPresso");
                root = go.AddComponent<SplatPressoRoot>();
                report.created.Add("GameObject 'SplatPresso' with SplatPressoRoot");
            }
            else
            {
                go = root.gameObject;
                report.present.Add($"SplatPressoRoot on '{go.name}'");
            }

            var capture = ResolveComponent<CaptureService>(root, "captureService", go, scene, report);
            var preview = ResolveComponent<PlacementPreviewService>(root, "previewService", go, scene, report);
            var spawn = ResolveComponent<ObjectSpawnService>(root, "spawnService", go, scene, report);

            VoiceAgent voice = null;
            if (options.addVoice)
            {
                voice = ResolveComponent<VoiceAgent>(root, "voiceAgent", go, scene, report);
                var voiceGo = voice.gameObject;
                ResolveComponent<MicCapture>(voice, "mic", voiceGo, scene, report);
                // [RequireComponent(typeof(AudioSource))] adds the AudioSource; the player configures it at runtime.
                ResolveComponent<AudioStreamPlayer>(voice, "player", voiceGo, scene, report);
            }
            else
            {
                voice = SplatPressoEditorUtil.ReadReference<VoiceAgent>(root, "voiceAgent") ?? SplatPressoEditorUtil.FindInScene<VoiceAgent>(scene);
            }

            if (options.addHud)
            {
                var hud = ResolveComponent<VoiceHud>(null, null, go, scene, report);
                AssignIfNull(hud, "voiceAgent", voice, report, optional: voice == null);
                AssignIfNull(hud, "root", root, report);
            }

            // Settings: assigned explicitly so a settings asset outside Resources still reaches the runtime.
            AssignIfNull(root, "settings", settings, report);
            AssignIfNull(capture, "settings", settings, report, optional: true);
            AssignIfNull(spawn, "settings", settings, report, optional: true);
            AssignIfNull(preview, "settings", settings, report, optional: true);
            if (voice != null)
                AssignIfNull(voice, "settings", settings, report, optional: true);
            AssignIfNull(spawn, "previewService", preview, report, optional: true);

            EnsureRendererResources(spawn, report);
            var previewShader = Shader.Find(kPreviewShaderName);
            if (previewShader != null)
                AssignIfNull(preview, "previewShader", previewShader, report, optional: true);

            var cam = ResolveCamera(scene, capture, options, report);
            if (cam != null)
            {
                AssignIfNull(capture, "targetCamera", cam, report);
                if (options.addCameraController)
                    EnsureCameraController(cam, report);
            }
            EnsureAudioListener(scene, cam, report);

            if (options.addDebugHotkeys)
            {
                var hotkeys = ResolveComponent<DebugHotkeys>(null, null, go, scene, report);
                AssignIfNull(hotkeys, "root", root, report, optional: true);
                AssignIfNull(hotkeys, "spawnService", spawn, report, optional: true);
                var nudge = ResolveComponent<PlacementNudgeController>(null, null, go, scene, report);
                AssignIfNull(nudge, "spawnService", spawn, report, optional: true);
            }

            if (options.addSceneToBuildSettings)
                AddSceneToBuildSettings(scene, report);
            if (report.created.Count > changesBefore)
                EditorSceneManager.MarkSceneDirty(scene); // a re-run that changes nothing leaves the scene clean
        }

        // Finds the component the owner already references, else one on the host, else one anywhere in the scene;
        // adds it to the host only when none exists. Then wires the owner's field if (and only if) it is empty.
        static T ResolveComponent<T>(Object owner, string property, GameObject host, Scene scene, SetupReport report) where T : Component
        {
            SerializedObject so = null;
            SerializedProperty p = null;
            T found = null;
            if (owner != null && property != null)
            {
                so = new SerializedObject(owner);
                p = so.FindProperty(property);
                if (p != null && p.propertyType == SerializedPropertyType.ObjectReference)
                    found = p.objectReferenceValue as T;
                else if (p == null)
                    report.warnings.Add($"{owner.GetType().Name} has no serialized field '{property}'; wire its {typeof(T).Name} by hand.");
            }
            if (found == null)
                found = host.GetComponent<T>();
            if (found == null)
                found = SplatPressoEditorUtil.FindInScene<T>(scene);
            if (found == null)
            {
                found = Undo.AddComponent<T>(host);
                report.created.Add($"{typeof(T).Name} on '{host.name}'");
            }
            else
            {
                report.present.Add($"{typeof(T).Name} on '{found.gameObject.name}'");
            }
            if (p != null && p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue == null)
            {
                p.objectReferenceValue = found;
                so.ApplyModifiedProperties();
                report.created.Add($"{owner.GetType().Name}.{property} -> {typeof(T).Name}");
            }
            return found;
        }

        // Assigns an object-reference field only while it is empty (never overwrites a user's choice). Goes through
        // SerializedObject so it works for private [SerializeField] fields, records Undo and prefab overrides.
        static bool AssignIfNull(Object target, string property, Object value, SetupReport report, bool optional = false)
        {
            if (target == null || value == null)
                return false;
            var so = new SerializedObject(target);
            var p = so.FindProperty(property);
            if (p == null || p.propertyType != SerializedPropertyType.ObjectReference)
            {
                if (!optional)
                    report.warnings.Add($"{target.GetType().Name} has no serialized reference field '{property}'; assign it by hand.");
                return false;
            }
            if (p.objectReferenceValue != null)
                return false;
            p.objectReferenceValue = value;
            so.ApplyModifiedProperties();
            if (p.objectReferenceValue == null)
            {
                report.warnings.Add($"Could not assign {value.GetType().Name} to {target.GetType().Name}.{property} (type mismatch).");
                return false;
            }
            report.created.Add($"{target.GetType().Name}.{property} -> {value.name}");
            return true;
        }

        // Spawned GaussianSplatRenderers get their shaders from here: upstream only fills them via the script's
        // default references when the component is added in the Inspector, never for AddComponent in players. These
        // serialized references are also what makes the shaders ship in builds.
        static void EnsureRendererResources(ObjectSpawnService spawn, SetupReport report)
        {
            if (spawn == null)
                return;
            var so = new SerializedObject(spawn);
            var resources = so.FindProperty("rendererResources");
            if (resources == null)
            {
                report.warnings.Add("ObjectSpawnService has no 'rendererResources' field; assign the Gaussian Splatting shaders by hand.");
                return;
            }
            var found = SplatRendererResources.FindInProject();
            var values = new KeyValuePair<string, Object>[]
            {
                new KeyValuePair<string, Object>("splatShader", found.splatShader),
                new KeyValuePair<string, Object>("compositeShader", found.compositeShader),
                new KeyValuePair<string, Object>("debugPointsShader", found.debugPointsShader),
                new KeyValuePair<string, Object>("debugBoxesShader", found.debugBoxesShader),
                new KeyValuePair<string, Object>("splatUtilities", found.splatUtilities),
            };
            bool changed = false;
            bool complete = true;
            foreach (var kv in values)
            {
                var p = resources.FindPropertyRelative(kv.Key);
                if (p == null)
                    continue;
                if (p.objectReferenceValue == null && kv.Value != null)
                {
                    p.objectReferenceValue = kv.Value;
                    changed = true;
                }
                complete &= p.objectReferenceValue != null;
            }
            if (changed)
            {
                so.ApplyModifiedProperties();
                report.created.Add("Gaussian Splatting shader references on ObjectSpawnService");
            }
            else if (complete)
            {
                report.present.Add("Gaussian Splatting shader references on ObjectSpawnService");
            }
            if (!complete)
                report.errors.Add("Could not find all Gaussian Splatting shaders (RenderGaussianSplats, GaussianComposite, the debug shaders, " +
                                  "SplatUtilities.compute); spawned splats would not render. Reinstall the package.");
        }

        static Camera ResolveCamera(Scene scene, CaptureService capture, SetupOptions options, SetupReport report)
        {
            var cam = SplatPressoEditorUtil.ReadReference<Camera>(capture, "targetCamera");
            if (cam == null)
            {
                var main = Camera.main;
                if (main != null && main.gameObject.scene == scene)
                    cam = main;
            }
            if (cam == null)
            {
                foreach (var go in scene.GetRootGameObjects())
                {
                    cam = go.GetComponentsInChildren<Camera>(true).FirstOrDefault(c => c.targetTexture == null);
                    if (cam != null)
                        break;
                }
            }
            if (cam != null)
            {
                report.present.Add($"camera '{cam.name}'");
                return cam;
            }
            if (!options.createCameraIfMissing)
            {
                report.warnings.Add("The scene has no camera; add one and run Setup again.");
                return null;
            }
            var camGo = new GameObject("Main Camera");
            if (camGo.scene != scene)
                SceneManager.MoveGameObjectToScene(camGo, scene);
            Undo.RegisterCreatedObjectUndo(camGo, "Create Camera");
            camGo.tag = "MainCamera";
            camGo.transform.position = new Vector3(0f, 1.6f, -3f);
            cam = camGo.AddComponent<Camera>();
            report.created.Add("camera 'Main Camera'");
            return cam;
        }

        // Only a bare camera (no scripts, no parent rig) gets the sample controller; an existing controller of any
        // kind (Cinemachine, a player rig, our own) is left alone.
        static void EnsureCameraController(Camera cam, SetupReport report)
        {
            if (cam.GetComponent<FirstPersonCamera>() != null || cam.GetComponent<FlyCamera>() != null)
            {
                report.present.Add($"camera controls on '{cam.name}'");
                return;
            }
            var scripts = cam.GetComponents<MonoBehaviour>()
                .Where(m => m != null && !(m is UniversalAdditionalCameraData))
                .Select(m => m.GetType().Name)
                .ToList();
            if (scripts.Count > 0 || cam.transform.parent != null)
            {
                string why = scripts.Count > 0 ? "it already has " + string.Join(", ", scripts) : "it has a parent";
                report.present.Add($"camera controls: left '{cam.name}' as is ({why})");
                return;
            }
            Undo.AddComponent<FirstPersonCamera>(cam.gameObject);
            report.created.Add($"FirstPersonCamera on '{cam.name}' (mouse look + WASD, Esc releases the cursor, Space stays push-to-talk)");
        }

        static void EnsureAudioListener(Scene scene, Camera cam, SetupReport report)
        {
            bool any = scene.GetRootGameObjects().Any(g => g.GetComponentsInChildren<AudioListener>(true).Length > 0);
            if (any)
            {
                report.present.Add("AudioListener");
                return;
            }
            if (cam == null)
            {
                report.warnings.Add("No AudioListener in the scene; the agent's speech would not be heard.");
                return;
            }
            Undo.AddComponent<AudioListener>(cam.gameObject);
            report.created.Add($"AudioListener on '{cam.name}'");
        }

        static void AddSceneToBuildSettings(Scene scene, SetupReport report)
        {
            if (string.IsNullOrEmpty(scene.path))
            {
                report.warnings.Add("Save the scene first to add it to Build Settings.");
                return;
            }
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == scene.path))
            {
                report.present.Add("build scene " + scene.path);
                return;
            }
            scenes.Add(new EditorBuildSettingsScene(scene.path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            report.created.Add("build scene " + scene.path);
        }

        static void Finish(SetupOptions options, bool interactive, SetupReport report)
        {
            if (!ApiKeys.Has(ApiKeyKind.Genpresso))
                report.warnings.Add("No GenPresso API key yet: add it in Project Settings > SplatPresso (get one at " + SplatPressoEditorUtil.KeyPortalUrl + ").");
#if !SPLATPRESSO_HAS_GLTFAST
            var settings = SplatPressoEditorUtil.FindSettingsAsset();
            if (settings != null && settings.representation == ObjectRepresentation.Mesh)
                report.warnings.Add("Representation is Mesh but glTFast is not installed; install it with " + kInstallDepsMenu.Replace("/", " > ") + ".");
#endif

            AssetDatabase.SaveAssets();
            if (options.setupScene)
            {
                var scene = SceneManager.GetActiveScene();
                if (interactive && options.saveScenes)
                {
                    if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                        report.warnings.Add("The scene was not saved; save it to keep the SplatPresso wiring.");
                    report.scenePath = scene.path;
                }
                else if (string.IsNullOrEmpty(scene.path) && !Application.isBatchMode)
                {
                    report.warnings.Add("The active scene is unsaved; save it to keep the SplatPresso wiring.");
                }
            }
        }

        static void OfferRestart(SetupReport report)
        {
            if (!report.restartRequired || !report.Success)
                return;
            if (EditorUtility.DisplayDialog(kTitle, $"The editor is running on {SystemInfo.graphicsDeviceType}, where Gaussian splats do not render. " +
                                                    "Restart the editor now so it uses D3D12/Vulkan?", "Restart Editor", "Later") &&
                EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorApplication.OpenProject(System.IO.Directory.GetCurrentDirectory());
        }
    }
}
