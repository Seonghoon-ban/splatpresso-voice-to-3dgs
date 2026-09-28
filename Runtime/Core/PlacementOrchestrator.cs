using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SplatPresso.Api;
using SplatPresso.Placement;
using UnityEngine;

namespace SplatPresso
{
    /// <summary>
    /// The pipeline spine of one run: Capture -> Decide -> Edit -> (hot Depth) -> Verify -> ProcessObjects -> Place
    /// (SceneContextual) or Capture -> Decide -> ProcessObjects -> Place (DirectTextTo3D).
    /// </summary>
    /// <remarks>
    /// Every stage caches its artifacts in the <see cref="PipelineSession"/>, so the pipeline can be replayed from any
    /// <see cref="StartStage"/> using a previous session's artifacts without re-spending API calls. One instance runs
    /// one run at a time; <see cref="SplatPressoRoot"/> creates one per concurrent run. All work (including the
    /// parallel per-object tasks) runs on Unity's main thread.
    /// </remarks>
    public sealed class PlacementOrchestrator
    {
        /// <summary>Per-object sub-stage strings raised through <see cref="OnObjectUpdate"/>.</summary>
        public static class SubStages
        {
            public const string Segmenting = "segmenting";
            public const string Cutout = "cutout";
            public const string Enhancing = "enhancing";
            public const string Enhanced = "enhanced";
            public const string TextToImage = "t2i";
            public const string Generating3D = "generating3d";
            public const string Downloading = "downloading";
            public const string Ready = "ready";
            public const string Skipped = "skipped";
        }

        const string kCancelledReason = "cancelled";
        const string kCostCapReason = "cost cap exceeded";
        // Data URIs are kept well below GenPresso's 4 MB request-body cap (base64 adds ~33%, the prompt needs room).
        const int kDataUriBudgetChars = 3000000;

        readonly SplatPressoSettings m_Settings;
        readonly ICaptureProvider m_CaptureProvider;
        readonly IObjectPlacer m_Placer;

        // per-run state
        CancellationTokenSource m_Cts;
        CostLedger m_Ledger;
        MediaJobClient m_Jobs;
        MediaEndpoints m_Media;
        PlacementDecisionService m_DecisionService;
        GenerationMode m_ActiveMode;
        ObjectRepresentation m_ActiveRep;

        // Hot depth-estimation task(s): restarted when the edit is retried, all joined before Placing.
        readonly List<Awaitable> m_DepthTasks = new List<Awaitable>();
        readonly List<CancellationTokenSource> m_DepthCtsAll = new List<CancellationTokenSource>();
        CancellationTokenSource m_DepthCts;
        int m_DepthGeneration;
        string m_DepthPath;

        int m_ObjectsDone;
        int m_ObjectsTotal;
        bool m_CostCapHitInObjects;
        bool m_CostCapNarrated;
        string m_FirstSkipReason;

        // ------------------------------------------------------------------------------------------
        // Public surface

        /// <summary>Current stage of the run (or the last run's terminal stage).</summary>
        public PlacementStage Stage { get; private set; } = PlacementStage.Idle;
        /// <summary>True while <see cref="RunAsync"/> is executing.</summary>
        public bool IsRunning { get; private set; }
        /// <summary>Id of the current/last run: the session folder name, so events join with the session artifacts.</summary>
        public string RunId { get; private set; }
        /// <summary>Session directory of the current/last run.</summary>
        public string SessionDir { get; private set; }

        /// <summary>
        /// Generation mode for the next run. Replays always keep the mode their session was created with
        /// (<c>mode.txt</c>); a running generation keeps the mode it started with.
        /// </summary>
        public GenerationMode Mode = GenerationMode.SceneContextual;
        /// <summary>What the next run generates (splat or mesh). Replays keep the session's value (<c>representation.txt</c>).</summary>
        public ObjectRepresentation Representation = ObjectRepresentation.GaussianSplat;
        /// <summary>
        /// When true, a Mesh run fails up front if no mesh spawner (glTFast) is registered, instead of paying for
        /// models that cannot be placed. Set by <see cref="SplatPressoRoot"/> when it places with ObjectSpawnService.
        /// </summary>
        public bool RequireMeshSpawner;

        /// <summary>Mode actually used by the current/last run.</summary>
        public GenerationMode ActiveMode => m_ActiveMode;
        /// <summary>Representation actually used by the current/last run.</summary>
        public ObjectRepresentation ActiveRepresentation => m_ActiveRep;
        /// <summary>Estimated credits spent by the current/last run.</summary>
        public double CostSoFar => m_Ledger?.TotalCost ?? 0.0;
        /// <summary>Last progress event of the current/last run (null before the first).</summary>
        public PlacementProgress LastProgress { get; private set; }
        /// <summary>Result of the last successful run (null otherwise).</summary>
        public PlacementResult LastResult { get; private set; }
        /// <summary>Failure of the last unsuccessful run (null otherwise).</summary>
        public PlacementFailure LastFailure { get; private set; }

        /// <summary>Every stage change and progress message.</summary>
        public event Action<PlacementProgress> OnProgress;
        /// <summary>The run placed its objects (some may be skipped; see each object's status).</summary>
        public event Action<PlacementResult> OnCompleted;
        /// <summary>The run was cancelled or failed.</summary>
        public event Action<PlacementFailure> OnFailed;
        /// <summary>
        /// The object list is known (right after verification / decision), before per-object processing starts:
        /// lets the engine show placement previews at the planned positions.
        /// </summary>
        public event Action<List<PlacedObjectResult>, CaptureResult> OnObjectsPlanned;
        /// <summary>Per-object sub-stage changes (see <see cref="SubStages"/>).</summary>
        public event Action<PlacedObjectResult, string> OnObjectUpdate;
        /// <summary>A step is about to be retried.</summary>
        public event Action<RetryInfo> OnRetry;
        /// <summary>The run entered a new stage (raised once per change, including the terminal stage).</summary>
        public event Action<string, PlacementStage> OnStageEntered;
        /// <summary>Cancel() was called on a running run (argument: run id).</summary>
        public event Action<string> OnCancelRequested;

        /// <param name="settings">Settings (null = <see cref="SplatPressoSettings.Active"/>).</param>
        /// <param name="captureProvider">Captures the view (null: runs fail at Capturing unless replayed from later stages).</param>
        /// <param name="placer">Spawns the objects (null = <see cref="NullObjectPlacer"/>).</param>
        public PlacementOrchestrator(SplatPressoSettings settings, ICaptureProvider captureProvider, IObjectPlacer placer)
        {
            m_Settings = settings != null ? settings : SplatPressoSettings.Active;
            m_CaptureProvider = IsNullObject(captureProvider) ? null : captureProvider;
            m_Placer = IsNullObject(placer) ? new NullObjectPlacer() : placer;
            Mode = m_Settings.defaultMode;
            Representation = m_Settings.representation;
        }

        /// <summary>Requests cancellation of the running run (no-op when idle).</summary>
        public void Cancel()
        {
            if (!IsRunning)
                return;
            Debug.Log($"[SplatPresso] Cancel requested for run {RunId}");
            Raise(OnCancelRequested, RunId, nameof(OnCancelRequested));
            m_Cts?.Cancel();
        }

        /// <summary>
        /// The mode a run on <paramref name="session"/> starting at <paramref name="startAt"/> will use: replays
        /// keep the stored mode, fresh runs use <paramref name="requested"/>.
        /// </summary>
        public static GenerationMode ResolveMode(PipelineSession session, StartStage startAt, GenerationMode requested)
        {
            if (session != null && startAt > StartStage.Capture &&
                TryParseEnum(session.LoadText(PipelineSession.ModeTxt), out GenerationMode stored))
                return stored;
            return requested;
        }

        /// <summary>The representation a run will use (replays keep the stored one; see <see cref="ResolveMode"/>).</summary>
        public static ObjectRepresentation ResolveRepresentation(PipelineSession session, StartStage startAt, ObjectRepresentation requested)
        {
            if (session != null && startAt > StartStage.Capture &&
                TryParseEnum(session.LoadText(PipelineSession.RepresentationTxt), out ObjectRepresentation stored))
                return stored;
            return requested;
        }

        // ------------------------------------------------------------------------------------------

        // Internal control-flow exception for graceful, attributed failures.
        sealed class StageFailedException : Exception
        {
            public readonly PlacementStage stage;
            public readonly PlacementEndKind kind;

            public StageFailedException(PlacementStage stage, string message, Exception inner = null,
                PlacementEndKind kind = PlacementEndKind.StageFailed)
                : base(message, inner)
            {
                this.stage = stage;
                this.kind = kind;
            }
        }

        // Fatal = never retried and no fallback attempted. Missing/invalid keys and an empty balance fail every
        // call the same way, so trying again (or another model) only delays the error.
        static bool IsFatal(Exception e) =>
            e is OperationCanceledException || e is CostCapExceededException || e is StageFailedException ||
            (e is GenpressoException g && (g.Kind == GenpressoErrorKind.Unauthorized || g.Kind == GenpressoErrorKind.InsufficientCredits));

        // Retrying the SAME call only helps for transient errors: deterministic ones (validation, a job whose result
        // was already paid for, ...) are marked non-retryable by the API layer, so a retry would just pay twice.
        // Other exceptions (e.g. TimeoutException of a media job) are retried, as in the proven pipeline.
        static bool IsRetryable(Exception e) =>
            !IsFatal(e) && !(e is GenpressoException g && !g.Retryable);

        static int RandomSeed() => UnityEngine.Random.Range(1, int.MaxValue);

        void SetStage(PlacementStage stage, string message, bool narrate = false, int done = 0, int total = 0)
        {
            bool changed = stage != Stage;
            Stage = stage;
            if (changed)
                Raise(OnStageEntered, RunId, stage, nameof(OnStageEntered));
            var p = new PlacementProgress
            {
                runId = RunId,
                stage = stage,
                message = message,
                objectsDone = done,
                objectsTotal = total,
                costSoFar = CostSoFar,
                narrate = narrate,
            };
            LastProgress = p;
            Raise(OnProgress, p, nameof(OnProgress));
        }

        void EnterTerminalStage(PlacementStage stage)
        {
            if (stage == Stage)
                return;
            Stage = stage;
            Raise(OnStageEntered, RunId, stage, nameof(OnStageEntered));
        }

        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Runs the pipeline on <paramref name="session"/>, starting at <paramref name="startAt"/> (earlier stages are
        /// loaded from the session). Returns the result, or null on cancellation/failure (reported through
        /// <see cref="OnFailed"/>) or when a run is already in progress. Never throws for pipeline errors.
        /// </summary>
        /// <param name="request">The request; null on replays (loaded from request.json).</param>
        public async Awaitable<PlacementResult> RunAsync(PlacementRequest request, PipelineSession session, StartStage startAt, CancellationToken externalCt)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (IsRunning)
            {
                Debug.LogWarning("[SplatPresso] A run is already in progress on this orchestrator; ignoring the new request");
                return null;
            }
            IsRunning = true;
            m_Cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var ct = m_Cts.Token;
            RunId = session.RunId;
            SessionDir = session.Dir;
            Stage = PlacementStage.Idle;
            LastProgress = null;
            LastResult = null;
            LastFailure = null;

            m_DepthTasks.Clear();
            m_DepthCtsAll.Clear();
            m_DepthCts = null;
            m_DepthPath = null;
            m_DepthGeneration = 0;
            m_ObjectsDone = 0;
            m_ObjectsTotal = 0;
            m_CostCapHitInObjects = false;
            m_CostCapNarrated = false;
            m_FirstSkipReason = null;
            m_ActiveMode = Mode;
            m_ActiveRep = Representation;

            try
            {
                // Every run (fresh or replay) starts a new ledger section, so a replay does not inherit the
                // earlier spend and the cap applies per run.
                m_Ledger = session.Ledger;
                m_Ledger.BeginRun(m_Settings.maxCostPerRun);

                // Clients are built per run so they share this run's ledger (cost cap).
                string genpressoKey = ApiKeys.Get(ApiKeyKind.Genpresso);
                var chat = new GenpressoChatClient(m_Settings.apiBaseUrl, genpressoKey, m_Ledger);
                m_DecisionService = new PlacementDecisionService(chat, m_Settings);
                m_Jobs = new MediaJobClient(m_Settings, m_Ledger);
                m_Media = new MediaEndpoints(m_Jobs, m_Settings);
                if (string.IsNullOrEmpty(genpressoKey) && startAt != StartStage.Place)
                    Debug.LogWarning("[SplatPresso] GenPresso API key is missing; stages that need it will fail. Set it in Project Settings > SplatPresso.");

                // Replays keep the mode and representation the session was created with; running generations
                // keep theirs even if the user toggles the defaults meanwhile.
                m_ActiveMode = ResolveMode(session, startAt, Mode);
                m_ActiveRep = ResolveRepresentation(session, startAt, Representation);
                session.SaveText(PipelineSession.ModeTxt, m_ActiveMode.ToString());
                session.SaveText(PipelineSession.RepresentationTxt, m_ActiveRep.ToString());

                if (m_ActiveRep == ObjectRepresentation.Mesh && RequireMeshSpawner && !MeshSpawnerRegistry.IsAvailable)
                    throw new StageFailedException(PlacementStage.Idle,
                        "Mesh mode needs glTFast (install it via SplatPresso > Install or Repair Dependencies), or switch to Gaussian splats");

                InvalidateStaleArtifacts(session, startAt);

                // ---- request (persist fresh; load from the session on replay) ----
                if (request == null)
                    request = session.LoadJson<PlacementRequest>(PipelineSession.RequestJson);
                if (startAt <= StartStage.Decide && request != null)
                    session.SaveJson(PipelineSession.RequestJson, request);

                PlacementResult result;
                if (startAt == StartStage.Place)
                {
                    result = session.LoadJson<PlacementResult>(PipelineSession.ResultJson);
                    if (result == null)
                        throw new StageFailedException(PlacementStage.Placing, $"replay: missing {PipelineSession.ResultJson} in {session.Dir}");
                    result.capture = CaptureResult.LoadFrom(session.Dir);
                    if (result.capture == null)
                        throw new StageFailedException(PlacementStage.Placing, $"replay: no capture artifacts in {session.Dir}");
                    PrepareResultForPlaceReplay(result, session);
                }
                else
                {
                    result = await RunPipelineAsync(request, session, startAt, ct);
                }

                // ---- 8. placing ----
                result.runId = RunId;
                SetStage(PlacementStage.Placing, "Placing objects into the scene");
                await m_Placer.PlaceAsync(result, ct);

                int placed = result.objects.Count(o => o.status == ObjectStatus.Placed);
                int skipped = result.objects.Count(o => o.status == ObjectStatus.Skipped);
                // Persist the final statuses (Placed / placement skips) for inspection and later Place replays.
                TrySaveJson(session, PipelineSession.ResultJson, result);
                // A placer stops early on cancel (objects already spawned stay); report the run as cancelled.
                ct.ThrowIfCancellationRequested();

                string doneMsg = skipped == 0
                    ? $"All {placed} object(s) placed."
                    : $"{placed} object(s) placed, {skipped} skipped.";
                // Not narrated here: the root narrates completion once, with the object names.
                SetStage(PlacementStage.Completed, doneMsg, narrate: false, done: placed, total: result.objects.Count);
                Debug.Log($"[SplatPresso] Run {RunId} complete: {doneMsg} (~{CostSoFar:0.##} credits, session {session.Dir})");
                LastResult = result;
                Raise(OnCompleted, result, nameof(OnCompleted));
                return result;
            }
            catch (Exception e)
            {
                ReportFailure(e);
                return null;
            }
            finally
            {
                // Drain any still-hot depth tasks so they never run unobserved (RunDepthAsync never throws).
                try
                {
                    m_DepthCts?.Cancel();
                    await JoinDepthTasksAsync();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SplatPresso] Depth task drain: {e.Message}");
                }
                foreach (var cts in m_DepthCtsAll)
                    cts.Dispose();
                m_DepthCtsAll.Clear();
                m_DepthCts = null;
                m_Cts.Dispose();
                m_Cts = null;
                IsRunning = false;
            }
        }

        void ReportFailure(Exception e)
        {
            PlacementStage failedStage = Stage;
            PlacementFailure failure;
            bool cancelled = e is OperationCanceledException && m_Cts != null && m_Cts.IsCancellationRequested;
            if (cancelled)
            {
                EnterTerminalStage(PlacementStage.Cancelled);
                Debug.Log($"[SplatPresso] Run {RunId} cancelled at {failedStage}");
                failure = new PlacementFailure { runId = RunId, stage = failedStage, kind = PlacementEndKind.Cancelled, reason = kCancelledReason, exception = e };
            }
            else if (e is CostCapExceededException)
            {
                EnterTerminalStage(PlacementStage.Failed);
                Debug.LogError($"[SplatPresso] Run {RunId}: cost cap exceeded at {failedStage}: {e.Message}");
                failure = new PlacementFailure { runId = RunId, stage = failedStage, kind = PlacementEndKind.CostCapExceeded, reason = "cost cap exceeded: " + e.Message, exception = e };
            }
            else if (e is StageFailedException sf)
            {
                EnterTerminalStage(PlacementStage.Failed);
                Debug.LogError($"[SplatPresso] Run {RunId} failed at {sf.stage}: {sf.Message}");
                failure = new PlacementFailure { runId = RunId, stage = sf.stage, kind = sf.kind, reason = sf.Message, exception = sf.InnerException ?? sf };
            }
            else
            {
                EnterTerminalStage(PlacementStage.Failed);
                Debug.LogError($"[SplatPresso] Run {RunId} failed at {failedStage}: {e}");
                failure = new PlacementFailure { runId = RunId, stage = failedStage, kind = PlacementEndKind.Error, reason = e.Message, exception = e };
            }
            LastFailure = failure;
            Raise(OnFailed, failure, nameof(OnFailed));
        }

        // Replays re-run some stages; artifacts derived from what they replace must not be reused.
        void InvalidateStaleArtifacts(PipelineSession session, StartStage startAt)
        {
            if (startAt == StartStage.Capture || startAt == StartStage.Place)
                return;
            // A new edit makes the previous edit's depth stale (it would otherwise be reused as-is).
            if (startAt <= StartStage.Edit)
            {
                session.DeleteFile(PipelineSession.DepthGenPng);
                session.DeleteFile(PipelineSession.DepthGenUrlTxt);
            }
            // Per-object caches are keyed only by object id: a new edit/verification (or, in Direct mode, a new
            // decision) would otherwise reuse cutouts / images made for the old one.
            bool objectCachesStale = m_ActiveMode == GenerationMode.DirectTextTo3D
                ? startAt <= StartStage.Decide
                : startAt <= StartStage.Verify;
            if (objectCachesStale)
                session.DeleteObjectsDir();
        }

        // Place replays: re-derive paths from the session folder (it may have moved) and make objects placeable again.
        void PrepareResultForPlaceReplay(PlacementResult result, PipelineSession session)
        {
            result.runId = RunId;
            result.sessionDir = session.Dir;
            result.objects ??= new List<PlacedObjectResult>();
            if (session.Has(PipelineSession.EditedJpg))
                result.editedImagePath = session.PathOf(PipelineSession.EditedJpg);
            result.generatedDepthPath = session.Has(PipelineSession.DepthGenPng) && m_Settings.useDepthEstimation
                ? session.PathOf(PipelineSession.DepthGenPng)
                : null;
            foreach (var o in result.objects)
            {
                o.runId = RunId;
                string model = Path.Combine(session.ObjectDirPath(o.id), PipelineSession.ModelFileName(o.representation));
                if (File.Exists(model))
                    o.modelPath = model;
                string cutout = Path.Combine(session.ObjectDirPath(o.id), PipelineSession.ObjectCutoutPng);
                if (File.Exists(cutout))
                    o.cutoutPath = cutout;
                if (o.status == ObjectStatus.Placed || o.status == ObjectStatus.Ready)
                {
                    if (File.Exists(o.modelPath))
                    {
                        o.status = ObjectStatus.Ready;
                    }
                    else
                    {
                        o.status = ObjectStatus.Skipped;
                        o.skipReason = "model file missing from the session";
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------
        // Stages 1..7 (everything before Placing).

        async Awaitable<PlacementResult> RunPipelineAsync(PlacementRequest request, PipelineSession session, StartStage startAt, CancellationToken ct)
        {
            // ---- 1. capture ----
            CaptureResult capture;
            if (startAt <= StartStage.Capture)
            {
                if (m_CaptureProvider == null || IsNullObject(m_CaptureProvider))
                    throw new StageFailedException(PlacementStage.Capturing, "no capture provider configured");
                SetStage(PlacementStage.Capturing, "Capturing the current view");
                capture = await m_CaptureProvider.CaptureAsync(ct);
                if (capture == null || capture.rgbJpeg == null)
                    throw new StageFailedException(PlacementStage.Capturing, "capture returned no image");
                capture.SaveTo(session.Dir);
            }
            else
            {
                capture = CaptureResult.LoadFrom(session.Dir);
                if (capture == null || capture.rgbJpeg == null)
                    throw new StageFailedException(PlacementStage.Capturing, $"replay: no capture artifacts in {session.Dir}");
            }

            // ---- 2. decide ----
            DecisionResult decision;
            if (startAt <= StartStage.Decide)
            {
                if (request == null)
                    throw new StageFailedException(PlacementStage.Deciding, "no placement request (none passed, none stored in session)");
                if (!request.IsValid(out string invalidReason))
                    throw new StageFailedException(PlacementStage.Deciding, "invalid placement request: " + invalidReason);
                SetStage(PlacementStage.Deciding, "Analyzing the scene and planning the placement");
                try
                {
                    decision = await m_DecisionService.DecideAsync(request, capture.rgbJpeg, ct);
                    if (decision == null)
                        throw new GenpressoException("DECIDE returned no result", GenpressoErrorKind.Parse, retryable: true);
                }
                catch (Exception e) when (IsRetryable(e))
                {
                    Debug.LogWarning($"[SplatPresso] Decide failed, retrying once: {e.Message}");
                    RaiseRetry("Deciding", null, 2, e);
                    decision = await m_DecisionService.DecideAsync(request, capture.rgbJpeg, ct);
                    if (decision == null)
                        throw new StageFailedException(PlacementStage.Deciding, "the planning model returned no result");
                }
                decision.objects ??= new List<DecidedObject>();
                decision.objects.RemoveAll(o => o == null);
                EnsureUniqueDecisionIds(decision);
                session.SaveJson(PipelineSession.DecisionJson, decision);
            }
            else
            {
                decision = session.LoadJson<DecisionResult>(PipelineSession.DecisionJson);
                if (decision == null)
                    throw new StageFailedException(PlacementStage.Deciding, $"replay: missing {PipelineSession.DecisionJson} in {session.Dir}");
                decision.objects ??= new List<DecidedObject>();
                decision.objects.RemoveAll(o => o == null);
            }

            if (!decision.feasible)
            {
                string reason = string.IsNullOrEmpty(decision.infeasibleReason)
                    ? "the request is not feasible in this view"
                    : decision.infeasibleReason;
                // Not narrated here: the failure itself is narrated once by the root.
                SetStage(PlacementStage.Deciding, $"Cannot place: {reason}");
                throw new StageFailedException(PlacementStage.Deciding, reason);
            }
            if (decision.objects.Count == 0)
                throw new StageFailedException(PlacementStage.Deciding, "the plan contains no objects");
            if (m_ActiveMode == GenerationMode.SceneContextual && startAt <= StartStage.Edit && string.IsNullOrWhiteSpace(decision.editPrompt))
                throw new StageFailedException(PlacementStage.Deciding, "the plan contains no image-edit prompt");

            // ---- DirectTextTo3D: the capture/decision fixed WHERE things go; the object images come from text
            // alone. Skip editing/verification/depth/segmentation entirely. ----
            if (m_ActiveMode == GenerationMode.DirectTextTo3D)
                return await RunDirectPipelineAsync(decision, request, capture, session, startAt, ct);

            // ---- 3. edit ----
            byte[] editedBytes;
            string editedUrl;
            if (startAt <= StartStage.Edit)
            {
                SetStage(PlacementStage.Editing, "Generating the edited image");
                (editedBytes, editedUrl) = await EditWithPolicyAsync(capture.rgbJpeg, decision.editPrompt, ct);
                SaveEdited(session, editedBytes, editedUrl);
            }
            else
            {
                editedBytes = session.LoadBytes(PipelineSession.EditedJpg);
                editedUrl = session.LoadText(PipelineSession.EditedUrlTxt);
                // The hosted URL is optional: without it (or once it expired) the bytes are sent as a data URI.
                if (editedBytes == null)
                    throw new StageFailedException(PlacementStage.Editing, $"replay: missing {PipelineSession.EditedJpg} in {session.Dir}");
            }
            // Downstream calls reuse the hosted URL (no re-upload); a URL loaded from an earlier run may have expired.
            var edited = new ImageInput(editedUrl, editedBytes, "image/jpeg", urlFromEarlierRun: startAt > StartStage.Edit);

            // ---- 4. hot depth (not awaited until step 7) ----
            if (m_Settings.useDepthEstimation)
            {
                if (session.Has(PipelineSession.DepthGenPng))
                    m_DepthPath = session.PathOf(PipelineSession.DepthGenPng);
                else
                    StartDepthTask(session, edited, ct);
            }

            // ---- 5. verify ----
            VerificationResult verification;
            if (startAt <= StartStage.Verify)
            {
                SetStage(PlacementStage.Verifying, "Verifying the edited image");
                verification = await VerifyWithRetryAsync(decision, capture.rgbJpeg, editedBytes, ct);

                if (IsVerificationBad(verification))
                {
                    Debug.LogWarning("[SplatPresso] Verification bad (camera changed or no objects found); retrying the edit with a new seed");
                    SetStage(PlacementStage.Editing, "Edit looked wrong; retrying the image edit");
                    RaiseRetry("Editing", null, 2, null);
                    var retry = await TryEditAndReverifyAsync(session, capture, decision, useFallbackRoute: false, ct);
                    if (retry.HasValue)
                        (editedBytes, edited, verification) = retry.Value;

                    if (!retry.HasValue || IsVerificationBad(verification))
                    {
                        Debug.LogWarning("[SplatPresso] Still bad; retrying the edit with the fallback model");
                        SetStage(PlacementStage.Editing, "Retrying the image edit with the fallback model");
                        RaiseRetry("Editing", null, 3, null);
                        var retry2 = await TryEditAndReverifyAsync(session, capture, decision, useFallbackRoute: true, ct);
                        if (retry2.HasValue)
                            (editedBytes, edited, verification) = retry2.Value;

                        if (!retry2.HasValue || IsVerificationBad(verification))
                            throw new StageFailedException(PlacementStage.Verifying,
                                "edited image failed verification (camera changed or requested objects not found)");
                    }
                }
                NormalizeVerifiedIds(decision, verification);
                session.SaveJson(PipelineSession.VerificationJson, verification);
            }
            else
            {
                verification = session.LoadJson<VerificationResult>(PipelineSession.VerificationJson);
                if (verification == null)
                    throw new StageFailedException(PlacementStage.Verifying, $"replay: missing {PipelineSession.VerificationJson} in {session.Dir}");
                NormalizeVerifiedIds(decision, verification);
            }

            var foundObjects = verification.objects.Where(o => o.found).ToList();
            var missingNames = verification.objects.Where(o => !o.found).Select(o => o.name).ToList();
            if (missingNames.Count > 0)
                SetStage(PlacementStage.Verifying, $"Could not add: {string.Join(", ", missingNames)}. Continuing with the rest.", narrate: true);

            // ---- 6. per-object processing ----
            var objects = BuildObjectList(session, decision, foundObjects, startAt);
            m_ObjectsTotal = objects.Count;
            m_ObjectsDone = objects.Count(o => o.status == ObjectStatus.Ready);
            var pending = objects.Where(o => o.status == ObjectStatus.Pending).ToList();
            SetStage(PlacementStage.ProcessingObjects,
                $"Processing {pending.Count} object(s)" + (m_ObjectsDone > 0 ? $" ({m_ObjectsDone} reused from session)" : ""),
                narrate: false, done: m_ObjectsDone, total: m_ObjectsTotal);
            RaiseObjectsPlanned(objects, capture);

            if (pending.Count > 0)
            {
                var semaphore = new SemaphoreSlim(Mathf.Max(1, m_Settings.maxConcurrentObjects));
                var tasks = new List<Awaitable>(pending.Count);
                foreach (var obj in pending)
                    tasks.Add(ProcessObjectAsync(obj, session, edited, editedBytes, semaphore, ct));
                foreach (var t in tasks)
                    await t; // each task swallows its own exceptions
                ct.ThrowIfCancellationRequested();
            }

            // ---- 7. join hot depth, assemble the result ----
            if (m_DepthTasks.Count > 0)
            {
                SetStage(PlacementStage.DepthEstimating, "Waiting for depth estimation", done: m_ObjectsDone, total: m_ObjectsTotal);
                await JoinDepthTasksAsync();
                ct.ThrowIfCancellationRequested();
            }

            var result = new PlacementResult
            {
                runId = RunId,
                sessionDir = session.Dir,
                capture = capture,
                editedImagePath = session.PathOf(PipelineSession.EditedJpg),
                editedImageUrl = edited.Url,
                generatedDepthPath = m_DepthPath,
                objects = objects,
            };
            // saved before the zero-object check so a failed run can still be inspected/replayed
            session.SaveJson(PipelineSession.ResultJson, result);
            ThrowIfNothingGenerated(objects);
            return result;
        }

        void ThrowIfNothingGenerated(List<PlacedObjectResult> objects)
        {
            if (objects.Count(o => o.status == ObjectStatus.Ready) > 0)
                return;
            if (m_CostCapHitInObjects)
                throw new StageFailedException(PlacementStage.ProcessingObjects, "no objects generated (cost cap exceeded)",
                    kind: PlacementEndKind.CostCapExceeded);
            throw new StageFailedException(PlacementStage.ProcessingObjects,
                string.IsNullOrEmpty(m_FirstSkipReason) ? "no objects generated" : "no objects generated: " + m_FirstSkipReason);
        }

        // ------------------------------------------------------------------------------------------
        // DirectTextTo3D pipeline: per object, text -> image -> 3D (splat), or text -> 3D (mesh). Placement uses the
        // decision's target boxes with the original capture depth (bbox-rect anchor).

        async Awaitable<PlacementResult> RunDirectPipelineAsync(DecisionResult decision, PlacementRequest request,
            CaptureResult capture, PipelineSession session, StartStage startAt, CancellationToken ct)
        {
            var objects = BuildDirectObjectList(session, decision, request, startAt);
            m_ObjectsTotal = objects.Count;
            m_ObjectsDone = objects.Count(o => o.status == ObjectStatus.Ready);
            var pending = objects.Where(o => o.status == ObjectStatus.Pending).ToList();
            SetStage(PlacementStage.ProcessingObjects,
                $"Generating {pending.Count} object(s) from text" + (m_ObjectsDone > 0 ? $" ({m_ObjectsDone} reused from session)" : ""),
                narrate: false, done: m_ObjectsDone, total: m_ObjectsTotal);
            RaiseObjectsPlanned(objects, capture);

            if (pending.Count > 0)
            {
                var semaphore = new SemaphoreSlim(Mathf.Max(1, m_Settings.maxConcurrentObjects));
                var tasks = new List<Awaitable>(pending.Count);
                foreach (var obj in pending)
                    tasks.Add(ProcessObjectDirectAsync(obj, session, semaphore, ct));
                foreach (var t in tasks)
                    await t; // each task swallows its own exceptions
                ct.ThrowIfCancellationRequested();
            }

            var result = new PlacementResult
            {
                runId = RunId,
                sessionDir = session.Dir,
                capture = capture,
                editedImagePath = null,
                editedImageUrl = null,
                generatedDepthPath = null,
                objects = objects,
            };
            session.SaveJson(PipelineSession.ResultJson, result);
            ThrowIfNothingGenerated(objects);
            return result;
        }

        // Deliberately SCENE-AGNOSTIC object descriptions: direct mode's whole point is that the generated objects do
        // NOT blend with the scene (clear contrast with SceneContextual), so the text-to-image / text-to-3D prompt gets
        // only what the USER asked for - never the decision's scene-informed look description (which is written while
        // viewing the capture).
        static string UserDescriptionFor(PlacementRequest request, DecidedObject d)
        {
            if (request?.objects != null)
            {
                foreach (var r in request.objects)
                {
                    if (r == null || string.IsNullOrEmpty(r.name) || d.name == null)
                        continue;
                    if (d.name.IndexOf(r.name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.name.IndexOf(d.name, StringComparison.OrdinalIgnoreCase) >= 0)
                        return string.IsNullOrEmpty(r.description) ? r.name : r.description;
                }
                if (request.objects.Count == 1 && request.objects[0] != null)
                {
                    var r = request.objects[0];
                    return string.IsNullOrEmpty(r.description) ? r.name : r.description;
                }
            }
            return null;
        }

        List<PlacedObjectResult> BuildDirectObjectList(PipelineSession session, DecisionResult decision, PlacementRequest request, StartStage startAt)
        {
            var list = new List<PlacedObjectResult>(decision.objects.Count);
            foreach (var d in decision.objects)
            {
                var reused = TryReuseFinishedObject(session, d.id, startAt);
                if (reused != null)
                {
                    list.Add(reused);
                    continue;
                }
                list.Add(new PlacedObjectResult
                {
                    id = d.id,
                    name = d.name,
                    descriptionForSegmentation = d.descriptionForSegmentation,
                    // user's own words only - no scene-informed description (see UserDescriptionFor)
                    descriptionForEdit = UserDescriptionFor(request, d) ?? d.name ?? "",
                    bboxGeneratedNorm = d.targetBboxNorm, // placement anchors on the DECIDED bbox
                    sizeHintM = d.sizeHintM,
                    restingSurface = d.restingSurface ?? "ground",
                    representation = m_ActiveRep,
                    status = ObjectStatus.Pending,
                    runId = RunId,
                });
            }
            return list;
        }

        // Catches ALL its own exceptions (never throws), so joining the parallel tasks is safe.
        async Awaitable ProcessObjectDirectAsync(PlacedObjectResult obj, PipelineSession session, SemaphoreSlim semaphore, CancellationToken ct)
        {
            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(ct);
                acquired = true;
                var media = CreateObjectMedia(obj);

                ImageInput source = null;
                if (m_ActiveRep == ObjectRepresentation.GaussianSplat)
                {
                    // a) text-to-image (cached on replay)
                    RaiseObjectUpdate(obj, SubStages.TextToImage);
                    string genPath = session.ObjectPath(obj.id, PipelineSession.ObjectGeneratedPng);
                    string genUrlPath = session.ObjectPath(obj.id, PipelineSession.ObjectGeneratedUrlTxt);
                    if (File.Exists(genPath))
                    {
                        byte[] cached = File.ReadAllBytes(genPath);
                        string cachedUrl = File.Exists(genUrlPath) ? File.ReadAllText(genUrlPath).Trim() : null;
                        source = new ImageInput(cachedUrl, cached, "image/png", urlFromEarlierRun: true);
                        Debug.Log($"[SplatPresso] Reusing cached generated image for '{obj.name}'");
                    }
                    else
                    {
                        var (png, url) = await WithRetryAsync(
                            _ => media.GenerateObjectImageAsync(obj.name, obj.descriptionForEdit, ct),
                            null, 2, SubStages.TextToImage, obj.id, ct);
                        File.WriteAllBytes(genPath, png);
                        File.WriteAllText(genUrlPath, url ?? "");
                        source = new ImageInput(url, png, "image/png", urlFromEarlierRun: false);
                    }
                    // recorded as the object's source image (the enhanced slot, as in the proven pipeline)
                    obj.enhancedPath = genPath;
                    obj.enhancedUrl = source.Url;
                    obj.sourceImagePath = genPath;
                }
                // Mesh: Rodin text-to-3D straight from the user's words (no image step).

                // b) 3D generation
                RaiseObjectUpdate(obj, SubStages.Generating3D);
                obj.modelPath = await Generate3DAsync(media, obj, source, session, ct);
                obj.sourceImageUrl = source?.Url;
                obj.status = ObjectStatus.Ready;
            }
            catch (Exception e)
            {
                MarkSkipped(obj, e, ct);
            }
            finally
            {
                FinishObject(obj, session, semaphore, acquired, ct);
            }
        }

        // ------------------------------------------------------------------------------------------
        // Edit helpers

        // Attempt 1: primary route, no seed. Attempt 2: primary, random seed. Attempt 3: fallback route.
        // (A non-retryable error skips attempt 2; a different model may still accept the request.)
        async Awaitable<(byte[] bytes, string url)> EditWithPolicyAsync(byte[] sourceJpeg, string editPrompt, CancellationToken ct)
        {
            try
            {
                return await m_Media.EditImageAsync(sourceJpeg, editPrompt, false, null, ct);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                Exception last = e;
                if (IsRetryable(e))
                {
                    Debug.LogWarning($"[SplatPresso] Edit failed, retrying with a random seed: {e.Message}");
                    RaiseRetry("Editing", null, 2, e);
                    try
                    {
                        return await m_Media.EditImageAsync(sourceJpeg, editPrompt, false, RandomSeed(), ct);
                    }
                    catch (Exception e2) when (!IsFatal(e2))
                    {
                        last = e2;
                    }
                }
                Debug.LogWarning($"[SplatPresso] Edit failed, trying the fallback model: {last.Message}");
                RaiseRetry("Editing", null, 3, last);
                return await m_Media.EditImageAsync(sourceJpeg, editPrompt, true, RandomSeed(), ct);
            }
        }

        // One edit (new seed) + re-verify pass used when verification rejected the previous edit.
        // Returns null when the attempt itself errored (non-fatal errors only).
        async Awaitable<(byte[] bytes, ImageInput image, VerificationResult verification)?> TryEditAndReverifyAsync(
            PipelineSession session, CaptureResult capture, DecisionResult decision, bool useFallbackRoute, CancellationToken ct)
        {
            try
            {
                var (bytes, url) = await m_Media.EditImageAsync(capture.rgbJpeg, decision.editPrompt, useFallbackRoute, RandomSeed(), ct);
                SaveEdited(session, bytes, url);
                var image = new ImageInput(url, bytes, "image/jpeg", urlFromEarlierRun: false);
                if (m_Settings.useDepthEstimation)
                    StartDepthTask(session, image, ct); // the previous depth task is for a stale image
                SetStage(PlacementStage.Verifying, "Re-verifying the edited image");
                var verification = await VerifyWithRetryAsync(decision, capture.rgbJpeg, bytes, ct);
                return (bytes, image, verification);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                Debug.LogWarning($"[SplatPresso] Edit/verify retry pass failed: {e.Message}");
                return null;
            }
        }

        static void SaveEdited(PipelineSession session, byte[] bytes, string url)
        {
            session.SaveBytes(PipelineSession.EditedJpg, bytes);
            session.SaveText(PipelineSession.EditedUrlTxt, url ?? "");
        }

        // ------------------------------------------------------------------------------------------
        // Verify helpers

        // Two attempts, then verify-fallback: a flaky vision model never kills the run; the decided boxes stand in
        // for the verified ones.
        async Awaitable<VerificationResult> VerifyWithRetryAsync(DecisionResult decision, byte[] originalJpeg, byte[] editedJpeg, CancellationToken ct)
        {
            try
            {
                return await m_DecisionService.VerifyAsync(decision, originalJpeg, editedJpeg, ct);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                Exception last = e;
                if (IsRetryable(e))
                {
                    Debug.LogWarning($"[SplatPresso] Verify failed, retrying once: {e.Message}");
                    RaiseRetry("Verifying", null, 2, e);
                    try
                    {
                        return await m_DecisionService.VerifyAsync(decision, originalJpeg, editedJpeg, ct);
                    }
                    catch (Exception e2) when (!IsFatal(e2))
                    {
                        last = e2;
                    }
                }
                Debug.LogWarning($"[SplatPresso] Verify failed ({last.Message}); synthesizing the verification from the decision (verify-fallback)");
                return SynthesizeVerification(decision);
            }
        }

        static VerificationResult SynthesizeVerification(DecisionResult decision)
        {
            var v = new VerificationResult { cameraUnchanged = true };
            foreach (var d in decision.objects)
            {
                v.objects.Add(new VerifiedObject
                {
                    id = d.id,
                    name = d.name,
                    found = true,
                    bboxNorm = d.targetBboxNorm,
                    fullyVisible = true,
                    notes = "verify-fallback",
                });
            }
            return v;
        }

        // Bad = camera changed or nothing found. Partial success is NOT bad (missing objects are reported and skipped).
        static bool IsVerificationBad(VerificationResult v)
        {
            if (v == null || !v.cameraUnchanged || v.objects == null)
                return true;
            foreach (var o in v.objects)
                if (o != null && o.found)
                    return false;
            return true;
        }

        // Object ids are chosen by the language model and name the objects/<id> cache folders; duplicates would make
        // two objects share (and overwrite) one folder. Reassign 1..N when any id repeats.
        static void EnsureUniqueDecisionIds(DecisionResult decision)
        {
            var seen = new HashSet<int>();
            bool duplicate = false;
            foreach (var o in decision.objects)
                if (!seen.Add(o.id))
                    duplicate = true;
            if (!duplicate)
                return;
            Debug.LogWarning("[SplatPresso] DECIDE returned duplicate object ids; renumbering them 1..N");
            for (int i = 0; i < decision.objects.Count; ++i)
                decision.objects[i].id = i + 1;
        }

        // Verified ids must be unique and refer to decided objects (they are matched back to the decision and name the
        // cache folders). A repeated/unknown id is re-matched by name (or to the only unused decided object); an entry
        // that matches nothing is dropped.
        static void NormalizeVerifiedIds(DecisionResult decision, VerificationResult verification)
        {
            verification.objects ??= new List<VerifiedObject>();
            verification.objects.RemoveAll(o => o == null);
            var decidedIds = new HashSet<int>(decision.objects.Select(d => d.id));
            var used = new HashSet<int>();
            var kept = new List<VerifiedObject>(verification.objects.Count);
            var unmatched = new List<VerifiedObject>();
            foreach (var v in verification.objects)
            {
                if (decidedIds.Contains(v.id) && used.Add(v.id))
                    kept.Add(v);
                else
                    unmatched.Add(v);
            }
            foreach (var v in unmatched)
            {
                var free = decision.objects.Where(d => !used.Contains(d.id)).ToList();
                var match = free.FirstOrDefault(d => NamesMatch(d.name, v.name)) ?? (free.Count == 1 ? free[0] : null);
                if (match == null)
                {
                    Debug.LogWarning($"[SplatPresso] Verification lists '{v.name}' (id {v.id}) that matches no planned object; ignoring it");
                    continue;
                }
                Debug.LogWarning($"[SplatPresso] Verification id {v.id} for '{v.name}' remapped to planned object {match.id}");
                v.id = match.id;
                used.Add(match.id);
                kept.Add(v);
            }
            verification.objects = kept;
        }

        static bool NamesMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            return a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0 || b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------------------------------
        // Hot depth estimation

        void StartDepthTask(PipelineSession session, ImageInput editedImage, CancellationToken runCt)
        {
            // Stale edit -> stale depth: supersede the running task, forget any result it already produced (a failing
            // new task must not leave the old edit's depth in use) and delete its files.
            m_DepthCts?.Cancel();
            m_DepthPath = null;
            session.DeleteFile(PipelineSession.DepthGenPng);
            session.DeleteFile(PipelineSession.DepthGenUrlTxt);
            m_DepthCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
            // Superseded sources are disposed only after the drain in RunAsync: their task may still observe the token.
            m_DepthCtsAll.Add(m_DepthCts);
            int generation = ++m_DepthGeneration;
            m_DepthTasks.Add(RunDepthAsync(session, editedImage, generation, m_DepthCts.Token));
        }

        // Never throws: failures just leave generatedDepthPath null (placement falls back to bbox math).
        async Awaitable RunDepthAsync(PipelineSession session, ImageInput image, int generation, CancellationToken ct)
        {
            int attempt = 1;
            while (true)
            {
                string source = null;
                try
                {
                    source = image.Value;
                    var (png, url) = await m_Media.EstimateDepthAsync(source, ct);
                    if (ct.IsCancellationRequested || generation != m_DepthGeneration)
                        return; // superseded; do not overwrite
                    session.SaveBytes(PipelineSession.DepthGenPng, png);
                    session.SaveText(PipelineSession.DepthGenUrlTxt, url ?? "");
                    m_DepthPath = session.PathOf(PipelineSession.DepthGenPng);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested || generation != m_DepthGeneration)
                        return;
                    Debug.LogWarning($"[SplatPresso] Depth estimation attempt {attempt} failed: {e.Message}");
                    bool viaDataUri = !IsFatal(e) && image.RetryWithDataUri(e, source, "depth");
                    if (!viaDataUri && (attempt >= 2 || !IsRetryable(e)))
                        return;
                    attempt++;
                }
            }
        }

        // Awaits (once each) and removes every depth task; safe to call repeatedly.
        async Awaitable JoinDepthTasksAsync()
        {
            while (m_DepthTasks.Count > 0)
            {
                var t = m_DepthTasks[0];
                m_DepthTasks.RemoveAt(0);
                await t; // RunDepthAsync never throws
            }
        }

        // ------------------------------------------------------------------------------------------
        // Per-object processing

        // Replay (startAt >= ProcessObjects): an object that already finished (Ready/Placed with its model file) is reused.
        PlacedObjectResult TryReuseFinishedObject(PipelineSession session, int id, StartStage startAt)
        {
            if (startAt < StartStage.ProcessObjects)
                return null;
            var existing = LoadObjectJson(session, id);
            string model = Path.Combine(session.ObjectDirPath(id), PipelineSession.ModelFileName(m_ActiveRep));
            if (existing == null || (existing.status != ObjectStatus.Ready && existing.status != ObjectStatus.Placed) || !File.Exists(model))
                return null;
            existing.status = ObjectStatus.Ready;
            existing.modelPath = model;
            existing.representation = m_ActiveRep;
            existing.runId = RunId;
            return existing;
        }

        List<PlacedObjectResult> BuildObjectList(PipelineSession session, DecisionResult decision, List<VerifiedObject> foundObjects, StartStage startAt)
        {
            var list = new List<PlacedObjectResult>(foundObjects.Count);
            foreach (var v in foundObjects)
            {
                var reused = TryReuseFinishedObject(session, v.id, startAt);
                if (reused != null)
                {
                    list.Add(reused);
                    continue;
                }

                var d = decision.objects.Find(o => o.id == v.id);
                list.Add(new PlacedObjectResult
                {
                    id = v.id,
                    name = !string.IsNullOrEmpty(v.name) ? v.name : d?.name,
                    descriptionForSegmentation = !string.IsNullOrEmpty(d?.descriptionForSegmentation) ? d.descriptionForSegmentation : v.name,
                    descriptionForEdit = d?.descriptionForImageEdit ?? "",
                    bboxGeneratedNorm = v.bboxNorm, // the verified box on the EDITED image
                    sizeHintM = d?.sizeHintM ?? 0f,
                    restingSurface = d?.restingSurface ?? "ground",
                    representation = m_ActiveRep,
                    status = ObjectStatus.Pending,
                    runId = RunId,
                });
            }
            return list;
        }

        // Catches ALL its own exceptions (never throws), so joining the parallel tasks is safe.
        async Awaitable ProcessObjectAsync(PlacedObjectResult obj, PipelineSession session, ImageInput edited, byte[] editedBytes,
            SemaphoreSlim semaphore, CancellationToken ct)
        {
            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(ct);
                acquired = true;
                var media = CreateObjectMedia(obj);
                RaiseObjectUpdate(obj, SubStages.Segmenting);

                // a) cutout: SAM-3 (2 tries) -> local crop + background removal -> raw crop.
                // Replays reuse the cached cutout (stale caches were deleted up front).
                string cutoutPath = session.ObjectPath(obj.id, PipelineSession.ObjectCutoutPng);
                string cutoutUrlPath = session.ObjectPath(obj.id, PipelineSession.ObjectCutoutUrlTxt);
                ImageInput cutout;
                if (File.Exists(cutoutPath))
                {
                    string cachedUrl = File.Exists(cutoutUrlPath) ? File.ReadAllText(cutoutUrlPath).Trim() : null;
                    cutout = new ImageInput(cachedUrl, File.ReadAllBytes(cutoutPath), "image/png", urlFromEarlierRun: true);
                    Debug.Log($"[SplatPresso] Reusing cached cutout for '{obj.name}'");
                }
                else
                {
                    cutout = await MakeCutoutAsync(media, obj, edited, editedBytes, ct);
                    File.WriteAllBytes(cutoutPath, cutout.Bytes);
                    if (cutout.HasUrl)
                        File.WriteAllText(cutoutUrlPath, cutout.Url);
                }
                obj.cutoutPath = cutoutPath;
                obj.cutoutUrl = cutout.Url;
                RaiseObjectUpdate(obj, SubStages.Cutout);

                // b) enhancement: re-render the (low-res) cutout as a high-quality single-object white-background image,
                // a much better 3D-generation input. Non-fatal: on failure the raw cutout is used. Replays reuse the
                // cached enhanced image.
                ImageInput source = cutout;
                string sourcePath = cutoutPath;
                if (m_Settings.enhanceObjectImages)
                {
                    RaiseObjectUpdate(obj, SubStages.Enhancing);
                    string enhPath = session.ObjectPath(obj.id, PipelineSession.ObjectEnhancedPng);
                    string enhUrlPath = session.ObjectPath(obj.id, PipelineSession.ObjectEnhancedUrlTxt);
                    ImageInput enhanced = null;
                    if (File.Exists(enhPath))
                    {
                        string cachedUrl = File.Exists(enhUrlPath) ? File.ReadAllText(enhUrlPath).Trim() : null;
                        enhanced = new ImageInput(cachedUrl, File.ReadAllBytes(enhPath), "image/png", urlFromEarlierRun: true);
                        Debug.Log($"[SplatPresso] Reusing cached enhanced image for '{obj.name}'");
                    }
                    else
                    {
                        try
                        {
                            var (png, url) = await WithRetryAsync(
                                src => media.EnhanceObjectImageAsync(src, obj.name, obj.descriptionForEdit, ct),
                                cutout, 2, SubStages.Enhancing, obj.id, ct);
                            File.WriteAllBytes(enhPath, png);
                            File.WriteAllText(enhUrlPath, url ?? "");
                            enhanced = new ImageInput(url, png, "image/png", urlFromEarlierRun: false);
                        }
                        catch (Exception e) when (!IsFatal(e))
                        {
                            Debug.LogWarning($"[SplatPresso] Enhancement failed for '{obj.name}' ({e.Message}); using the raw cutout");
                        }
                    }
                    if (enhanced != null)
                    {
                        obj.enhancedPath = enhPath;
                        obj.enhancedUrl = enhanced.Url;
                        source = enhanced;
                        sourcePath = enhPath;
                        RaiseObjectUpdate(obj, SubStages.Enhanced);
                    }
                }

                // c) 3D generation from the best available source: enhanced > cutout (hosted URL, else data URI)
                RaiseObjectUpdate(obj, SubStages.Generating3D);
                obj.sourceImagePath = sourcePath;
                obj.modelPath = await Generate3DAsync(media, obj, source, session, ct);
                obj.sourceImageUrl = source.Url;
                obj.status = ObjectStatus.Ready;
            }
            catch (Exception e)
            {
                MarkSkipped(obj, e, ct);
            }
            finally
            {
                FinishObject(obj, session, semaphore, acquired, ct);
            }
        }

        async Awaitable<ImageInput> MakeCutoutAsync(MediaEndpoints media, PlacedObjectResult obj, ImageInput edited, byte[] editedBytes, CancellationToken ct)
        {
            if (m_Settings.useSegmentation)
            {
                try
                {
                    var (png, url, score) = await WithRetryAsync(src =>
                        {
                            var (w, h) = edited.DimsOf(src); // SAM-3 wants pixel-space boxes of the exact image sent
                            return media.SegmentObjectAsync(src, obj.descriptionForSegmentation, obj.BboxGenerated, w, h, ct);
                        },
                        edited, 2, SubStages.Segmenting, obj.id, ct);
                    obj.segScore = score;
                    return new ImageInput(url, png, "image/png", urlFromEarlierRun: false);
                }
                catch (Exception e) when (!IsFatal(e))
                {
                    Debug.LogWarning($"[SplatPresso] Segmentation failed for '{obj.name}' ({e.Message}); falling back to a local crop");
                }
            }

            byte[] crop = ImageUtil.CropToPng(editedBytes, obj.BboxGenerated.Expand(0.15f));
            if (crop == null)
                throw new Exception(m_Settings.useSegmentation
                    ? "segmentation failed and the local crop is unavailable"
                    : "the local crop is unavailable (invalid box)");
            try
            {
                var (png, url) = await media.RemoveBackgroundAsync(crop, "image/png", ct);
                return new ImageInput(url, png, "image/png", urlFromEarlierRun: false);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                Debug.LogWarning($"[SplatPresso] Background removal failed for '{obj.name}' ({e.Message}); using the raw crop");
                return new ImageInput(null, crop, "image/png", urlFromEarlierRun: false);
            }
        }

        // Generates the object's model into objects/<id>/model.ply|model.glb (one retry on transient errors).
        async Awaitable<string> Generate3DAsync(MediaEndpoints media, PlacedObjectResult obj, ImageInput source, PipelineSession session, CancellationToken ct)
        {
            string dest = session.ObjectPath(obj.id, PipelineSession.ModelFileName(m_ActiveRep));
            if (m_ActiveRep == ObjectRepresentation.GaussianSplat)
            {
                if (source == null)
                    throw new InvalidOperationException("no source image for the splat model");
                return await WithRetryAsync(src => media.GenerateSplatAsync(src, m_Settings.numGaussians, dest, ct),
                    source, 2, SubStages.Generating3D, obj.id, ct);
            }
            if (source != null)
                return await WithRetryAsync(src => media.GenerateMeshFromImageAsync(src, dest, ct),
                    source, 2, SubStages.Generating3D, obj.id, ct);

            string desc = obj.descriptionForEdit;
            string prompt = string.IsNullOrWhiteSpace(desc) || string.Equals(desc.Trim(), obj.name, StringComparison.OrdinalIgnoreCase)
                ? obj.name
                : obj.name + ". " + desc;
            return await WithRetryAsync(_ => media.GenerateMeshFromTextAsync(prompt, dest, ct),
                null, 2, SubStages.Generating3D, obj.id, ct);
        }

        // A per-object endpoints wrapper, so job status lines can be attributed to the object ("downloading").
        MediaEndpoints CreateObjectMedia(PlacedObjectResult obj)
        {
            return new MediaEndpoints(m_Jobs, m_Settings)
            {
                OnJobStatus = status =>
                {
                    if (Is3DJobCompleted(status))
                        RaiseObjectUpdate(obj, SubStages.Downloading);
                },
            };
        }

        // MediaEndpoints reports "<routeKey>: <status>"; a completed 3D job is followed by the model download.
        static bool Is3DJobCompleted(string status)
        {
            if (string.IsNullOrEmpty(status) || status.IndexOf("COMPLETED", StringComparison.Ordinal) < 0)
                return false;
            return status.StartsWith(MediaRouteKeys.ImageToSplat + ":", StringComparison.Ordinal) ||
                   status.StartsWith(MediaRouteKeys.ImageToMesh + ":", StringComparison.Ordinal) ||
                   status.StartsWith(MediaRouteKeys.TextToMesh + ":", StringComparison.Ordinal);
        }

        void MarkSkipped(PlacedObjectResult obj, Exception e, CancellationToken ct)
        {
            obj.status = ObjectStatus.Skipped;
            if (e is OperationCanceledException && ct.IsCancellationRequested)
            {
                obj.skipReason = kCancelledReason;
                return;
            }
            if (e is CostCapExceededException)
            {
                obj.skipReason = kCostCapReason;
                m_CostCapHitInObjects = true;
            }
            else
            {
                obj.skipReason = e.Message;
            }
            m_FirstSkipReason ??= obj.skipReason;
            Debug.LogWarning($"[SplatPresso] Object '{obj.name}' skipped: {e.Message}");
        }

        void FinishObject(PlacedObjectResult obj, PipelineSession session, SemaphoreSlim semaphore, bool acquired, CancellationToken ct)
        {
            if (acquired)
                semaphore.Release();
            try { SaveObjectJson(session, obj); }
            catch (Exception e) { Debug.LogWarning($"[SplatPresso] Failed to save object.json for {obj.id}: {e.Message}"); }

            m_ObjectsDone++;
            bool skipped = obj.status == ObjectStatus.Skipped;
            RaiseObjectUpdate(obj, skipped ? SubStages.Skipped : SubStages.Ready);

            // Milestones only: objects dropped by a cancel are not narrated one by one (the cancel is narrated once),
            // and a cost-cap stop is narrated once per run.
            bool narrate = skipped && !ct.IsCancellationRequested && obj.skipReason != kCancelledReason;
            if (narrate && obj.skipReason == kCostCapReason)
            {
                narrate = !m_CostCapNarrated;
                m_CostCapNarrated = true;
            }
            SetStage(PlacementStage.ProcessingObjects,
                skipped ? $"Object '{obj.name}' skipped: {obj.skipReason}" : $"Object '{obj.name}' is ready",
                narrate: narrate, done: m_ObjectsDone, total: m_ObjectsTotal);
        }

        // Runs a media call (fed input.Value, or null without an input); retries on transient errors up to maxAttempts.
        // When the call was fed a hosted URL from an earlier run that the provider rejects (hosted URLs expire), it is
        // repeated once with a data URI of the cached bytes, even for otherwise non-retryable errors.
        async Awaitable<T> WithRetryAsync<T>(Func<string, Awaitable<T>> call, ImageInput input, int maxAttempts, string stage, int? objectId, CancellationToken ct)
        {
            int attempt = 1;
            while (true)
            {
                string source = null;
                try
                {
                    source = input?.Value;
                    return await call(source);
                }
                catch (Exception e) when (!IsFatal(e))
                {
                    bool viaDataUri = input != null && input.RetryWithDataUri(e, source, stage);
                    if (!viaDataUri && (attempt >= maxAttempts || !IsRetryable(e)))
                        throw;
                    ct.ThrowIfCancellationRequested();
                    attempt++;
                    string forObject = objectId.HasValue ? $" for object {objectId.Value}" : "";
                    Debug.LogWarning($"[SplatPresso] {stage} failed{forObject} ({e.Message}); retrying (attempt {attempt})");
                    RaiseRetry(stage, objectId, attempt, e);
                }
            }
        }

        static PlacedObjectResult LoadObjectJson(PipelineSession session, int id)
        {
            string path = Path.Combine(session.ObjectDirPath(id), PipelineSession.ObjectJson);
            if (!File.Exists(path))
                return null;
            try { return JsonUtil.Deserialize<PlacedObjectResult>(File.ReadAllText(path)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Failed to load object.json for {id}: {e.Message}");
                return null;
            }
        }

        static void SaveObjectJson(PipelineSession session, PlacedObjectResult obj) =>
            File.WriteAllText(session.ObjectPath(obj.id, PipelineSession.ObjectJson), JsonUtil.Serialize(obj));

        static void TrySaveJson<T>(PipelineSession session, string fileName, T obj)
        {
            try { session.SaveJson(fileName, obj); }
            catch (Exception e) { Debug.LogWarning($"[SplatPresso] Failed to save {fileName}: {e.Message}"); }
        }

        // ------------------------------------------------------------------------------------------
        // Image inputs

        /// <summary>
        /// An image fed to a media model: its hosted URL when there is one (downstream calls reuse hosted URLs instead of
        /// re-uploading bytes), otherwise a data URI of the local bytes (there is no upload endpoint). One instance may
        /// be shared by parallel object tasks (the edited image); everything runs on the main thread.
        /// </summary>
        sealed class ImageInput
        {
            readonly byte[] m_Bytes;
            readonly string m_Mime;
            readonly string m_EarlierRunUrl; // hosted URL loaded from an earlier run's *_url.txt (may have expired)
            string m_Url;
            string m_DataUri;
            byte[] m_DataBytes; // the bytes inside the data URI (re-encoded when needed to fit the body cap)

            public ImageInput(string url, byte[] bytes, string mime, bool urlFromEarlierRun)
            {
                m_Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
                m_Bytes = bytes;
                m_Mime = mime;
                m_EarlierRunUrl = urlFromEarlierRun ? m_Url : null;
            }

            public bool HasUrl => m_Url != null;
            /// <summary>Hosted URL, or null when the image is sent as a data URI.</summary>
            public string Url => m_Url;
            public byte[] Bytes => m_Bytes;

            /// <summary>What to put in the request: the hosted URL, else a data URI of the bytes.</summary>
            public string Value
            {
                get
                {
                    if (m_Url != null)
                        return m_Url;
                    if (m_DataUri == null)
                    {
                        m_DataBytes = FitForDataUri(m_Bytes, m_Mime, out string mime);
                        m_DataUri = MediaEndpoints.DataUri(mime, m_DataBytes);
                    }
                    return m_DataUri;
                }
            }

            /// <summary>Pixel size of the image a <see cref="Value"/> refers to (a data URI may be downscaled).</summary>
            public (int w, int h) DimsOf(string value) =>
                ImageUtil.ImageDims(value != null && ReferenceEquals(value, m_DataUri) ? m_DataBytes : m_Bytes);

            /// <summary>
            /// True when a call that used <paramref name="usedValue"/> should be repeated with a data URI: the value was
            /// an earlier run's hosted URL and <paramref name="e"/> looks like the provider rejected it (hosted URLs
            /// expire). Switches this input to the data URI for every later call. Once per call that used the URL.
            /// </summary>
            public bool RetryWithDataUri(Exception e, string usedValue, string stage)
            {
                if (m_EarlierRunUrl == null || usedValue == null || usedValue != m_EarlierRunUrl ||
                    m_Bytes == null || m_Bytes.Length == 0 || !IsHostedUrlRejected(e))
                    return false;
                if (m_Url != null)
                {
                    Debug.LogWarning($"[SplatPresso] {stage}: the cached hosted URL was rejected (it may have expired); retrying with the cached bytes");
                    m_Url = null;
                }
                return true;
            }
        }

        // A 4xx or failed job on a step fed an earlier run's hosted URL: most likely the URL expired. Auth, balance,
        // missing model paths, rate limits and payload size have their own causes and are excluded.
        static bool IsHostedUrlRejected(Exception e)
        {
            if (!(e is GenpressoException g))
                return false;
            switch (g.Kind)
            {
                case GenpressoErrorKind.Validation:
                case GenpressoErrorKind.JobFailed:
                    return true;
                case GenpressoErrorKind.Unauthorized:
                case GenpressoErrorKind.InsufficientCredits:
                case GenpressoErrorKind.NotFound:
                case GenpressoErrorKind.PayloadTooLarge:
                case GenpressoErrorKind.RateLimited:
                case GenpressoErrorKind.Timeout:
                    return false;
                default:
                    return g.StatusCode >= 400 && g.StatusCode < 500;
            }
        }

        // Keeps a data URI under the body budget: PNGs are flattened on white (the background every 3D input uses) and
        // re-encoded as JPEG, then downscaled if still too large. Returns the input unchanged when it already fits.
        static byte[] FitForDataUri(byte[] bytes, string mime, out string outMime)
        {
            outMime = string.IsNullOrEmpty(mime) ? "image/png" : mime;
            if (bytes == null || Base64Length(bytes.Length) + 64 <= kDataUriBudgetChars)
                return bytes;
            byte[] jpeg = ImageUtil.IsPng(bytes) ? FlattenOnWhiteJpeg(bytes, 90) : ImageUtil.ReencodeJpeg(bytes, 0, 90);
            if (jpeg != null && Base64Length(jpeg.Length) + 64 > kDataUriBudgetChars)
                jpeg = ImageUtil.ReencodeJpeg(jpeg, 2048, 90) ?? jpeg;
            if (jpeg != null && Base64Length(jpeg.Length) + 64 > kDataUriBudgetChars)
                jpeg = ImageUtil.ReencodeJpeg(jpeg, 1280, 85) ?? jpeg;
            if (jpeg == null)
                return bytes; // the media client's 4 MB guard reports it clearly
            outMime = "image/jpeg";
            return jpeg;
        }

        static long Base64Length(long byteCount) => (byteCount + 2) / 3 * 4;

        static byte[] FlattenOnWhiteJpeg(byte[] png, int quality)
        {
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(png))
                    return null;
                var px = tex.GetPixels32();
                for (int i = 0; i < px.Length; ++i)
                {
                    int a = px[i].a;
                    if (a == 255)
                        continue;
                    int inv = 255 - a;
                    px[i] = new Color32(
                        (byte)((px[i].r * a + 255 * inv) / 255),
                        (byte)((px[i].g * a + 255 * inv) / 255),
                        (byte)((px[i].b * a + 255 * inv) / 255),
                        255);
                }
                tex.SetPixels32(px);
                tex.Apply(false);
                return tex.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Flattening an image failed: {e.Message}");
                return null;
            }
            finally
            {
                ImageUtil.DestroySafe(tex);
            }
        }

        // ------------------------------------------------------------------------------------------
        // Events (handler exceptions are isolated: a UI bug must never kill the pipeline)

        void RaiseObjectsPlanned(List<PlacedObjectResult> objects, CaptureResult capture) =>
            Raise(OnObjectsPlanned, objects, capture, nameof(OnObjectsPlanned));

        void RaiseObjectUpdate(PlacedObjectResult obj, string subStage) =>
            Raise(OnObjectUpdate, obj, subStage, nameof(OnObjectUpdate));

        void RaiseRetry(string stage, int? objectId, int attempt, Exception error) =>
            Raise(OnRetry, new RetryInfo { runId = RunId, objectId = objectId, stage = stage, attempt = attempt, error = error }, nameof(OnRetry));

        static void Raise<T>(Action<T> handlers, T arg, string eventName)
        {
            if (handlers == null)
                return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<T>)d)(arg); }
                catch (Exception e) { Debug.LogWarning($"[SplatPresso] {eventName} handler threw: {e}"); }
            }
        }

        static void Raise<T1, T2>(Action<T1, T2> handlers, T1 a, T2 b, string eventName)
        {
            if (handlers == null)
                return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<T1, T2>)d)(a, b); }
                catch (Exception e) { Debug.LogWarning($"[SplatPresso] {eventName} handler threw: {e}"); }
            }
        }

        // ------------------------------------------------------------------------------------------
        // Misc

        // A destroyed UnityEngine.Object behind an interface reference is not C#-null.
        static bool IsNullObject(object o) => o == null || (o is UnityEngine.Object uo && uo == null);

        static bool TryParseEnum<T>(string text, out T value) where T : struct, Enum
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return Enum.TryParse(text.Trim(), true, out value) && Enum.IsDefined(typeof(T), value);
        }
    }
}
