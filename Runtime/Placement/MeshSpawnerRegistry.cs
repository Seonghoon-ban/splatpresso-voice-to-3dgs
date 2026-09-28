using System;
using System.Threading;
using UnityEngine;

namespace SplatPresso.Placement
{
    /// <summary>Loads a generated .glb into the scene (implemented by the optional glTFast integration).</summary>
    public interface IMeshSpawner
    {
        /// <summary>Instantiates the model; the returned handle's root is parented/placed by the caller.</summary>
        Awaitable<MeshSpawnHandle> SpawnAsync(string glbPath, string label, CancellationToken ct);
    }

    /// <summary>A spawned mesh model.</summary>
    public sealed class MeshSpawnHandle
    {
        /// <summary>Root GameObject of the instantiated model.</summary>
        public GameObject root;
        /// <summary>Keeps the importer's resources alive; disposed when the object is destroyed (may be null).</summary>
        public IDisposable owner;
        /// <summary>Bounds of the model's renderers in the root's local space.</summary>
        public Bounds localBounds;
    }

    /// <summary>
    /// Holds the mesh spawner used by Mesh mode. The runtime assembly does not depend on glTFast; the optional
    /// SplatPresso.Mesh assembly registers itself here when glTFast is installed.
    /// </summary>
    public static class MeshSpawnerRegistry
    {
        static IMeshSpawner s_Current;

        /// <summary>The registered spawner, or null when Mesh mode is unavailable (glTFast not installed).</summary>
        public static IMeshSpawner Current
        {
            get
            {
                // a spawner implemented as a UnityEngine.Object may have been destroyed
                if (s_Current is UnityEngine.Object o && o == null)
                    s_Current = null;
                return s_Current;
            }
            set => s_Current = value;
        }

        /// <summary>True when a spawner is registered.</summary>
        public static bool IsAvailable => Current != null;

        // Statics survive play sessions when domain reload is disabled. The registration itself is deliberately
        // NOT cleared: spawners register at SubsystemRegistration too, and the order of methods within one load
        // type is undefined, so clearing here could wipe a fresh registration. Only a spawner that is a destroyed
        // UnityEngine.Object (left over from the previous session) is dropped.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            if (s_Current is UnityEngine.Object o && o == null)
                s_Current = null;
        }
    }
}
