using System.Collections.Generic;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>
    /// On-screen voice HUD (IMGUI, no UI assets): a bottom pill with the push-to-talk state and mic level, the
    /// generation-mode chips ([M] Scene-aware | Direct, [N] Splat | Mesh), the agent's reply as a fading subtitle
    /// (plus what it heard), a text box for typed requests (Enter), the [V] microphone picker, a list of running
    /// generations and a banner when the GenPresso key is missing. Not visible in XR headsets (screen-space IMGUI).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Voice HUD")]
    public sealed class VoiceHud : MonoBehaviour
    {
        [Tooltip("Voice agent to display (found automatically when empty).")]
        public VoiceAgent voiceAgent;
        [Tooltip("Root for the mode chips, run list and typed requests (found automatically when empty).")]
        public SplatPressoRoot root;

        [Header("Display")]
        public bool showModeChips = true;
        [Tooltip("Show what the agent heard above its reply.")]
        public bool showHeardLine = true;
        public bool showRunList = true;
        public bool showKeyBanner = true;
        [Tooltip("Seconds a reply stays on screen.")]
        [Range(2f, 30f)] public float replySeconds = 8f;
        [Tooltip("Extra scale on top of the automatic resolution scaling.")]
        [Range(0.5f, 3f)] public float uiScale = 1f;

        [Header("Keys (None disables)")]
        public KeyCode devicePanelKey = KeyCode.V;
        public KeyCode textInputKey = KeyCode.Return;

        /// <summary>
        /// True while the HUD text box is open: push-to-talk and keyboard shortcuts must be ignored meanwhile
        /// (VoiceAgent, SplatPressoRoot and the camera controllers check this).
        /// </summary>
        public static bool TextInputFocused { get; private set; }

        /// <summary>
        /// True while the V microphone-picker panel is showing (camera controllers suppress click-to-relock so the
        /// device buttons are clickable).
        /// </summary>
        public static bool DevicePanelOpen { get; private set; }

        const string kTextControlName = "SplatPressoVoiceText";
        const int kMaxTextLength = 300;
        const float kErrorSeconds = 6f;
        const float kRunLingerSeconds = 8f;
        const float kFailedRunLingerSeconds = 14f;
        const int kMaxRunLines = 6;
        const float kLookupIntervalSec = 2f;

        static Texture2D s_White;

        sealed class RunLine
        {
            public string runId;
            public string label;
            public PlacementStage stage;
            public string message;
            public int done, total;
            public float endedAt = -1f;
            public bool failed, cancelled;
        }

        readonly List<RunLine> m_Runs = new List<RunLine>();
        SplatPressoRoot m_SubscribedRoot;
        float m_NextLookup;
        float m_NextKeyCheck;
        bool m_HasGenpressoKey = true;

        bool m_TextOpen;
        string m_Text = "";
        int m_FocusRequests;
        bool m_StylesReady;
        GUIStyle m_TitleStyle, m_SmallStyle, m_ButtonStyle, m_FieldStyle, m_BubbleStyle, m_HeardStyle, m_ChipStyle, m_LineStyle, m_BannerStyle;
        GUIStyle m_ErrorStyle, m_HeaderStyle, m_RunStyle, m_HintStyle;

        static readonly Color kText = Color.white;
        static readonly Color kTextDim = new Color(1f, 1f, 1f, 0.75f);
        static readonly Color kAccent = new Color(0.35f, 0.85f, 1f, 1f);
        static readonly Color kOk = new Color(0.45f, 1f, 0.55f, 1f);
        static readonly Color kWarn = new Color(1f, 0.8f, 0.25f, 1f);
        static readonly Color kError = new Color(1f, 0.45f, 0.4f, 1f);

        // Projects often disable domain reload, so statics survive play sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            TextInputFocused = false;
            DevicePanelOpen = false;
            s_White = null;
        }

        // ------------------------------------------------------------------------------------------
        // lifecycle

        void Awake() => ResolveReferences(force: true);

        void OnDisable()
        {
            if (m_TextOpen)
                CloseTextInput();
            DevicePanelOpen = false;
            SubscribeRoot(null);
        }

        void OnDestroy() => SubscribeRoot(null);

        void Update()
        {
            ResolveReferences(force: false);
            SubscribeRoot(root);
            PruneRuns();

            if (Time.unscaledTime >= m_NextKeyCheck)
            {
                m_NextKeyCheck = Time.unscaledTime + 1f;
                m_HasGenpressoKey = ApiKeys.Has(ApiKeyKind.Genpresso);
            }

            if (TextInputFocused)
                return; // typing: no hotkeys

            if (devicePanelKey != KeyCode.None && InputCompat.GetKeyDown(devicePanelKey))
            {
                DevicePanelOpen = !DevicePanelOpen;
                if (DevicePanelOpen)
                    UnlockCursor();
            }
            if (!DevicePanelOpen)
                return;
            if (InputCompat.GetKeyDown(KeyCode.Escape))
                DevicePanelOpen = false;
            if (voiceAgent == null)
                return;
            if (InputCompat.GetKeyDown(KeyCode.Alpha0))
                voiceAgent.SetMicDevice("");
            var devices = MicCapture.Devices;
            for (int i = 0; i < Mathf.Min(9, devices.Length); ++i)
                if (InputCompat.GetKeyDown(KeyCode.Alpha1 + i))
                    voiceAgent.SetMicDevice(devices[i]);
        }

        void ResolveReferences(bool force)
        {
            if (voiceAgent != null && root != null)
                return;
            if (!force && Time.unscaledTime < m_NextLookup)
                return;
            m_NextLookup = Time.unscaledTime + kLookupIntervalSec;
            if (voiceAgent == null)
                voiceAgent = GetComponent<VoiceAgent>();
            if (voiceAgent == null)
                voiceAgent = FindFirstObjectByType<VoiceAgent>();
            if (root == null)
                root = GetComponent<SplatPressoRoot>();
            if (root == null)
                root = FindFirstObjectByType<SplatPressoRoot>();
        }

        static void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // ------------------------------------------------------------------------------------------
        // run list (from root events)

        void SubscribeRoot(SplatPressoRoot target)
        {
            if (ReferenceEquals(target, m_SubscribedRoot))
                return;
            if (!ReferenceEquals(m_SubscribedRoot, null))
            {
                m_SubscribedRoot.RunStarted -= OnRunStarted;
                m_SubscribedRoot.RunProgress -= OnRunProgress;
                m_SubscribedRoot.RunCompleted -= OnRunCompleted;
                m_SubscribedRoot.RunFailed -= OnRunFailed;
            }
            m_SubscribedRoot = target;
            if (target != null)
            {
                target.RunStarted += OnRunStarted;
                target.RunProgress += OnRunProgress;
                target.RunCompleted += OnRunCompleted;
                target.RunFailed += OnRunFailed;
            }
        }

        RunLine FindRun(string runId, bool create)
        {
            if (string.IsNullOrEmpty(runId))
                return null;
            foreach (var r in m_Runs)
                if (r.runId == runId)
                    return r;
            if (!create)
                return null;
            var line = new RunLine { runId = runId, label = ShortRunId(runId), stage = PlacementStage.Idle };
            m_Runs.Add(line);
            return line;
        }

        void OnRunStarted(RunStartedInfo info)
        {
            if (info == null)
                return;
            var line = FindRun(info.runId, create: true);
            if (line == null)
                return;
            var names = new List<string>();
            if (info.request?.objects != null)
                foreach (var o in info.request.objects)
                    if (o != null && !string.IsNullOrWhiteSpace(o.name))
                        names.Add(o.count > 1 ? $"{o.name} x{o.count}" : o.name);
            if (names.Count > 0)
                line.label = string.Join(", ", names);
            line.stage = PlacementStage.Capturing;
        }

        void OnRunProgress(PlacementProgress p)
        {
            var line = FindRun(p?.runId, create: true);
            if (line == null || line.endedAt >= 0f)
                return;
            line.stage = p.stage;
            line.message = p.message;
            line.done = p.objectsDone;
            line.total = p.objectsTotal;
        }

        void OnRunCompleted(PlacementResult result)
        {
            var line = FindRun(result?.runId, create: true);
            if (line == null)
                return;
            int placed = 0, total = 0;
            if (result.objects != null)
            {
                total = result.objects.Count;
                foreach (var o in result.objects)
                    if (o != null && o.status == ObjectStatus.Placed)
                        placed++;
            }
            line.stage = PlacementStage.Completed;
            line.done = placed;
            line.total = total;
            line.message = null;
            line.endedAt = Time.unscaledTime;
        }

        void OnRunFailed(PlacementFailure failure)
        {
            var line = FindRun(failure?.runId, create: true);
            if (line == null)
                return;
            line.cancelled = failure.kind == PlacementEndKind.Cancelled;
            line.failed = !line.cancelled;
            line.stage = line.cancelled ? PlacementStage.Cancelled : PlacementStage.Failed;
            line.message = failure.reason;
            line.endedAt = Time.unscaledTime;
        }

        void PruneRuns()
        {
            float now = Time.unscaledTime;
            m_Runs.RemoveAll(r => r.endedAt >= 0f && now - r.endedAt > (r.failed ? kFailedRunLingerSeconds : kRunLingerSeconds));
            while (m_Runs.Count > kMaxRunLines)
            {
                int idx = m_Runs.FindIndex(r => r.endedAt >= 0f);
                m_Runs.RemoveAt(idx >= 0 ? idx : 0);
            }
        }

        static string ShortRunId(string runId) => runId.Length > 8 ? "run " + runId.Substring(runId.Length - 6) : "run " + runId;

        static string StageLabel(PlacementStage stage)
        {
            switch (stage)
            {
                case PlacementStage.Idle: return "Starting";
                case PlacementStage.Capturing: return "Capturing the view";
                case PlacementStage.Deciding: return "Planning";
                case PlacementStage.Editing: return "Editing the image";
                case PlacementStage.Verifying: return "Verifying";
                case PlacementStage.DepthEstimating: return "Estimating depth";
                case PlacementStage.ProcessingObjects: return "Generating 3D";
                case PlacementStage.Placing: return "Placing";
                case PlacementStage.Completed: return "Done";
                case PlacementStage.Failed: return "Failed";
                case PlacementStage.Cancelled: return "Cancelled";
                default: return stage.ToString();
            }
        }

        // ------------------------------------------------------------------------------------------
        // IMGUI

        static Texture2D White()
        {
            if (s_White == null)
            {
                s_White = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "SplatPressoHudWhite" };
                s_White.SetPixel(0, 0, Color.white);
                s_White.Apply();
            }
            return s_White;
        }

        static void Fill(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, White());
            GUI.color = prev;
        }

        // Explicit text colors in every state: never rely on the default skin's colors (they differ between the
        // editor and player skins and some states are dark-on-dark).
        static void SetTextColor(GUIStyle s, Color c)
        {
            s.normal.textColor = c;
            s.hover.textColor = c;
            s.active.textColor = c;
            s.focused.textColor = c;
            s.onNormal.textColor = c;
            s.onHover.textColor = c;
            s.onActive.textColor = c;
            s.onFocused.textColor = c;
        }

        static void SetBackground(GUIStyle s, Texture2D t)
        {
            s.normal.background = t;
            s.hover.background = t;
            s.active.background = t;
            s.focused.background = t;
            s.onNormal.background = t;
            s.onHover.background = t;
            s.onActive.background = t;
            s.onFocused.background = t;
        }

        void EnsureStyles()
        {
            if (m_StylesReady && m_TitleStyle != null)
                return;
            m_TitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            SetTextColor(m_TitleStyle, kText);
            m_SmallStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            SetTextColor(m_SmallStyle, kTextDim);
            m_LineStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, wordWrap = false };
            SetTextColor(m_LineStyle, kText);
            m_BubbleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.UpperCenter, wordWrap = true };
            SetTextColor(m_BubbleStyle, kText);
            m_HeardStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Italic, alignment = TextAnchor.UpperCenter, wordWrap = true };
            SetTextColor(m_HeardStyle, kTextDim);
            m_ChipStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            SetTextColor(m_ChipStyle, kText);
            m_BannerStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            SetTextColor(m_BannerStyle, new Color(0.1f, 0.07f, 0f, 1f));
            m_ErrorStyle = new GUIStyle(m_SmallStyle);
            SetTextColor(m_ErrorStyle, kError);
            m_HeaderStyle = new GUIStyle(m_LineStyle) { fontStyle = FontStyle.Bold };
            SetTextColor(m_HeaderStyle, kText);
            m_RunStyle = new GUIStyle(m_LineStyle); // text color set per line
            m_HintStyle = new GUIStyle(m_LineStyle) { fontSize = 14 };
            SetTextColor(m_HintStyle, new Color(1f, 1f, 1f, 0.35f));

            m_ButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 2, 2) };
            SetTextColor(m_ButtonStyle, kText);
            m_ButtonStyle.active.textColor = new Color(0.75f, 1f, 0.8f);

            m_FieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 10, 4, 4), clipping = TextClipping.Clip };
            SetTextColor(m_FieldStyle, kText);
            SetBackground(m_FieldStyle, White());
            m_StylesReady = true;
        }

        float Scale => Mathf.Clamp(Screen.height / 1080f, 1f, 2f) * Mathf.Max(0.5f, uiScale);

        void OnGUI()
        {
            var e = Event.current;
            if (e == null)
                return;
            EnsureStyles();
            HandleTextKeys(e);

            float scale = Scale;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale;
            float h = Screen.height / scale;

            float y = DrawPill(w, h - 16f);
            if (showModeChips && root != null)
                y = DrawModeChips(w, y - 6f);
            y = DrawErrorLine(w, y - 4f);
            if (m_TextOpen)
                y = DrawTextInput(w, y - 6f);
            DrawSubtitle(w, y - 8f);
            if (showRunList)
                DrawRunList();
            if (showKeyBanner && !m_HasGenpressoKey)
                DrawKeyBanner(w);
            if (DevicePanelOpen)
                DrawDevicePanel(w);

            GUI.matrix = oldMatrix;
        }

        // ---- text input --------------------------------------------------------------------------

        void HandleTextKeys(Event e)
        {
            if (e.type != EventType.KeyDown)
                return;
            bool enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter;
            bool openKey = textInputKey != KeyCode.None &&
                           (e.keyCode == textInputKey || (textInputKey == KeyCode.Return && e.keyCode == KeyCode.KeypadEnter));
            if (!m_TextOpen)
            {
                if (openKey && !DevicePanelOpen)
                {
                    OpenTextInput();
                    e.Use();
                }
                return;
            }
            if (enter)
            {
                SubmitTextInput();
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                CloseTextInput();
                e.Use();
            }
        }

        void OpenTextInput()
        {
            m_TextOpen = true;
            m_Text = "";
            m_FocusRequests = 3;
            TextInputFocused = true;
            UnlockCursor();
        }

        void CloseTextInput()
        {
            m_TextOpen = false;
            m_FocusRequests = 0;
            TextInputFocused = false;
            GUIUtility.keyboardControl = 0;
        }

        void SubmitTextInput()
        {
            string text = (m_Text ?? "").Trim();
            CloseTextInput();
            if (text.Length == 0)
                return;
            if (root != null)
                root.SubmitText(text);
            else if (voiceAgent != null)
                voiceAgent.SubmitText(text);
            else
                Debug.LogWarning("[SplatPresso] Typed request ignored: no SplatPressoRoot or VoiceAgent in the scene");
        }

        float DrawTextInput(float w, float bottom)
        {
            const float fw = 560f, fh = 34f;
            var r = new Rect((w - fw) * 0.5f, bottom - fh - 16f, fw, fh);
            Fill(new Rect(r.x - 4f, r.y - 4f, r.width + 8f, r.height + 24f), new Color(0f, 0f, 0f, 0.6f));

            var prevColor = GUI.color;
            var prevBg = GUI.backgroundColor;
            var prevCursor = GUI.skin.settings.cursorColor;
            var prevSelection = GUI.skin.settings.selectionColor;
            GUI.backgroundColor = new Color(0.08f, 0.1f, 0.12f, 0.95f); // tints the white field background
            GUI.skin.settings.cursorColor = Color.white;
            GUI.skin.settings.selectionColor = new Color(0.25f, 0.55f, 0.9f, 0.6f);
            GUI.SetNextControlName(kTextControlName);
            m_Text = GUI.TextField(r, m_Text ?? "", kMaxTextLength, m_FieldStyle);
            if (m_FocusRequests > 0)
            {
                GUI.FocusControl(kTextControlName);
                if (Event.current.type == EventType.Repaint)
                    m_FocusRequests--;
            }
            GUI.skin.settings.cursorColor = prevCursor;
            GUI.skin.settings.selectionColor = prevSelection;
            GUI.backgroundColor = prevBg;
            GUI.color = prevColor;

            GUI.Label(new Rect(r.x, r.yMax + 2f, r.width, 16f), "Type a request - Enter sends, Esc cancels", m_SmallStyle);
            if (string.IsNullOrEmpty(m_Text))
                GUI.Label(new Rect(r.x + 12f, r.y, r.width - 24f, r.height), "e.g. \"put a green armchair next to the window\"", m_HintStyle);
            return r.y - 4f;
        }

        // ---- pill ---------------------------------------------------------------------------------

        static string KeyLabel(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Return: return "Enter";
                case KeyCode.KeypadEnter: return "Enter";
                case KeyCode.None: return "-";
                default:
                    if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                        return ((int)(key - KeyCode.Alpha0)).ToString();
                    return key.ToString();
            }
        }

        float DrawPill(float w, float bottom)
        {
            const float pw = 300f, ph = 46f;
            float x = (w - pw) * 0.5f;
            float y = bottom - ph;
            var rect = new Rect(x, y, pw, ph);
            var titleRect = new Rect(x + 8f, y + 4f, pw - 16f, 20f);
            var subRect = new Rect(x + 8f, y + ph - 20f, pw - 16f, 16f);

            string ptt = KeyLabel(voiceAgent != null ? voiceAgent.PushToTalkKey : KeyCode.Space);
            string typeHint = textInputKey != KeyCode.None ? $"[{KeyLabel(textInputKey)}] type" : null;
            string micHint = devicePanelKey != KeyCode.None ? $"[{KeyLabel(devicePanelKey)}] mic" : null;

            if (voiceAgent == null)
            {
                Fill(rect, new Color(0.15f, 0.15f, 0.15f, 0.7f));
                GUI.Label(titleRect, "No voice agent in the scene", m_TitleStyle);
                GUI.Label(subRect, "Run SplatPresso > Setup Scene", m_SmallStyle);
                return y;
            }

            if (!voiceAgent.IsRunning || voiceAgent.ActiveBackend == VoiceBackendKind.None)
            {
                Fill(rect, new Color(0.15f, 0.15f, 0.15f, 0.7f));
                GUI.Label(titleRect, voiceAgent.IsRunning ? "Voice input off" : "Voice agent stopped", m_TitleStyle);
                GUI.Label(subRect, typeHint != null ? typeHint + " a request" : "", m_SmallStyle);
                return y;
            }

            bool handsFree = voiceAgent.IsHandsFree;
            bool recording = voiceAgent.IsRecording && !handsFree;

            if (recording)
            {
                Fill(rect, new Color(0.35f, 0.02f, 0.02f, 0.85f));
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 7f);
                Fill(new Rect(x + 14f, y + 9f, 12f, 12f), new Color(1f, 0.2f, 0.2f, pulse));
                GUI.Label(titleRect, $"Listening...  {voiceAgent.RecordingSeconds:0.0}s", m_TitleStyle);
                DrawLevelBar(new Rect(x + 30f, y + ph - 14f, pw - 60f, 7f), voiceAgent.MicLevel);
                return y;
            }

            if (!voiceAgent.IsReady)
            {
                Fill(rect, new Color(0.15f, 0.15f, 0.15f, 0.75f));
                if (voiceAgent.ActiveBackend == VoiceBackendKind.OpenAIRealtime)
                {
                    bool failed = voiceAgent.RealtimeBackend != null && voiceAgent.RealtimeBackend.HasFailed;
                    GUI.Label(titleRect, failed ? "Voice agent offline" : "Connecting voice agent...", m_TitleStyle);
                    GUI.Label(subRect, failed ? "Check the OpenAI key and model" : "OpenAI Realtime", m_SmallStyle);
                }
                else
                {
                    GUI.Label(titleRect, "Voice needs a GenPresso key", m_TitleStyle);
                    GUI.Label(subRect, "Project Settings > SplatPresso", m_SmallStyle);
                }
                return y;
            }

            if (voiceAgent.IsSpeaking)
            {
                Fill(rect, new Color(0f, 0.12f, 0.2f, 0.7f));
                GUI.Label(titleRect, "Agent responding...", m_TitleStyle);
                GUI.Label(subRect, handsFree ? "Just talk to interrupt" : $"Press [{ptt}] to interrupt", m_SmallStyle);
                return y;
            }

            if (handsFree)
            {
                Fill(rect, new Color(0f, 0f, 0f, 0.55f));
                GUI.Label(titleRect, voiceAgent.IsBusy ? "Thinking" + Dots() : "Listening (hands-free)", m_TitleStyle);
                DrawLevelBar(new Rect(x + 30f, y + ph - 14f, pw - 60f, 7f), voiceAgent.MicLevel);
                return y;
            }

            Fill(rect, new Color(0f, 0f, 0f, 0.55f));
            if (voiceAgent.IsBusy)
            {
                GUI.Label(titleRect, "Thinking" + Dots(), m_TitleStyle);
                GUI.Label(subRect, $"Hold [{ptt}] to add another request", m_SmallStyle);
            }
            else
            {
                GUI.Label(titleRect, voiceAgent.CanTalk ? $"Hold [{ptt}] to talk" : "No microphone", m_TitleStyle);
                string sub = JoinHints(typeHint, micHint);
                GUI.Label(subRect, sub, m_SmallStyle);
            }
            return y;
        }

        static string JoinHints(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + "  \u00B7  " + b;
        }

        static string Dots()
        {
            int n = 1 + (int)(Time.unscaledTime * 2.5f) % 3;
            return new string('.', n);
        }

        static void DrawLevelBar(Rect r, float micLevel)
        {
            float level = Mathf.Clamp01(micLevel * 1.6f);
            Fill(r, new Color(1f, 1f, 1f, 0.18f));
            Fill(new Rect(r.x, r.y, r.width * level, r.height), level > 0.03f ? new Color(0.3f, 1f, 0.4f, 0.95f) : new Color(1f, 0.8f, 0.2f, 0.9f));
        }

        // ---- mode chips ---------------------------------------------------------------------------

        float DrawModeChips(float w, float bottom)
        {
            const float ch = 24f, segW = 92f, keyW = 30f, gap = 12f;
            float groupW = keyW + segW * 2f;
            float total = groupW * 2f + gap;
            float x = (w - total) * 0.5f;
            float y = bottom - ch;

            bool direct = root.Mode == GenerationMode.DirectTextTo3D;
            int picked = DrawSegmented(new Rect(x, y, groupW, ch), "[M]", "Scene-aware", "Direct", direct ? 1 : 0);
            if (picked >= 0)
                root.Mode = picked == 1 ? GenerationMode.DirectTextTo3D : GenerationMode.SceneContextual;

            bool mesh = root.Representation == ObjectRepresentation.Mesh;
            picked = DrawSegmented(new Rect(x + groupW + gap, y, groupW, ch), "[N]", "Splat", "Mesh", mesh ? 1 : 0);
            if (picked >= 0)
                root.Representation = picked == 1 ? ObjectRepresentation.Mesh : ObjectRepresentation.GaussianSplat;
            return y;
        }

        // Returns the clicked segment (0/1) or -1.
        int DrawSegmented(Rect r, string key, string a, string b, int active)
        {
            const float keyW = 30f;
            float segW = (r.width - keyW) * 0.5f;
            Fill(r, new Color(0f, 0f, 0f, 0.55f));
            GUI.Label(new Rect(r.x, r.y, keyW, r.height), key, m_ChipStyle);
            int clicked = -1;
            for (int i = 0; i < 2; i++)
            {
                var seg = new Rect(r.x + keyW + segW * i + 1f, r.y + 2f, segW - 2f, r.height - 4f);
                if (i == active)
                    Fill(seg, new Color(kAccent.r, kAccent.g, kAccent.b, 0.45f));
                GUI.Label(seg, i == 0 ? a : b, m_ChipStyle);
                if (i != active && GUI.Button(seg, GUIContent.none, GUIStyle.none))
                    clicked = i;
            }
            return clicked;
        }

        // ---- error line / subtitle ----------------------------------------------------------------

        float DrawErrorLine(float w, float bottom)
        {
            if (voiceAgent == null || string.IsNullOrEmpty(voiceAgent.LastError) || Time.unscaledTime - voiceAgent.LastErrorTime > kErrorSeconds)
                return bottom;
            const float lh = 18f;
            float ew = Mathf.Min(720f, w - 32f);
            var r = new Rect((w - ew) * 0.5f, bottom - lh, ew, lh);
            Fill(r, new Color(0.2f, 0f, 0f, 0.6f));
            GUI.Label(r, voiceAgent.LastError, m_ErrorStyle);
            return r.y;
        }

        void DrawSubtitle(float w, float bottom)
        {
            if (voiceAgent == null)
                return;
            float now = Time.unscaledTime;
            string reply = voiceAgent.LastAgentReply;
            float replyAge = now - voiceAgent.LastAgentReplyTime;
            bool showReply = !string.IsNullOrEmpty(reply) && replyAge < replySeconds;
            string heard = voiceAgent.LastHeardTranscript;
            float heardAge = now - voiceAgent.LastHeardTime;
            bool showHeard = showHeardLine && !string.IsNullOrEmpty(heard) && heardAge < replySeconds;
            // the user spoke again after this reply: the old reply is stale until the new one arrives
            if (showHeard && showReply && voiceAgent.LastHeardTime > voiceAgent.LastAgentReplyTime + 0.25f)
                showReply = false;
            if (!showReply && !showHeard)
                return;

            float age = showReply ? Mathf.Min(replyAge, heardAge) : heardAge;
            float alpha = Mathf.Clamp01(replySeconds - age); // fade out during the last second

            float bw = Mathf.Min(680f, w - 32f);
            float innerW = bw - 24f;
            string heardText = showHeard ? "Heard: \u201C" + heard + "\u201D" : null;
            float heardH = showHeard ? m_HeardStyle.CalcHeight(new GUIContent(heardText), innerW) : 0f;
            float replyH = showReply ? m_BubbleStyle.CalcHeight(new GUIContent(reply), innerW) : 0f;
            float bh = 12f + heardH + (showHeard && showReply ? 4f : 0f) + replyH;
            var r = new Rect((w - bw) * 0.5f, bottom - bh, bw, bh);

            var prev = GUI.color;
            Fill(r, new Color(0f, 0f, 0f, 0.62f * alpha));
            GUI.color = new Color(1f, 1f, 1f, alpha);
            float cy = r.y + 6f;
            if (showHeard)
            {
                GUI.Label(new Rect(r.x + 12f, cy, innerW, heardH), heardText, m_HeardStyle);
                cy += heardH + 4f;
            }
            if (showReply)
                GUI.Label(new Rect(r.x + 12f, cy, innerW, replyH), reply, m_BubbleStyle);
            GUI.color = prev;
        }

        // ---- run list -----------------------------------------------------------------------------

        void DrawRunList()
        {
            int active = root != null ? root.ActiveRunCount : 0;
            if (m_Runs.Count == 0 && active == 0)
                return;
            const float lw = 420f, lh = 20f;
            float x = 16f, y = 16f;
            int lines = m_Runs.Count + 1;
            Fill(new Rect(x - 6f, y - 4f, lw + 12f, lines * lh + 8f), new Color(0f, 0f, 0f, 0.5f));
            GUI.Label(new Rect(x, y, lw, lh), active > 0 ? $"Generating ({active})" : "Generations", m_HeaderStyle);
            y += lh;
            foreach (var r in m_Runs)
            {
                Color c = r.failed ? kError : r.cancelled ? new Color(0.7f, 0.7f, 0.7f, 1f) : r.stage == PlacementStage.Completed ? kOk : kText;
                string counts = r.total > 0 ? $" {r.done}/{r.total}" : "";
                string status = StageLabel(r.stage) + counts;
                if ((r.failed || r.cancelled) && !string.IsNullOrEmpty(r.message))
                    status += ": " + r.message;
                SetTextColor(m_RunStyle, c);
                string dot = r.endedAt >= 0f ? "\u25CF" : (((int)(Time.unscaledTime * 2f) % 2) == 0 ? "\u25CF" : "\u25CB");
                GUI.Label(new Rect(x, y, lw, lh), $"{dot}  {r.label}  \u2014  {status}", m_RunStyle);
                y += lh;
            }
        }

        // ---- key banner ---------------------------------------------------------------------------

        void DrawKeyBanner(float w)
        {
            float bw = Mathf.Min(640f, w - 32f);
            var r = new Rect((w - bw) * 0.5f, 16f, bw, 40f);
            Fill(r, new Color(1f, 0.78f, 0.2f, 0.92f));
            GUI.Label(new Rect(r.x + 8f, r.y, r.width - 16f, r.height),
                "GenPresso API key missing - set it in Project Settings > SplatPresso (or the GENPRESSO_API_KEY environment variable).",
                m_BannerStyle);
        }

        // ---- microphone picker --------------------------------------------------------------------

        void DrawDevicePanel(float w)
        {
            var devices = MicCapture.Devices;
            const float pw = 440f;
            float ph = 122f + devices.Length * 26f;
            var panel = new Rect(w - pw - 16f, 64f, pw, ph);
            Fill(panel, new Color(0f, 0f, 0f, 0.82f));

            GUILayout.BeginArea(new Rect(panel.x + 10f, panel.y + 8f, panel.width - 20f, panel.height - 16f));
            GUILayout.Label($"Microphone  -  [{KeyLabel(devicePanelKey)}] close, [0-9] or click to select", m_TitleStyle);
            string active = voiceAgent != null ? voiceAgent.ActiveMicDevice : "?";
            GUILayout.Label($"Active: {active}", m_SmallStyle);

            // live level of the active device: speak and watch which device responds
            var bar = GUILayoutUtility.GetRect(10f, 8f, GUILayout.ExpandWidth(true));
            DrawLevelBar(bar, voiceAgent != null ? voiceAgent.MicLevel : 0f);
            GUILayout.Space(4f);

            if (voiceAgent == null)
            {
                GUILayout.Label("(no voice agent)", m_SmallStyle);
            }
            else
            {
                bool usingDefault = voiceAgent.mic != null && voiceAgent.mic.UsingDefaultDevice;
                if (GUILayout.Button($"0  {(usingDefault ? "\u25B6 " : "")}System default", m_ButtonStyle, GUILayout.Height(22f)))
                    voiceAgent.SetMicDevice("");
                if (devices.Length == 0)
                {
                    GUILayout.Label("(no microphone devices found)", m_SmallStyle);
                }
                else
                {
                    for (int i = 0; i < devices.Length; ++i)
                    {
                        bool isActive = !usingDefault && devices[i] == active;
                        string label = $"{(i < 9 ? (i + 1).ToString() : "\u00B7")}  {(isActive ? "\u25B6 " : "")}{devices[i]}";
                        if (GUILayout.Button(label, m_ButtonStyle, GUILayout.Height(22f)))
                            voiceAgent.SetMicDevice(devices[i]);
                    }
                }
            }
            GUILayout.EndArea();
        }
    }
}
