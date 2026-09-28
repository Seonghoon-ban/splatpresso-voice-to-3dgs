using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SplatPresso.Placement
{
    /// <summary>
    /// While an object is being generated, shows a translucent "hologram" box (pulsing edges + faint volume) at the
    /// planned placement position, with a rising fill bar and a floating label + progress percentage.
    /// </summary>
    /// <remarks>
    /// Positions are solved from the capture with the same math the final placement uses
    /// (<see cref="SplatPlacement.Solve"/>), refined with the segmentation mask once it exists, and synced to the
    /// final solution right before the object spawns. Previews are keyed by <see cref="PlacedObjectResult"/>
    /// reference, so concurrent runs can show their boxes side by side without id collisions.
    /// Labels are IMGUI, which is not rendered inside an XR headset.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Placement Preview Service")]
    public sealed class PlacementPreviewService : MonoBehaviour
    {
        const string kPreviewShaderName = "SplatPresso/Preview";

        /// <summary>Settings (placement tuning); null uses <see cref="SplatPressoSettings.Active"/>.</summary>
        [Tooltip("Empty = the active SplatPresso settings.")]
        public SplatPressoSettings settings;

        /// <summary>
        /// Unlit transparent shader for the boxes ("SplatPresso/Preview"). Auto-filled in the editor; the serialized
        /// reference makes builds include it. Falls back to Sprites/Default when missing.
        /// </summary>
        [Tooltip("Shader for the preview boxes (SplatPresso/Preview). Auto-filled in the editor; referenced here so builds include it.")]
        public Shader previewShader;

        /// <summary>Edge color (alpha pulses).</summary>
        public Color edgeColor = new Color(0.35f, 0.9f, 1f, 0.9f);
        /// <summary>Box volume color.</summary>
        public Color volumeColor = new Color(0.35f, 0.9f, 1f, 0.05f);
        /// <summary>Rising progress fill color.</summary>
        public Color fillColor = new Color(0.35f, 1f, 0.6f, 0.14f);
        /// <summary>Expected image-to-splat duration used for the time-based progress creep (seconds).</summary>
        [Tooltip("Expected image-to-splat (TripoSplat) duration used for the time-based progress creep (seconds).")]
        public float expectedGenerationSeconds = 75f;
        /// <summary>Expected mesh generation duration used for the progress creep (seconds).</summary>
        [Tooltip("Expected mesh (Rodin) generation duration used for the time-based progress creep (seconds).")]
        public float expectedMeshGenerationSeconds = 150f;
        /// <summary>Camera used to position the labels; null uses <see cref="Camera.main"/>.</summary>
        [Tooltip("Camera for the floating labels. Empty = Camera.main.")]
        public Camera labelCamera;
        /// <summary>Show the floating IMGUI labels.</summary>
        public bool showLabels = true;

        sealed class Preview
        {
            public GameObject go;
            public Transform fill;          // bottom-anchored fill volume, scaled by progress
            public Vector3 size;
            public string label;
            public string stageText;
            public float targetFraction;
            public float shownFraction;
            public float generationStartTime = -1f;
            public float expectedSeconds;
            public bool isMesh;
            public Vector3 targetGroundPos; // boxes glide here (refined as better data arrives)
            public Quaternion targetRot;
            public CaptureResult capture;   // this run's capture, for mask-based refinement
        }

        static readonly int s_BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int s_ColorId = Shader.PropertyToID("_Color");
        static bool s_WarnedNoShader;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_WarnedNoShader = false;

        readonly Dictionary<PlacedObjectResult, Preview> m_Previews = new Dictionary<PlacedObjectResult, Preview>();
        Material m_EdgeMat;
        Material m_VolumeMat;
        Material m_FillMat;
        GUIStyle m_LabelStyle;
        PlacementTuning m_DefaultTuning;

        PlacementTuning Tuning
        {
            get
            {
                var s = settings != null ? settings : SplatPressoSettings.Active;
                if (s != null && s.placement != null)
                    return s.placement;
                return m_DefaultTuning ??= new PlacementTuning();
            }
        }

        /// <summary>Number of previews currently shown.</summary>
        public int Count => m_Previews.Count;

        // ---- public API (wired from SplatPressoRoot / ObjectSpawnService) -------------------------------------

        /// <summary>Creates boxes for the planned objects of a run (skipped and already-shown objects are ignored).</summary>
        public void ShowPreviews(List<PlacedObjectResult> objects, CaptureResult capture)
        {
            if (objects == null || capture == null)
                return;
            var tuning = Tuning;
            foreach (var obj in objects)
            {
                if (obj == null || obj.status == ObjectStatus.Skipped || m_Previews.ContainsKey(obj))
                    continue;
                bool isMesh = obj.representation == ObjectRepresentation.Mesh;
                // same solver as the final placement, but with unit content bounds so uniformScale comes back as
                // (approx object height * scale factor)
                var sol = SplatPlacement.Solve(capture, obj.BboxGenerated, obj.sizeHintM, null, null, Vector3.one, tuning, isMesh);
                if (!sol.valid)
                    continue;
                float h = Mathf.Clamp(sol.uniformScale / Mathf.Max(SplatPlacement.ScaleFactor(tuning, isMesh), 1e-3f), 0.05f, 10f);
                var bbox = obj.BboxGenerated;
                float aspect = Mathf.Clamp(bbox.h > 1e-4f ? (bbox.w * capture.width) / (bbox.h * capture.height) : 1f, 0.25f, 3f);
                var size = new Vector3(h * aspect, h, h * aspect * 0.8f);
                CreatePreview(obj, capture, sol.position, BoxRotation(sol.rotation, tuning, isMesh), size, isMesh);
                UpdateObject(obj, obj.status == ObjectStatus.Ready ? "ready" : "queued");
            }
        }

        /// <summary>
        /// Advances a preview for a per-object sub-stage: queued, segmenting, t2i, cutout, enhancing, enhanced,
        /// generating3d, downloading, ready, skipped (removes the box). Unknown stages are ignored.
        /// </summary>
        public void UpdateObject(PlacedObjectResult obj, string subStage)
        {
            if (obj == null || !m_Previews.TryGetValue(obj, out var p))
                return;
            switch (subStage)
            {
                case "queued":       p.targetFraction = Mathf.Max(p.targetFraction, 0.05f); p.stageText = "queued"; break;
                case "segmenting":   p.targetFraction = 0.10f; p.stageText = "isolating object"; break;
                case "t2i":          p.targetFraction = 0.25f; p.stageText = "generating image"; break;
                case "cutout":
                    p.targetFraction = 0.30f; p.stageText = "object isolated";
                    TryRefineWithMask(obj, p); // the final placement anchors on the cutout mask
                    break;
                case "enhancing":    p.targetFraction = 0.35f; p.stageText = "enhancing image"; break;
                case "enhanced":     p.targetFraction = 0.45f; p.stageText = "image enhanced"; break;
                case "generating3d": p.targetFraction = 0.55f; p.stageText = "generating 3D"; p.generationStartTime = Time.unscaledTime; break;
                case "downloading":  p.targetFraction = 0.92f; p.stageText = "downloading model"; p.generationStartTime = -1f; break;
                case "ready":        p.targetFraction = 1f;    p.stageText = "ready to place"; p.generationStartTime = -1f; break;
                case "skipped":      RemoveFor(obj); break;
            }
        }

        /// <summary>Glides a preview toward the FINAL solved pose (called right before the real object spawns).</summary>
        public void SyncFinal(PlacedObjectResult obj, Vector3 position, Quaternion rotation)
        {
            if (obj == null || !m_Previews.TryGetValue(obj, out var p))
                return;
            p.targetGroundPos = position;
            p.targetRot = BoxRotation(rotation, Tuning, p.isMesh);
        }

        /// <summary>Removes the preview of one object.</summary>
        public void RemoveFor(PlacedObjectResult obj)
        {
            if (obj == null || !m_Previews.TryGetValue(obj, out var p))
                return;
            DestroySafe(p.go);
            m_Previews.Remove(obj);
        }

        /// <summary>Removes the previews of several objects.</summary>
        public void RemoveObjects(IEnumerable<PlacedObjectResult> objects)
        {
            if (objects == null)
                return;
            foreach (var obj in objects)
                RemoveFor(obj);
        }

        /// <summary>Removes every preview.</summary>
        public void ClearAll()
        {
            foreach (var p in m_Previews.Values)
                DestroySafe(p.go);
            m_Previews.Clear();
        }

        // The solved rotation includes the calibrated yaw offset that turns the GENERATOR's front toward the
        // camera. A box has no front: face it to the camera so its width (x) spans the view like the bbox does.
        static Quaternion BoxRotation(Quaternion solved, PlacementTuning tuning, bool isMesh) =>
            solved * Quaternion.Euler(0f, -SplatPlacement.YawOffset(tuning, isMesh), 0f);

        // Re-solves the preview position with the segmentation mask - the same anchor the final placement uses -
        // so the box glides to where the object will actually land.
        void TryRefineWithMask(PlacedObjectResult obj, Preview p)
        {
            if (p.capture == null || string.IsNullOrEmpty(obj.cutoutPath))
                return;
            try
            {
                bool[] mask = ObjectSpawnService.LoadObjectMask(obj.cutoutPath, p.capture.width, p.capture.height);
                if (mask == null)
                    return;
                var tuning = Tuning;
                var sol = SplatPlacement.Solve(p.capture, obj.BboxGenerated, obj.sizeHintM, null, mask, Vector3.one, tuning, p.isMesh);
                if (!sol.valid)
                    return;
                p.targetGroundPos = sol.position;
                p.targetRot = BoxRotation(sol.rotation, tuning, p.isMesh);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Preview refine failed for '{obj.name}': {e.Message}");
            }
        }

        // ---- lifecycle ----------------------------------------------------------------------------------------

        void Update()
        {
            if (m_Previews.Count == 0)
                return;

            // pulsing edge glow (shared material)
            if (m_EdgeMat != null)
            {
                float pulse = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 2.6f);
                var c = edgeColor;
                c.a = edgeColor.a * Mathf.Lerp(0.45f, 1f, pulse);
                SetColor(m_EdgeMat, c);
            }

            float dt = Time.unscaledDeltaTime;
            foreach (var p in m_Previews.Values)
            {
                if (p.go != null)
                {
                    // glide toward the (possibly refined) target pose
                    var tr = p.go.transform;
                    tr.position = Vector3.Lerp(tr.position, p.targetGroundPos, dt * 5f);
                    tr.rotation = Quaternion.Slerp(tr.rotation, p.targetRot, dt * 5f);
                }

                // time-based creep while the 3D model is generated (0.55 -> 0.90)
                if (p.generationStartTime >= 0f)
                {
                    float t = Mathf.Clamp01((Time.unscaledTime - p.generationStartTime) / Mathf.Max(10f, p.expectedSeconds));
                    p.targetFraction = Mathf.Max(p.targetFraction, Mathf.Lerp(0.55f, 0.90f, t));
                }
                p.shownFraction = Mathf.MoveTowards(p.shownFraction, p.targetFraction, dt * 0.5f);

                if (p.fill != null)
                {
                    float f = Mathf.Max(0.001f, p.shownFraction);
                    p.fill.localScale = new Vector3(0.96f, f, 0.96f);
                    p.fill.localPosition = new Vector3(0f, f * 0.5f - 0.5f + 0.02f, 0f);
                }
            }
        }

        void OnDestroy()
        {
            ClearAll();
            DestroySafe(m_EdgeMat);
            DestroySafe(m_VolumeMat);
            DestroySafe(m_FillMat);
        }

        void OnGUI()
        {
            if (!showLabels || m_Previews.Count == 0)
                return;
            var cam = labelCamera != null ? labelCamera : Camera.main;
            if (cam == null)
                return;

            // explicit colors: never rely on the default skin
            m_LabelStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.75f, 0.97f, 1f, 0.95f) },
            };

            var prevColor = GUI.color;
            foreach (var p in m_Previews.Values)
            {
                if (p.go == null)
                    continue;
                Vector3 top = p.go.transform.position + Vector3.up * (p.size.y + 0.12f);
                Vector3 sp = cam.WorldToScreenPoint(top);
                if (sp.z <= 0f)
                    continue; // behind the camera
                float x = sp.x, y = Screen.height - sp.y;
                string text = $"{p.label} - {p.stageText} {Mathf.RoundToInt(p.shownFraction * 100f)}%";
                var rect = new Rect(x - 150f, y - 22f, 300f, 20f);
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(rect, text, m_LabelStyle);
            }
            GUI.color = prevColor;
        }

#if UNITY_EDITOR
        void Reset() => AutoFillShader();

        void OnValidate() => AutoFillShader();

        void AutoFillShader()
        {
            if (previewShader != null)
                return;
            var s = Shader.Find(kPreviewShaderName);
            if (s == null)
                return;
            previewShader = s;
            // OnValidate cannot mark the object dirty itself; defer so the reference gets saved with the scene
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null)
                    UnityEditor.EditorUtility.SetDirty(this);
            };
        }
#endif

        // ---- construction -------------------------------------------------------------------------------------

        void EnsureMaterials()
        {
            if (m_EdgeMat != null)
                return;
            var shader = ResolveShader();
            if (shader == null)
                return;
            m_EdgeMat = MakeUnlitTransparent(shader, edgeColor);
            m_VolumeMat = MakeUnlitTransparent(shader, volumeColor);
            m_FillMat = MakeUnlitTransparent(shader, fillColor);
        }

        Shader ResolveShader()
        {
            // Builds strip shaders nothing references and Shader.Find then returns null: the serialized field is the
            // reliable path; the Sprites/Default fallback is always included in builds.
            var shader = previewShader;
            if (shader == null || !shader.isSupported)
                shader = Shader.Find(kPreviewShaderName);
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null && !s_WarnedNoShader)
            {
                s_WarnedNoShader = true;
                Debug.LogWarning("[SplatPresso] No preview shader available in this build; placement previews disabled. " +
                                 "Assign PlacementPreviewService.previewShader (SplatPresso/Preview).");
            }
            return shader;
        }

        static Material MakeUnlitTransparent(Shader shader, Color color)
        {
            var m = new Material(shader) { name = "SplatPressoPreview", hideFlags = HideFlags.DontSave };
            if (shader.name != kPreviewShaderName)
            {
                // generic fallback shaders: request alpha blending the URP/Unlit way (ignored where unsupported)
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_ZWrite", 0f);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            m.renderQueue = (int)RenderQueue.Transparent;
            SetColor(m, color);
            return m;
        }

        // The package shader outputs _BaseColor * _Color (_Color is a tint that stays white): set only _BaseColor,
        // since setting both would square the color and drop the alpha. Fallback shaders declare either
        // _BaseColor (URP) or _Color (Sprites/Default) as their main color: set whichever exists.
        static void SetColor(Material m, Color c)
        {
            if (m.shader != null && m.shader.name == kPreviewShaderName)
            {
                m.SetColor(s_BaseColorId, c);
                return;
            }
            if (m.HasProperty(s_BaseColorId))
                m.SetColor(s_BaseColorId, c);
            if (m.HasProperty(s_ColorId))
                m.SetColor(s_ColorId, c);
        }

        void CreatePreview(PlacedObjectResult obj, CaptureResult capture, Vector3 groundPos, Quaternion rotation, Vector3 size, bool isMesh)
        {
            EnsureMaterials();
            if (m_EdgeMat == null)
                return; // no usable shader in this build - previews disabled
            RemoveFor(obj);

            var root = new GameObject($"PlacementPreview_{obj.id}_{obj.name}");
            root.transform.SetParent(transform, false);
            root.transform.SetPositionAndRotation(groundPos, rotation);

            // box center sits half the height above the ground anchor
            var box = new GameObject("Box");
            box.transform.SetParent(root.transform, false);
            box.transform.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
            box.transform.localScale = size;

            AddCube(box.transform, m_VolumeMat, Vector3.zero, Vector3.one, "Volume");

            // rising fill volume (bottom anchored; rescaled every frame)
            var fill = AddCube(box.transform, m_FillMat, new Vector3(0f, -0.48f, 0f), new Vector3(0.96f, 0.001f, 0.96f), "Fill");

            // 12 glowing edges (thickness relative to the smallest box dimension)
            float t = Mathf.Clamp(Mathf.Min(size.x, Mathf.Min(size.y, size.z)) * 0.04f, 0.008f, 0.05f);
            var tl = new Vector3(t / size.x, t / size.y, t / size.z); // thickness in box-local units
            for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
                AddCube(box.transform, m_EdgeMat, new Vector3(xi * 0.5f, yi * 0.5f, 0f), new Vector3(tl.x, tl.y, 1f + tl.z), "EdgeZ");
            for (int xi = -1; xi <= 1; xi += 2)
            for (int zi = -1; zi <= 1; zi += 2)
                AddCube(box.transform, m_EdgeMat, new Vector3(xi * 0.5f, 0f, zi * 0.5f), new Vector3(tl.x, 1f + tl.y, tl.z), "EdgeY");
            for (int yi = -1; yi <= 1; yi += 2)
            for (int zi = -1; zi <= 1; zi += 2)
                AddCube(box.transform, m_EdgeMat, new Vector3(0f, yi * 0.5f, zi * 0.5f), new Vector3(1f + tl.x, tl.y, tl.z), "EdgeX");

            m_Previews[obj] = new Preview
            {
                go = root,
                fill = fill,
                size = size,
                label = string.IsNullOrEmpty(obj.name) ? $"object {obj.id}" : obj.name,
                stageText = "waiting",
                isMesh = isMesh,
                expectedSeconds = isMesh ? expectedMeshGenerationSeconds : expectedGenerationSeconds,
                targetGroundPos = groundPos,
                targetRot = rotation,
                capture = capture,
            };
        }

        static Transform AddCube(Transform parent, Material mat, Vector3 localPos, Vector3 localScale, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null)
                DestroyImmediate(col); // previews must never block raycasts or physics
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        static void DestroySafe(UnityEngine.Object o) => ImageUtil.DestroySafe(o);
    }
}
