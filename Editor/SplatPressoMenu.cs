using System.IO;
using SplatPresso.Extras;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SplatPresso.EditorTools
{
    /// <summary>The SplatPresso menu. "Install or Repair Dependencies" lives in the bootstrap assembly (it must work before GS exists).</summary>
    public static class SplatPressoMenu
    {
        [MenuItem("SplatPresso/Setup Scene…", priority = 1)]
        static void SetupScene() => SetupWizard.Open();

        [MenuItem("SplatPresso/Create Demo Scene", priority = 2)]
        static void CreateDemoScene() => DemoSceneBuilder.Build(interactive: true);

        [MenuItem("SplatPresso/Settings", priority = 20)]
        static void Settings() => SplatPressoEditorUtil.OpenSettings();

        [MenuItem("SplatPresso/Test Connection", priority = 21)]
        static void TestConnection()
        {
            SplatPressoEditorUtil.OpenSettings();
            ConnectionTestRunner.Start(SplatPressoEditorUtil.FindSettingsAsset(), false);
        }

        [MenuItem("SplatPresso/Debug Window", priority = 22)]
        static void DebugWindow() => SplatPressoDebugWindow.Open();

        [MenuItem("SplatPresso/Validate Project", priority = 40)]
        static void ValidateProject() => ProjectValidator.RunFromMenu();

        [MenuItem("SplatPresso/Open Sessions Folder", priority = 41)]
        static void SessionsFolder() => OpenSessionsFolder();

        [MenuItem("SplatPresso/Samples/Camera Controls/Use First-Person", priority = 60)]
        static void UseFirstPerson() => SwitchCameraControls(firstPerson: true);

        [MenuItem("SplatPresso/Samples/Camera Controls/Use Fly Camera", priority = 61)]
        static void UseFly() => SwitchCameraControls(firstPerson: false);

        [MenuItem("SplatPresso/Samples/Camera Controls/Use First-Person", true)]
        [MenuItem("SplatPresso/Samples/Camera Controls/Use Fly Camera", true)]
        static bool CanSwitchCameraControls() => !EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>Reveals the sessions folder (the newest session selected), creating the root if needed.</summary>
        public static void OpenSessionsFolder()
        {
            string root = PipelineSession.ResolveSessionsRoot(SplatPressoEditorUtil.FindSettingsAsset());
            Directory.CreateDirectory(root);
            string latest = PipelineSession.LatestSessionDir(root);
            EditorUtility.RevealInFinder(!string.IsNullOrEmpty(latest) ? latest : root);
        }

        // Camera control samples. First-person = cursor locked + always-on mouse look + WASD walk (Esc releases the
        // cursor, Q/E adjusts eye height; Space stays reserved for push-to-talk). Fly = hold RMB to look.
        static void SwitchCameraControls(bool firstPerson)
        {
            var cam = Camera.main;
            if (cam == null)
                cam = SplatPressoEditorUtil.FindInScene<Camera>(SceneManager.GetActiveScene());
            if (cam == null)
            {
                Debug.LogWarning("[SplatPresso] No camera in the active scene; nothing to switch.");
                return;
            }

            Undo.SetCurrentGroupName(firstPerson ? "Use First-Person Camera" : "Use Fly Camera");
            var fly = cam.GetComponent<FlyCamera>();
            var fps = cam.GetComponent<FirstPersonCamera>();
            if (firstPerson)
            {
                if (fly != null)
                    Undo.DestroyObjectImmediate(fly);
                if (fps == null)
                    Undo.AddComponent<FirstPersonCamera>(cam.gameObject);
                Debug.Log($"[SplatPresso] '{cam.name}': first-person controls. Mouse = look, WASD = walk, Q/E = height, Shift = fast, " +
                          "Esc = release cursor, left-click = re-lock. Space stays push-to-talk.");
            }
            else
            {
                if (fps != null)
                    Undo.DestroyObjectImmediate(fps);
                if (fly == null)
                    Undo.AddComponent<FlyCamera>(cam.gameObject);
                Debug.Log($"[SplatPresso] '{cam.name}': fly camera. Hold RMB = look, WASD + Q/E = fly, Shift = fast.");
            }
            EditorSceneManager.MarkSceneDirty(cam.gameObject.scene);
        }
    }
}
