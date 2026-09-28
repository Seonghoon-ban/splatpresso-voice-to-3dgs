using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GaussianSplatting.Runtime;
using SplatPresso.Rendering;
using UnityEngine;

namespace SplatPresso.Placement
{
    /// <summary>
    /// <see cref="IObjectPlacer"/> implementation: loads generated models (.ply gaussian splats, or .glb meshes
    /// through the optional glTFast integration), solves their scene placement (<see cref="SplatPlacement"/>)
    /// and spawns them. Splat objects are built at runtime without a prefab.
    /// </summary>
    /// <remarks>
    /// Spawned hierarchy: root <c>Splat_&lt;label&gt;</c> / <c>Mesh_&lt;label&gt;</c> (ground-contact pivot, yaw,
    /// uniform scale, <see cref="GeneratedObject"/>) with a child <c>Content</c> that carries the model and the
    /// content transform from <see cref="PlacementTuning"/> (single source of truth for the placement math and
    /// the spawned transform). Model loading runs on the main thread.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Object Spawn Service")]
    public sealed class ObjectSpawnService : MonoBehaviour, IObjectPlacer
    {
        /// <summary>Reason passed to <see cref="Removed"/> when the spawn cap evicts an object.</summary>
        public const string ReasonSpawnCap = "spawn_cap";
        /// <summary>Reason passed to <see cref="Removed"/> by <see cref="ClearAll"/>.</summary>
        public const string ReasonCleared = "cleared";
        /// <summary>Reason passed to <see cref="Removed"/> when an object was destroyed by other code.</summary>
        public const string ReasonDestroyed = "destroyed";

        const string kMeshNeedsGltf = "Mesh mode needs glTFast (install via SplatPresso > Install or Repair Dependencies)";

        /// <summary>Settings (placement tuning, spawn cap); null uses <see cref="SplatPressoSettings.Active"/>.</summary>
        [Tooltip("Empty = the active SplatPresso settings.")]
        public SplatPressoSettings settings;

        /// <summary>
        /// Gaussian Splatting shaders assigned to every spawned renderer. Auto-filled in the editor; keeping them
        /// referenced here also includes them in player builds.
        /// </summary>
        [Tooltip("Gaussian Splatting shaders for spawned renderers (auto-filled in the editor; referenced here so builds include them).")]
        public SplatRendererResources rendererResources = new SplatRendererResources();

        /// <summary>Optional: final positions are synced to the preview boxes, which are removed on spawn.</summary>
        [Tooltip("Optional: preview boxes are synced to the final placement and removed when the object spawns.")]
        public PlacementPreviewService previewService;

        /// <summary>Optional parent for spawned objects (should be unscaled); null = scene root.</summary>
        [Tooltip("Optional parent for spawned objects. Empty = scene root.")]
        public Transform spawnParent;

        /// <summary>Raised after an object was spawned.</summary>
        public event Action<GeneratedObject> Spawned;

        /// <summary>
        /// Raised when a spawned object leaves the list: <see cref="ReasonSpawnCap"/>, <see cref="ReasonCleared"/>,
        /// <see cref="ReasonDestroyed"/> or the reason given to <see cref="Remove"/>. The object is still alive
        /// during the callback (destroyed right after, except for <see cref="ReasonDestroyed"/>).
        /// </summary>
        public event Action<GeneratedObject, string> Removed;

        readonly List<GeneratedObject> m_Spawned = new List<GeneratedObject>();
        GeneratedObject m_LastSpawned;
        PlacementTuning m_DefaultTuning;
        bool m_WarnedHiddenOverCap;

        SplatPressoSettings Settings => settings != null ? settings : SplatPressoSettings.Active;

        PlacementTuning Tuning
        {
            get
            {
                var s = Settings;
                if (s != null && s.placement != null)
                    return s.placement;
                return m_DefaultTuning ??= new PlacementTuning();
            }
        }

        /// <summary>Spawned objects that still exist, oldest first.</summary>
        public IReadOnlyList<GeneratedObject> SpawnedObjects
        {
            get
            {
                Prune();
                return m_Spawned;
            }
        }

        /// <summary>The most recently spawned object that still exists, or null.</summary>
        public GeneratedObject LastSpawned
        {
            get
            {
                if (m_LastSpawned != null)
                    return m_LastSpawned;
                Prune();
                m_LastSpawned = m_Spawned.Count > 0 ? m_Spawned[m_Spawned.Count - 1] : null;
                return m_LastSpawned;
            }
        }

        // ---- IObjectPlacer ------------------------------------------------------------------------------------

        /// <summary>
        /// Spawns every <see cref="ObjectStatus.Ready"/> object of the result: sets it to
        /// <see cref="ObjectStatus.Placed"/>, or to <see cref="ObjectStatus.Skipped"/> with a reason (siblings
        /// continue). On cancel, objects already spawned stay.
        /// </summary>
        public async Awaitable PlaceAsync(PlacementResult result, CancellationToken ct)
        {
            if (result == null || result.objects == null)
                return;

            var capture = result.capture;
            if (capture == null)
            {
                foreach (var o in result.objects)
                {
                    if (o == null || o.status != ObjectStatus.Ready)
                        continue;
                    o.status = ObjectStatus.Skipped;
                    o.skipReason = "no capture data in result";
                }
                Debug.LogWarning("[SplatPresso] ObjectSpawnService: result has no capture data; nothing placed");
                return;
            }

            var tuning = Tuning;
            float[] genDepth = LoadGenDepth(result.generatedDepthPath, capture.width, capture.height);

            int placed = 0, skipped = 0;
            foreach (var obj in result.objects)
            {
                if (ct.IsCancellationRequested)
                    break; // already spawned objects stay
                if (obj == null || obj.status != ObjectStatus.Ready)
                    continue;
                if (string.IsNullOrEmpty(obj.modelPath) || !File.Exists(obj.modelPath))
                {
                    Skip(obj, "model file missing");
                    skipped++;
                    continue;
                }

                try
                {
                    bool isMesh = obj.representation == ObjectRepresentation.Mesh;
                    GeneratedObject go = isMesh
                        ? await PlaceMeshAsync(obj, result, capture, genDepth, tuning, ct)
                        : PlaceSplat(obj, result, capture, genDepth, tuning);
                    if (go == null)
                    {
                        skipped++; // PlaceMeshAsync already recorded the reason
                        continue;
                    }
                    obj.status = ObjectStatus.Placed;
                    placed++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Skip(obj, e.Message);
                    skipped++;
                    Debug.LogWarning($"[SplatPresso] Skipped '{obj.name}': {e.Message}");
                }

                try
                {
                    await Awaitable.NextFrameAsync(ct); // keep the UI alive between objects
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            Debug.Log($"[SplatPresso] Placement done: {placed} placed, {skipped} skipped");
        }

        GeneratedObject PlaceSplat(PlacedObjectResult obj, PlacementResult result, CaptureResult capture, float[] genDepth, PlacementTuning tuning)
        {
            EnsureRendererResources();
            GaussianSplatAsset asset = RuntimeSplatAssetFactory.CreateFromFile(obj.modelPath, $"gen_{obj.id}_{obj.name}", out bool hasSH);
            if (asset == null)
                throw new InvalidOperationException("splat file parsed to 0 splats");
            bool consumed = false;
            try
            {
                Vector3 contentSize = ContentBoundsSize(asset.boundsMax - asset.boundsMin, tuning.contentRotationEuler, tuning.contentScale);
                bool[] mask = LoadObjectMask(obj.cutoutPath, capture.width, capture.height);
                var sol = SplatPlacement.Solve(capture, obj.BboxGenerated, obj.sizeHintM, genDepth, mask, contentSize, tuning);
                if (!sol.valid)
                    throw new InvalidOperationException($"placement solve failed: {sol.note}");

                if (previewService != null)
                    previewService.SyncFinal(obj, sol.position, sol.rotation);
                consumed = true; // SpawnSplatAsset owns the asset from here on, even if it throws
                var go = SpawnSplatAsset(asset, hasSH, sol.position, sol.rotation, sol.uniformScale, obj.name, tuning,
                    o => Describe(o, obj, result, sol.note));
                if (previewService != null)
                    previewService.RemoveFor(obj); // the real object replaces the placeholder
                Debug.Log($"[SplatPresso] Placed '{obj.name}' at {sol.position} scale {sol.uniformScale:F3} ({sol.note})");
                return go;
            }
            finally
            {
                if (!consumed)
                    RuntimeSplatAssetFactory.DestroyRuntimeAsset(asset);
            }
        }

        async Awaitable<GeneratedObject> PlaceMeshAsync(PlacedObjectResult obj, PlacementResult result, CaptureResult capture,
            float[] genDepth, PlacementTuning tuning, CancellationToken ct)
        {
            var spawner = MeshSpawnerRegistry.Current;
            if (spawner == null)
            {
                Skip(obj, kMeshNeedsGltf);
                Debug.LogWarning($"[SplatPresso] Skipped '{obj.name}': {kMeshNeedsGltf}");
                return null;
            }

            MeshSpawnHandle handle = await spawner.SpawnAsync(obj.modelPath, obj.name, ct);
            if (handle == null || handle.root == null)
            {
                ReleaseHandle(handle);
                throw new InvalidOperationException("the mesh loader returned no model");
            }
            bool consumed = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                Bounds local = handle.localBounds.size.sqrMagnitude > 0f ? handle.localBounds : CalculateLocalBounds(handle.root.transform);
                if (local.size.sqrMagnitude <= 0f)
                    throw new InvalidOperationException("the mesh has no renderable geometry");

                Vector3 contentSize = ContentBoundsSize(local.size, tuning.meshContentRotationEuler, Vector3.one);
                bool[] mask = LoadObjectMask(obj.cutoutPath, capture.width, capture.height);
                var sol = SplatPlacement.Solve(capture, obj.BboxGenerated, obj.sizeHintM, genDepth, mask, contentSize, tuning, isMesh: true);
                if (!sol.valid)
                    throw new InvalidOperationException($"placement solve failed: {sol.note}");

                if (previewService != null)
                    previewService.SyncFinal(obj, sol.position, sol.rotation);
                consumed = true; // SpawnMeshHandle owns the handle from here on
                var go = SpawnMeshHandle(handle, local, sol.position, sol.rotation, sol.uniformScale, obj.name, tuning,
                    o => Describe(o, obj, result, sol.note));
                if (previewService != null)
                    previewService.RemoveFor(obj);
                Debug.Log($"[SplatPresso] Placed mesh '{obj.name}' at {sol.position} scale {sol.uniformScale:F3} ({sol.note})");
                return go;
            }
            finally
            {
                if (!consumed)
                    ReleaseHandle(handle);
            }
        }

        static void Describe(GeneratedObject o, PlacedObjectResult obj, PlacementResult result, string note)
        {
            o.runId = !string.IsNullOrEmpty(obj.runId) ? obj.runId : result.runId;
            o.objectId = obj.id;
            o.sessionDir = result.sessionDir;
            o.modelPath = obj.modelPath;
            o.placementNote = note;
            o.source = obj;
        }

        void Skip(PlacedObjectResult obj, string reason)
        {
            obj.status = ObjectStatus.Skipped;
            obj.skipReason = reason;
            if (previewService != null)
                previewService.RemoveFor(obj);
        }

        // ---- direct spawning ----------------------------------------------------------------------------------

        /// <summary>
        /// Loads a splat file (.ply) and spawns it with an explicit pose (no placement solve), e.g. for debugging.
        /// The pivot is the bottom of the content bounds.
        /// </summary>
        public Awaitable<GeneratedObject> SpawnSplatFromFileAsync(string plyPath, Vector3 position, Quaternion rotation, float uniformScale,
            string label, CancellationToken ct = default)
        {
            var src = new AwaitableCompletionSource<GeneratedObject>();
            try
            {
                ct.ThrowIfCancellationRequested();
                EnsureRendererResources();
                if (string.IsNullOrEmpty(label))
                    label = Path.GetFileNameWithoutExtension(plyPath);
                var asset = RuntimeSplatAssetFactory.CreateFromFile(plyPath, label, out bool hasSH);
                if (asset == null)
                    throw new InvalidOperationException($"[SplatPresso] Failed to load splat file: {plyPath}");
                string path = plyPath;
                var go = SpawnSplatAsset(asset, hasSH, position, rotation, uniformScale, label, Tuning, o => o.modelPath = path);
                src.SetResult(go);
            }
            catch (Exception e)
            {
                src.SetException(e);
            }
            return src.Awaitable;
        }

        /// <summary>Destroys a spawned object (raises <see cref="Removed"/> with <paramref name="reason"/>).</summary>
        public void Remove(GeneratedObject obj, string reason)
        {
            if (obj == null)
                return;
            bool tracked = m_Spawned.Remove(obj);
            obj.destroyedCallback = null;
            if (m_LastSpawned == obj)
                m_LastSpawned = null;
            if (tracked)
                RaiseRemoved(obj, string.IsNullOrEmpty(reason) ? "removed" : reason);
            ImageUtil.DestroySafe(obj.gameObject);
        }

        /// <summary>Destroys every spawned object (raises <see cref="Removed"/> with <see cref="ReasonCleared"/>).</summary>
        public void ClearAll()
        {
            Prune();
            var all = m_Spawned.ToArray();
            foreach (var o in all)
                Remove(o, ReasonCleared);
            m_Spawned.Clear();
            m_LastSpawned = null;
        }

        // ---- spawning -----------------------------------------------------------------------------------------

        // Takes ownership of the asset (released on failure, or with the object).
        GeneratedObject SpawnSplatAsset(GaussianSplatAsset asset, bool hasSH, Vector3 position, Quaternion rotation, float uniformScale,
            string label, PlacementTuning tuning, Action<GeneratedObject> describe)
        {
            var rootGo = new GameObject($"Splat_{label}");
            GeneratedObject gen = null;
            try
            {
                // Ownership first: from here on destroying the root frees the asset.
                gen = rootGo.AddComponent<GeneratedObject>();
                gen.AttachSplatAsset(asset);
                gen.label = label;
                gen.representation = ObjectRepresentation.GaussianSplat;
                describe?.Invoke(gen);
                PlaceRoot(rootGo.transform, position, rotation, uniformScale);

                // Content child carries the TripoSplat -> Unity convention (3DGS is Y-down), taken from the tuning so
                // the placement math (ContentBoundsSize) and the spawned transform can never drift apart.
                var contentGo = new GameObject("Content");
                contentGo.SetActive(false); // no OnEnable until the shader references are in place
                var content = contentGo.transform;
                content.SetParent(rootGo.transform, false);
                content.localRotation = Quaternion.Euler(tuning.contentRotationEuler);
                content.localScale = tuning.contentScale;

                var renderer = contentGo.AddComponent<GaussianSplatRenderer>();
                rendererResources.ApplyTo(renderer);
                renderer.m_SHOrder = hasSH ? 3 : 0;
                // Activate before assigning the asset: OnEnable then only sets up materials/sorter, and the explicit
                // Update() below uploads the asset exactly once, in this frame (assigning first would upload twice).
                contentGo.SetActive(true);
                renderer.m_Asset = asset;
                renderer.Update();

                // Pivot: put the content bounds bottom at the root origin. Corners are transformed by the Content
                // child's local rotation/scale into root space; root scale (which carries uniformScale) then applies
                // uniformly on top, so it must not be included here. Only Y is re-centred (TripoSplat output is
                // XZ-centred already).
                Vector3 bmin = asset.boundsMin, bmax = asset.boundsMax;
                float minY = float.PositiveInfinity;
                for (int i = 0; i < 8; ++i)
                {
                    var c = new Vector3(
                        (i & 1) == 0 ? bmin.x : bmax.x,
                        (i & 2) == 0 ? bmin.y : bmax.y,
                        (i & 4) == 0 ? bmin.z : bmax.z);
                    c = content.localRotation * Vector3.Scale(c, content.localScale);
                    minY = Mathf.Min(minY, c.y);
                }
                float bottomRootLocal = content.localPosition.y + minY;
                content.localPosition += Vector3.up * -bottomRootLocal;
            }
            catch
            {
                if (gen == null)
                    RuntimeSplatAssetFactory.DestroyRuntimeAsset(asset);
                DestroyImmediateSafe(rootGo); // GeneratedObject.OnDestroy frees the asset
                throw;
            }
            Register(gen);
            return gen;
        }

        // Takes ownership of the handle (released on failure, or with the object).
        GeneratedObject SpawnMeshHandle(MeshSpawnHandle handle, Bounds local, Vector3 position, Quaternion rotation, float uniformScale,
            string label, PlacementTuning tuning, Action<GeneratedObject> describe)
        {
            var rootGo = new GameObject($"Mesh_{label}");
            GeneratedObject gen = null;
            try
            {
                gen = rootGo.AddComponent<GeneratedObject>();
                gen.AttachOwner(handle.owner);
                gen.label = label;
                gen.representation = ObjectRepresentation.Mesh;
                describe?.Invoke(gen);
                PlaceRoot(rootGo.transform, position, rotation, uniformScale);

                var content = handle.root.transform;
                content.name = "Content";
                content.SetParent(rootGo.transform, false);
                content.localRotation = Quaternion.Euler(tuning.meshContentRotationEuler);
                content.localScale = Vector3.one;
                content.localPosition = Vector3.zero;

                // Pivot: bottom-centre of the rotated bounds at the root origin. Unlike TripoSplat output, generated
                // meshes are not guaranteed to be centred, so X/Z are re-centred as well.
                Vector3 bmin = local.min, bmax = local.max;
                Vector3 rmin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 rmax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                for (int i = 0; i < 8; ++i)
                {
                    var c = new Vector3(
                        (i & 1) == 0 ? bmin.x : bmax.x,
                        (i & 2) == 0 ? bmin.y : bmax.y,
                        (i & 4) == 0 ? bmin.z : bmax.z);
                    c = content.localRotation * c;
                    rmin = Vector3.Min(rmin, c);
                    rmax = Vector3.Max(rmax, c);
                }
                content.localPosition = new Vector3(-(rmin.x + rmax.x) * 0.5f, -rmin.y, -(rmin.z + rmax.z) * 0.5f);
                handle.root.SetActive(true);
            }
            catch
            {
                if (gen == null)
                    ReleaseHandle(handle);
                else if (handle.root != null && handle.root.transform.parent != rootGo.transform)
                    DestroyImmediateSafe(handle.root);
                DestroyImmediateSafe(rootGo); // GeneratedObject.OnDestroy disposes the importer
                throw;
            }
            Register(gen);
            return gen;
        }

        void PlaceRoot(Transform root, Vector3 position, Quaternion rotation, float uniformScale)
        {
            if (spawnParent != null)
                root.SetParent(spawnParent, false);
            root.SetPositionAndRotation(position, rotation);
            // world scale = uniformScale even under a (discouraged) scaled parent
            Vector3 parentScale = root.parent != null ? root.parent.lossyScale : Vector3.one;
            root.localScale = new Vector3(
                uniformScale / NonZero(parentScale.x),
                uniformScale / NonZero(parentScale.y),
                uniformScale / NonZero(parentScale.z));
        }

        static float NonZero(float v) => Mathf.Abs(v) < 1e-6f ? 1f : v;

        void Register(GeneratedObject gen)
        {
            Prune();
            gen.destroyedCallback = HandleDestroyed;
            m_Spawned.Add(gen);
            m_LastSpawned = gen;
            RaiseSpawned(gen);
            EnforceSpawnCap();
        }

        // Spawn cap. Counts only objects that are still ACTIVE, and only ever evicts an active one.
        //
        // An earlier version evicted the oldest entry unconditionally. Applications that hide objects with
        // SetActive(false) rather than removing them (e.g. to switch between saved layouts) then silently lost
        // hidden objects the moment the cap was crossed, and nothing reported it. Inactive objects are therefore
        // never auto-evicted; the cap only bounds what is visible (and in GPU memory).
        void EnforceSpawnCap()
        {
            var s = Settings;
            int max = Mathf.Max(1, s != null ? s.maxSpawnedObjects : 24);
            while (CountActiveSpawned() > max)
            {
                GeneratedObject oldest = null;
                foreach (var e in m_Spawned)
                {
                    if (e != null && e.gameObject.activeInHierarchy)
                    {
                        oldest = e;
                        break;
                    }
                }
                if (oldest == null)
                {
                    // Everything over the cap is inactive - keep it and let memory grow.
                    if (!m_WarnedHiddenOverCap)
                    {
                        m_WarnedHiddenOverCap = true;
                        Debug.LogWarning($"[SplatPresso] Spawn cap ({max}) exceeded but every older object is inactive; keeping them. " +
                                         "Raise maxSpawnedObjects in the SplatPresso settings.");
                    }
                    break;
                }
                Debug.LogWarning($"[SplatPresso] Spawn cap reached ({max}): removing oldest active object '{oldest.name}'. " +
                                 "Raise maxSpawnedObjects in the SplatPresso settings to keep more.");
                // the reason separates a cap eviction from an explicit deletion
                Remove(oldest, ReasonSpawnCap);
            }
        }

        int CountActiveSpawned()
        {
            int n = 0;
            foreach (var e in m_Spawned)
                if (e != null && e.gameObject.activeInHierarchy)
                    n++;
            return n;
        }

        void HandleDestroyed(GeneratedObject obj)
        {
            if (m_LastSpawned == obj)
                m_LastSpawned = null;
            // While quitting (or leaving play mode) everything is torn down in undefined order: do not call into
            // listeners that may already be destroyed.
            if (m_Spawned.Remove(obj) && !s_Quitting)
                RaiseRemoved(obj, ReasonDestroyed);
        }

        static bool s_Quitting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Quitting = false;
            Application.quitting -= OnQuitting; // domain reload may be disabled: never subscribe twice
            Application.quitting += OnQuitting;
        }

        static void OnQuitting() => s_Quitting = true;

        void Prune()
        {
            m_Spawned.RemoveAll(o => o == null);
        }

        void RaiseSpawned(GeneratedObject obj)
        {
            var handlers = Spawned;
            if (handlers == null)
                return;
            foreach (Action<GeneratedObject> h in handlers.GetInvocationList())
            {
                try
                {
                    h(obj);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        void RaiseRemoved(GeneratedObject obj, string reason)
        {
            var handlers = Removed;
            if (handlers == null)
                return;
            foreach (Action<GeneratedObject, string> h in handlers.GetInvocationList())
            {
                try
                {
                    h(obj, reason);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        void EnsureRendererResources()
        {
            rendererResources ??= new SplatRendererResources();
#if UNITY_EDITOR
            if (!rendererResources.IsComplete)
                rendererResources = SplatRendererResources.FindInProject();
#endif
            if (!rendererResources.IsComplete)
                throw new InvalidOperationException("ObjectSpawnService.rendererResources is incomplete (Gaussian Splatting shaders not assigned). " +
                                                    "Run SplatPresso > Setup Scene.");
            if (!SystemInfo.supportsComputeShaders)
                throw new InvalidOperationException("this platform/graphics API does not support compute shaders, which Gaussian Splatting needs");
        }

        static void ReleaseHandle(MeshSpawnHandle handle)
        {
            if (handle == null)
                return;
            if (handle.root != null)
                DestroyImmediateSafe(handle.root);
            try
            {
                handle.owner?.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Releasing a mesh importer failed: {e.Message}");
            }
        }

        // Failed spawns are torn down immediately so nothing half-built renders for a frame.
        static void DestroyImmediateSafe(GameObject go)
        {
            if (go != null)
                DestroyImmediate(go);
        }

        void OnDestroy()
        {
            // Spawned objects own their data and outlive this service (they are independent scene objects);
            // only stop tracking them.
            foreach (var o in m_Spawned)
                if (o != null)
                    o.destroyedCallback = null;
            m_Spawned.Clear();
            m_LastSpawned = null;
        }

#if UNITY_EDITOR
        void Reset() => AutoFillResources();

        void OnValidate()
        {
            if (rendererResources != null && rendererResources.IsComplete)
                return;
            // AssetDatabase lookups are not safe inside OnValidate during imports: defer.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null)
                    AutoFillResources();
            };
        }

        void AutoFillResources()
        {
            if (UnityEditor.AssetDatabase.IsAssetImportWorkerProcess())
                return;
            if (rendererResources != null && rendererResources.IsComplete)
                return;
            var found = SplatRendererResources.FindInProject();
            // Nothing found (GS missing): leave the field alone so the scene is not dirtied on every validate.
            // Partial result: never overwrite a partial manual assignment with another partial one.
            if (!found.IsComplete && (IsEmpty(found) || (rendererResources != null && !IsEmpty(rendererResources))))
                return;
            rendererResources = found;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        static bool IsEmpty(SplatRendererResources r) =>
            r.splatShader == null && r.compositeShader == null && r.debugPointsShader == null &&
            r.debugBoxesShader == null && r.splatUtilities == null;
#endif

        // ---- helpers ------------------------------------------------------------------------------------------

        /// <summary>
        /// Size of a model's axis-aligned bounds after the content transform (rotate/scale the extents), in the
        /// placed root's unscaled space.
        /// </summary>
        /// <param name="rawSize">Bounds size in the model's own space.</param>
        /// <param name="contentRotationEuler">Content local rotation.</param>
        /// <param name="contentScale">Content local scale (sign ignored).</param>
        public static Vector3 ContentBoundsSize(Vector3 rawSize, Vector3 contentRotationEuler, Vector3 contentScale)
        {
            Vector3 e = Vector3.Scale(rawSize, new Vector3(
                Mathf.Abs(contentScale.x), Mathf.Abs(contentScale.y), Mathf.Abs(contentScale.z))) * 0.5f;
            Quaternion r = Quaternion.Euler(contentRotationEuler);
            Vector3 ax = r * new Vector3(e.x, 0, 0);
            Vector3 ay = r * new Vector3(0, e.y, 0);
            Vector3 az = r * new Vector3(0, 0, e.z);
            return 2f * new Vector3(
                Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
        }

        /// <summary>
        /// Bounds of all meshes under <paramref name="root"/> (inactive ones included) in root's local space,
        /// computed from mesh data, so it also works before the hierarchy is activated. Size zero when there are none.
        /// </summary>
        public static Bounds CalculateLocalBounds(Transform root)
        {
            bool any = false;
            var b = new Bounds();
            if (root == null)
                return b;
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null)
                    Encapsulate(ref b, ref any, mf.sharedMesh.bounds, toRoot * mf.transform.localToWorldMatrix);
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.sharedMesh != null)
                    Encapsulate(ref b, ref any, smr.sharedMesh.bounds, toRoot * smr.transform.localToWorldMatrix);
            return b;
        }

        static void Encapsulate(ref Bounds b, ref bool any, Bounds meshBounds, Matrix4x4 m)
        {
            Vector3 min = meshBounds.min, max = meshBounds.max;
            for (int i = 0; i < 8; ++i)
            {
                var c = m.MultiplyPoint3x4(new Vector3(
                    (i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y,
                    (i & 4) == 0 ? min.z : max.z));
                if (!any)
                {
                    b = new Bounds(c, Vector3.zero);
                    any = true;
                }
                else
                {
                    b.Encapsulate(c);
                }
            }
        }

        /// <summary>
        /// Relative depth PNG of the generated image -> float[0..1] (red channel) resampled to capture dims,
        /// row 0 = top (matches capture depth indexing). Null when missing/unreadable.
        /// </summary>
        public static float[] LoadGenDepth(string path, int w, int h)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || w <= 0 || h <= 0)
                return null;
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(path)))
                    return null;
                var depth = new float[w * h];
                for (int v = 0; v < h; ++v)
                {
                    float uvY = 1f - (v + 0.5f) / h; // texture v=1 is image top
                    int rowBase = v * w;
                    for (int u = 0; u < w; ++u)
                        depth[rowBase + u] = tex.GetPixelBilinear((u + 0.5f) / w, uvY).r;
                }
                return depth;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Failed to load generated depth: {e.Message}");
                return null;
            }
            finally
            {
                if (tex != null)
                    DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// Object mask from the cutout PNG (alpha &gt; 16/255), resampled to capture dims, row 0 = top. Only usable
        /// when the cutout covers the full generated frame (same aspect as the capture within 2%); returns null
        /// otherwise so the caller falls back to the bbox rect. Also used by the preview to refine positions.
        /// </summary>
        public static bool[] LoadObjectMask(string cutoutPath, int w, int h)
        {
            if (string.IsNullOrEmpty(cutoutPath) || !File.Exists(cutoutPath) || w <= 0 || h <= 0)
                return null;
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(cutoutPath)))
                    return null;
                // Aspect is a proxy for "full frame": a crop-based cutout (no segmentation) has another aspect.
                float texAspect = (float)tex.width / tex.height;
                float capAspect = (float)w / h;
                if (Mathf.Abs(texAspect - capAspect) / capAspect > 0.02f)
                    return null;
                const float threshold = 16f / 255f;
                var mask = new bool[w * h];
                for (int v = 0; v < h; ++v)
                {
                    float uvY = 1f - (v + 0.5f) / h; // texture v=1 is image top
                    int rowBase = v * w;
                    for (int u = 0; u < w; ++u)
                        mask[rowBase + u] = tex.GetPixelBilinear((u + 0.5f) / w, uvY).a > threshold;
                }
                return mask;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (tex != null)
                    DestroyImmediate(tex);
            }
        }
    }
}
