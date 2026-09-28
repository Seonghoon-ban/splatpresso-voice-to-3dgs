using UnityEditor;
using UnityEngine;

namespace SplatPresso.EditorTools
{
    /// <summary>Inspector for <see cref="SplatPressoRoot"/>: key status, live run info and shortcuts.</summary>
    [CustomEditor(typeof(SplatPressoRoot))]
    public sealed class SplatPressoRootEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var root = (SplatPressoRoot)target;
            EditorGUILayout.Space();

            string key = ApiKeys.Get(ApiKeyKind.Genpresso, out var source);
            if (string.IsNullOrEmpty(key))
                EditorGUILayout.HelpBox("No GenPresso API key: requests are rejected until you add one in Project Settings > SplatPresso.", MessageType.Warning);
            else
                EditorGUILayout.LabelField("GenPresso key", $"{ApiKeys.Mask(key)} ({KeyUI.SourceLabel(ApiKeyKind.Genpresso, source)})", EditorStyles.wordWrappedLabel);

            var settingsProp = serializedObject.FindProperty("settings");
            if (settingsProp != null && settingsProp.objectReferenceValue == null)
            {
                var projectSettings = SplatPressoEditorUtil.FindSettingsAsset(out bool loadable);
                if (projectSettings == null)
                    EditorGUILayout.HelpBox("No settings asset: built-in defaults are used. SplatPresso > Setup Scene creates one.", MessageType.Info);
                else if (!loadable)
                    EditorGUILayout.HelpBox("'Settings' is empty and the project's settings asset is not in a Resources folder, so players use defaults.", MessageType.Warning);
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Mode", $"{root.Mode}, {root.Representation}");
                EditorGUILayout.LabelField("Active runs", root.ActiveRunCount.ToString());
                using (new EditorGUI.DisabledScope(root.ActiveRunCount == 0))
                {
                    if (GUILayout.Button("Cancel All Runs"))
                        root.CancelAll();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Test Connection"))
                {
                    SplatPressoEditorUtil.OpenSettings();
                    ConnectionTestRunner.Start(SplatPressoEditorUtil.FindSettingsAsset(), false);
                }
                if (GUILayout.Button("Settings & Keys"))
                    SplatPressoEditorUtil.OpenSettings();
                if (GUILayout.Button("Debug Window"))
                    SplatPressoDebugWindow.Open();
            }
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Setup Scene..."))
                    SetupWizard.Open();
            }
        }

        /// <inheritdoc/>
        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }
}
