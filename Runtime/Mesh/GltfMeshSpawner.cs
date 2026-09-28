using System;
using System.IO;
using System.Threading;
using GLTFast;
using SplatPresso.Placement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SplatPresso.Mesh
{
    /// <summary>
    /// Loads generated .glb meshes (Mesh mode, e.g. Rodin) at runtime with glTFast. Compiled only when glTFast is
    /// installed; registers itself as <see cref="MeshSpawnerRegistry.Current"/> at startup unless another spawner
    /// was registered first.
    /// </summary>
    /// <remarks>
    /// Players need glTFast's shader graphs in the build (see glTFast's project setup docs); a missing shader
    /// renders magenta.
    /// </remarks>
    public sealed class GltfMeshSpawner : IMeshSpawner
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterAtStartup() => Register();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterInEditor() => Register();
#endif

        static void Register()
        {
            // Never replace a spawner the application registered itself.
            if (MeshSpawnerRegistry.Current == null)
                MeshSpawnerRegistry.Current = new GltfMeshSpawner();
        }

        /// <summary>
        /// Loads the model and instantiates its main scene under a new, INACTIVE root GameObject (the caller places
        /// and activates it). The handle's owner is the <see cref="GltfImport"/>, which owns the meshes, materials
        /// and textures: dispose it when the object is destroyed.
        /// </summary>
        public async Awaitable<MeshSpawnHandle> SpawnAsync(string glbPath, string label, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(glbPath) || !File.Exists(glbPath))
                throw new FileNotFoundException("Mesh file not found", glbPath);
            ct.ThrowIfCancellationRequested();
            byte[] data = File.ReadAllBytes(glbPath);

            // The default defer agent keeps the frame rate stable but lives on a DontDestroyOnLoad GameObject,
            // which only works in play mode.
            var gltf = Application.isPlaying ? new GltfImport() : new GltfImport(deferAgent: new UninterruptedDeferAgent());
            var root = new GameObject(string.IsNullOrEmpty(label) ? Path.GetFileNameWithoutExtension(glbPath) : label);
            root.SetActive(false); // nothing renders until the caller has placed it
            bool ok = false;
            try
            {
                var importSettings = new ImportSettings { GenerateMipMaps = true, AnisotropicFilterLevel = 3 };
                // base URI so a .gltf with external buffers/images resolves them next to the file
                var baseUri = new Uri(Path.GetFullPath(glbPath));
                bool loaded = await gltf.Load(data, baseUri, importSettings, ct);
                ct.ThrowIfCancellationRequested(); // older glTFast versions return false instead of throwing
                if (!loaded)
                    throw new InvalidDataException($"glTFast could not load '{glbPath}' (see previous glTFast errors)");

                bool instantiated = await gltf.InstantiateMainSceneAsync(root.transform, ct);
                ct.ThrowIfCancellationRequested();
                if (!instantiated)
                    throw new InvalidDataException($"glTFast could not instantiate '{glbPath}'");

                var handle = new MeshSpawnHandle
                {
                    root = root,
                    owner = gltf,
                    localBounds = ObjectSpawnService.CalculateLocalBounds(root.transform),
                };
                ok = true;
                return handle;
            }
            finally
            {
                if (!ok)
                {
                    if (Application.isPlaying)
                        Object.Destroy(root);
                    else
                        Object.DestroyImmediate(root);
                    gltf.Dispose();
                }
            }
        }
    }
}
