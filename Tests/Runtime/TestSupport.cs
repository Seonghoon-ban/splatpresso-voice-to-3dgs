using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using NUnit.Framework;
using SplatPresso.Placement;
using SplatPresso.Rendering;
using SplatPresso.Voice;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Runs an async (Awaitable) test body from a [UnityTest] coroutine: start it, yield while <see cref="KeepWaiting"/>,
    /// then call <see cref="AssertSucceeded"/>. A timeout cancels the body's token and fails the test.
    /// </summary>
    internal sealed class AsyncOp
    {
        readonly CancellationTokenSource m_Cts = new CancellationTokenSource();
        readonly string m_What;
        readonly float m_TimeoutSec;
        readonly double m_Deadline;

        AsyncOp(string what, float timeoutSec)
        {
            m_What = what;
            m_TimeoutSec = timeoutSec;
            m_Deadline = Time.realtimeSinceStartupAsDouble + timeoutSec;
        }

        public bool Completed { get; private set; }
        public Exception Error { get; private set; }

        /// <summary>Starts <paramref name="body"/> now (on the main thread).</summary>
        public static AsyncOp Run(string what, float timeoutSec, Func<CancellationToken, Awaitable> body)
        {
            var op = new AsyncOp(what, timeoutSec);
            op.Start(body);
            return op;
        }

        async void Start(Func<CancellationToken, Awaitable> body)
        {
            try
            {
                await body(m_Cts.Token);
            }
            catch (Exception e)
            {
                Error = e;
            }
            finally
            {
                Completed = true;
            }
        }

        /// <summary>True while the body runs and the timeout has not elapsed (cancels the body on timeout).</summary>
        public bool KeepWaiting
        {
            get
            {
                if (Completed)
                    return false;
                if (Time.realtimeSinceStartupAsDouble <= m_Deadline)
                    return true;
                m_Cts.Cancel();
                return false;
            }
        }

        /// <summary>Fails the test on timeout; rethrows NUnit results (Assert/Ignore/Inconclusive) and reports other exceptions.</summary>
        public void AssertSucceeded(Func<string> context = null)
        {
            if (!Completed)
                Assert.Fail($"{m_What} did not finish within {m_TimeoutSec:0} s.{Context(context)}");
            if (Error is ResultStateException)
                ExceptionDispatchInfo.Capture(Error).Throw();
            if (Error != null)
                Assert.Fail($"{m_What} threw {Error.GetType().Name}: {Error.Message}{Context(context)}\n{Error}");
        }

        static string Context(Func<string> context)
        {
            if (context == null)
                return "";
            try
            {
                return "\n" + context();
            }
            catch (Exception e)
            {
                return "\n(context failed: " + e.Message + ")";
            }
        }
    }

    /// <summary>Environment checks and fixture lookup shared by the PlayMode tests.</summary>
    internal static class TestEnv
    {
        const string kPackageName = "com.splatpresso.voice-to-3dgs";

        /// <summary>Absolute path of <c>Tests/Runtime/Fixtures</c> (editor only), or null.</summary>
        public static string FixturesDir
        {
            get
            {
#if UNITY_EDITOR
                string root = null;
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TestEnv).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                    root = info.resolvedPath;
                else if (Directory.Exists(Path.Combine("Packages", kPackageName)))
                    root = Path.GetFullPath(Path.Combine("Packages", kPackageName));
                if (root == null)
                    return null;
                string dir = Path.Combine(root, "Tests", "Runtime", "Fixtures");
                return Directory.Exists(dir) ? dir : null;
#else
                return null;
#endif
            }
        }

        /// <summary>The fixtures folder; ignores the test when it is unavailable (player builds) or incomplete.</summary>
        public static string RequireFixtures()
        {
            string dir = FixturesDir;
            if (dir == null)
                Assert.Ignore("Test fixtures are only reachable in the editor (package folder Tests/Runtime/Fixtures).");
            foreach (var name in new[] { "object.ply", "edited.jpg", "cutout.png", "enhanced.png", "generated.png", "depth.png", "object.glb", "decision.json", "verification.json" })
                if (!File.Exists(Path.Combine(dir, name)))
                    Assert.Ignore($"Fixture {name} is missing. Regenerate the fixtures with: python Tools~/make_fixtures.py");
            return dir;
        }

        /// <summary>
        /// Null when the active pipeline can render and capture splats, else the reason (for Assert.Ignore): URP must
        /// be active and its renderer must carry GaussianSplatURPFeature and SplatCaptureFeature (the test project is
        /// prepared with SplatPresso > Setup Scene / SetupWizard.RunBatch).
        /// </summary>
        public static string CaptureUnavailableReason()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return "No graphics device (-nographics): rendering tests cannot run.";
            if (!SystemInfo.supportsAsyncGPUReadback)
                return "AsyncGPUReadback is not supported on " + SystemInfo.graphicsDeviceType + ".";
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null)
                return "URP is not the active render pipeline (current: " +
                       (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in") +
                       "). Run SplatPresso > Setup Scene (or SetupWizard.RunBatch) in the test project first.";
            bool hasGs = false, hasCapture = false;
            foreach (var data in urp.rendererDataList)
            {
                if (data == null)
                    continue;
                foreach (var feature in data.rendererFeatures)
                {
                    if (feature == null || !feature.isActive)
                        continue;
                    if (feature is SplatCaptureFeature)
                        hasCapture = true;
                    else if (feature.GetType().FullName == "GaussianSplatting.Runtime.GaussianSplatURPFeature")
                        hasGs = true;
                }
            }
            if (!hasGs)
                return "The URP renderer has no GaussianSplatURPFeature. Run SplatPresso > Setup Scene in the test project first.";
            if (!hasCapture)
                return "The URP renderer has no SplatCaptureFeature. Run SplatPresso > Setup Scene in the test project first.";
            return null;
        }

        /// <summary>Ignores the test unless splats can be rendered and captured.</summary>
        public static void RequireCapture()
        {
            string why = CaptureUnavailableReason();
            if (why != null)
                Assert.Ignore(why);
        }

        /// <summary>GS shader/compute references found in the project (editor only); ignores the test otherwise.</summary>
        public static SplatRendererResources RequireRendererResources()
        {
#if UNITY_EDITOR
            var res = SplatRendererResources.FindInProject();
            if (res == null || !res.IsComplete)
                Assert.Ignore("UnityGaussianSplatting shaders were not found (is org.nesnausk.gaussian-splatting installed?).");
            return res;
#else
            Assert.Ignore("Renderer resources are looked up with AssetDatabase (editor only).");
            return null;
#endif
        }

        /// <summary>Creates a fresh scratch folder under the OS temp directory.</summary>
        public static string NewTempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SplatPressoTests", tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Best-effort recursive delete.</summary>
        public static void DeleteDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // a locked temp file must not fail the test
            }
        }

        /// <summary>Test settings pointing at <paramref name="apiBaseUrl"/> with sessions in <paramref name="sessionsDir"/>.</summary>
        public static SplatPressoSettings CreateSettings(string apiBaseUrl, string sessionsDir)
        {
            var s = ScriptableObject.CreateInstance<SplatPressoSettings>();
            s.name = "SplatPressoSettings (test)";
            s.apiBaseUrl = apiBaseUrl;
            s.apiKey = "";
            s.mediaProvider = MediaProvider.Genpresso;
            s.falFallbackWhenMissing = false; // a FAL_KEY on the dev machine must never route test jobs to fal.ai
            s.sessionsFolder = sessionsDir;
            s.chatTimeoutSec = 30;
            s.downloadTimeoutSec = 30;
            s.maxConcurrentMediaJobs = 6;
            s.maxConcurrentObjects = 4;
            s.maxConcurrentRuns = 3;
            s.maxCostPerRun = 25f;
            s.voiceBackend = VoiceBackendKind.GenpressoChat;
            s.sendFrameWithSpeech = false;
            s.runInBackground = true;
            return s;
        }
    }

    /// <summary>
    /// A minimal runtime scene: a camera at eye height (1.6 m, pitched down 10 degrees) rendering into a 640x360
    /// render texture (deterministic size and a 16:9 aspect that matches the fixtures), a large floor at y = 0, a
    /// light, and optionally the SplatPresso services on one GameObject.
    /// </summary>
    internal sealed class SceneRig : IDisposable
    {
        public const int Width = 640, Height = 360;
        public static readonly Vector3 CameraPosition = new Vector3(0f, 1.6f, 0f);
        public static readonly Quaternion CameraRotation = Quaternion.Euler(10f, 0f, 0f);

        public Camera camera;
        public RenderTexture target;
        public GameObject floor;
        public GameObject light;
        public GameObject servicesGo;
        public CaptureService capture;
        public ObjectSpawnService spawner;
        public PlacementPreviewService preview;
        public SplatPressoRoot root;
        public VoiceAgent voice;

        /// <summary>Camera, floor and light only.</summary>
        public static SceneRig CreateBasic()
        {
            var rig = new SceneRig();
            rig.target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "SplatPressoTestTarget", antiAliasing = 1 };
            rig.target.Create();

            var camGo = new GameObject("TestCamera") { tag = "MainCamera" };
            camGo.transform.SetPositionAndRotation(CameraPosition, CameraRotation);
            rig.camera = camGo.AddComponent<Camera>();
            rig.camera.fieldOfView = 60f;
            rig.camera.nearClipPlane = 0.1f;
            rig.camera.farClipPlane = 200f;
            rig.camera.clearFlags = CameraClearFlags.SolidColor;
            rig.camera.backgroundColor = Color.black;
            rig.camera.allowMSAA = false; // the capture refuses MSAA targets
            rig.camera.allowHDR = false;
            rig.camera.targetTexture = rig.target;

            rig.floor = GameObject.CreatePrimitive(PrimitiveType.Plane); // 10 x 10 m
            rig.floor.name = "TestFloor";
            rig.floor.transform.position = new Vector3(0f, 0f, 10f);
            rig.floor.transform.localScale = new Vector3(4f, 1f, 4f);

            rig.light = new GameObject("TestLight");
            var l = rig.light.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.2f;
            rig.light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            return rig;
        }

        /// <summary>
        /// Camera/floor/light plus CaptureService, ObjectSpawnService and PlacementPreviewService, and optionally
        /// SplatPressoRoot and VoiceAgent. Fields are assigned while the GameObject is inactive, so Awake sees them.
        /// </summary>
        public static SceneRig CreateWithServices(SplatPressoSettings settings, SplatRendererResources resources, bool withRoot, bool withVoice)
        {
            var rig = CreateBasic();
            rig.servicesGo = new GameObject("SplatPresso (test)");
            rig.servicesGo.SetActive(false);

            rig.capture = rig.servicesGo.AddComponent<CaptureService>();
            rig.capture.targetCamera = rig.camera;
            rig.capture.settings = settings;

            rig.preview = rig.servicesGo.AddComponent<PlacementPreviewService>();

            rig.spawner = rig.servicesGo.AddComponent<ObjectSpawnService>();
            rig.spawner.settings = settings;
            rig.spawner.rendererResources = resources;
            rig.spawner.previewService = rig.preview;

            if (withVoice)
            {
                rig.voice = rig.servicesGo.AddComponent<VoiceAgent>();
                rig.voice.settings = settings;
                rig.voice.snapshotCamera = rig.camera;
            }

            if (withRoot)
            {
                rig.root = rig.servicesGo.AddComponent<SplatPressoRoot>();
                rig.root.settings = settings;
                rig.root.captureService = rig.capture;
                rig.root.spawnService = rig.spawner;
                rig.root.previewService = rig.preview;
                rig.root.voiceAgent = rig.voice;
                rig.root.startVoiceOnStart = withVoice;
                rig.root.enableModeHotkeys = false;
            }

            rig.servicesGo.SetActive(true);
            return rig;
        }

        /// <summary>Linear eye depth of the floor seen through a pixel of <paramref name="cap"/> (row 0 = top), 0 when it sees the sky.</summary>
        public static float FloorEyeDepth(CaptureResult cap, float px, float py)
        {
            float f = cap.FocalPixels;
            var dirCam = new Vector3((px + 0.5f - cap.width * 0.5f) / f, -(py + 0.5f - cap.height * 0.5f) / f, 1f);
            Vector3 dir = cap.cameraRotation * dirCam;
            if (dir.y > -1e-6f)
                return 0f;
            return cap.cameraPosition.y / -dir.y;
        }

        public void Dispose()
        {
            // stop the voice backend first: its in-flight chat calls must not outlive the test's settings
            if (voice != null)
                voice.StopBackend();
            if (root != null)
                root.CancelAll();
            if (spawner != null)
                spawner.ClearAll();
            Kill(servicesGo);
            Kill(light);
            Kill(floor);
            if (camera != null)
            {
                camera.targetTexture = null;
                Kill(camera.gameObject);
            }
            if (target != null)
            {
                target.Release();
                Kill(target);
            }
        }

        static void Kill(Object o)
        {
            if (o != null)
                Object.Destroy(o);
        }
    }
}
