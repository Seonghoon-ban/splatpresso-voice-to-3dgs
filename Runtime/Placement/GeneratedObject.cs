using System;
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using SplatPresso.Rendering;
using UnityEngine;

namespace SplatPresso.Placement
{
    /// <summary>
    /// Marker + owner component on the root of every object spawned by <see cref="ObjectSpawnService"/>.
    /// Owns the runtime splat asset (or the mesh importer resources) and frees them when the object is destroyed,
    /// however it is destroyed (eviction, <see cref="ObjectSpawnService.Remove"/>, scene unload, user code).
    /// </summary>
    /// <remarks>
    /// Runs in edit mode too so that OnDestroy frees the asset when objects are spawned and destroyed outside
    /// play mode (tests, editor tools).
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("")] // added by code only
    public sealed class GeneratedObject : MonoBehaviour
    {
        /// <summary>Display label (object name from the request, or the file name).</summary>
        public string label;
        /// <summary>Run id (session folder name) that generated the object; null for direct spawns.</summary>
        public string runId;
        /// <summary>Object id within the run; -1 for direct spawns.</summary>
        public int objectId = -1;
        /// <summary>Gaussian splat or mesh.</summary>
        public ObjectRepresentation representation;
        /// <summary>Local path of the model file the object was loaded from.</summary>
        public string modelPath;
        /// <summary>Session directory of the run; null for direct spawns.</summary>
        public string sessionDir;
        /// <summary>Notes of the placement solve ("ok" or the fallbacks taken); empty for direct spawns.</summary>
        public string placementNote;

        /// <summary>The pipeline record this object was placed from (null for direct spawns). Not serialized.</summary>
        [NonSerialized] public PlacedObjectResult source;

        GaussianSplatAsset m_SplatAsset;
        List<IDisposable> m_Owners;
        bool m_Released;

        // Set by the spawning service so it can drop the entry when the object is destroyed externally.
        internal Action<GeneratedObject> destroyedCallback;

        /// <summary>The runtime splat asset rendered by this object (null for meshes).</summary>
        public GaussianSplatAsset SplatAsset => m_SplatAsset;

        /// <summary>The splat renderer under this root, if any.</summary>
        public GaussianSplatRenderer SplatRenderer => GetComponentInChildren<GaussianSplatRenderer>(true);

        /// <summary>
        /// Takes ownership of a runtime splat asset: it is destroyed (with its data) together with this object.
        /// A previously attached, different asset is released first. Imported project assets are never destroyed.
        /// </summary>
        public void AttachSplatAsset(GaussianSplatAsset a)
        {
            if (ReferenceEquals(a, m_SplatAsset))
                return;
            var previous = m_SplatAsset;
            m_SplatAsset = a;
            if (!ReferenceEquals(previous, null))
                RuntimeSplatAssetFactory.DestroyRuntimeAsset(previous);
            if (m_Released && !ReferenceEquals(a, null))
            {
                // attached after destruction started: release right away rather than leak
                RuntimeSplatAssetFactory.DestroyRuntimeAsset(a);
                m_SplatAsset = null;
            }
        }

        /// <summary>Takes ownership of a disposable (e.g. a glTF importer); disposed when this object is destroyed.</summary>
        public void AttachOwner(IDisposable owner)
        {
            if (owner == null)
                return;
            if (m_Released)
            {
                DisposeQuietly(owner);
                return;
            }
            m_Owners ??= new List<IDisposable>();
            if (!m_Owners.Contains(owner))
                m_Owners.Add(owner);
        }

        void OnDestroy()
        {
            var cb = destroyedCallback;
            destroyedCallback = null;
            if (cb != null)
            {
                try
                {
                    cb(this);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            Release();
        }

        void Release()
        {
            if (m_Released)
                return;
            m_Released = true;
            // The renderer on the child is disabled/destroyed in the same frame, which is what
            // DestroyRuntimeAsset requires.
            if (!ReferenceEquals(m_SplatAsset, null))
            {
                RuntimeSplatAssetFactory.DestroyRuntimeAsset(m_SplatAsset);
                m_SplatAsset = null;
            }
            if (m_Owners != null)
            {
                foreach (var owner in m_Owners)
                    DisposeQuietly(owner);
                m_Owners = null;
            }
        }

        static void DisposeQuietly(IDisposable d)
        {
            try
            {
                d.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Releasing a generated object's resources failed: {e.Message}");
            }
        }
    }
}
