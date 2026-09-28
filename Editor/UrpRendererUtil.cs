using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SplatPresso.Rendering;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SplatPresso.EditorTools
{
    /// <summary>
    /// Finds every URP renderer the project can render with and installs renderer features the way URP's own
    /// editor does. Used by Setup (writes) and the validator (reads).
    /// </summary>
    public static class UrpRendererUtil
    {
        /// <summary>Full name of upstream's splat render feature. It is <c>internal</c>, so it is found by name.</summary>
        public const string GsUrpFeatureTypeName = "GaussianSplatting.Runtime.GaussianSplatURPFeature";
        /// <summary>The research fork's capture feature that <see cref="SplatCaptureFeature"/> replaces.</summary>
        public const string LegacyCaptureFeatureTypeName = "GaussianSplatting.Runtime.GaussianSplatCaptureFeature";

        /// <summary>The render pipeline one quality level actually uses.</summary>
        public sealed class QualityLevelPipeline
        {
            public int level;
            public string name;
            /// <summary>The level's own pipeline asset, or null when it inherits the default.</summary>
            public RenderPipelineAsset overrideAsset;
            /// <summary>Override, else GraphicsSettings.defaultRenderPipeline (null = Built-in).</summary>
            public RenderPipelineAsset effective;
        }

        /// <summary>
        /// Upstream's GaussianSplatURPFeature type, or null when GS is missing or was compiled without URP support
        /// (GS only defines it under GS_ENABLE_URP). TypeCache includes internal types.
        /// </summary>
        public static Type GsUrpFeatureType =>
            TypeCache.GetTypesDerivedFrom<ScriptableRendererFeature>().FirstOrDefault(t => t.FullName == GsUrpFeatureTypeName);

        /// <summary>The effective pipeline of every quality level.</summary>
        public static List<QualityLevelPipeline> GetQualityLevelPipelines()
        {
            var list = new List<QualityLevelPipeline>();
            var def = GraphicsSettings.defaultRenderPipeline;
            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                var ov = QualitySettings.GetRenderPipelineAssetAt(i);
                list.Add(new QualityLevelPipeline { level = i, name = names[i], overrideAsset = ov, effective = ov != null ? ov : def });
            }
            return list;
        }

        /// <summary>Distinct URP assets in use: the default pipeline plus every quality-level override.</summary>
        public static List<UniversalRenderPipelineAsset> FindUrpAssets()
        {
            var list = new List<UniversalRenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset d)
                list.Add(d);
            for (int i = 0; i < QualitySettings.names.Length; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset q && !list.Contains(q))
                    list.Add(q);
            return list;
        }

        /// <summary>
        /// Every renderer data referenced by the URP assets in use (all quality levels + default). Installing on all
        /// of them makes per-camera renderer overrides a non-issue.
        /// </summary>
        public static List<ScriptableRendererData> FindUrpRenderers()
        {
            var list = new List<ScriptableRendererData>();
            foreach (var asset in FindUrpAssets())
            {
                // rendererDataList is a ReadOnlySpan (cannot live in an iterator), so copy into the list here.
                var span = asset.rendererDataList;
                for (int i = 0; i < span.Length; i++)
                    if (span[i] != null && !list.Contains(span[i]))
                        list.Add(span[i]);
            }
            return list;
        }

        /// <summary>
        /// False for assets Setup must not modify: not persistent, inside an immutable package (git/registry/tarball),
        /// or locked by version control.
        /// </summary>
        public static bool IsWritable(Object asset, out string reason)
        {
            reason = null;
            if (asset == null || !EditorUtility.IsPersistent(asset))
            {
                reason = "not a project asset";
                return false;
            }
            string path = AssetDatabase.GetAssetPath(asset);
            var package = PackageInfo.FindForAssetPath(path);
            if (package != null && package.source != PackageSource.Embedded && package.source != PackageSource.Local)
            {
                reason = $"inside the read-only package {package.name}";
                return false;
            }
            if (!AssetDatabase.IsOpenForEdit(path))
            {
                reason = "locked by version control";
                return false;
            }
            return true;
        }

        /// <summary>The first feature of exactly <paramref name="type"/> on the renderer, or null.</summary>
        public static ScriptableRendererFeature FindFeature(ScriptableRendererData rendererData, Type type)
        {
            if (rendererData == null || type == null)
                return null;
            return rendererData.rendererFeatures.FirstOrDefault(f => f != null && f.GetType() == type);
        }

        /// <summary>True when a feature whose type has the given full name is on the renderer.</summary>
        public static bool HasFeatureNamed(ScriptableRendererData rendererData, string fullTypeName) =>
            rendererData != null && rendererData.rendererFeatures.Any(f => f != null && f.GetType().FullName == fullTypeName);

        /// <summary>Index of the first feature of <paramref name="type"/>, or -1.</summary>
        public static int IndexOf(ScriptableRendererData rendererData, Type type)
        {
            if (rendererData == null || type == null)
                return -1;
            var features = rendererData.rendererFeatures;
            for (int i = 0; i < features.Count; i++)
                if (features[i] != null && features[i].GetType() == type)
                    return i;
            return -1;
        }

        /// <summary>
        /// Adds a feature exactly like URP's ScriptableRendererDataEditor.AddComponent (verified against the URP 17
        /// package source): AddObjectToAsset -> TryGetGUIDAndLocalFileIdentifier -> append to BOTH
        /// <c>m_RendererFeatures</c> (object reference) and <c>m_RendererFeatureMap</c> (local file id, long). The map is
        /// serialized as hex int64s - never hand-edit it; go through SerializedProperty only. Undo-aware.
        /// </summary>
        public static ScriptableRendererFeature AddFeature(ScriptableRendererData rendererData, Type type, Action<ScriptableRendererFeature> init)
        {
            var so = new SerializedObject(rendererData);
            so.Update();
            var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(type);
            feature.name = type.Name;
            init?.Invoke(feature);
            Undo.RegisterCreatedObjectUndo(feature, "Add Renderer Feature");
            if (EditorUtility.IsPersistent(rendererData))
                AssetDatabase.AddObjectToAsset(feature, rendererData);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var features = so.FindProperty("m_RendererFeatures");
            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
            var map = so.FindProperty("m_RendererFeatureMap");
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(rendererData);
            return feature;
        }

        /// <summary>
        /// Moves <paramref name="first"/> in front of <paramref name="second"/> when it is behind it, on both serialized
        /// lists in one SerializedObject. Returns true when something moved. SplatCaptureFeature must come AFTER
        /// GaussianSplatURPFeature: it reuses the per-splat view data the splat pass computed earlier in the same frame.
        /// </summary>
        public static bool EnsureOrder(ScriptableRendererData rendererData, Type first, Type second)
        {
            int a = IndexOf(rendererData, first);
            int b = IndexOf(rendererData, second);
            if (a < 0 || b < 0 || a < b)
                return false;
            var so = new SerializedObject(rendererData);
            var features = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            if (!features.MoveArrayElement(a, b))
                return false;
            if (map != null && map.arraySize > Math.Max(a, b))
                map.MoveArrayElement(a, b);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(rendererData);
            return true;
        }

        /// <summary>Rebuilds the renderer's passes (URP's SetDirty) and saves the asset.</summary>
        public static void SaveRendererData(ScriptableRendererData rendererData)
        {
            rendererData.SetDirty();
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssetIfDirty(rendererData);
        }

        /// <summary>
        /// Copies the settings' depth alpha threshold onto every writable SplatCaptureFeature (the settings asset is
        /// the single source of truth; the feature keeps a serialized copy for rendering). Returns how many changed.
        /// </summary>
        public static int SyncDepthAlphaThreshold(float threshold)
        {
            int changed = 0;
            foreach (var rd in FindUrpRenderers())
            {
                if (!(FindFeature(rd, typeof(SplatCaptureFeature)) is SplatCaptureFeature cap) || !IsWritable(rd, out _))
                    continue;
                if (Mathf.Approximately(cap.m_DepthAlphaThreshold, threshold))
                    continue;
                var so = new SerializedObject(cap);
                var p = so.FindProperty("m_DepthAlphaThreshold");
                if (p == null)
                    continue;
                p.floatValue = threshold;
                so.ApplyModifiedProperties();
                SaveRendererData(rd);
                changed++;
            }
            return changed;
        }

        // ------------------------------------------------------------------------------------------
        // Render Graph

        /// <summary>URP's RenderGraphSettings, even while URP is not (yet) the active pipeline.</summary>
        public static bool TryGetRenderGraphSettings(out RenderGraphSettings settings)
        {
            settings = null;
            try
            {
                if (EditorGraphicsSettings.TryGetRenderPipelineSettingsForPipeline<RenderGraphSettings, UniversalRenderPipeline>(out settings) && settings != null)
                    return true;
            }
            catch (Exception) { /* no URP global settings registered yet */ }
            try
            {
                if (GraphicsSettings.TryGetRenderPipelineSettings(out settings) && settings != null)
                    return true;
            }
            catch (Exception) { /* not the active pipeline */ }
            settings = null;
            return false;
        }

        /// <summary>True/false for URP's Render Graph compatibility mode, or null when it cannot be read.</summary>
        public static bool? IsRenderGraphCompatibilityMode() =>
            TryGetRenderGraphSettings(out var s) ? s.enableRenderCompatibilityMode : (bool?)null;

        /// <summary>
        /// Turns compatibility mode off (both GS features implement only RecordRenderGraph, so nothing renders in
        /// compatibility mode). Returns true when the setting changed.
        /// </summary>
        public static bool EnableRenderGraph()
        {
            if (!TryGetRenderGraphSettings(out var s) || !s.enableRenderCompatibilityMode)
                return false;
            s.enableRenderCompatibilityMode = false;
            var globalSettings = EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>();
            if (globalSettings != null)
            {
                EditorUtility.SetDirty(globalSettings);
                AssetDatabase.SaveAssetIfDirty(globalSettings);
            }
            return true;
        }

        /// <summary>
        /// Best effort: creates/registers URP's global settings asset right away (URP otherwise does it the first time
        /// the pipeline renders), so a fresh Built-in project switched to URP by Setup can have its Render Graph
        /// setting checked in the same call. Calls URP's internal <c>UniversalRenderPipelineGlobalSettings.Ensure</c>
        /// by reflection and silently does nothing if that API changed.
        /// </summary>
        public static bool TryEnsureUrpGlobalSettings()
        {
            try
            {
                // The type itself is internal in URP 17, hence the lookup by name.
                var type = typeof(UniversalRenderPipeline).Assembly.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings");
                var ensure = type?.GetMethod("Ensure",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(bool) }, null);
                if (ensure == null)
                    return false;
                return ensure.Invoke(null, new object[] { true }) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
