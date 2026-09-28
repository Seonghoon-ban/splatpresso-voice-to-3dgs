using System.Collections.Generic;
using System.IO;
using SplatPresso.Api;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SplatPresso.EditorTools
{
    /// <summary>
    /// Project Settings &gt; SplatPresso: API keys (stored per user, outside the project), connection test, and the
    /// project's <see cref="SplatPressoSettings"/> asset.
    /// </summary>
    public sealed class SplatPressoSettingsProvider : SettingsProvider
    {
        static GUIStyle s_Padding;
        UnityEditor.Editor m_SettingsEditor;
        Vector2 m_Scroll;
        bool m_ShowKeys = true;
        bool m_ShowConnection = true;
        bool m_ShowSettings = true;
        string m_PackageVersion;

        SplatPressoSettingsProvider()
            : base(SplatPressoEditorUtil.SettingsProviderPath, SettingsScope.Project,
                new HashSet<string> { "SplatPresso", "GenPresso", "API key", "gaussian", "splat", "TripoSplat", "voice", "model" })
        {
            label = "SplatPresso";
        }

        /// <summary>Registers the page.</summary>
        [SettingsProvider]
        public static SettingsProvider Create() => new SplatPressoSettingsProvider();

        /// <inheritdoc/>
        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            ConnectionTestRunner.Changed -= Repaint;
            ConnectionTestRunner.Changed += Repaint;
            var info = PackageInfo.FindForAssembly(typeof(SplatPressoSettings).Assembly);
            m_PackageVersion = info != null ? info.version : null;
        }

        /// <inheritdoc/>
        public override void OnDeactivate()
        {
            ConnectionTestRunner.Changed -= Repaint;
            if (m_SettingsEditor != null)
                Object.DestroyImmediate(m_SettingsEditor);
            m_SettingsEditor = null;
        }

        /// <inheritdoc/>
        public override void OnGUI(string searchContext)
        {
            var settings = SplatPressoEditorUtil.FindSettingsAsset(out bool loadable);
            float oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 200;
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            s_Padding ??= new GUIStyle { padding = new RectOffset(10, 10, 4, 10) };
            using (new EditorGUILayout.VerticalScope(s_Padding))
            {
                EditorGUILayout.LabelField("SplatPresso Voice To 3DGS" + (string.IsNullOrEmpty(m_PackageVersion) ? "" : " " + m_PackageVersion), EditorStyles.largeLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Setup Scene...", EditorStyles.miniButtonLeft))
                        SetupWizard.Open();
                    if (GUILayout.Button("Validate Project", EditorStyles.miniButtonMid))
                        ProjectValidator.RunFromMenu();
                    if (GUILayout.Button("Debug Window", EditorStyles.miniButtonMid))
                        SplatPressoDebugWindow.Open();
                    if (GUILayout.Button("Sessions Folder", EditorStyles.miniButtonRight))
                        SplatPressoMenu.OpenSessionsFolder();
                }
                EditorGUILayout.Space();

                m_ShowKeys = EditorGUILayout.BeginFoldoutHeaderGroup(m_ShowKeys, "API Keys");
                if (m_ShowKeys)
                    KeyUI.DrawKeysSection(settings);
                EditorGUILayout.EndFoldoutHeaderGroup();
                EditorGUILayout.Space();

                m_ShowConnection = EditorGUILayout.BeginFoldoutHeaderGroup(m_ShowConnection, "Connection");
                if (m_ShowConnection)
                    DrawConnection(settings);
                EditorGUILayout.EndFoldoutHeaderGroup();
                EditorGUILayout.Space();

                m_ShowSettings = EditorGUILayout.BeginFoldoutHeaderGroup(m_ShowSettings, "Settings Asset");
                if (m_ShowSettings)
                    DrawSettingsAsset(settings, loadable);
                EditorGUILayout.EndFoldoutHeaderGroup();
            }
            EditorGUILayout.EndScrollView();
            EditorGUIUtility.labelWidth = oldLabelWidth;
        }

        static void DrawConnection(SplatPressoSettings settings)
        {
            EditorGUILayout.LabelField("Checks the API root, the key, your balance and the chat model with a 1-token request " +
                                       "(well under 0.01 credit). Media probes send deliberately invalid requests to every model path; " +
                                       "they are rejected, not billed.", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(ConnectionTestRunner.IsRunning))
                {
                    if (GUILayout.Button("Test Connection"))
                        ConnectionTestRunner.Start(settings, false);
                    if (GUILayout.Button("Test + Probe Media Models"))
                        ConnectionTestRunner.Start(settings, true);
                }
                if (GUILayout.Button(new GUIContent("Clear Cached Model Paths", "Forget which GenPresso path each media step resolved to (" + ModelPathCache.FilePath + ")")))
                {
                    ModelPathCache.Clear();
                    Debug.Log("[SplatPresso] Cleared the cached media model paths.");
                }
            }
            ConnectionTestRunner.DrawResultsGUI();
        }

        void DrawSettingsAsset(SplatPressoSettings settings, bool loadable)
        {
            if (settings == null)
            {
                EditorGUILayout.HelpBox("No SplatPressoSettings asset yet; runtime uses built-in defaults.", MessageType.Info);
                if (GUILayout.Button("Create " + SplatPressoEditorUtil.SettingsAssetPath))
                {
                    settings = SplatPressoEditorUtil.CreateSettingsAsset();
                    EditorGUIUtility.PingObject(settings);
                }
                return;
            }

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Asset", settings, typeof(SplatPressoSettings), false);
            if (!loadable)
                EditorGUILayout.HelpBox($"This asset is not '{SplatPressoSettings.ResourceName}' at the root of a Resources folder, so players only find it " +
                                        $"through scene references (SplatPressoRoot.settings). Move it to {SplatPressoEditorUtil.SettingsAssetPath} to make it the default.",
                    MessageType.Warning);
            if (!UrpRendererUtil.IsWritable(settings, out string reason))
                EditorGUILayout.HelpBox("Read-only asset: " + reason, MessageType.Info);

            UnityEditor.Editor.CreateCachedEditor(settings, null, ref m_SettingsEditor);
            float threshold = settings.placement != null ? settings.placement.depthAlphaThreshold : -1f;
            EditorGUI.BeginChangeCheck();
            m_SettingsEditor.OnInspectorGUI();
            if (EditorGUI.EndChangeCheck() && settings.placement != null && !Mathf.Approximately(threshold, settings.placement.depthAlphaThreshold))
            {
                // The capture feature keeps a serialized copy of this value; keep them in sync.
                int n = UrpRendererUtil.SyncDepthAlphaThreshold(settings.placement.depthAlphaThreshold);
                if (n > 0)
                    Debug.Log($"[SplatPresso] Depth alpha threshold {settings.placement.depthAlphaThreshold:0.###} applied to {n} SplatCaptureFeature(s).");
            }
            EditorGUILayout.Space();
            string sessions = PipelineSession.ResolveSessionsRoot(settings);
            EditorGUILayout.LabelField("Sessions folder", sessions, EditorStyles.wordWrappedMiniLabel);
            if (!Directory.Exists(sessions))
                EditorGUILayout.LabelField(" ", "(created by the first run)", EditorStyles.miniLabel);
        }
    }
}
