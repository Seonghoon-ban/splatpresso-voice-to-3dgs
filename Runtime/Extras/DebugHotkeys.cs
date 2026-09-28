using System;
using System.Collections.Generic;
using System.IO;
using SplatPresso.Placement;
using UnityEngine;

namespace SplatPresso.Extras
{
    /// <summary>
    /// Opt-in keyboard shortcuts for testing the pipeline, plus a small status overlay:
    /// Tab toggles the overlay, F5 runs a canned request, F6 / F7 replay the latest session from Decide /
    /// ProcessObjects, F8 cancels all runs, F9 spawns a .ply directly (no generation, no placement solve).
    /// </summary>
    /// <remarks>Keys are ignored while the HUD text box has focus.</remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Extras/Debug Hotkeys")]
    public sealed class DebugHotkeys : MonoBehaviour
    {
        /// <summary>Root to drive; null = found automatically.</summary>
        [Tooltip("Empty = found automatically.")]
        public SplatPressoRoot root;
        /// <summary>Spawn service used by F9; null = found automatically.</summary>
        [Tooltip("Empty = found automatically.")]
        public ObjectSpawnService spawnService;
        /// <summary>Overlay visibility (toggled with Tab).</summary>
        [Tooltip("Debug overlay visibility (toggled with Tab)")]
        public bool showOverlay;
        /// <summary>
        /// .ply spawned by F9: absolute, or relative to the Assets folder (Application.dataPath). Empty = the newest
        /// model.ply of the latest session.
        /// </summary>
        [Tooltip("F9 splat file: absolute or relative to Assets/. Empty = newest model.ply of the latest session.")]
        public string plyPath = "";
        /// <summary>Log a heartbeat line every N seconds (0 = off); helps locate freezes in Player.log.</summary>
        [Tooltip("Log a heartbeat every N seconds (0 = off). Helps locate player freezes in Player.log.")]
        [Min(0f)] public float heartbeatSeconds;

        PlacementProgress m_LastProgress;
        string m_LastMessage = "";
        SplatPressoRoot m_Subscribed;
        GUIStyle m_OverlayStyle;
        float m_NextHeartbeat;

        void Awake()
        {
            if (root == null) root = GetComponent<SplatPressoRoot>();
            if (root == null) root = FindFirstObjectByType<SplatPressoRoot>();
            if (spawnService == null) spawnService = GetComponent<ObjectSpawnService>();
            if (spawnService == null) spawnService = FindFirstObjectByType<ObjectSpawnService>();
        }

        void OnEnable() => TrySubscribe();

        void OnDisable() => Unsubscribe();

        void TrySubscribe()
        {
            if (m_Subscribed == root)
                return;
            Unsubscribe();
            if (root == null)
                return;
            // root-level aggregated events cover every concurrent run
            root.RunProgress += HandleProgress;
            root.RunCompleted += HandleCompleted;
            root.RunFailed += HandleFailed;
            m_Subscribed = root;
        }

        void Unsubscribe()
        {
            if (m_Subscribed == null)
            {
                m_Subscribed = null;
                return;
            }
            m_Subscribed.RunProgress -= HandleProgress;
            m_Subscribed.RunCompleted -= HandleCompleted;
            m_Subscribed.RunFailed -= HandleFailed;
            m_Subscribed = null;
        }

        void HandleProgress(PlacementProgress p)
        {
            m_LastProgress = p;
            m_LastMessage = p?.message ?? "";
        }

        void HandleCompleted(PlacementResult r) =>
            m_LastMessage = $"Completed ({(r?.objects != null ? r.objects.Count : 0)} object(s))";

        void HandleFailed(PlacementFailure f)
        {
            if (f == null)
                return;
            m_LastMessage = f.kind == PlacementEndKind.Cancelled
                ? $"Cancelled during {f.stage}"
                : $"Failed at {f.stage} ({f.kind}): {f.reason}";
        }

        void Update()
        {
            TrySubscribe();
            if (heartbeatSeconds > 0f && Time.realtimeSinceStartup >= m_NextHeartbeat)
            {
                m_NextHeartbeat = Time.realtimeSinceStartup + heartbeatSeconds;
                Debug.Log($"[SplatPresso] heartbeat t={Time.realtimeSinceStartup:F0}s frame={Time.frameCount} focused={Application.isFocused}");
            }
            if (ExtrasInput.TextInputFocused)
                return;
            if (InputCompat.GetKeyDown(KeyCode.Tab)) showOverlay = !showOverlay;
            if (InputCompat.GetKeyDown(KeyCode.F5)) RunCanned();
            if (InputCompat.GetKeyDown(KeyCode.F6)) ReplayLatest(StartStage.Decide);
            if (InputCompat.GetKeyDown(KeyCode.F7)) ReplayLatest(StartStage.ProcessObjects);
            if (InputCompat.GetKeyDown(KeyCode.F8) && root != null) root.CancelAll();
            if (InputCompat.GetKeyDown(KeyCode.F9)) SpawnPly();
        }

        /// <summary>The request F5 sends: one camping chair on the ground in an empty area.</summary>
        public static PlacementRequest BuildCannedRequest() => new PlacementRequest
        {
            intentSummary = "Add a single camping chair on the ground in an empty area of the view.",
            objects = new List<RequestedObject>
            {
                new RequestedObject
                {
                    name = "camping chair",
                    description = "a folding camping chair, dark red fabric, black metal frame, about 0.8 m tall",
                    count = 1,
                },
            },
            placementHint = "on the ground in an empty area",
        };

        void RunCanned()
        {
            if (root == null)
            {
                Debug.LogWarning("[SplatPresso] DebugHotkeys: no SplatPressoRoot in the scene");
                return;
            }
            string runId = root.StartRun(BuildCannedRequest());
            if (string.IsNullOrEmpty(runId))
                Debug.LogWarning("[SplatPresso] DebugHotkeys: the canned request was rejected (see previous messages)");
        }

        void ReplayLatest(StartStage from)
        {
            if (root == null)
            {
                Debug.LogWarning("[SplatPresso] DebugHotkeys: no SplatPressoRoot in the scene");
                return;
            }
            string dir = PipelineSession.LatestSessionDir();
            if (string.IsNullOrEmpty(dir))
            {
                Debug.LogWarning("[SplatPresso] No previous session to replay");
                return;
            }
            root.StartReplay(dir, from);
        }

        async void SpawnPly()
        {
            try
            {
                if (spawnService == null)
                {
                    Debug.LogWarning("[SplatPresso] DebugHotkeys: no ObjectSpawnService in the scene");
                    return;
                }
                string path = ResolvePlyPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Debug.LogWarning("[SplatPresso] DebugHotkeys F9: no .ply to spawn. Set DebugHotkeys.plyPath " +
                                     "(or run the pipeline once so the latest session has a model.ply).");
                    return;
                }
                var cam = Camera.main;
                Vector3 pos;
                if (cam != null)
                {
                    // 2 m ahead at eye height (the pivot is the bottom of the object)
                    pos = cam.transform.position + cam.transform.forward * 2f;
                    pos.y = cam.transform.position.y;
                }
                else
                {
                    pos = new Vector3(0f, 0f, 2f);
                }
                var go = await spawnService.SpawnSplatFromFileAsync(path, pos, Quaternion.identity, 1f,
                    Path.GetFileNameWithoutExtension(path), destroyCancellationToken);
                Debug.Log($"[SplatPresso] F9 spawned '{(go != null ? go.name : "null")}' from {path} at {pos}");
            }
            catch (OperationCanceledException)
            {
                // component destroyed while loading
            }
            catch (Exception e)
            {
                Debug.LogError($"[SplatPresso] F9 spawn failed: {e}");
            }
        }

        string ResolvePlyPath()
        {
            if (!string.IsNullOrWhiteSpace(plyPath))
                return Path.IsPathRooted(plyPath) ? plyPath : Path.Combine(Application.dataPath, plyPath);

            // newest model.ply of the latest session
            string session = PipelineSession.LatestSessionDir();
            if (string.IsNullOrEmpty(session))
                return null;
            string objectsDir = Path.Combine(session, PipelineSession.ObjectsDir);
            if (!Directory.Exists(objectsDir))
                return null;
            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(objectsDir, PipelineSession.ObjectPly, SearchOption.AllDirectories))
            {
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (newest == null || t > newestTime)
                {
                    newest = f;
                    newestTime = t;
                }
            }
            return newest;
        }

        void OnGUI()
        {
            if (!showOverlay)
                return;
            m_OverlayStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = Color.white }, // explicit: never rely on the default skin
            };
            var area = new Rect(10, 10, 520, 92);
            var prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(area.x + 8, area.y + 6, area.width - 16, area.height - 12));
            var p = m_LastProgress;
            int runs = root != null ? root.ActiveRunCount : 0;
            GUILayout.Label("[SplatPresso] stage: " + (p != null ? p.stage.ToString() : "-")
                + (runs > 0 ? $"    runs: {runs}" : "")
                + $"    cost: {(p != null ? p.costSoFar : 0.0):F2} cr"
                + (p != null && p.objectsTotal > 0 ? $"    objects: {p.objectsDone}/{p.objectsTotal}" : ""), m_OverlayStyle);
            GUILayout.Label(m_LastMessage ?? "", m_OverlayStyle);
            GUILayout.Label("Tab hide | F5 run canned | F6 replay@Decide | F7 replay@Objects | F8 cancel all | F9 spawn .ply", m_OverlayStyle);
            GUILayout.EndArea();
            GUI.color = prevColor;
        }
    }
}
