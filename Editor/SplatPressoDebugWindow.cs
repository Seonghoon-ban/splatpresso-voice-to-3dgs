using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SplatPresso.EditorTools
{
    /// <summary>
    /// SplatPresso &gt; Debug Window: session browser with artifact previews, ledger and result summary; canned or
    /// typed requests; replays from any stage; live per-run progress and cost. Runs and replays need play mode.
    /// </summary>
    public sealed class SplatPressoDebugWindow : EditorWindow
    {
        const float kThumbWidth = 150f;

        sealed class RunView
        {
            public string runId;
            public PlacementStage stage;
            public string message;
            public int done, total;
            public double cost;
            public bool finished;
            public string outcome;
        }

        sealed class SessionInfo
        {
            public string dir;
            public bool capture, request, decision, edited, verification, result;
            public bool direct;
            /// <summary>A session folder Delete may remove (checked when loaded, re-checked before deleting).</summary>
            public bool deletable;
            public string modeText, representationText;
            public List<string> objectDirs = new List<string>();
            public CostLedger ledger;
            public PlacementResult resultData;
            public double loadedAt;
        }

        // Sessions: the selection is stored as a PATH. The list is newest-first and refreshed when runs end, so an
        // index would silently shift to another session when a new one appears.
        string[] m_Dirs = Array.Empty<string>();
        string[] m_Names = Array.Empty<string>();
        [SerializeField] string m_SelectedDir;
        SessionInfo m_Info;

        // Canned request (same as DebugHotkeys' F5 request by default) and free text.
        [SerializeField] string m_Intent = "Add a single camping chair on the ground in an empty area of the view.";
        [SerializeField] string m_ObjectName = "camping chair";
        [SerializeField] string m_ObjectDescription = "a folding camping chair, dark red fabric, black metal frame, about 0.8 m tall";
        [SerializeField] string m_Hint = "on the ground in an empty area";
        [SerializeField] string m_Text = "";
        [SerializeField] bool m_ShowRequest = true;
        [SerializeField] bool m_ShowArtifacts = true;
        [SerializeField] bool m_ShowLedger;
        [SerializeField] bool m_ShowResult;

        SplatPressoRoot m_Subscribed;
        SplatPressoRoot m_Root;
        double m_NextRootSearch;
        readonly Dictionary<string, RunView> m_Runs = new Dictionary<string, RunView>();
        readonly List<string> m_RunOrder = new List<string>();
        string m_LastEvent = "";
        Vector2 m_Scroll;

        readonly Dictionary<string, KeyValuePair<DateTime, Texture2D>> m_Thumbs = new Dictionary<string, KeyValuePair<DateTime, Texture2D>>();

        /// <summary>Opens the window.</summary>
        public static void Open() => GetWindow<SplatPressoDebugWindow>("SplatPresso Debug");

        void OnEnable()
        {
            RefreshSessions();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            Unsubscribe();
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            ClearThumbs();
        }

        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                Unsubscribe();
                foreach (var run in m_Runs.Values)
                {
                    if (run.finished)
                        continue;
                    run.finished = true;
                    run.outcome = "Play mode ended";
                }
                m_Root = null;
            }
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                m_Runs.Clear();
                m_RunOrder.Clear();
                m_LastEvent = "";
            }
            if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
                RefreshSessions();
            Repaint();
        }

        // Periodic repaint: progress arrives through events, but elapsed state (and new session folders) should not
        // wait for the next event.
        void OnInspectorUpdate()
        {
            bool reloaded = false;
            if (m_Info != null && EditorApplication.timeSinceStartup - m_Info.loadedAt > 2.0)
            {
                m_Info = LoadInfo(m_SelectedDir); // artifacts appear while a run writes them
                reloaded = true;
            }
            if (EditorApplication.isPlaying || reloaded)
                Repaint();
        }

        static string SessionsRoot =>
            EditorApplication.isPlaying ? PipelineSession.SessionsRoot : PipelineSession.ResolveSessionsRoot(SplatPressoEditorUtil.FindSettingsAsset());

        // Cached: OnGUI runs several times per repaint and the scene search walks every root object.
        SplatPressoRoot FindRoot()
        {
            if (!EditorApplication.isPlaying)
                return null;
            if (m_Root == null && EditorApplication.timeSinceStartup >= m_NextRootSearch)
            {
                m_Root = SplatPressoEditorUtil.FindInLoadedScenes<SplatPressoRoot>();
                m_NextRootSearch = EditorApplication.timeSinceStartup + 1.0;
            }
            return m_Root;
        }

        void RefreshSessions()
        {
            m_Dirs = PipelineSession.ListSessionDirs(SessionsRoot).Where(IsSessionFolderName).ToArray();
            m_Names = m_Dirs.Select(Path.GetFileName).ToArray();
            if (string.IsNullOrEmpty(m_SelectedDir) || !m_Dirs.Contains(m_SelectedDir))
                m_SelectedDir = m_Dirs.Length > 0 ? m_Dirs[0] : null;
            m_Info = LoadInfo(m_SelectedDir);
        }

        void OnGUI()
        {
            var root = FindRoot();
            TrySubscribe(root);

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            DrawRunControls(root);
            EditorGUILayout.Space();
            DrawLiveRuns(root);
            EditorGUILayout.Space();
            DrawSessions(root);
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------------------------------

        void DrawRunControls(SplatPressoRoot root)
        {
            EditorGUILayout.LabelField("Run", EditorStyles.boldLabel);
            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter play mode to run the pipeline. Sessions can be browsed below.", MessageType.Info);
                var s = SplatPressoEditorUtil.FindSettingsAsset();
                if (s != null)
                    EditorGUILayout.LabelField("Defaults", $"{s.defaultMode}, {s.representation} (Project Settings > SplatPresso)");
            }
            else if (root == null)
            {
                EditorGUILayout.HelpBox("No SplatPressoRoot in the loaded scenes. Run SplatPresso > Setup Scene.", MessageType.Warning);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var mode = (GenerationMode)EditorGUILayout.EnumPopup("Mode [M]", root.Mode);
                    if (mode != root.Mode)
                        root.Mode = mode;
                }
                var rep = (ObjectRepresentation)EditorGUILayout.EnumPopup("Representation [N]", root.Representation);
                if (rep != root.Representation)
                    root.Representation = rep;
            }

            m_ShowRequest = EditorGUILayout.Foldout(m_ShowRequest, "Canned request", true);
            if (m_ShowRequest)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    m_Intent = EditorGUILayout.TextField("Intent", m_Intent);
                    m_ObjectName = EditorGUILayout.TextField("Object Name", m_ObjectName);
                    m_ObjectDescription = EditorGUILayout.TextField("Description", m_ObjectDescription);
                    m_Hint = EditorGUILayout.TextField("Placement Hint", m_Hint);
                }
            }
            using (new EditorGUI.DisabledScope(root == null))
            {
                // Runs may start while others are generating (parallel runs are supported up to maxConcurrentRuns).
                if (GUILayout.Button("Run canned request (new session)"))
                {
                    string runId = root.StartRun(BuildRequest());
                    m_LastEvent = runId != null ? "Started " + runId : "Request rejected (see the Console)";
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    m_Text = EditorGUILayout.TextField(new GUIContent("Say (text)", "Goes through the voice agent's language model, like a spoken request"), m_Text);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Text)))
                    {
                        if (GUILayout.Button("Send", GUILayout.Width(60)))
                        {
                            root.SubmitText(m_Text.Trim());
                            m_LastEvent = "Sent text: " + m_Text.Trim();
                            m_Text = "";
                            GUI.FocusControl(null);
                        }
                    }
                }
            }
        }

        void DrawLiveRuns(SplatPressoRoot root)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Runs", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(root == null || root.ActiveRunCount == 0))
                {
                    if (GUILayout.Button("Cancel All", GUILayout.Width(90)))
                        root.CancelAll();
                }
                using (new EditorGUI.DisabledScope(m_RunOrder.Count == 0))
                {
                    if (GUILayout.Button("Clear", GUILayout.Width(60)))
                        ClearFinishedRuns();
                }
            }
            if (root != null)
                EditorGUILayout.LabelField("Active runs", root.ActiveRunCount.ToString(CultureInfo.InvariantCulture));
            if (m_RunOrder.Count == 0)
                EditorGUILayout.LabelField("(no runs yet in this play session)", EditorStyles.miniLabel);
            for (int i = m_RunOrder.Count - 1; i >= 0; i--)
            {
                var run = m_Runs[m_RunOrder[i]];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string objects = run.total > 0 ? $"  objects {run.done}/{run.total}" : "";
                        EditorGUILayout.LabelField($"{run.runId}  {run.stage}{objects}  ~{run.cost:0.##} credits", EditorStyles.miniBoldLabel);
                        using (new EditorGUI.DisabledScope(run.finished || root == null))
                        {
                            if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(56)))
                                root.Cancel(run.runId);
                        }
                    }
                    string line = run.finished ? run.outcome : run.message;
                    if (!string.IsNullOrEmpty(line))
                        EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
                }
            }
            if (!string.IsNullOrEmpty(m_LastEvent))
                EditorGUILayout.LabelField("Last event", m_LastEvent, EditorStyles.wordWrappedLabel);
        }

        void DrawSessions(SplatPressoRoot root)
        {
            EditorGUILayout.LabelField("Sessions", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (m_Dirs.Length == 0)
                {
                    EditorGUILayout.LabelField("(no sessions yet)");
                }
                else
                {
                    int index = Array.IndexOf(m_Dirs, m_SelectedDir);
                    int picked = EditorGUILayout.Popup(Mathf.Max(0, index), m_Names);
                    if (picked != index && picked >= 0 && picked < m_Dirs.Length)
                    {
                        m_SelectedDir = m_Dirs[picked];
                        m_Info = LoadInfo(m_SelectedDir);
                        ClearThumbs();
                    }
                }
                if (GUILayout.Button("Refresh", GUILayout.Width(64)))
                {
                    RefreshSessions();
                    ClearThumbs();
                }
                using (new EditorGUI.DisabledScope(m_Info == null))
                {
                    if (GUILayout.Button("Open Folder", GUILayout.Width(88)))
                        EditorUtility.RevealInFinder(m_SelectedDir);
                    bool running = m_Info != null && IsSessionRunning(root, m_Info.dir);
                    using (new EditorGUI.DisabledScope(running || m_Info == null || !m_Info.deletable))
                    {
                        if (GUILayout.Button("Delete", GUILayout.Width(56)))
                            DeleteSelectedSession();
                    }
                }
            }
            EditorGUILayout.LabelField(SessionsRoot, EditorStyles.miniLabel);
            if (m_Info == null)
                return;

            var info = m_Info;
            EditorGUILayout.LabelField("Mode", $"{info.modeText ?? "?"}, {info.representationText ?? ObjectRepresentation.GaussianSplat.ToString()}");

            // Replays reuse this session's artifacts and keep its stored mode; each button needs the artifacts the
            // orchestrator loads for that entry point (a missing hosted URL is fine: the bytes are sent instead).
            EditorGUILayout.LabelField("Replay from:");
            using (new EditorGUI.DisabledScope(root == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                ReplayButton(root, "Decide", StartStage.Decide, Missing("capture", info.capture, "request.json", info.request));
                ReplayButton(root, "Edit", StartStage.Edit, info.direct ? "not used in Direct mode"
                    : Missing("capture", info.capture, "decision.json", info.decision));
                ReplayButton(root, "Verify", StartStage.Verify, info.direct ? "not used in Direct mode"
                    : Missing("capture", info.capture, "decision.json", info.decision, "edited.jpg", info.edited));
                ReplayButton(root, "Objects", StartStage.ProcessObjects, info.direct
                    ? Missing("capture", info.capture, "decision.json", info.decision)
                    : Missing("capture", info.capture, "decision.json", info.decision, "edited.jpg", info.edited, "verification.json", info.verification));
                ReplayButton(root, "Place", StartStage.Place, Missing("capture", info.capture, "result.json", info.result));
            }

            m_ShowArtifacts = EditorGUILayout.Foldout(m_ShowArtifacts, "Images", true);
            if (m_ShowArtifacts)
                DrawArtifacts(info);
            m_ShowResult = EditorGUILayout.Foldout(m_ShowResult, "Result", true);
            if (m_ShowResult)
                DrawResult(info);
            m_ShowLedger = EditorGUILayout.Foldout(m_ShowLedger, "Cost ledger (estimated credits)", true);
            if (m_ShowLedger)
                DrawLedger(info);
        }

        void ReplayButton(SplatPressoRoot root, string label, StartStage stage, string missing)
        {
            using (new EditorGUI.DisabledScope(missing != null))
            {
                var content = new GUIContent(label, missing != null ? "Unavailable: " + missing : "Replay this session from " + stage);
                if (GUILayout.Button(content) && root != null)
                {
                    root.StartReplay(m_SelectedDir, stage);
                    m_LastEvent = $"Replay {Path.GetFileName(m_SelectedDir)} from {stage}";
                }
            }
        }

        // Returns null when every artifact is present, else "missing a, b".
        static string Missing(params object[] nameHasPairs)
        {
            var missing = new List<string>();
            for (int i = 0; i + 1 < nameHasPairs.Length; i += 2)
                if (!(bool)nameHasPairs[i + 1])
                    missing.Add((string)nameHasPairs[i]);
            return missing.Count == 0 ? null : "missing " + string.Join(", ", missing);
        }

        void DrawArtifacts(SessionInfo info)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawThumb(Path.Combine(info.dir, PipelineSession.CaptureJpg), "capture");
                DrawThumb(Path.Combine(info.dir, PipelineSession.EditedJpg), "edited");
                DrawThumb(Path.Combine(info.dir, PipelineSession.DepthGenPng), "depth");
                GUILayout.FlexibleSpace();
            }
            foreach (string objDir in info.objectDirs)
            {
                EditorGUILayout.LabelField("Object " + Path.GetFileName(objDir), EditorStyles.miniBoldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawThumb(Path.Combine(objDir, PipelineSession.ObjectCutoutPng), "cutout");
                    DrawThumb(Path.Combine(objDir, PipelineSession.ObjectEnhancedPng), "enhanced");
                    DrawThumb(Path.Combine(objDir, PipelineSession.ObjectGeneratedPng), "generated");
                    GUILayout.FlexibleSpace();
                }
            }
        }

        void DrawThumb(string path, string label)
        {
            var tex = LoadThumb(path);
            if (tex == null)
                return;
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(kThumbWidth)))
            {
                float h = kThumbWidth * tex.height / Mathf.Max(1f, tex.width);
                var rect = GUILayoutUtility.GetRect(kThumbWidth, h, GUILayout.Width(kThumbWidth), GUILayout.Height(h));
                EditorGUI.DrawTextureTransparent(rect, tex, ScaleMode.ScaleToFit);
                if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && rect.Contains(Event.current.mousePosition))
                    EditorUtility.OpenWithDefaultApp(path);
                EditorGUILayout.LabelField(label, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(kThumbWidth));
            }
        }

        void DrawResult(SessionInfo info)
        {
            var result = info.resultData;
            if (result == null)
            {
                EditorGUILayout.LabelField(info.result ? "(result.json could not be read)" : "(no result.json yet)", EditorStyles.miniLabel);
                return;
            }
            if (result.objects == null || result.objects.Count == 0)
            {
                EditorGUILayout.LabelField("(no objects)", EditorStyles.miniLabel);
                return;
            }
            foreach (var o in result.objects)
            {
                if (o == null)
                    continue;
                string reason = string.IsNullOrEmpty(o.skipReason) ? "" : " - " + o.skipReason;
                EditorGUILayout.LabelField($"#{o.id} {o.name}", $"{o.status} ({o.representation}){reason}", EditorStyles.wordWrappedLabel);
            }
        }

        static void DrawLedger(SessionInfo info)
        {
            var ledger = info.ledger;
            if (ledger == null || ledger.entries == null || ledger.entries.Count == 0)
            {
                EditorGUILayout.LabelField("(no entries)", EditorStyles.miniLabel);
                return;
            }
            // Each run of the session (a fresh request or a replay) is its own section; the cap applies per run.
            foreach (var group in ledger.entries.Where(e => e != null).GroupBy(e => e.run).OrderBy(g => g.Key))
            {
                EditorGUILayout.LabelField($"Run section {group.Key}", $"{group.Sum(e => e.cost):0.###} credits", EditorStyles.boldLabel);
                foreach (var e in group)
                    EditorGUILayout.LabelField($"   {e.timestamp}  {e.item}", e.cost.ToString("0.###", CultureInfo.InvariantCulture));
            }
            EditorGUILayout.LabelField("Session total", $"{ledger.LifetimeCost:0.###} credits (estimates, not a bill)", EditorStyles.boldLabel);
        }

        // Session folders are named yyyyMMdd_HHmmss[_n] by PipelineSession.CreateNew. sessionsFolder accepts any path,
        // so a root pointed at an existing folder (a project, Documents/...) must not list or delete its other folders.
        static bool IsSessionFolderName(string dir)
        {
            string name = Path.GetFileName(dir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name) || name.Length < 15 || name[8] != '_')
                return false;
            for (int i = 0; i < name.Length; i++)
            {
                if (i == 8)
                    continue;
                if (i == 15)
                {
                    // optional "_<n>" collision suffix
                    if (name[15] != '_' || name.Length == 16)
                        return false;
                    continue;
                }
                if (name[i] < '0' || name[i] > '9')
                    return false;
            }
            return true;
        }

        static string NormalizeDir(string dir) =>
            Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Delete only a session-named folder directly under the sessions root that holds session artifacts (or nothing).
        static bool IsDeletableSession(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || !IsSessionFolderName(dir))
                    return false;
                string parent = Path.GetDirectoryName(NormalizeDir(dir));
                if (parent == null || !string.Equals(parent, NormalizeDir(SessionsRoot), StringComparison.OrdinalIgnoreCase))
                    return false;
                string P(string name) => Path.Combine(dir, name);
                return File.Exists(P(PipelineSession.LedgerJson)) || File.Exists(P(PipelineSession.RequestJson)) ||
                       File.Exists(P(PipelineSession.CaptureMetaJson)) || File.Exists(P(PipelineSession.ModeTxt)) ||
                       Directory.Exists(P(PipelineSession.ObjectsDir)) || !Directory.EnumerateFileSystemEntries(dir).Any();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The live view can miss a run (window opened mid-run, cleared list), so the root's active runs are checked too.
        bool IsSessionRunning(SplatPressoRoot root, string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return false;
            if (m_Runs.TryGetValue(Path.GetFileName(dir), out var r) && !r.finished)
                return true;
            if (root == null)
                return false;
            try
            {
                string target = NormalizeDir(dir);
                return root.ActiveRuns.Any(o => o != null && !string.IsNullOrEmpty(o.SessionDir) &&
                                                string.Equals(NormalizeDir(o.SessionDir), target, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return true; // unknown: keep Delete disabled
            }
        }

        void DeleteSelectedSession()
        {
            string dir = m_SelectedDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            if (!IsDeletableSession(dir) || IsSessionRunning(FindRoot(), dir))
            {
                EditorUtility.DisplayDialog("Delete session", $"{dir}\nis not an idle SplatPresso session folder directly under the sessions root; " +
                                                              "not deleted.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Delete session", $"Delete {Path.GetFileName(dir)} and all its artifacts?\n{dir}", "Delete", "Cancel"))
                return;
            ClearThumbs();
            try
            {
                Directory.Delete(dir, true);
                Debug.Log("[SplatPresso] Deleted session " + dir);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Delete session", "Could not delete the session: " + e.Message, "OK");
            }
            m_SelectedDir = null;
            RefreshSessions();
        }

        PlacementRequest BuildRequest() => new PlacementRequest
        {
            intentSummary = m_Intent,
            objects = new List<RequestedObject> { new RequestedObject { name = m_ObjectName, description = m_ObjectDescription, count = 1 } },
            placementHint = m_Hint,
        };

        // ------------------------------------------------------------------------------------------
        // Session loading

        static SessionInfo LoadInfo(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;
            string P(string name) => Path.Combine(dir, name);
            var info = new SessionInfo
            {
                dir = dir,
                loadedAt = EditorApplication.timeSinceStartup,
                capture = File.Exists(P(PipelineSession.CaptureJpg)) && File.Exists(P(PipelineSession.CaptureMetaJson)),
                request = File.Exists(P(PipelineSession.RequestJson)),
                decision = File.Exists(P(PipelineSession.DecisionJson)),
                edited = File.Exists(P(PipelineSession.EditedJpg)),
                verification = File.Exists(P(PipelineSession.VerificationJson)),
                result = File.Exists(P(PipelineSession.ResultJson)),
                deletable = IsDeletableSession(dir),
            };
            info.modeText = ReadText(P(PipelineSession.ModeTxt))?.Trim();
            info.representationText = ReadText(P(PipelineSession.RepresentationTxt))?.Trim();
            info.direct = Enum.TryParse(info.modeText, out GenerationMode mode) && mode == GenerationMode.DirectTextTo3D;

            string objects = P(PipelineSession.ObjectsDir);
            if (Directory.Exists(objects))
                info.objectDirs = Directory.GetDirectories(objects)
                    .OrderBy(d => int.TryParse(Path.GetFileName(d), out int n) ? n : int.MaxValue)
                    .ThenBy(d => d, StringComparer.Ordinal)
                    .ToList();
            try
            {
                string ledger = ReadText(P(PipelineSession.LedgerJson));
                info.ledger = string.IsNullOrEmpty(ledger) ? null : JsonUtil.Deserialize<CostLedger>(ledger);
            }
            catch (Exception) { info.ledger = null; }
            try
            {
                string result = ReadText(P(PipelineSession.ResultJson));
                info.resultData = string.IsNullOrEmpty(result) ? null : JsonUtil.Deserialize<PlacementResult>(result);
            }
            catch (Exception) { info.resultData = null; }
            return info;
        }

        static string ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                return null; // being written by a run
            }
        }

        Texture2D LoadThumb(string path)
        {
            if (!File.Exists(path))
                return null;
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return null; }
            if (m_Thumbs.TryGetValue(path, out var cached) && cached.Key == stamp && cached.Value != null)
                return cached.Value;
            if (cached.Value != null)
                DestroyImmediate(cached.Value);
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                if (!tex.LoadImage(File.ReadAllBytes(path)))
                {
                    DestroyImmediate(tex);
                    tex = null;
                }
            }
            catch (Exception)
            {
                if (tex != null)
                    DestroyImmediate(tex);
                tex = null;
            }
            m_Thumbs[path] = new KeyValuePair<DateTime, Texture2D>(stamp, tex);
            return tex;
        }

        void ClearThumbs()
        {
            foreach (var kv in m_Thumbs.Values)
                if (kv.Value != null)
                    DestroyImmediate(kv.Value);
            m_Thumbs.Clear();
        }

        // ------------------------------------------------------------------------------------------
        // Live events (root-level aggregated events cover every concurrent run)

        void TrySubscribe(SplatPressoRoot root)
        {
            if (root == null || ReferenceEquals(m_Subscribed, root))
                return;
            Unsubscribe();
            root.RunStarted += HandleStarted;
            root.RunProgress += HandleProgress;
            root.RunCompleted += HandleCompleted;
            root.RunFailed += HandleFailed;
            root.RequestRejected += HandleRejected;
            m_Subscribed = root;
        }

        void Unsubscribe()
        {
            if (ReferenceEquals(m_Subscribed, null))
                return;
            m_Subscribed.RunStarted -= HandleStarted;
            m_Subscribed.RunProgress -= HandleProgress;
            m_Subscribed.RunCompleted -= HandleCompleted;
            m_Subscribed.RunFailed -= HandleFailed;
            m_Subscribed.RequestRejected -= HandleRejected;
            m_Subscribed = null;
        }

        RunView GetRun(string runId)
        {
            runId ??= "?";
            if (!m_Runs.TryGetValue(runId, out var run))
            {
                run = new RunView { runId = runId };
                m_Runs[runId] = run;
                m_RunOrder.Add(runId);
            }
            return run;
        }

        void ClearFinishedRuns()
        {
            foreach (string id in m_RunOrder.ToList())
                if (m_Runs[id].finished)
                {
                    m_Runs.Remove(id);
                    m_RunOrder.Remove(id);
                }
        }

        void HandleStarted(RunStartedInfo info)
        {
            if (info == null)
                return;
            var run = GetRun(info.runId);
            // A replay reuses its session's folder name as run id, so a finished view of that session is live again.
            run.finished = false;
            run.outcome = null;
            run.stage = PlacementStage.Idle;
            run.done = run.total = 0;
            run.cost = 0;
            m_RunOrder.Remove(run.runId);
            m_RunOrder.Add(run.runId);
            run.message = $"{info.mode}, {info.representation}" + (string.IsNullOrEmpty(info.sourceUtterance) ? "" : $" - \"{info.sourceUtterance}\"");
            m_LastEvent = "Started " + info.runId;
            RefreshSessions();
            Repaint();
        }

        void HandleProgress(PlacementProgress p)
        {
            if (p == null)
                return;
            var run = GetRun(p.runId);
            run.stage = p.stage;
            run.message = SplatPressoRoot.FormatProgress(p);
            run.done = p.objectsDone;
            run.total = p.objectsTotal;
            run.cost = p.costSoFar;
            Repaint();
        }

        void HandleCompleted(PlacementResult r)
        {
            if (r == null)
                return;
            var run = GetRun(r.runId);
            run.finished = true;
            run.stage = PlacementStage.Completed;
            int count = r.objects?.Count ?? 0;
            run.outcome = $"Completed: {count} object(s)";
            m_LastEvent = $"{run.outcome}, session {Path.GetFileName(r.sessionDir)}";
            RefreshSessions();
            Repaint();
        }

        void HandleFailed(PlacementFailure f)
        {
            if (f == null)
                return;
            var run = GetRun(f.runId);
            run.finished = true;
            run.outcome = f.kind == PlacementEndKind.Cancelled ? $"Cancelled during {f.stage}" : $"{f.kind} at {f.stage}: {f.reason}";
            run.stage = f.kind == PlacementEndKind.Cancelled ? PlacementStage.Cancelled : PlacementStage.Failed;
            m_LastEvent = run.runId + ": " + run.outcome;
            RefreshSessions();
            Repaint();
        }

        void HandleRejected(PlacementRequest request, RequestRejectReason reason)
        {
            m_LastEvent = "Request rejected: " + reason;
            Repaint();
        }
    }
}
