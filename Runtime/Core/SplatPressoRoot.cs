using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SplatPresso.Placement;
using SplatPresso.Voice;
using UnityEngine;

namespace SplatPresso
{
    /// <summary>
    /// Scene entry point. Creates one <see cref="PlacementOrchestrator"/> per run (several runs may generate at once,
    /// capped by <see cref="SplatPressoSettings.maxConcurrentRuns"/>), wires the voice agent to the pipeline and relays
    /// the pipeline's milestones back to the voice agent.
    /// </summary>
    /// <remarks>
    /// Scripting: <see cref="StartRun"/> (fire and forget) or <see cref="RunAsync"/> (awaitable), plus the aggregated
    /// run events. Every event carries a run id (the session folder name).
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10)]
    public sealed class SplatPressoRoot : MonoBehaviour
    {
        [Tooltip("Settings asset (empty = SplatPressoSettings.Active, i.e. Resources/SplatPressoSettings).")]
        public SplatPressoSettings settings;
        [Tooltip("Captures the view (RGB + depth + pose). Auto-found when empty.")]
        public CaptureService captureService;
        [Tooltip("Spawns the generated objects. Auto-found when empty; without it nothing is placed.")]
        public ObjectSpawnService spawnService;
        [Tooltip("Optional; the pipeline runs without voice when absent (use StartRun / SubmitText).")]
        public VoiceAgent voiceAgent;
        [Tooltip("Optional; shows glowing placeholder boxes with progress at the planned positions.")]
        public PlacementPreviewService previewService;
        [Tooltip("Start the voice backend in Start().")]
        public bool startVoiceOnStart = true;
        [Tooltip("M toggles Scene-aware / Direct generation, N toggles Gaussian splat / mesh (suppressed while typing).")]
        public bool enableModeHotkeys = true;

        /// <summary>Hotkey that toggles <see cref="Mode"/>.</summary>
        public const KeyCode ModeToggleKey = KeyCode.M;
        /// <summary>Hotkey that toggles <see cref="Representation"/>.</summary>
        public const KeyCode RepresentationToggleKey = KeyCode.N;

        const int kNarrationReasonMaxChars = 160;

        GenerationMode m_Mode;
        ObjectRepresentation m_Representation;
        bool m_ModeStateInitialized;
        readonly List<PlacementOrchestrator> m_ActiveRuns = new List<PlacementOrchestrator>();
        VoiceAgent m_SubscribedVoice;
        bool m_Destroying;
        bool m_KeyProblemNarrated;
        CancelBatch m_CancelBatch;

        // A CancelAll over several runs is narrated once, after all of them ended.
        sealed class CancelBatch
        {
            public readonly HashSet<string> pending = new HashSet<string>();
            public int cancelled;
        }

        // ------------------------------------------------------------------------------------------
        // Public API

        /// <summary>
        /// Generation mode for NEW runs (running generations and replays keep theirs). Starts at
        /// <see cref="SplatPressoSettings.defaultMode"/>.
        /// </summary>
        public GenerationMode Mode
        {
            get { InitModeState(); return m_Mode; }
            set
            {
                InitModeState();
                if (m_Mode == value)
                    return;
                m_Mode = value;
                Debug.Log($"[SplatPresso] Generation mode: {value} (running generations keep their mode)");
                RaiseSafe(ModeChanged, value, nameof(ModeChanged));
            }
        }

        /// <summary>
        /// What NEW runs generate: Gaussian splats (TripoSplat) or meshes (Rodin, needs glTFast). Starts at
        /// <see cref="SplatPressoSettings.representation"/>.
        /// </summary>
        public ObjectRepresentation Representation
        {
            get { InitModeState(); return m_Representation; }
            set
            {
                InitModeState();
                if (m_Representation == value)
                    return;
                m_Representation = value;
                Debug.Log($"[SplatPresso] Object representation: {value} (running generations keep theirs)");
                if (value == ObjectRepresentation.Mesh && !MeshSpawnerRegistry.IsAvailable)
                    Debug.LogWarning("[SplatPresso] Mesh mode needs glTFast: install it via SplatPresso > Install or Repair Dependencies.");
                RaiseSafe(RepresentationChanged, value, nameof(RepresentationChanged));
            }
        }

        /// <summary>Raised when <see cref="Mode"/> changes.</summary>
        public event Action<GenerationMode> ModeChanged;
        /// <summary>Raised when <see cref="Representation"/> changes.</summary>
        public event Action<ObjectRepresentation> RepresentationChanged;

        /// <summary>Number of runs currently generating.</summary>
        public int ActiveRunCount => m_ActiveRuns.Count;
        /// <summary>The orchestrators of the runs currently generating.</summary>
        public IReadOnlyList<PlacementOrchestrator> ActiveRuns => m_ActiveRuns;
        /// <summary>The most recent run's orchestrator (an idle one before the first run), for status displays.</summary>
        public PlacementOrchestrator LatestOrchestrator { get; private set; }

        /// <summary>
        /// Optional gate for incoming requests (e.g. ignore speech while a modal UI is open). Return false to drop the
        /// request; <see cref="RequestRejected"/> is raised with <see cref="RequestRejectReason.Gated"/>.
        /// </summary>
        public Func<PlacementRequest, bool> RequestGate;

        /// <summary>A run (new request or replay) started.</summary>
        public event Action<RunStartedInfo> RunStarted;
        /// <summary>Every progress tick of every run.</summary>
        public event Action<PlacementProgress> RunProgress;
        /// <summary>A run placed its objects.</summary>
        public event Action<PlacementResult> RunCompleted;
        /// <summary>A run was cancelled or failed.</summary>
        public event Action<PlacementFailure> RunFailed;
        /// <summary>Per-object sub-stage changes of every run (see <see cref="PlacementOrchestrator.SubStages"/>).</summary>
        public event Action<PlacedObjectResult, string> ObjectUpdated;
        /// <summary>A step of some run is being retried.</summary>
        public event Action<RetryInfo> RunRetry;
        /// <summary>A run entered a new stage (run id, stage).</summary>
        public event Action<string, PlacementStage> RunStageEntered;
        /// <summary>A request was refused before a run started (the request is null when a null request was passed).</summary>
        public event Action<PlacementRequest, RequestRejectReason> RequestRejected;

        /// <summary>
        /// Starts a run for <paramref name="request"/> (fire and forget). Returns the run id (session folder name), or
        /// null when the request was rejected (see <see cref="RequestRejected"/>). Results arrive through
        /// <see cref="RunCompleted"/> / <see cref="RunFailed"/>.
        /// </summary>
        /// <param name="sourceUtterance">The spoken/typed text that produced the request (for logs), if any.</param>
        public string StartRun(PlacementRequest request, string sourceUtterance = null)
        {
            if (!TryBeginRun(request, sourceUtterance, out var orch, out var session))
                return null;
            RunDetached(orch, request, session, StartStage.Capture, destroyCancellationToken);
            return session.RunId;
        }

        /// <summary>
        /// Awaitable variant of <see cref="StartRun"/>: returns the result, or null when the request was rejected or the
        /// run failed/was cancelled (details via <see cref="RequestRejected"/> / <see cref="RunFailed"/>).
        /// </summary>
        public async Awaitable<PlacementResult> RunAsync(PlacementRequest request, string sourceUtterance, CancellationToken ct)
        {
            if (!TryBeginRun(request, sourceUtterance, out var orch, out var session))
                return null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, destroyCancellationToken))
                return await RunOnAsync(orch, request, session, StartStage.Capture, linked.Token);
        }

        /// <summary>
        /// Re-runs a previous session from <paramref name="from"/>, reusing the artifacts of the earlier stages (and the
        /// session's mode and representation). Fire and forget.
        /// </summary>
        public void StartReplay(string sessionDir, StartStage from)
        {
            if (TryBeginReplay(sessionDir, from, out var orch, out var session))
                RunDetached(orch, null, session, from, destroyCancellationToken);
        }

        /// <summary>Awaitable variant of <see cref="StartReplay"/> (null when rejected, failed or cancelled).</summary>
        public async Awaitable<PlacementResult> ReplayAsync(string sessionDir, StartStage from, CancellationToken ct)
        {
            if (!TryBeginReplay(sessionDir, from, out var orch, out var session))
                return null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, destroyCancellationToken))
                return await RunOnAsync(orch, null, session, from, linked.Token);
        }

        /// <summary>Cancels every running generation (narrated once).</summary>
        public void CancelAll()
        {
            var running = new List<PlacementOrchestrator>();
            foreach (var orch in m_ActiveRuns)
                if (orch.IsRunning)
                    running.Add(orch);
            if (running.Count == 0)
                return;
            if (running.Count > 1 && !m_Destroying)
            {
                m_CancelBatch ??= new CancelBatch();
                foreach (var orch in running)
                    m_CancelBatch.pending.Add(orch.RunId);
            }
            foreach (var orch in running)
                orch.Cancel();
        }

        /// <summary>Cancels one run by id (no-op when it is not running).</summary>
        public void Cancel(string runId)
        {
            foreach (var orch in m_ActiveRuns)
            {
                if (orch.RunId == runId && orch.IsRunning)
                {
                    orch.Cancel();
                    return;
                }
            }
            Debug.Log($"[SplatPresso] Cancel: run '{runId}' is not running");
        }

        /// <summary>Relays a message to the user through the voice agent (subtitle/voice, per its narration mode).</summary>
        public void Narrate(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || m_Destroying)
                return;
            if (voiceAgent != null)
                voiceAgent.NotifyPipeline(message, true);
        }

        /// <summary>
        /// Sends a typed request through the voice agent's language-model turn (as if spoken). Without a voice agent,
        /// build a <see cref="PlacementRequest"/> and call <see cref="StartRun"/> instead.
        /// </summary>
        public void SubmitText(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return;
            if (voiceAgent == null && Application.isPlaying)
                ResolveReferences();
            if (voiceAgent == null)
            {
                Debug.LogWarning("[SplatPresso] SubmitText needs a VoiceAgent (its language model turns text into a request). Use StartRun(PlacementRequest) instead.");
                return;
            }
            voiceAgent.SubmitText(userText);
        }

        /// <summary>"{stage}: {message} ({done}/{total} objects) | ~{cost} credits".</summary>
        public static string FormatProgress(PlacementProgress p)
        {
            if (p == null)
                return "";
            string s = $"{p.stage}: {p.message}";
            if (p.objectsTotal > 0)
                s += $" ({p.objectsDone}/{p.objectsTotal} objects)";
            s += $" | ~{p.costSoFar:0.##} credits";
            return s;
        }

        // ------------------------------------------------------------------------------------------
        // Unity lifecycle

        void Awake()
        {
            if (settings != null)
                SplatPressoSettings.Active = settings;
            ResolveReferences();
            if (captureService == null)
                Debug.LogError("[SplatPresso] No CaptureService found; runs will fail at the capture stage. Run SplatPresso > Setup Scene.");
            if (spawnService == null)
                Debug.LogWarning("[SplatPresso] No ObjectSpawnService found; generated objects will not be placed.");
            InitModeState();
            Application.runInBackground = Settings.runInBackground;
            LatestOrchestrator ??= CreateOrchestrator(); // idle instance so status UIs always have one
            SyncVoiceSubscription();
        }

        void Start()
        {
            SyncVoiceSubscription();
            if (startVoiceOnStart && voiceAgent != null)
                voiceAgent.StartBackend();
        }

        void Update()
        {
            SyncVoiceSubscription(); // the voice agent may be assigned or replaced at runtime
            if (!enableModeHotkeys || VoiceHud.TextInputFocused)
                return;
            if (InputCompat.GetKeyDown(ModeToggleKey))
                Mode = Mode == GenerationMode.SceneContextual ? GenerationMode.DirectTextTo3D : GenerationMode.SceneContextual;
            if (InputCompat.GetKeyDown(RepresentationToggleKey))
                Representation = Representation == ObjectRepresentation.GaussianSplat ? ObjectRepresentation.Mesh : ObjectRepresentation.GaussianSplat;
        }

        void OnDestroy()
        {
            m_Destroying = true;
            Unsubscribe();
            CancelAll();
        }

        /// <summary>The settings in use: the assigned asset, else <see cref="SplatPressoSettings.Active"/>.</summary>
        public SplatPressoSettings Settings => settings != null ? settings : SplatPressoSettings.Active;

        // Fills missing references (GetComponent, then the scene). Runs in Awake and again before every run, so
        // components that are added or assigned later (e.g. by scripts or tests) are picked up.
        void ResolveReferences()
        {
            if (captureService == null) captureService = GetComponent<CaptureService>();
            if (captureService == null) captureService = FindFirstObjectByType<CaptureService>();
            if (spawnService == null) spawnService = GetComponent<ObjectSpawnService>();
            if (spawnService == null) spawnService = FindFirstObjectByType<ObjectSpawnService>();
            if (voiceAgent == null) voiceAgent = GetComponent<VoiceAgent>();
            if (voiceAgent == null) voiceAgent = FindFirstObjectByType<VoiceAgent>();
            if (previewService == null) previewService = GetComponent<PlacementPreviewService>();
            if (previewService == null) previewService = FindFirstObjectByType<PlacementPreviewService>();
            if (spawnService != null && spawnService.previewService == null && previewService != null)
                spawnService.previewService = previewService;
        }

        // Mode and representation start from the settings; reading them has no other side effect (safe in edit mode).
        void InitModeState()
        {
            if (m_ModeStateInitialized)
                return;
            m_ModeStateInitialized = true;
            var s = Settings;
            m_Mode = s.defaultMode;
            m_Representation = s.representation;
        }

        void SyncVoiceSubscription()
        {
            if (m_Destroying)
                return;
            var current = voiceAgent != null ? voiceAgent : null;
            if (ReferenceEquals(current, m_SubscribedVoice))
                return;
            Unsubscribe();
            if (current == null)
                return;
            current.PlacementRequested += HandlePlacementRequested;
            current.CancelRequested += HandleCancelRequested;
            m_SubscribedVoice = current;
        }

        void Unsubscribe()
        {
            if (ReferenceEquals(m_SubscribedVoice, null))
                return;
            m_SubscribedVoice.PlacementRequested -= HandlePlacementRequested;
            m_SubscribedVoice.CancelRequested -= HandleCancelRequested;
            m_SubscribedVoice = null;
        }

        // ------------------------------------------------------------------------------------------
        // Run plumbing

        bool TryBeginRun(PlacementRequest request, string sourceUtterance, out PlacementOrchestrator orch, out PipelineSession session)
        {
            orch = null;
            session = null;
            if (!PrepareForRun())
                return false;

            string invalidReason = null;
            if (request == null || !request.IsValid(out invalidReason))
            {
                Debug.LogWarning($"[SplatPresso] Placement request ignored: {invalidReason ?? "no request"}");
                RaiseRejected(request, RequestRejectReason.InvalidRequest);
                return false;
            }

            if (RequestGate != null)
            {
                bool allowed;
                try { allowed = RequestGate(request); }
                catch (Exception e)
                {
                    Debug.LogError($"[SplatPresso] RequestGate threw; dropping the request: {e}");
                    allowed = false;
                }
                if (!allowed)
                {
                    Debug.Log("[SplatPresso] Placement request dropped by RequestGate");
                    RaiseRejected(request, RequestRejectReason.Gated);
                    return false;
                }
            }

            if (!CheckKeys(out string keyProblem))
            {
                Debug.LogError("[SplatPresso] " + keyProblem);
                RaiseRejected(request, RequestRejectReason.MissingKey);
                if (!m_KeyProblemNarrated)
                {
                    m_KeyProblemNarrated = true;
                    Narrate(keyProblem);
                }
                return false;
            }

            if (!HasCapacity())
            {
                RaiseRejected(request, RequestRejectReason.Capacity);
                return false;
            }

            try
            {
                session = PipelineSession.CreateNew(PipelineSession.ResolveSessionsRoot(Settings));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SplatPresso] Could not create a session folder: {e.Message}");
                return false;
            }

            m_KeyProblemNarrated = false;
            orch = AcquireOrchestrator();
            Debug.Log($"[SplatPresso] Starting run {session.RunId} ({m_ActiveRuns.Count} active, {orch.Mode}, {orch.Representation}), session {session.Dir}");
            RaiseSafe(RunStarted, new RunStartedInfo
            {
                runId = session.RunId,
                sessionDir = session.Dir,
                request = request,
                mode = orch.Mode,
                representation = orch.Representation,
                sourceUtterance = sourceUtterance,
            }, nameof(RunStarted));
            return true;
        }

        bool TryBeginReplay(string sessionDir, StartStage from, out PlacementOrchestrator orch, out PipelineSession session)
        {
            orch = null;
            session = null;
            if (!PrepareForRun())
                return false;
            if (string.IsNullOrEmpty(sessionDir))
            {
                Debug.LogWarning("[SplatPresso] No session directory to replay");
                return false;
            }
            // Two orchestrators on one folder would overwrite each other's artifacts and ledger (and the replay would
            // discard the live run's caches); both would also report the same run id.
            if (IsSessionRunning(sessionDir))
            {
                Debug.LogWarning($"[SplatPresso] Session {sessionDir} is still running; replay refused");
                Narrate("That generation is still running.");
                return false;
            }
            if (from != StartStage.Place && !CheckKeys(out string keyProblem))
            {
                Debug.LogError("[SplatPresso] " + keyProblem);
                return false;
            }
            if (!HasCapacity())
                return false;
            try
            {
                session = PipelineSession.Open(sessionDir);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SplatPresso] Replay failed to start: {e.Message}");
                return false;
            }

            orch = AcquireOrchestrator();
            // Replays keep the session's stored mode/representation; resolve them now so RunStarted is accurate.
            orch.Mode = PlacementOrchestrator.ResolveMode(session, from, orch.Mode);
            orch.Representation = PlacementOrchestrator.ResolveRepresentation(session, from, orch.Representation);
            PlacementRequest storedRequest = null;
            try { storedRequest = session.LoadJson<PlacementRequest>(PipelineSession.RequestJson); }
            catch (Exception e) { Debug.LogWarning($"[SplatPresso] Could not read the stored request: {e.Message}"); }

            Debug.Log($"[SplatPresso] Replaying session {session.Dir} from {from}");
            RaiseSafe(RunStarted, new RunStartedInfo
            {
                runId = session.RunId,
                sessionDir = session.Dir,
                request = storedRequest,
                mode = orch.Mode,
                representation = orch.Representation,
                sourceUtterance = null,
            }, nameof(RunStarted));
            return true;
        }

        // True when an active run works in sessionDir. SessionDir is set synchronously when a run starts, before its
        // first await, so a run started earlier in the same frame is already covered.
        bool IsSessionRunning(string sessionDir)
        {
            string target = NormalizeDir(sessionDir);
            foreach (var orch in m_ActiveRuns)
            {
                if (!string.IsNullOrEmpty(orch.SessionDir) &&
                    string.Equals(NormalizeDir(orch.SessionDir), target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static string NormalizeDir(string dir)
        {
            try { return System.IO.Path.GetFullPath(dir).TrimEnd('/', '\\'); }
            catch (Exception) { return dir.TrimEnd('/', '\\'); }
        }

        bool PrepareForRun()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[SplatPresso] Placement runs need play mode.");
                return false;
            }
            if (m_Destroying)
                return false;
            ResolveReferences();
            InitModeState();
            SyncVoiceSubscription();
            return true;
        }

        bool CheckKeys(out string problem)
        {
            if (!ApiKeys.Has(ApiKeyKind.Genpresso))
            {
                problem = "GenPresso API key is missing. Set it in Project Settings > SplatPresso.";
                return false;
            }
            if (Settings.mediaProvider == MediaProvider.FalDirect && !ApiKeys.Has(ApiKeyKind.Fal))
            {
                problem = "fal.ai key (FAL_KEY) is missing, but the media provider is set to FalDirect. Set it in Project Settings > SplatPresso.";
                return false;
            }
            problem = null;
            return true;
        }

        bool HasCapacity()
        {
            int maxRuns = Mathf.Max(1, Settings.maxConcurrentRuns);
            if (m_ActiveRuns.Count < maxRuns)
                return true;
            Debug.LogWarning($"[SplatPresso] {m_ActiveRuns.Count} runs already generating (cap {maxRuns}); request ignored");
            Narrate($"Too many generations running ({m_ActiveRuns.Count}); ask again in a moment.");
            return false;
        }

        // One orchestrator per run: its event subscriptions (and the planned-preview list) belong to that run only.
        PlacementOrchestrator AcquireOrchestrator()
        {
            var orch = CreateOrchestrator();
            orch.Mode = m_Mode;
            orch.Representation = m_Representation;
            // Counted right away, so several requests in the same frame respect the capacity cap.
            m_ActiveRuns.Add(orch);
            LatestOrchestrator = orch;
            return orch;
        }

        PlacementOrchestrator CreateOrchestrator()
        {
            ICaptureProvider capture = captureService != null ? captureService : null;
            IObjectPlacer placer = spawnService != null ? spawnService : null; // null -> NullObjectPlacer
            var orch = new PlacementOrchestrator(Settings, capture, placer)
            {
                // Mesh runs placed by ObjectSpawnService need glTFast; fail before paying for unplaceable models.
                RequireMeshSpawner = spawnService != null,
            };
            List<PlacedObjectResult> planned = null;

            orch.OnProgress += p =>
            {
                // Only milestone events reach the voice conversation: injecting every progress tick as a [PIPELINE]
                // message pollutes the model's context and makes it keep bringing up pipeline history. Visual
                // progress lives in the HUD / previews.
                if (p.narrate)
                    Narrate(p.message);
                RaiseSafe(RunProgress, p, nameof(RunProgress));
            };
            orch.OnObjectsPlanned += (objs, cap) =>
            {
                planned = objs;
                if (previewService != null)
                    previewService.ShowPreviews(objs, cap);
            };
            orch.OnObjectUpdate += (o, s) =>
            {
                if (previewService != null)
                    previewService.UpdateObject(o, s);
                RaiseSafe(ObjectUpdated, o, s, nameof(ObjectUpdated));
            };
            orch.OnRetry += r => RaiseSafe(RunRetry, r, nameof(RunRetry));
            orch.OnStageEntered += (id, stage) => RaiseSafe(RunStageEntered, id, stage, nameof(RunStageEntered));
            orch.OnCompleted += r =>
            {
                HandleCompleted(r);
                if (previewService != null && r.objects != null)
                    previewService.RemoveObjects(r.objects); // the real objects replaced the placeholders
                RaiseSafe(RunCompleted, r, nameof(RunCompleted));
            };
            orch.OnFailed += f =>
            {
                HandleFailed(f);
                if (previewService != null && planned != null)
                    previewService.RemoveObjects(planned);
                RaiseSafe(RunFailed, f, nameof(RunFailed));
            };
            return orch;
        }

        async void RunDetached(PlacementOrchestrator orch, PlacementRequest request, PipelineSession session, StartStage from, CancellationToken ct)
        {
            await RunOnAsync(orch, request, session, from, ct); // never throws
        }

        async Awaitable<PlacementResult> RunOnAsync(PlacementOrchestrator orch, PlacementRequest request, PipelineSession session, StartStage from, CancellationToken ct)
        {
            if (!m_ActiveRuns.Contains(orch))
                m_ActiveRuns.Add(orch);
            try
            {
                return await orch.RunAsync(request, session, from, ct);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SplatPresso] Run {session?.RunId} crashed: {e}");
                return null;
            }
            finally
            {
                m_ActiveRuns.Remove(orch);
                // a run that ended without an event must not hold back the batched cancel narration
                if (m_CancelBatch != null && m_CancelBatch.pending.Remove(orch.RunId))
                    FlushCancelBatch();
            }
        }

        // ------------------------------------------------------------------------------------------
        // Voice / narration

        void HandlePlacementRequested(VoicePlacementRequest v)
        {
            if (v == null)
                return;
            StartRun(v.request, v.sourceUtterance);
        }

        void HandleCancelRequested() => CancelAll();

        void HandleCompleted(PlacementResult result)
        {
            if (m_CancelBatch != null && m_CancelBatch.pending.Remove(result.runId))
                FlushCancelBatch();
            string msg = CompletionNarration(result);
            Debug.Log($"[SplatPresso] {msg} (session {result.sessionDir})");
            Narrate(msg);
        }

        void HandleFailed(PlacementFailure f)
        {
            string msg = FailureNarration(f);
            Debug.LogWarning($"[SplatPresso] Run {f.runId}: {msg}");
            if (m_CancelBatch != null && m_CancelBatch.pending.Remove(f.runId))
            {
                if (f.kind == PlacementEndKind.Cancelled)
                {
                    m_CancelBatch.cancelled++;
                    FlushCancelBatch();
                    return; // narrated once for the whole batch
                }
                FlushCancelBatch();
            }
            Narrate(msg);
        }

        void FlushCancelBatch()
        {
            if (m_CancelBatch == null || m_CancelBatch.pending.Count > 0)
                return;
            int n = m_CancelBatch.cancelled;
            m_CancelBatch = null;
            if (n > 0)
                Narrate(n == 1 ? "Placement cancelled." : $"Cancelled {n} generations.");
        }

        string CompletionNarration(PlacementResult r)
        {
            var placed = new List<string>();
            var skipped = new List<string>();
            string firstSkipReason = null;
            if (r.objects != null)
            {
                foreach (var o in r.objects)
                {
                    if (o.status == ObjectStatus.Placed)
                    {
                        placed.Add(DisplayName(o));
                    }
                    else if (o.status == ObjectStatus.Skipped)
                    {
                        skipped.Add(DisplayName(o));
                        firstSkipReason ??= o.skipReason;
                    }
                }
            }
            string why = string.IsNullOrEmpty(firstSkipReason) ? "" : $" ({Shorten(firstSkipReason)})";
            if (placed.Count == 0)
                return skipped.Count == 0
                    ? "Placement finished, but there was nothing to place."
                    : $"Placement finished, but nothing could be placed: {JoinNames(skipped)}{why}.";
            if (skipped.Count == 0)
                return $"Placement complete: placed {JoinNames(placed)}.";
            return $"Placement complete: placed {JoinNames(placed)}; could not place {JoinNames(skipped)}{why}.";
        }

        string FailureNarration(PlacementFailure f)
        {
            switch (f.kind)
            {
                case PlacementEndKind.Cancelled:
                    return $"Placement cancelled while {StageLabel(f.stage)}.";
                case PlacementEndKind.CostCapExceeded:
                    return $"Placement stopped: the run reached its cost limit of {Settings.maxCostPerRun:0.#} credits.";
            }
            if (f.kind == PlacementEndKind.StageFailed && f.stage == PlacementStage.Deciding)
                return $"Cannot place that here: {Shorten(f.reason)}";
            if (f.stage == PlacementStage.Idle)
                return $"Placement could not start: {Shorten(f.reason)}";
            return $"Placement failed while {StageLabel(f.stage)}: {Shorten(f.reason)}";
        }

        static string StageLabel(PlacementStage stage)
        {
            switch (stage)
            {
                case PlacementStage.Idle: return "starting";
                case PlacementStage.Capturing: return "capturing the view";
                case PlacementStage.Deciding: return "planning the placement";
                case PlacementStage.Editing: return "editing the image";
                case PlacementStage.Verifying: return "verifying the edited image";
                case PlacementStage.DepthEstimating: return "estimating depth";
                case PlacementStage.ProcessingObjects: return "generating the objects";
                case PlacementStage.Placing: return "placing the objects";
                default: return stage.ToString();
            }
        }

        static string DisplayName(PlacedObjectResult o) => string.IsNullOrWhiteSpace(o.name) ? $"object {o.id}" : o.name.Trim();

        // "chair (x2), lamp and vase"
        static string JoinNames(List<string> names)
        {
            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                if (counts.TryGetValue(n, out int c))
                {
                    counts[n] = c + 1;
                }
                else
                {
                    counts[n] = 1;
                    order.Add(n);
                }
            }
            var sb = new StringBuilder();
            for (int i = 0; i < order.Count; ++i)
            {
                if (i > 0)
                    sb.Append(i == order.Count - 1 ? " and " : ", ");
                sb.Append(order[i]);
                if (counts[order[i]] > 1)
                    sb.Append(" (x").Append(counts[order[i]]).Append(')');
            }
            return sb.ToString();
        }

        static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "unknown reason";
            s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= kNarrationReasonMaxChars ? s : s.Substring(0, kNarrationReasonMaxChars) + "...";
        }

        void RaiseRejected(PlacementRequest request, RequestRejectReason reason) =>
            RaiseSafe(RequestRejected, request, reason, nameof(RequestRejected));

        // Subscriber exceptions are isolated: one faulty listener must not break the others or the pipeline.
        static void RaiseSafe<T>(Action<T> handlers, T arg, string eventName)
        {
            if (handlers == null)
                return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<T>)d)(arg); }
                catch (Exception e) { Debug.LogWarning($"[SplatPresso] {eventName} handler threw: {e}"); }
            }
        }

        static void RaiseSafe<T1, T2>(Action<T1, T2> handlers, T1 a, T2 b, string eventName)
        {
            if (handlers == null)
                return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<T1, T2>)d)(a, b); }
                catch (Exception e) { Debug.LogWarning($"[SplatPresso] {eventName} handler threw: {e}"); }
            }
        }
    }
}
