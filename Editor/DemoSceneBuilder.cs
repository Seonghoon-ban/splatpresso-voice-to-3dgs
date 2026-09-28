using System.IO;
using SplatPresso.Extras;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SplatPresso.EditorTools
{
    /// <summary>
    /// SplatPresso &gt; Create Demo Scene: a small furnished room built from primitives (floor, three walls, a table,
    /// a couch), warm lighting and an eye-height first-person camera, then <see cref="SetupWizard.Run"/> on it.
    /// Real geometry matters: placement anchors objects on the captured scene depth.
    /// </summary>
    public static class DemoSceneBuilder
    {
        const string kMaterialsFolder = SplatPressoEditorUtil.DemoFolder + "/Materials";

        /// <summary>
        /// Headless variant for -executeMethod / tests: no dialogs, always rebuilds and saves the demo scene and returns
        /// its path (null on failure). Opens the new scene in Single mode, so unsaved changes in open scenes are discarded.
        /// </summary>
        public static string BuildBatch() => Build(interactive: false);

        /// <summary>
        /// Builds (or, when interactive, optionally just opens) the demo scene at
        /// <see cref="SplatPressoEditorUtil.DemoScenePath"/>. Returns the scene path, or null when cancelled or failed.
        /// <paramref name="baseOptions"/> carries the project choices (e.g. the Setup window's opt-outs); only the scene
        /// fields are overridden. Without it, an interactive build asks before Setup changes project-wide settings.
        /// </summary>
        public static string Build(bool interactive = true, SetupOptions baseOptions = null)
        {
            interactive &= !Application.isBatchMode;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[SplatPresso] Exit play mode before creating the demo scene.");
                return null;
            }
            var options = (baseOptions ?? new SetupOptions()).Clone();
            options.interactive = interactive;
            options.setupScene = true;
            options.createCameraIfMissing = false;
            options.addCameraController = true;
            options.addSceneToBuildSettings = false;
            options.saveScenes = false;

            string path = SplatPressoEditorUtil.DemoScenePath;
            if (interactive)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return null;
                if (File.Exists(path))
                {
                    int choice = EditorUtility.DisplayDialogComplex("SplatPresso Demo Scene", $"{path} already exists.",
                        "Open it", "Cancel", "Rebuild it");
                    if (choice == 1)
                        return null;
                    if (choice == 0)
                    {
                        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                        return path;
                    }
                }
                if (baseOptions == null)
                {
                    // Menu path: nobody chose these project-wide changes yet, and Setup makes them without its own dialog.
                    var pending = SetupWizard.PendingUnaskedProjectChanges(options);
                    if (pending.Count > 0)
                    {
                        int choice = EditorUtility.DisplayDialogComplex("SplatPresso Demo Scene",
                            "Creating the demo scene also runs Setup, which changes these project-wide settings:\n\n- " +
                            string.Join("\n- ", pending) + "\n\nGaussian splats do not render without them. Apply them?",
                            "Apply", "Cancel", "Skip them");
                        if (choice == 1)
                            return null;
                        if (choice == 2)
                        {
                            options.enableRenderGraph = false;
                            options.fixGraphicsApis = false;
                        }
                    }
                }
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildRoom();
            BuildLighting();
            BuildCamera();

            SplatPressoEditorUtil.EnsureFolder(SplatPressoEditorUtil.DemoFolder);
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                Debug.LogError("[SplatPresso] Could not save the demo scene to " + path);
                return null;
            }

            var report = SetupWizard.Run(options);
            if (!EditorSceneManager.SaveScene(scene, path))
                Debug.LogError("[SplatPresso] Could not save the demo scene to " + path);
            Debug.Log($"[SplatPresso] Demo scene ready: {path}. Press Play, then hold Space and say what to add " +
                      "(or press Enter to type a request)." + (report.Success ? "" : " Setup reported errors (see above)."));
            return path;
        }

        static void BuildRoom()
        {
            var floorMat = GetOrCreateMaterial("Floor", new Color(0.62f, 0.58f, 0.52f), 0.15f);
            var wallMat = GetOrCreateMaterial("Wall", new Color(0.86f, 0.84f, 0.80f), 0.05f);
            var woodMat = GetOrCreateMaterial("Wood", new Color(0.45f, 0.30f, 0.18f), 0.35f);
            var fabricMat = GetOrCreateMaterial("Fabric", new Color(0.24f, 0.34f, 0.50f), 0.05f);

            var room = new GameObject("Room");
            // Floor: a Plane primitive is 10 x 10 m. The front (-Z) side stays open for the camera.
            Primitive(PrimitiveType.Plane, "Floor", room.transform, Vector3.zero, Vector3.one, floorMat);
            Primitive(PrimitiveType.Cube, "Wall Back", room.transform, new Vector3(0f, 1.5f, 5f), new Vector3(10f, 3f, 0.1f), wallMat);
            Primitive(PrimitiveType.Cube, "Wall Left", room.transform, new Vector3(-5f, 1.5f, 0f), new Vector3(0.1f, 3f, 10f), wallMat);
            Primitive(PrimitiveType.Cube, "Wall Right", room.transform, new Vector3(5f, 1.5f, 0f), new Vector3(0.1f, 3f, 10f), wallMat);

            // Table: 1.4 x 0.8 m top at 0.75 m.
            var table = new GameObject("Table");
            table.transform.SetParent(room.transform, false);
            table.transform.localPosition = new Vector3(1.4f, 0f, 1.6f);
            Primitive(PrimitiveType.Cube, "Top", table.transform, new Vector3(0f, 0.735f, 0f), new Vector3(1.4f, 0.04f, 0.8f), woodMat);
            foreach (var corner in new[] { new Vector2(-0.62f, -0.32f), new Vector2(0.62f, -0.32f), new Vector2(-0.62f, 0.32f), new Vector2(0.62f, 0.32f) })
                Primitive(PrimitiveType.Cube, "Leg", table.transform, new Vector3(corner.x, 0.36f, corner.y), new Vector3(0.06f, 0.72f, 0.06f), woodMat);

            // Couch against the back wall.
            var couch = new GameObject("Couch");
            couch.transform.SetParent(room.transform, false);
            couch.transform.localPosition = new Vector3(-2f, 0f, 4.4f);
            Primitive(PrimitiveType.Cube, "Seat", couch.transform, new Vector3(0f, 0.22f, 0f), new Vector3(2.0f, 0.44f, 0.9f), fabricMat);
            Primitive(PrimitiveType.Cube, "Back", couch.transform, new Vector3(0f, 0.55f, 0.4f), new Vector3(2.0f, 1.1f, 0.2f), fabricMat);
            Primitive(PrimitiveType.Cube, "Arm Left", couch.transform, new Vector3(-1.05f, 0.33f, 0f), new Vector3(0.18f, 0.66f, 0.9f), fabricMat);
            Primitive(PrimitiveType.Cube, "Arm Right", couch.transform, new Vector3(1.05f, 0.33f, 0f), new Vector3(0.18f, 0.66f, 0.9f), fabricMat);
        }

        static void BuildLighting()
        {
            var sunGo = new GameObject("Directional Light");
            sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.94f, 0.84f);
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;

            var lampGo = new GameObject("Warm Lamp");
            lampGo.transform.position = new Vector3(1.4f, 2.4f, 1.6f);
            var lamp = lampGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = new Color(1f, 0.82f, 0.62f);
            lamp.intensity = 1.5f;
            lamp.range = 6f;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.66f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.50f, 0.47f, 0.43f);
            RenderSettings.ambientGroundColor = new Color(0.28f, 0.25f, 0.22f);
        }

        static void BuildCamera()
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.position = new Vector3(0f, 1.6f, -3.5f);  // eye height
            camGo.transform.rotation = Quaternion.Euler(8f, 0f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<FirstPersonCamera>();
        }

        static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            if (mat != null)
                go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        static Material GetOrCreateMaterial(string name, Color color, float smoothness)
        {
            string path = $"{kMaterialsFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null)
                return mat;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                var def = GraphicsSettings.defaultRenderPipeline;
                shader = def != null && def.defaultMaterial != null ? def.defaultMaterial.shader : Shader.Find("Standard");
            }
            if (shader == null)
                return null;
            SplatPressoEditorUtil.EnsureFolder(kMaterialsFolder);
            mat = new Material(shader) { name = name, color = color };  // color maps to _BaseColor ([MainColor]) on URP Lit
            if (mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", smoothness);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
