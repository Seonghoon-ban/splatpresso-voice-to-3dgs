using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SplatPresso.EditorTools
{
    /// <summary>Paths and small helpers shared by the SplatPresso editor tools.</summary>
    public static class SplatPressoEditorUtil
    {
        /// <summary>Folder for everything Setup generates. Packages are read-only when installed from git, so generated content lives under Assets/.</summary>
        public const string RootFolder = "Assets/SplatPresso";
        /// <summary>Resources folder holding the settings asset (so players can <c>Resources.Load</c> it).</summary>
        public const string ResourcesFolder = RootFolder + "/Resources";
        /// <summary>Default settings asset path.</summary>
        public const string SettingsAssetPath = ResourcesFolder + "/" + SplatPressoSettings.ResourceName + ".asset";
        /// <summary>Folder for the URP asset Setup creates in Built-in projects.</summary>
        public const string RenderingFolder = RootFolder + "/Rendering";
        /// <summary>URP pipeline asset created when the project has none.</summary>
        public const string UrpAssetPath = RenderingFolder + "/SplatPresso_URP.asset";
        /// <summary>Universal renderer data created with <see cref="UrpAssetPath"/>.</summary>
        public const string UrpRendererPath = RenderingFolder + "/SplatPresso_URP_Renderer.asset";
        /// <summary>Demo content folder.</summary>
        public const string DemoFolder = RootFolder + "/Demo";
        /// <summary>Scene written by SplatPresso &gt; Create Demo Scene.</summary>
        public const string DemoScenePath = DemoFolder + "/SplatPressoDemo.unity";
        /// <summary>Scene written by <see cref="SetupWizard.RunBatch"/> when the active scene is unsaved.</summary>
        public const string BatchScenePath = DemoFolder + "/SplatPressoTest.unity";
        /// <summary>Project Settings page path.</summary>
        public const string SettingsProviderPath = "Project/SplatPresso";
        /// <summary>Where users get a GenPresso key.</summary>
        public const string KeyPortalUrl = "https://genpresso.ai/ko/developers";

        /// <summary>Creates a project folder and every missing parent (AssetDatabase.CreateAsset refuses missing folders).</summary>
        public static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
                return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>The project's settings asset (see <see cref="FindSettingsAsset(out bool)"/>), or null.</summary>
        public static SplatPressoSettings FindSettingsAsset() => FindSettingsAsset(out _);

        /// <summary>
        /// Finds the project's settings asset: the default path first, then any asset players can load via
        /// <c>Resources.Load("SplatPressoSettings")</c>, then any other SplatPressoSettings asset.
        /// </summary>
        /// <param name="loadableAtRuntime">True when players can find the returned asset through Resources.</param>
        public static SplatPressoSettings FindSettingsAsset(out bool loadableAtRuntime)
        {
            var atDefault = AssetDatabase.LoadAssetAtPath<SplatPressoSettings>(SettingsAssetPath);
            if (atDefault != null)
            {
                loadableAtRuntime = true;
                return atDefault;
            }
            SplatPressoSettings other = null;
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(SplatPressoSettings)))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<SplatPressoSettings>(path);
                if (asset == null)
                    continue;
                if (IsResourcesLoadable(path))
                {
                    loadableAtRuntime = true;
                    return asset;
                }
                if (other == null)
                    other = asset;
            }
            loadableAtRuntime = false;
            return other;
        }

        /// <summary>True when <paramref name="assetPath"/> is <c>.../Resources/SplatPressoSettings.asset</c> outside an Editor folder.</summary>
        public static bool IsResourcesLoadable(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;
            string p = assetPath.Replace('\\', '/');
            string dir = Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "";
            return Path.GetFileNameWithoutExtension(p) == SplatPressoSettings.ResourceName &&
                   Path.GetFileName(dir) == "Resources" &&
                   !("/" + dir + "/").Contains("/Editor/");
        }

        /// <summary>Creates the settings asset at <see cref="SettingsAssetPath"/> (caller checks it does not exist yet).</summary>
        public static SplatPressoSettings CreateSettingsAsset()
        {
            EnsureFolder(ResourcesFolder);
            var settings = ScriptableObject.CreateInstance<SplatPressoSettings>();
            AssetDatabase.CreateAsset(settings, SettingsAssetPath);
            AssetDatabase.SaveAssetIfDirty(settings);
            return settings;
        }

        /// <summary>Opens Project Settings &gt; SplatPresso.</summary>
        public static void OpenSettings() => SettingsService.OpenProjectSettings(SettingsProviderPath);

        /// <summary>First component of type T in <paramref name="scene"/>, including inactive objects.</summary>
        public static T FindInScene<T>(Scene scene) where T : Component
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var c = root.GetComponentInChildren<T>(true);
                if (c != null)
                    return c;
            }
            return null;
        }

        /// <summary>First component of type T in any loaded scene, including inactive objects.</summary>
        public static T FindInLoadedScenes<T>() where T : Component
        {
            var active = SceneManager.GetActiveScene();
            var found = FindInScene<T>(active);
            if (found != null)
                return found;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s == active)
                    continue;
                found = FindInScene<T>(s);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>Reads an object-reference serialized field (works for public and [SerializeField] private fields).</summary>
        public static T ReadReference<T>(Object target, string propertyPath) where T : Object
        {
            if (target == null)
                return null;
            var p = new SerializedObject(target).FindProperty(propertyPath);
            return p != null && p.propertyType == SerializedPropertyType.ObjectReference ? p.objectReferenceValue as T : null;
        }
    }
}
