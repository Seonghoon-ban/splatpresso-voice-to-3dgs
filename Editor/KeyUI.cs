using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SplatPresso.EditorTools
{
    /// <summary>
    /// IMGUI for the API key section of Project Settings &gt; SplatPresso. Always shows the EFFECTIVE key (masked) and
    /// where it comes from - otherwise a stale environment variable silently overrides what the user just typed.
    /// Typed values stay in memory until saved and are never logged.
    /// </summary>
    public static class KeyUI
    {
        static readonly string[] s_Pending = new string[3];
        static GUIStyle s_MissingStyle;

        /// <summary>Draws the key rows, helper buttons and plain-text warnings.</summary>
        public static void DrawKeysSection(SplatPressoSettings settings)
        {
            EditorGUILayout.HelpBox(
                "Keys are saved OUTSIDE the project, in " + ApiKeys.UserProfileKeysPath + " (read by play mode and by players on this " +
                "machine), so sharing or committing the project never leaks them. Environment variables (GENPRESSO_API_KEY, FAL_KEY, " +
                "OPENAI_API_KEY) take priority over that file.", MessageType.None);

            DrawKey(ApiKeyKind.Genpresso, "GenPresso", "Required. Chat, image/3D models and voice all run on this one key.", true);
            DrawKey(ApiKeyKind.Fal, "fal.ai", "Optional. Only for Media Provider = FalDirect or the fal fallback.", false);
            DrawKey(ApiKeyKind.OpenAI, "OpenAI", "Optional. Only for the OpenAI Realtime voice backend.", false);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Get a GenPresso key"))
                    Application.OpenURL(SplatPressoEditorUtil.KeyPortalUrl);
                if (GUILayout.Button("Reveal keys file"))
                    RevealKeysFile();
                if (GUILayout.Button(new GUIContent("Reload keys", "Re-read environment variables and key files.")))
                    ApiKeys.Reset();
            }

            if (settings != null && !string.IsNullOrWhiteSpace(settings.apiKey))
            {
                EditorGUILayout.HelpBox("The settings asset stores a GenPresso key in plain text: it is committed with the project and " +
                                        "shipped inside builds.", MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Move it to the user profile"))
                        MoveAssetKeyToUserProfile(settings);
                    if (GUILayout.Button("Remove it from the asset"))
                        RemoveAssetKey(settings);
                }
            }
            if (File.Exists(ApiKeys.StreamingAssetsKeysPath))
                EditorGUILayout.HelpBox("StreamingAssets/splatpresso.keys.json exists: every key in it ships in plain text inside your builds. " +
                                        "Use it only for builds you hand to trusted people.", MessageType.Warning);
        }

        static void DrawKey(ApiKeyKind kind, string label, string help, bool required)
        {
            int i = (int)kind;
            string key = ApiKeys.Get(kind, out var source);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(90));
                    if (string.IsNullOrEmpty(key))
                    {
                        if (s_MissingStyle == null)
                            s_MissingStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.95f, 0.45f, 0.3f) } };
                        EditorGUILayout.LabelField(required ? "Not set (required)" : "Not set", required ? s_MissingStyle : EditorStyles.label);
                    }
                    else
                    {
                        EditorGUILayout.LabelField($"{ApiKeys.Mask(key)}   from {SourceLabel(kind, source)}");
                    }
                }
                EditorGUILayout.LabelField(help, EditorStyles.wordWrappedMiniLabel);
                if (kind == ApiKeyKind.Genpresso && !string.IsNullOrEmpty(key) && !key.StartsWith("gp_", StringComparison.Ordinal))
                    EditorGUILayout.HelpBox("GenPresso keys normally start with 'gp_'.", MessageType.Warning);

                using (new EditorGUILayout.HorizontalScope())
                {
                    s_Pending[i] = EditorGUILayout.PasswordField("New key", s_Pending[i] ?? "");
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(s_Pending[i])))
                    {
                        if (GUILayout.Button(new GUIContent("Save", "Save to " + ApiKeys.UserProfileKeysPath), GUILayout.Width(56)))
                            Save(kind, label);
                    }
                    using (new EditorGUI.DisabledScope(!File.Exists(ApiKeys.UserProfileKeysPath)))
                    {
                        if (GUILayout.Button(new GUIContent("Clear", "Remove this key from the user-profile keys file"), GUILayout.Width(56)))
                            Clear(kind, label);
                    }
                }
            }
        }

        /// <summary>Human-readable key source.</summary>
        public static string SourceLabel(ApiKeyKind kind, KeySource source)
        {
            switch (source)
            {
                case KeySource.Override: return "a runtime override (ApiKeys.SetOverride)";
                case KeySource.Environment: return "environment variable " + ApiKeys.EnvVarName(kind);
                case KeySource.UserProfileFile: return "your user-profile keys file";
                case KeySource.SettingsAsset: return "the settings asset (plain text in the project!)";
                case KeySource.StreamingAssets: return "StreamingAssets/splatpresso.keys.json (ships in builds!)";
                default: return "nowhere";
            }
        }

        static void Save(ApiKeyKind kind, string label)
        {
            int i = (int)kind;
            string value = (s_Pending[i] ?? "").Trim();
            if (kind == ApiKeyKind.Genpresso && !value.StartsWith("gp_", StringComparison.Ordinal) &&
                !EditorUtility.DisplayDialog("SplatPresso", "This does not look like a GenPresso key (they start with 'gp_'). Save it anyway?", "Save", "Cancel"))
                return;
            try
            {
                ApiKeys.SaveToUserProfile(kind, value);
                ApiKeys.Reset();
                s_Pending[i] = "";
                GUI.FocusControl(null);
                ApiKeys.Get(kind, out var source);
                string msg = $"[SplatPresso] Saved the {label} key to {ApiKeys.UserProfileKeysPath}.";
                if (source == KeySource.Override || source == KeySource.Environment)
                    msg += $" Note: {SourceLabel(kind, source)} still takes priority over the saved key.";
                Debug.Log(msg);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("SplatPresso", "Could not save the key: " + e.Message, "OK");
            }
        }

        static void Clear(ApiKeyKind kind, string label)
        {
            if (!EditorUtility.DisplayDialog("SplatPresso", $"Remove the {label} key from {ApiKeys.UserProfileKeysPath}?", "Remove", "Cancel"))
                return;
            try
            {
                ApiKeys.ClearFromUserProfile(kind);
                ApiKeys.Reset();
                Debug.Log($"[SplatPresso] Removed the {label} key from {ApiKeys.UserProfileKeysPath}.");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("SplatPresso", "Could not update the keys file: " + e.Message, "OK");
            }
        }

        static void MoveAssetKeyToUserProfile(SplatPressoSettings settings)
        {
            try
            {
                ApiKeys.SaveToUserProfile(ApiKeyKind.Genpresso, settings.apiKey.Trim());
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("SplatPresso", "Could not save the key: " + e.Message, "OK");
                return;
            }
            RemoveAssetKey(settings, confirm: false);
            Debug.Log($"[SplatPresso] Moved the GenPresso key from {AssetDatabase.GetAssetPath(settings)} to {ApiKeys.UserProfileKeysPath}.");
        }

        static void RemoveAssetKey(SplatPressoSettings settings, bool confirm = true)
        {
            if (confirm && !EditorUtility.DisplayDialog("SplatPresso", "Remove the GenPresso key from the settings asset?", "Remove", "Cancel"))
                return;
            Undo.RecordObject(settings, "Remove API key from settings");
            settings.apiKey = "";
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            ApiKeys.Reset();
        }

        static void RevealKeysFile()
        {
            string path = ApiKeys.UserProfileKeysPath;
            if (File.Exists(path))
            {
                EditorUtility.RevealInFinder(path);
                return;
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                EditorUtility.RevealInFinder(dir);
            else
                EditorUtility.DisplayDialog("SplatPresso", "There is no keys file yet. It is created when you save a key:\n" + path, "OK");
        }
    }
}
