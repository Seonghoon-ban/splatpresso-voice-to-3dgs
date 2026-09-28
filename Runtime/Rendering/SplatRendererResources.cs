// SPDX-License-Identifier: MIT

using System;
using GaussianSplatting.Runtime;
using UnityEngine;

namespace SplatPresso.Rendering
{
    /// <summary>
    /// The shader/compute references a <see cref="GaussianSplatRenderer"/> needs. Upstream only fills them through
    /// the script's default references when the component is added in the Inspector, so renderers created from
    /// code (AddComponent) must get them explicitly. Keep an instance serialized on a scene component so the
    /// shaders are referenced and therefore included in player builds.
    /// </summary>
    [Serializable]
    public sealed class SplatRendererResources
    {
        /// <summary>"Gaussian Splatting/Render Splats".</summary>
        public Shader splatShader;
        /// <summary>"Hidden/Gaussian Splatting/Composite".</summary>
        public Shader compositeShader;
        /// <summary>"Gaussian Splatting/Debug/Render Points".</summary>
        public Shader debugPointsShader;
        /// <summary>"Gaussian Splatting/Debug/Render Boxes".</summary>
        public Shader debugBoxesShader;
        /// <summary>SplatUtilities.compute.</summary>
        public ComputeShader splatUtilities;

        /// <summary>True when every reference is assigned (the renderer silently renders nothing otherwise).</summary>
        public bool IsComplete =>
            splatShader != null && compositeShader != null && debugPointsShader != null &&
            debugBoxesShader != null && splatUtilities != null;

        /// <summary>
        /// Copies the references onto a renderer. Do this while its GameObject is inactive (before OnEnable),
        /// then assign the asset and activate: upstream's OnEnable skips GPU setup when references are missing.
        /// </summary>
        public void ApplyTo(GaussianSplatRenderer r)
        {
            if (r == null)
                throw new ArgumentNullException(nameof(r));
            r.m_ShaderSplats = splatShader;
            r.m_ShaderComposite = compositeShader;
            r.m_ShaderDebugPoints = debugPointsShader;
            r.m_ShaderDebugBoxes = debugBoxesShader;
            r.m_CSSplatUtilities = splatUtilities;
        }

#if UNITY_EDITOR
        // Asset GUIDs of upstream's .meta files (stable across git/registry/file installs).
        const string kSplatShaderGuid = "ed800126ae8844a67aad1974ddddd59c";       // Shaders/RenderGaussianSplats.shader
        const string kCompositeShaderGuid = "7e184af7d01193a408eb916d8acafff9";   // Shaders/GaussianComposite.shader
        const string kDebugPointsShaderGuid = "b44409fc67214394f8f47e4e2648425e"; // Shaders/GaussianDebugRenderPoints.shader
        const string kDebugBoxesShaderGuid = "4006f2680fd7c8b4cbcb881454c782be";  // Shaders/GaussianDebugRenderBoxes.shader
        const string kSplatUtilitiesGuid = "ec84f78b836bd4f96a105d6b804f08bd";    // Shaders/SplatUtilities.compute
        const string kGsShadersFolder = "Packages/org.nesnausk.gaussian-splatting/Shaders/";

        /// <summary>
        /// Editor only: locates upstream's shaders by GUID, then by package path, then by shader name.
        /// The result may be incomplete if GaussianSplatting is not installed (check <see cref="IsComplete"/>).
        /// </summary>
        public static SplatRendererResources FindInProject()
        {
            return new SplatRendererResources
            {
                splatShader = LoadShader(kSplatShaderGuid, "RenderGaussianSplats.shader", "Gaussian Splatting/Render Splats"),
                compositeShader = LoadShader(kCompositeShaderGuid, "GaussianComposite.shader", "Hidden/Gaussian Splatting/Composite"),
                debugPointsShader = LoadShader(kDebugPointsShaderGuid, "GaussianDebugRenderPoints.shader", "Gaussian Splatting/Debug/Render Points"),
                debugBoxesShader = LoadShader(kDebugBoxesShaderGuid, "GaussianDebugRenderBoxes.shader", "Gaussian Splatting/Debug/Render Boxes"),
                splatUtilities = LoadCompute(kSplatUtilitiesGuid, "SplatUtilities.compute", "SplatUtilities"),
            };
        }

        static Shader LoadShader(string guid, string fileName, string shaderName)
        {
            var s = LoadByGuidOrPath<Shader>(guid, fileName);
            return s != null ? s : Shader.Find(shaderName);
        }

        static ComputeShader LoadCompute(string guid, string fileName, string assetName)
        {
            var cs = LoadByGuidOrPath<ComputeShader>(guid, fileName);
            if (cs != null)
                return cs;
            foreach (string foundGuid in UnityEditor.AssetDatabase.FindAssets(assetName + " t:ComputeShader"))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(foundGuid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == assetName)
                    return UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }
            return null;
        }

        static T LoadByGuidOrPath<T>(string guid, string fileName) where T : UnityEngine.Object
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            T asset = string.IsNullOrEmpty(path) ? null : UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(kGsShadersFolder + fileName);
            return asset;
        }
#endif
    }
}
