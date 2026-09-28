# SplatPresso Voice To 3DGS — architecture notes

Developer-facing notes for people changing or extending the package. User documentation is in `README.md`.
Unity ignores this folder (`Documentation~`), so nothing here is imported.

## 1. Assemblies and compile gating

The package must compile with **zero errors before aras-p's renderer is installed**. If any assembly in a running editor
fails to compile, Unity does not reload the domain, so the dependency bootstrap would never run. Everything that touches
`GaussianSplatting` or URP is therefore gated with `versionDefines` + `defineConstraints`. References to assemblies that do
not exist yet are silently ignored by Unity, so every external assembly is referenced **by name**.

| Assembly | Folder | Platforms | Compiles when | Notes |
|---|---|---|---|---|
| `SplatPresso.Bootstrap.Editor` | `Editor/Bootstrap` | Editor | always | References nothing. Installs the renderer. May use Newtonsoft (auto-referenced) for the OpenUPM manifest edit |
| `SplatPresso.Runtime` | `Runtime` | all | `SPLATPRESSO_HAS_GS` && `SPLATPRESSO_HAS_URP` | Everything at runtime. `allowUnsafeCode` for the Burst asset jobs. `SPLATPRESSO_INPUTSYSTEM` is a soft define |
| `SplatPresso.Mesh` | `Runtime/Mesh` | all | GS && URP && `SPLATPRESSO_HAS_GLTFAST` | Registers the glTFast mesh spawner |
| `SplatPresso.Editor` | `Editor` | Editor | GS && URP | Namespace `SplatPresso.EditorTools` (not `SplatPresso.Editor`, which would shadow `UnityEditor.Editor`) |
| `SplatPresso.Tests.Editor` | `Tests/Editor` | Editor | `UNITY_INCLUDE_TESTS` && GS && URP | `overrideReferences`: nunit + Newtonsoft listed explicitly |
| `SplatPresso.Tests` | `Tests/Runtime` | all | `UNITY_INCLUDE_TESTS` && GS && URP | PlayMode tests, mock server |

Symbols defined by `versionDefines` are only visible inside the asmdef that declares them, so every gated asmdef repeats
the same `versionDefines`. The GS expression is `1.1.0` (Unity reads it as >= 1.1.0): 1.1.0 is the first renderer release whose
URP pass implements `RecordRenderGraph`; 1.0.0 and older only override `Execute()`, which does nothing under Render Graph, so
compiling against them would give a package that renders no splats and captures garbage depth. With an older renderer every
gated assembly is excluded and only `SplatPresso.Bootstrap.Editor` runs, so the version check for that case lives there.
Dependency direction is strictly gated → core; the core never references `SplatPresso.Mesh` (it talks to it through
`MeshSpawnerRegistry`).

Namespaces: `SplatPresso` (root, core types, settings, keys, `InputCompat`), `SplatPresso.Api`, `SplatPresso.Voice`,
`SplatPresso.Rendering`, `SplatPresso.Rendering.IO`, `SplatPresso.Placement`, `SplatPresso.Extras`, `SplatPresso.Mesh`,
`SplatPresso.EditorTools`, `SplatPresso.Bootstrap`, `SplatPresso.Tests`.

### Why these registry dependencies and not others

- `package.json` may only depend on registry packages; a git URL there makes the **whole project** fail to resolve.
  So the renderer is installed by the bootstrap into the project manifest instead.
- `com.unity.inputsystem` is not declared: installing it into an old-Input-Manager project pops an editor-restart dialog.
  `InputCompat` supports both backends (`ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM`, else `ENABLE_LEGACY_INPUT_MANAGER`).
  Nothing outside `InputCompat` may call `UnityEngine.Input` (it throws in Input-System-only projects).
- `com.unity.cloud.gltfast` is optional (Mesh mode). Setup / Install or Repair Dependencies offers it.
- Declared minimum versions (Burst 1.8.8, Collections 2.1.4, Mathematics 1.2.6) are GS's own minimums; the editor raises them.

## 2. Dependency bootstrap (`Editor/Bootstrap/DependencyInstaller.cs`)

```
[InitializeOnLoad] ─ skip in asset import workers ─ once per editor session (SessionState)
   └ delayCall → wait while isCompiling/isUpdating, never in play mode, one Client request at a time
        ├ PackageInfo.FindForPackageName("org.nesnausk.gaussian-splatting") != null
        │     version >= 1.1.0 → done (never touch any existing install)
        │     version <  1.1.0 → LogError + manifest line (SplatPresso stays inactive); interactive: offer to replace with
        │                        the pinned commit (Client.Add only on explicit accept, respects "Don't ask again" on the
        │                        automatic path); batch + -splatpressoExitWhenDone → Exit(1)
        ├ "Don't ask again" set (UserSettings/SplatPresso.Bootstrap.asset) → done
        ├ batchmode:
        │     no -splatpressoInstallDeps → log the manifest line as an error, done
        │     -splatpressoInstallDeps    → install without a dialog
        │     -splatpressoExitWhenDone   → EditorApplication.Exit(0|1) after the request AND the following domain reload
        │                                  (SessionState flag set before Client.Add, checked on the next [InitializeOnLoad])
        └ dialog: Install (git) / Not now / Don't ask again
              git --version fails → offer OpenUPM (scoped registry + "1.1.1", Client.Resolve)
              Client.Add(git URL pinned to 2c6fed37da67a217367261fcfcd3316d34c73e76)
              failure hints: "No 'git' executable" (restart Unity AND Hub), "Filename too long" (short path / OpenUPM)
```

The pin is a commit, not `#v1.1.1`: HEAD after v1.1.1 contains the `GaussianComposite.shader` change the placement and
colour calibration was made with. `package.json` of that commit still says 1.1.1, so `versionDefines` cannot tell the
two apart; the code relies only on upstream's public API plus the single reflected field in `GsInternals`.

## 3. Runtime components

Setup puts these on one `SplatPresso` GameObject (references are auto-resolved in `Awake` when left empty):

| Component | Role |
|---|---|
| `SplatPressoRoot` | Public entry point. Owns runs (one `PlacementOrchestrator` + one session per run), capacity guard, request gate, mode/representation, M/N hotkeys, narration of milestones to the voice agent, aggregated events with `runId` |
| `VoiceAgent` | Facade over `IVoiceBackend` (`GenpressoVoiceBackend` default, `OpenAIRealtimeBackend` optional), push-to-talk edge detection, mic, snapshot, text submit |
| `MicCapture` / `AudioStreamPlayer` | Warm microphone with 0.35 s pre-roll and a capped utterance buffer (16 kHz chat / 24 kHz realtime); streamed 24 kHz PCM playback (Realtime audio and spoken GenPresso replies) |
| `ReplySpeaker` (plain class, owned by `VoiceAgent`) | Speaks GenPresso chat replies: `textToSpeech` media route (MiniMax speech-02-turbo, 24 kHz PCM; other routes decoded as WAV/MP3), ungated and fast-polled, in order, cancelled by push-to-talk |
| `ModelWarmer` (static) | Free warm-up requests (inputs that fail validation) that boot the 3D model's worker on talk / typing / run start, and keep it warm while the user is active |
| `VoiceHud` | IMGUI HUD: state pill, mode chips, reply bubble, text box (`TextInputFocused`), mic picker (`DevicePanelOpen`), run list, key banner |
| `CaptureService` | `ICaptureProvider`. Requests a one-shot capture from `SplatCaptureFeature`, fails fast when the feature is not active, times out with an actionable message, encodes JPEG; also serves voice snapshots (never throws, returns null when busy) |
| `ObjectSpawnService` | `IObjectPlacer`. Loads `.ply` → runtime `GaussianSplatAsset`, solves placement, spawns `Splat_<label>` (root + `Content` child), or spawns meshes through `MeshSpawnerRegistry`; spawn cap; `GeneratedObject` per root |
| `PlacementPreviewService` | Hologram boxes at the solved placement while objects are generated; glides to the refined pose when a mask arrives |
| `SplatCaptureFeature` | `ScriptableRendererFeature` on every URP renderer, after `GaussianSplatURPFeature` |

Settings live in one `SplatPressoSettings` ScriptableObject (`Assets/SplatPresso/Resources/SplatPressoSettings.asset`,
loaded through `SplatPressoSettings.Active`; the package folder is read-only when installed from git).

## 4. Data flow

```
VoiceAgent ── PTT release ──► (silence guard: peak < silenceThreshold → not sent) ──► GenpressoVoiceBackend
   WAV (16 kHz) + snapshot JPEG + last N turns (audio/image rewritten to text) + pending [PIPELINE] notices
   └► POST {apiBaseUrl}/chat/completions  (json_schema voice_turn; schema-drop retry; one JSON repair; image-drop retry)
        { transcript, reply, actions[] } → create → PlacementRequested(VoicePlacementRequest) → SplatPressoRoot.StartRun
                                          reply  → AgentReply (subtitle) + ReplySpeaker → media/{textToSpeech} → AudioStreamPlayer
                                          cancel → CancelRequested → SplatPressoRoot.CancelAll

SplatPressoRoot.StartRun ─► gate/capacity/key checks ─► PipelineSession.CreateNew ─► PlacementOrchestrator.RunAsync
   per-run clients sharing one CostLedger:
     GenpressoChatClient ─► PlacementDecisionService (DECIDE / VERIFY, strict schemas)
     MediaJobClient      ─► MediaEndpoints (edit, enhance, t2i, segment, removeBackground, depth, splat, mesh)

   Capturing ─► Deciding ─┬─ SceneContextual: Editing ─► (hot Depth, parallel) ─► Verifying [re-edit ≤2] ─►
                          │                   ProcessingObjects (segment → cutout → enhance → 3D, per object, parallel)
                          │                   ─► DepthEstimating (join) ─► Placing
                          └─ DirectTextTo3D:  ProcessingObjects (t2i → 3D) ─► Placing
   ─► Completed | Failed | Cancelled   (result.json is written before placing so a failed run can be replayed)

ObjectSpawnService.PlaceAsync ─► SplatPlacement.Solve(capture, bbox, sizeHint, genDepth, mask, contentBounds, tuning)
```

Rules the pipeline keeps (all ported from the prototype, see the code comments for the reasons):

- Every continuation runs on the main thread (`Awaitable`); no `ConfigureAwait(false)`; `Task.Run` only in the WebSocket loops.
- Fatal errors (cancel, cost cap, stage failure) are never retried; other errors get the per-stage retry.
- Edit policy: primary model without seed → primary with a random seed → fallback route. Verify falls back to a
  verification synthesized from the decision after two failures, so a flaky VLM never kills a run.
- Hot depth restarts on every re-edit, never overwrites once superseded, never throws, and is always drained.
- Enhancement is non-fatal (falls back to the raw cutout). 3D source priority: enhanced > hosted cutout > data URI.
- Hosted URLs are reused downstream instead of re-uploading bytes; an expired URL on replay is retried once as a data URI.
- Direct mode is deliberately scene-agnostic: only the user's words reach text-to-image; placement anchors on the decided box.
- Only milestone progress (`narrate == true`) reaches the voice model; every tick would pollute its context.
- LLM object ids are de-duplicated; `Bbox.FromXYWHNormLenient` absorbs Gemini's 0–1000 box leakage (per component).
- Event handlers are isolated: an exception in a subscriber is logged and never kills a run.

### Placement solve (summary)

1. Pick the ground-contact pixel: bottom rows of the object mask (band = `groundAnchorBandFraction`), median column; bbox bottom when no mask.
2. Anchor depth = original capture depth at that pixel (the ground still exists there in the unedited view);
   fallbacks: fitted generated depth (`s·R + t ≈ 1/D`, robust refit, |corr| ≥ 0.2) → bbox depth median → 2 m; clamped to `[minDistance, maxDistance]`.
3. Position = unproject with the **capture-time** pose. Height from the mask's row extent (verify boxes are loose),
   sanity-checked against `size_hint_m`; `uniformScale = height / contentBounds.y · uniformScaleFactor`.
4. Yaw faces the capture camera horizontally, plus `yawOffsetDeg` (270° for TripoSplat, calibrated). Meshes use the `mesh*` tuning.

The TripoSplat → Unity content fix (`contentRotationEuler` (180,0,0), `contentScale` (1,1,-1)) is applied to the `Content`
child from settings at spawn time and used for bounds sizing, so the two can never drift apart.

## 5. GenPresso media protocol (`MediaJobClient`)

- Submit `POST {apiBaseUrl}/media/{path}` with `Authorization: Bearer gp_…`. Candidate paths come from the cached path
  (`ModelPathCache`, 7-day TTL, `<persistentDataPath>/SplatPresso/model_paths.json`) and then `ModelRoute.genpressoPaths`.
- 404 (JSON or HTML) or a 400 mentioning model/not found/unsupported → next candidate (not queued, not billed).
  422 at submit → path exists, input invalid (not retried). 401/402/403/413 → abort. 408/429/5xx/network → retry the same
  candidate up to 3 times, honouring `Retry-After`.
- All candidates missing → fal.ai fallback (`falFallbackWhenMissing` + fal key), else `NotFound` listing the tried paths.
- `status_url` / `response_url` / `cancel_url` are used verbatim; if absent, `media/requests/{id}[/status|/cancel]` (no model path).
- Poll until `route.timeoutSec`. Terminal failures: `FAILED`, `ERROR`, `EXPIRED`, `CANCELED`, `CANCELLED` (result fetched once
  for the reason). Always fetch and check the result even after `COMPLETED`: a 422 there is the real validation error, and a
  200 `{"detail": "...in progress..."}` means "not yet". Result-fetch errors are retried without re-submitting (already paid).
- Timeout or cancellation sends a fire-and-forget `PUT cancel_url` (no upload handler; an empty `UploadHandlerRaw` is rejected).
- Request bodies are kept under 4,000,000 bytes (GenPresso's cap; the hosting layer returns a plain-text 413 above ~4.5 MB).
- A global gate limits concurrent media jobs to `maxConcurrentMediaJobs` across all runs.
- Downloads never send auth headers (CDN URLs), happen immediately (URLs expire) and write `.tmp` then move.

Chat (`GenpressoChatClient`): `response_format: json_schema (strict)`; schema-drop retry on 400/404/422 mentioning the
schema; `error` object on HTTP 200 is an error; content may be a string or an array of parts; code fences stripped; one
JSON-repair round trip; model-not-found retries once with `chatFallbackModel`. Both error shapes (OpenAI `error{}` and
FastAPI `detail[]`) are parsed by `GenpressoError`.

## 6. Session artifacts and replay

Session folder: `settings.sessionsFolder` or `<persistentDataPath>/SplatPresso/sessions/<yyyyMMdd_HHmmss>[_n]`.
The run id is the folder name, so logs and events join with artifacts.

| File | Written by | Content |
|---|---|---|
| `capture.jpg`, `capture_depth.bin`, `capture_meta.json` | Capturing | JPEG; float32 eye depth W×H little-endian, row 0 = top, 0 = invalid; pose/FOV/near/far |
| `request.json` | run start | `PlacementRequest` (snake_case JSON) |
| `mode.txt`, `representation.txt` | run start | Fixed for the session; replays keep them |
| `decision.json` | Deciding | `DecisionResult` |
| `edited.jpg`, `edited_url.txt` | Editing | Edited view + hosted URL reused downstream |
| `verification.json` | Verifying | `VerificationResult` (boxes on the edited image) |
| `depth_gen.png`, `depth_gen_url.txt` | hot depth | Relative depth of the edited image |
| `objects/<id>/cutout.png`, `cutout_url.txt` | per object | Segmentation cutout (full frame) or crop + background removal |
| `objects/<id>/enhanced.png`, `enhanced_url.txt` | per object | Enhanced white-background object image |
| `objects/<id>/generated.png`, `generated_url.txt` | per object (Direct) | Text-to-image output |
| `objects/<id>/model.ply` / `model.glb` | per object | TripoSplat splat / Rodin mesh |
| `objects/<id>/object.json` | per object | `PlacedObjectResult` |
| `result.json` | before placing | `PlacementResult` |
| `ledger.json` | every billable call | Append-only history; `TotalCost` counts the current run only |

Replay (`StartReplay(dir, StartStage)`): stages after the entry point are re-run, earlier artifacts are loaded.
Replaying from `Decide`/`Edit`/`Verify` discards the `objects/` caches (they were cut from the old edited image) and, from
`Edit` or earlier, the stale `depth_gen.*`. From `ProcessObjects`, finished objects with a model file are reused.
`Place` needs `result.json`. JSON paths are absolute, so moving a session folder breaks replay of later stages.

Cost ledger: unit = estimated credits from `ModelRoute.estimatedCost` (chat calls use a small per-call estimate).
The cap is checked **before** a media submit and recorded after an accepted submit. It exists to stop runaway retry loops,
not for accounting; GenPresso bills actual usage at completion.

## 7. Rendering

- **No changes to aras-p's package.** The renderer is referenced as the assembly `GaussianSplatting`.
- `RuntimeSplatAssetFactory` builds a `GaussianSplatAsset` at runtime with the public API: `Initialize` → `SetDataHash`
  (unique, only used for change detection) → `SetAssetFiles(null, pos, other, color, sh)` with four `TextAsset`s created from
  `NativeArray` spans. Layouts are byte-identical to the upstream editor's "VeryHigh" output (Float32 pos/scale, Float32x4
  colour texture with Morton tile swizzle, Float32 SH or a zeroed Norm6 table when the file has no `f_rest_*`, no chunks).
  Upstream assets have no `OnDestroy`, so `DestroyRuntimeAsset` destroys the TextAssets explicitly; `GeneratedObject` calls it.
  The TextAssets must live as long as the object: the renderer re-uploads on every `OnEnable`.
- `SplatCaptureFeature` (Render Graph only) runs only while a capture is pending, at `AfterRenderingTransparents`:
  1. prime a D32 target with the scene depth (`Hidden/SplatPresso/Scene Depth Prime`),
  2. draw every visible splat renderer's quads with `Hidden/SplatPresso/Splat Depth`, writing the splat-centre depth where
     alpha ≥ `m_DepthAlphaThreshold` (ZTest LEqual, so scene geometry occludes splats; draw order is irrelevant),
  3. blit the camera colour, 4. async-read both back, linearise to eye depth (reversed-Z aware), flip rows so row 0 is the top.
  The pose/FOV are recorded in the same frame that is rendered. `requiresIntermediateTexture = true`.
  Guards: MSAA > 1, XR rendering, no async readback, `GsInternals.Validate` failure → the request fails with a message.
  A watchdog fails a request that no renderer consumed within 30 frames / 2 s.
- `GsInternals` is the only file touching non-public renderer members: it reads `GaussianSplatRenderer.m_GpuView`
  (per-splat view data, 40-byte stride, computed earlier in the same frame by the GS pass). The feature owns its own
  6-index quad buffer. `SplatPressoLinkXml` (an `IUnityLinkerProcessor`) emits a link.xml into `Library/` at build time
  so IL2CPP stripping keeps the reflected members.
- The depth shader includes `Packages/org.nesnausk.gaussian-splatting/Shaders/GaussianSplatting.hlsl`, which resolves by
  package name for git, registry, `file:` and embedded installs.
- `GaussianSplatRenderer` script default references only apply in the Inspector, so `ObjectSpawnService` assigns the shaders
  and compute shader explicitly from `SplatRendererResources` (filled in the editor by upstream asset GUIDs). The serialized
  references are also what makes those shaders ship in builds.

## 8. Conventions

- Every static field is reset in a `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`
  method: projects often disable domain reload, and surviving statics make fixes look like they did not apply.
- Keys: `ApiKeys` resolves override → environment → `~/.splatpresso/keys.json` → settings asset (GenPresso only) →
  `StreamingAssets/splatpresso.keys.json`. Key names are compared case- and separator-insensitively (a one-character name
  mismatch once silently returned null). Never log key values; use `ApiKeys.Mask`.
- Logging prefix `[SplatPresso]`. English identifiers, comments and UI strings.
- C# 9 (no file-scoped namespaces, no `record struct`, no global usings, no raw string literals).

## 9. Extension points

| Want to… | Use |
|---|---|
| Block requests while your UI is open | `SplatPressoRoot.RequestGate` (return false → `RequestRejected(…, Gated)`) |
| Start runs from your own UI or logic | `SplatPressoRoot.StartRun` / `RunAsync` / `SubmitText` |
| React to progress, objects and failures | `RunStarted`, `RunProgress`, `ObjectUpdated`, `RunRetry`, `RunCompleted`, `RunFailed`, `RequestRejected`; `ObjectSpawnService.Spawned` / `Removed` |
| Push-to-talk from XR controllers | `VoiceAgent.BeginTalk` / `EndTalk` or `VoiceAgent.ExternalTalkHeld` |
| Capture from another camera (e.g. a mono camera in XR) | `CaptureService.targetCamera`; `VoiceAgent.snapshotCamera` |
| Provide your own view / depth source | implement `ICaptureProvider` (`CaptureResult` needs JPEG, eye depth, pose, FOV, near/far) |
| Provide your own placer | implement `IObjectPlacer` (set each `Ready` object to `Placed` or `Skipped` with a reason) |
| Load meshes with something other than glTFast | set `MeshSpawnerRegistry.Current` to your `IMeshSpawner` |
| Add a voice backend | implement `IVoiceBackend` |
| Use different models | edit the `ModelRoute`s in settings (ordered GenPresso paths, fal endpoint, cost estimate, timeout) |
| Supply keys at runtime | `ApiKeys.SetOverride(ApiKeyKind.Genpresso, key)` |

## 10. Testing

- **EditMode** (`Tests/Editor`): `Bbox` (lenient 0–1000 parsing, pixel conversion), `SplatPlacement.Solve` on synthetic
  captures, PLY reading and runtime asset layout/bounds/destroy (with a parity check against the upstream editor creator
  when reachable), `GenpressoError` parsing, voice-turn mapping and clamping, `ApiKeys` resolution order (through a
  test hook for the user-profile path), `ModelRoute` defaults, `JsonUtil` Vector3/Quaternion.
- **PlayMode** (`Tests/Runtime`):
  1. render + capture: a runtime URP camera scene with a fixture splat 2 m ahead on a mesh floor; asserts RGBA and depth;
  2. end-to-end against `MockGenpressoServer` (an in-process `HttpListener` on 127.0.0.1) that serves chat
     (DECIDE / VERIFY / voice turn), the media queue with GenPresso's quirks (first candidate 404, COMPLETED then one
     200 "in progress", one 429 with `Retry-After`) and fixture downloads; runs Scene-aware and Direct to completion and asserts
     a spawned `GeneratedObject` with a valid splat asset;
  3. a voice text turn through `GenpressoVoiceBackend` that ends in a completed run.
- **Fixtures** (`Tests/Runtime/Fixtures`) are synthetic and small; regenerate them with `python Tools~/make_fixtures.py`
  (see the script header for options). They never contain real session images or keys.
- **Running**: add the package to `testables` in the consuming project's `Packages/manifest.json`
  (`"testables": ["com.splatpresso.voice-to-3dgs"]`), install the Test Framework, then use Window > General > Test Runner, or
  `Unity -batchmode -projectPath <project> -runTests -testPlatform EditMode|PlayMode -testResults results.xml`.
  The render/capture PlayMode test needs a GPU with D3D12/Vulkan/Metal (do not pass `-nographics`).

## 11. Still to verify on real hardware / accounts

- Which GenPresso media paths exist for each route (TripoSplat in particular); `Test + Probe Media Models` answers this with a key.
- Whether GenPresso now reports validation failures as `FAILED` (current docs) or as `COMPLETED` + 422 on the result
  (observed earlier). The client handles both.
- `image_url` + `input_audio` in one chat message and `response_format: json_schema` through GenPresso (fallbacks exist).
- `new TextAsset(span)` and the reflected field under IL2CPP with high stripping; capture with `IntermediateTextureMode = Auto`.
