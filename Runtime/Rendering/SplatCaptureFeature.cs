// SPDX-License-Identifier: MIT
// Ported from the SplatPresso research fork of aras-p/UnityGaussianSplatting (GaussianSplatCaptureFeature),
// re-targeted at the UNMODIFIED upstream package: the splat-depth draw reads one internal field through
// GsInternals instead of a patched GaussianSplatRenderSystem.RenderSplatDepth.

using System;
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace SplatPresso.Rendering
{
    /// <summary>
    /// URP renderer feature that performs one-shot captures of {RGB, metric depth} for a camera, with gaussian
    /// splats included in the depth (the normal splat rendering never writes depth).
    /// </summary>
    /// <remarks>
    /// Register it on the URP renderer AFTER GaussianSplatURPFeature (SplatPresso > Setup Scene does this). It
    /// injects at AfterRenderingTransparents and reuses the per-splat view data the splat render computed earlier
    /// in the same frame for the same camera. It also works with no splats in the scene: the scene-depth prime
    /// alone yields mesh depth. Requirements: Render Graph (compatibility mode off), MSAA off, a mono (non-XR)
    /// camera, AsyncGPUReadback. The capture is taken before post-processing and UI.
    /// Usage: <c>SplatCaptureFeature.RequestCapture(camera, result => { ... });</c>
    /// </remarks>
    [DisallowMultipleRendererFeature("SplatPresso Capture")]
    public class SplatCaptureFeature : ScriptableRendererFeature
    {
        /// <summary>Name of the package's splat depth shader.</summary>
        public const string SplatDepthShaderName = "Hidden/SplatPresso/Splat Depth";
        /// <summary>Name of the package's scene depth prime shader.</summary>
        public const string SceneDepthPrimeShaderName = "Hidden/SplatPresso/Scene Depth Prime";

        /// <summary>The "Hidden/SplatPresso/Splat Depth" shader (auto-assigned in the editor when null).</summary>
        [Tooltip("Shader: " + SplatDepthShaderName)]
        public Shader m_SplatDepthShader;
        /// <summary>The "Hidden/SplatPresso/Scene Depth Prime" shader (auto-assigned in the editor when null).</summary>
        [Tooltip("Shader: " + SceneDepthPrimeShaderName)]
        public Shader m_SceneDepthPrimeShader;
        /// <summary>Gaussian falloff alpha above which a splat pixel writes depth (placement tuning default 0.3).</summary>
        [Range(0.01f, 1.0f)]
        [Tooltip("Gaussian falloff alpha threshold above which a splat pixel writes depth")]
        public float m_DepthAlphaThreshold = 0.3f;

        /// <summary>
        /// Override for readback row order. When null, rows are flipped if the graphics API renders render-texture
        /// UV top-down (D3D/Metal/Vulkan) - verified correct on D3D12. Set explicitly if captures come out
        /// vertically inverted.
        /// </summary>
        public static bool? FlipReadbackOverride;

        // Watchdog: a request nobody consumes (feature missing from the camera's renderer, camera not rendering,
        // Render Graph compatibility mode) used to stay pending forever, and every later RequestCapture failed.
        // A pending request is normally consumed in the frame it was made. Stale after 30 frames (but at least
        // 0.5 s, so very high frame rates do not shorten it to nothing) or after 2 s (but at least 2 frames, so a
        // single long frame such as a synchronous asset load is not a timeout).
        const int kPendingTimeoutFrames = 30;
        const double kPendingMinSecondsForFrameTimeout = 0.5;
        const double kPendingTimeoutSeconds = 2.0;
        const int kMinFramesForTimeTimeout = 2;
        const int kInFlightTimeoutFrames = 10;    // readbacks normally complete within 1-3 frames
        const double kInFlightTimeoutSeconds = 5.0;
        const int kActiveWindowFrames = 3;

        sealed class CaptureRequest
        {
            public Camera camera;
            public string cameraName;
            public Action<SplatCaptureResult> callback;
            public SplatCaptureResult result;
            public bool orthographic;
            public float[] rawDepth;
            public int depthWidth, depthHeight;
            public byte[] rgba;
            public int colorWidth, colorHeight;
            public bool hasDepth, hasColor, failed, done;
            public int stageFrame;      // frame of the request, then of the consume
            public double stageTime;
        }

        struct SplatDraw
        {
            public GraphicsBuffer view;
            public int count;
            public Matrix4x4 matrix;
        }

        static CaptureRequest s_Pending;
        static CaptureRequest s_InFlight;
        static int s_LastActiveFrame = -1;
        static bool s_WatchdogHooked;
        static string s_LastError;
        static bool s_WarnedGsInternals;

        // Domain reload may be disabled: surviving statics make fixes look like they did not apply, and a stale
        // pending request would block every capture of the next play session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            UnhookWatchdog();
            s_Pending = null;
            s_InFlight = null;
            s_LastActiveFrame = -1;
            s_LastError = null;
            s_WarnedGsInternals = false;
            FlipReadbackOverride = null;
        }

        static readonly int s_SceneDepthTexID = Shader.PropertyToID("_SplatPressoSceneDepthTex");
        static readonly int s_AlphaThresholdID = Shader.PropertyToID("_SplatPressoDepthAlphaThreshold");
        static readonly int s_SplatViewDataID = Shader.PropertyToID("_SplatViewData");

        /// <summary>
        /// Requests a one-shot capture of the given camera. The callback is invoked on the main thread a few frames
        /// later (async GPU readback) with the result, or with null on failure/cancel (see <see cref="LastError"/>).
        /// Only one capture can be pending or in flight at a time; returns false if one already is.
        /// </summary>
        public static bool RequestCapture(Camera cam, Action<SplatCaptureResult> onComplete)
        {
            CheckWatchdog();
            if (cam == null || onComplete == null)
                return false;
            if (s_Pending != null || s_InFlight != null)
                return false;
            s_Pending = new CaptureRequest
            {
                camera = cam,
                cameraName = cam.name,
                callback = onComplete,
                stageFrame = Time.frameCount,
                stageTime = Now,
            };
            HookWatchdog();
            return true;
        }

        /// <summary>True while a capture is pending or in flight. Also expires stale requests (watchdog).</summary>
        public static bool HasPendingCapture
        {
            get
            {
                CheckWatchdog();
                return s_Pending != null || s_InFlight != null;
            }
        }

        /// <summary>
        /// True if the feature ran on some URP renderer within the last few frames. False means no rendering
        /// camera uses a renderer with this feature: a capture request would never be served.
        /// </summary>
        public static bool IsActiveOnAnyRenderer => s_LastActiveFrame >= 0 && Time.frameCount - s_LastActiveFrame <= kActiveWindowFrames;

        /// <summary>Frame (<see cref="Time.frameCount"/>) in which the feature last ran for a game/scene camera, or -1.</summary>
        public static int LastActiveFrame => s_LastActiveFrame;

        /// <summary>Reason of the last failed or cancelled capture; null after a success.</summary>
        public static string LastError => s_LastError;

        /// <summary>
        /// Cancels the pending and/or in-flight capture; its callback receives null. Late GPU readbacks are ignored.
        /// </summary>
        public static void CancelPending(string reason)
        {
            CaptureRequest pending = s_Pending, inFlight = s_InFlight;
            if (pending == null && inFlight == null)
                return;
            string message = string.IsNullOrEmpty(reason) ? "Capture cancelled" : "Capture cancelled: " + reason;
            s_LastError = message;
            Debug.Log("[SplatPresso] " + message);
            Complete(pending, null);
            Complete(inFlight, null);
        }

        CapturePass m_Pass;
        Material m_MatSplatDepth;
        Material m_MatDepthPrime;
        RenderTexture m_DepthRT;
        RenderTexture m_ColorRT;
        RenderTexture m_DepthAttachmentRT;
        RTHandle m_DepthRTHandle;
        RTHandle m_ColorRTHandle;
        RTHandle m_DepthAttachmentHandle;
        GraphicsBuffer m_QuadIndices;
        MaterialPropertyBlock m_Mpb;
        readonly List<SplatDraw> m_Draws = new List<SplatDraw>();

        /// <inheritdoc/>
        public override void Create()
        {
            // requiresIntermediateTexture: with a renderer whose Intermediate Texture mode is Auto, the active
            // color/depth could otherwise be the back buffer, which cannot be sampled. The pass is only enqueued
            // while a capture is pending, so this costs nothing otherwise.
            m_Pass = new CapturePass(this)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents,
                requiresIntermediateTexture = true,
            };
#if UNITY_EDITOR
            if (m_SplatDepthShader == null)
                m_SplatDepthShader = Shader.Find(SplatDepthShaderName);
            if (m_SceneDepthPrimeShader == null)
                m_SceneDepthPrimeShader = Shader.Find(SceneDepthPrimeShaderName);
#endif
            if (!GsInternals.Validate(out string gsError) && !s_WarnedGsInternals)
            {
                s_WarnedGsInternals = true;
                Debug.LogWarning("[SplatPresso] " + gsError);
            }
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            if (cam != null && cam.cameraType != CameraType.Preview && cam.cameraType != CameraType.Reflection)
                s_LastActiveFrame = Time.frameCount;
            CheckWatchdog();

            CaptureRequest req = s_Pending;
            if (req == null || req.camera != cam || m_Pass == null)
                return;
            if (IsRenderGraphCompatibilityMode())
            {
                Fail(req, "Render Graph compatibility mode is enabled (Project Settings > Graphics > URP). SplatPresso capture " +
                          "and the Gaussian Splatting URP pass need Render Graph - turn compatibility mode off.");
                return;
            }
            if (!EnsurePrimeMaterial(out string error))
            {
                Fail(req, error);
                return;
            }
            renderer.EnqueuePass(m_Pass);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            ReleaseTargets();
            CoreUtils.Destroy(m_MatSplatDepth);
            m_MatSplatDepth = null;
            CoreUtils.Destroy(m_MatDepthPrime);
            m_MatDepthPrime = null;
            m_QuadIndices?.Dispose();
            m_QuadIndices = null;
            m_Draws.Clear();
            m_Pass = null;
        }

        bool EnsurePrimeMaterial(out string error)
        {
            if (m_SceneDepthPrimeShader == null)
            {
                error = "SplatCaptureFeature: the '" + SceneDepthPrimeShaderName + "' shader reference is missing - run SplatPresso > Setup Scene.";
                return false;
            }
            if (!m_SceneDepthPrimeShader.isSupported)
            {
                error = $"SplatCaptureFeature: '{SceneDepthPrimeShaderName}' is not supported on {SystemInfo.graphicsDeviceType}.";
                return false;
            }
            if (m_MatDepthPrime == null)
                m_MatDepthPrime = CoreUtils.CreateEngineMaterial(m_SceneDepthPrimeShader);
            error = null;
            return true;
        }

        bool EnsureSplatDepthMaterial(out string error)
        {
            if (m_SplatDepthShader == null)
            {
                error = "SplatCaptureFeature: the '" + SplatDepthShaderName + "' shader reference is missing - run SplatPresso > Setup Scene.";
                return false;
            }
            if (!m_SplatDepthShader.isSupported)
            {
                error = $"SplatCaptureFeature: '{SplatDepthShaderName}' is not supported on {SystemInfo.graphicsDeviceType} " +
                        "(needs compute + DXC: use Direct3D12, Vulkan or Metal).";
                return false;
            }
            if (m_MatSplatDepth == null)
                m_MatSplatDepth = CoreUtils.CreateEngineMaterial(m_SplatDepthShader);
            error = null;
            return true;
        }

        void EnsureQuadIndices()
        {
            if (m_QuadIndices != null && m_QuadIndices.IsValid())
                return;
            m_QuadIndices?.Dispose();
            // Same as the first quad of upstream's m_GpuIndexBuffer, so SV_VertexID is 0..3 like in its splat shader.
            m_QuadIndices = new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, sizeof(ushort)) { name = "SplatPressoQuadIndices" };
            m_QuadIndices.SetData(new ushort[] { 0, 1, 2, 1, 3, 2 });
        }

        // Targets are kept between captures and re-created only on resize (~40 MB at 2560x1330).
        void EnsureTargets(int width, int height)
        {
            // Linear projects: the sRGB target encodes the linear camera color, so the bytes are sRGB either way.
            GraphicsFormat colorFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                ? GraphicsFormat.R8G8B8A8_SRGB
                : GraphicsFormat.R8G8B8A8_UNorm;
            if (m_DepthRT != null && m_DepthRT.width == width && m_DepthRT.height == height &&
                m_ColorRT != null && m_ColorRT.graphicsFormat == colorFormat && m_DepthAttachmentRT != null)
                return;
            ReleaseTargets();
            m_DepthRT = new RenderTexture(width, height, 0, GraphicsFormat.R32_SFloat) { name = "SplatPressoCaptureDepth", hideFlags = HideFlags.HideAndDontSave };
            m_DepthRT.Create();
            m_ColorRT = new RenderTexture(width, height, 0, colorFormat) { name = "SplatPressoCaptureColor", hideFlags = HideFlags.HideAndDontSave };
            m_ColorRT.Create();
            m_DepthAttachmentRT = new RenderTexture(width, height, GraphicsFormat.None, GraphicsFormat.D32_SFloat) { name = "SplatPressoCaptureDepthAttachment", hideFlags = HideFlags.HideAndDontSave };
            m_DepthAttachmentRT.Create();
            m_DepthRTHandle = RTHandles.Alloc(m_DepthRT);
            m_ColorRTHandle = RTHandles.Alloc(m_ColorRT);
            m_DepthAttachmentHandle = RTHandles.Alloc(m_DepthAttachmentRT);
        }

        void ReleaseTargets()
        {
            m_DepthRTHandle?.Release();
            m_DepthRTHandle = null;
            m_ColorRTHandle?.Release();
            m_ColorRTHandle = null;
            m_DepthAttachmentHandle?.Release();
            m_DepthAttachmentHandle = null;
            DestroyTarget(ref m_DepthRT);
            DestroyTarget(ref m_ColorRT);
            DestroyTarget(ref m_DepthAttachmentRT);
        }

        static void DestroyTarget(ref RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                CoreUtils.Destroy(rt);
            }
            rt = null;
        }

        static bool IsRenderGraphCompatibilityMode()
        {
            return GraphicsSettings.TryGetRenderPipelineSettings(out RenderGraphSettings settings) && settings.enableRenderCompatibilityMode;
        }

        class PassData
        {
            internal TextureHandle SourceColor;
            internal TextureHandle SourceDepth;
            internal RTHandle DepthCapture;
            internal RTHandle ColorCapture;
            internal RTHandle DepthAttachment;
            internal RenderTexture DepthRT;
            internal RenderTexture ColorRT;
            internal Material MatSplatDepth;
            internal Material MatDepthPrime;
            internal GraphicsBuffer QuadIndices;
            internal MaterialPropertyBlock Mpb;
            internal List<SplatDraw> Splats;
            internal Action<AsyncGPUReadbackRequest> OnDepth;
            internal Action<AsyncGPUReadbackRequest> OnColor;
        }

        sealed class CapturePass : ScriptableRenderPass
        {
            readonly SplatCaptureFeature m_Feature;

            public CapturePass(SplatCaptureFeature feature)
            {
                m_Feature = feature;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                m_Feature.RecordCapture(renderGraph, frameData);
            }
        }

        void RecordCapture(RenderGraph renderGraph, ContextContainer frameData)
        {
            CaptureRequest req = s_Pending;
            if (req == null)
                return;
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.camera != req.camera)
                return;

            // Consume the request and record the camera parameters now: same frame as the rendered pixels
            // (a request-time snapshot would drift if the camera moves before it renders).
            s_Pending = null;
            s_InFlight = req;
            req.stageFrame = Time.frameCount;
            req.stageTime = Now;
            Camera cam = req.camera;
            Transform camTr = cam.transform;
            req.orthographic = cam.orthographic;
            req.result = new SplatCaptureResult
            {
                cameraPosition = camTr.position,
                cameraRotation = camTr.rotation,
                verticalFovDeg = cam.fieldOfView,
                nearPlane = cam.nearClipPlane,
                farPlane = cam.farClipPlane,
                orthographic = req.orthographic,
            };

            if (cameraData.xrRendering)
            {
                // Stereo single-pass targets are texture arrays (or capture one eye only): use a mono camera
                // (stereoTargetEye = None) that follows the HMD, enabled only while a capture is pending.
                Fail(req, $"camera '{req.cameraName}' renders in XR stereo; capture needs a mono camera (stereoTargetEye = None).");
                return;
            }
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            if (desc.msaaSamples > 1)
            {
                // The prime shader's Texture2D<float> cannot bind a multisampled depth attachment.
                Fail(req, $"MSAA ({desc.msaaSamples}x) is enabled for camera '{req.cameraName}'. Disable MSAA on the URP asset (and the camera).");
                return;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                Fail(req, $"AsyncGPUReadback is not supported on {SystemInfo.graphicsDeviceType}.");
                return;
            }
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer || !resourceData.activeColorTexture.IsValid() || !resourceData.activeDepthTexture.IsValid())
            {
                Fail(req, $"camera '{req.cameraName}' renders straight to the back buffer, which cannot be read back.");
                return;
            }

            // Splats to draw into depth: the same filter GaussianSplatRenderSystem.GatherSplatsForCamera uses, so
            // exactly these had CalcViewData run for this camera in the GS pass (BeforeRenderingTransparents).
            m_Draws.Clear();
            bool gsOk = GsInternals.Validate(out string gsError);
            GaussianSplatRenderer[] renderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (GaussianSplatRenderer gs in renderers)
            {
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                if (!gsOk)
                {
                    // Splats present but their view data is unreachable: depth would silently miss them.
                    Fail(req, gsError);
                    return;
                }
                GraphicsBuffer view = GsInternals.GetViewBuffer(gs);
                int count = gs.splatCount;
                if (view == null || !view.IsValid() || count <= 0 || view.count < count)
                    continue;
                m_Draws.Add(new SplatDraw { view = view, count = count, matrix = gs.transform.localToWorldMatrix });
            }
            if (m_Draws.Count > 0)
            {
                if (!EnsureSplatDepthMaterial(out string error))
                {
                    Fail(req, error);
                    return;
                }
                EnsureQuadIndices();
                m_MatSplatDepth.SetFloat(s_AlphaThresholdID, m_DepthAlphaThreshold);
            }
            if (m_MatDepthPrime == null && !EnsurePrimeMaterial(out string primeError))
            {
                Fail(req, primeError);
                return;
            }
            m_Mpb ??= new MaterialPropertyBlock();

            EnsureTargets(desc.width, desc.height);
            TextureHandle depthCapHandle = renderGraph.ImportTexture(m_DepthRTHandle);
            TextureHandle colorCapHandle = renderGraph.ImportTexture(m_ColorRTHandle);
            TextureHandle depthAttHandle = renderGraph.ImportTexture(m_DepthAttachmentHandle);

            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("SplatPresso Capture", out PassData passData))
            {
                passData.SourceColor = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                passData.DepthCapture = m_DepthRTHandle;
                passData.ColorCapture = m_ColorRTHandle;
                passData.DepthAttachment = m_DepthAttachmentHandle;
                passData.DepthRT = m_DepthRT;
                passData.ColorRT = m_ColorRT;
                passData.MatSplatDepth = m_Draws.Count > 0 ? m_MatSplatDepth : null;
                passData.MatDepthPrime = m_MatDepthPrime;
                passData.QuadIndices = m_QuadIndices;
                passData.Mpb = m_Mpb;
                passData.Splats = m_Draws; // read during execution, which follows recording within this camera's render
                passData.OnDepth = r => OnDepthReadback(req, r);
                passData.OnColor = r => OnColorReadback(req, r);

                builder.UseTexture(resourceData.activeColorTexture);
                builder.UseTexture(resourceData.activeDepthTexture);
                builder.UseTexture(depthCapHandle, AccessFlags.Write);
                builder.UseTexture(colorCapHandle, AccessFlags.Write);
                builder.UseTexture(depthAttHandle, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }

        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmb = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            // 1) Prime the capture depth target with the camera depth: raw values both to the color target (for
            //    readback) and to SV_Depth (so scene geometry occludes the splats drawn next).
            cmb.SetGlobalTexture(s_SceneDepthTexID, data.SourceDepth);
            CoreUtils.SetRenderTarget(cmb, data.DepthCapture, data.DepthAttachment, ClearFlag.All, Color.clear);
            cmb.DrawProcedural(Matrix4x4.identity, data.MatDepthPrime, 0, MeshTopology.Triangles, 3, 1);

            // 2) Splat centre depths on top (ZTest LEqual against the primed depth). Only _SplatViewData is bound:
            //    no _OrderBuffer, since depth-tested output does not depend on draw order. The command buffer copies
            //    the property block at call time, so one block is reused.
            if (data.MatSplatDepth != null)
            {
                foreach (SplatDraw s in data.Splats)
                {
                    data.Mpb.Clear();
                    data.Mpb.SetBuffer(s_SplatViewDataID, s.view);
                    cmb.DrawProcedural(data.QuadIndices, s.matrix, data.MatSplatDepth, 0, MeshTopology.Triangles, 6, s.count, data.Mpb);
                }
            }
            data.Splats.Clear(); // commands are recorded; do not keep references to the renderers' buffers

            // 3) Copy the current camera color (splats already composited by the GS pass).
            Blitter.BlitCameraTexture(cmb, data.SourceColor, data.ColorCapture);

            // 4) Async readbacks: complete on the main thread a few frames later.
            cmb.RequestAsyncReadback(data.DepthRT, 0, TextureFormat.RFloat, data.OnDepth);
            cmb.RequestAsyncReadback(data.ColorRT, 0, TextureFormat.RGBA32, data.OnColor);
        }

        static void OnDepthReadback(CaptureRequest req, AsyncGPUReadbackRequest request)
        {
            if (req.done)
                return; // cancelled or timed out: ignore the late readback
            if (request.hasError)
            {
                req.failed = true;
            }
            else
            {
                req.rawDepth = request.GetData<float>().ToArray();
                req.depthWidth = request.width;
                req.depthHeight = request.height;
            }
            req.hasDepth = true;
            TryFinish(req);
        }

        static void OnColorReadback(CaptureRequest req, AsyncGPUReadbackRequest request)
        {
            if (req.done)
                return;
            if (request.hasError)
            {
                req.failed = true;
            }
            else
            {
                req.rgba = request.GetData<byte>().ToArray();
                req.colorWidth = request.width;
                req.colorHeight = request.height;
            }
            req.hasColor = true;
            TryFinish(req);
        }

        static void TryFinish(CaptureRequest req)
        {
            if (req.done || !req.hasDepth || !req.hasColor)
                return;
            if (req.failed)
            {
                Fail(req, "GPU readback failed.");
                return;
            }
            int w = req.depthWidth, h = req.depthHeight;
            if (w <= 0 || h <= 0 || req.colorWidth != w || req.colorHeight != h ||
                req.rawDepth == null || req.rawDepth.Length < w * h || req.rgba == null || req.rgba.Length < w * h * 4)
            {
                Fail(req, $"readback size mismatch (depth {w}x{h}, color {req.colorWidth}x{req.colorHeight}).");
                return;
            }

            SplatCaptureResult res = req.result;
            res.width = w;
            res.height = h;
            res.rgba = req.rgba;
            res.depthEye = LinearizeDepthInPlace(req.rawDepth, res.nearPlane, res.farPlane, req.orthographic);

            // make row 0 = top of image
            bool flip = FlipReadbackOverride ?? SystemInfo.graphicsUVStartsAtTop;
            if (flip)
            {
                FlipRowsInPlace(res.rgba, w * 4, h);
                FlipRowsInPlace(res.depthEye, w, h);
            }

            s_LastError = null;
            Complete(req, res);
        }

        // Raw depth -> eye-space depth (world units along the camera forward); raw 0 (reversed-Z far) or raw 1
        // (non-reversed far) = no geometry -> 0 sentinel. Perspective uses Unity's _ZBufferParams formulas.
        static float[] LinearizeDepthInPlace(float[] depth, float n, float f, bool orthographic)
        {
            bool reversed = SystemInfo.usesReversedZBuffer;
            for (int i = 0; i < depth.Length; ++i)
            {
                float raw = depth[i];
                float eye;
                if (!orthographic)
                {
                    if (reversed)
                        eye = raw <= 0.0f ? 0.0f : f / (raw * (f / n - 1.0f) + 1.0f);
                    else
                        eye = raw >= 1.0f ? 0.0f : f / (raw * (1.0f - f / n) + f / n);
                }
                else
                {
                    // orthographic depth is linear between the planes
                    float t = reversed ? 1.0f - raw : raw;
                    eye = t >= 1.0f ? 0.0f : n + t * (f - n);
                }
                if (!(eye > 0.0f) || float.IsInfinity(eye))
                    eye = 0.0f; // NaN / negative / inf -> invalid
                depth[i] = eye;
            }
            return depth;
        }

        static void FlipRowsInPlace<T>(T[] data, int rowLength, int rows)
        {
            var tmp = new T[rowLength];
            for (int y = 0; y < rows / 2; ++y)
            {
                int a = y * rowLength;
                int b = (rows - 1 - y) * rowLength;
                Array.Copy(data, a, tmp, 0, rowLength);
                Array.Copy(data, b, data, a, rowLength);
                Array.Copy(tmp, 0, data, b, rowLength);
            }
        }

        static void Fail(CaptureRequest req, string message)
        {
            if (req == null || req.done)
                return;
            s_LastError = message;
            Debug.LogError("[SplatPresso] Capture failed: " + message);
            Complete(req, null);
        }

        // Single exit for every request: clears the slot BEFORE invoking the callback so the callback may request
        // the next capture right away. Callback exceptions are logged, never propagated into URP rendering.
        static void Complete(CaptureRequest req, SplatCaptureResult result)
        {
            if (req == null || req.done)
                return;
            req.done = true;
            req.rawDepth = null;
            if (result == null)
                req.rgba = null;
            if (s_Pending == req)
                s_Pending = null;
            if (s_InFlight == req)
                s_InFlight = null;
            UnhookWatchdogIfIdle();
            try
            {
                req.callback?.Invoke(result);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        static double Now => Time.realtimeSinceStartupAsDouble;

        static void CheckWatchdog()
        {
            CaptureRequest pending = s_Pending;
            if (pending != null)
            {
                int frames = Time.frameCount - pending.stageFrame;
                double seconds = Now - pending.stageTime;
                if (pending.camera == null)
                {
                    Fail(pending, $"camera '{pending.cameraName}' was destroyed before it rendered.");
                }
                else if ((frames >= kPendingTimeoutFrames && seconds >= kPendingMinSecondsForFrameTimeout) ||
                         (seconds >= kPendingTimeoutSeconds && frames >= kMinFramesForTimeTimeout))
                {
                    Fail(pending, $"SplatCaptureFeature did not run for camera '{pending.cameraName}' - run SplatPresso > Setup Scene " +
                                  "(the feature must be on the URP renderer this camera uses, and the camera must be enabled and rendering).");
                }
            }

            CaptureRequest inFlight = s_InFlight;
            if (inFlight != null)
            {
                int frames = Time.frameCount - inFlight.stageFrame;
                double seconds = Now - inFlight.stageTime;
                if (frames >= kInFlightTimeoutFrames && seconds >= kInFlightTimeoutSeconds)
                    Fail(inFlight, "GPU readback did not complete in time.");
            }
        }

        // The feature's own callbacks only run for cameras whose renderer has it, so the watchdog also ticks from
        // the render pipeline (every rendered frame) while a request is outstanding.
        static void HookWatchdog()
        {
            if (s_WatchdogHooked)
                return;
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            s_WatchdogHooked = true;
        }

        static void UnhookWatchdog()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            s_WatchdogHooked = false;
        }

        static void UnhookWatchdogIfIdle()
        {
            if (s_Pending == null && s_InFlight == null)
                UnhookWatchdog();
        }

        static void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            CheckWatchdog();
            UnhookWatchdogIfIdle();
        }
    }
}
