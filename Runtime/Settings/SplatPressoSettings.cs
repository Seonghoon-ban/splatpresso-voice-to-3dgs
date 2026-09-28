using System.Collections.Generic;
using UnityEngine;

namespace SplatPresso
{
    /// <summary>Which service runs the media (image / 3D) models.</summary>
    public enum MediaProvider
    {
        /// <summary>GenPresso media queue (one GenPresso key for everything).</summary>
        Genpresso,
        /// <summary>fal.ai queue directly (needs FAL_KEY).</summary>
        FalDirect,
    }

    /// <summary>Voice front end.</summary>
    public enum VoiceBackendKind
    {
        /// <summary>Push-to-talk WAV sent to GenPresso chat/completions (GenPresso key only).</summary>
        GenpressoChat,
        /// <summary>OpenAI Realtime over WebSocket (needs OPENAI_API_KEY).</summary>
        OpenAIRealtime,
        /// <summary>No voice; requests come from code, the text box or the debug tools.</summary>
        None,
    }

    /// <summary>Language the agent replies in.</summary>
    public enum ReplyLanguage
    {
        Auto,
        English,
        Korean,
        Japanese,
        Chinese,
        Spanish,
        French,
        German,
    }

    /// <summary>How pipeline milestones are voiced to the user.</summary>
    public enum NarrationMode
    {
        /// <summary>The language model phrases milestones in the reply language.</summary>
        Llm,
        /// <summary>Milestones are shown as subtitles only (no extra model calls).</summary>
        Subtitle,
        /// <summary>No narration.</summary>
        Off,
    }

    /// <summary>
    /// All tunable settings of the package. One instance lives at
    /// <c>Assets/SplatPresso/Resources/SplatPressoSettings.asset</c> (created by SplatPresso &gt; Setup Scene) so it
    /// can be loaded in players via <c>Resources.Load</c>. API keys should NOT be stored here (see <see cref="ApiKeys"/>).
    /// </summary>
    [CreateAssetMenu(menuName = "SplatPresso/Settings", fileName = "SplatPressoSettings")]
    public sealed class SplatPressoSettings : ScriptableObject
    {
        /// <summary>Resources name of the settings asset.</summary>
        public const string ResourceName = "SplatPressoSettings";
        /// <summary>Default GenPresso API root.</summary>
        public const string DefaultApiBaseUrl = "https://genpresso.ai/api/v1";

        [Header("GenPresso API")]
        [Tooltip("GenPresso API root. Chat and media calls hang off this.")]
        public string apiBaseUrl = DefaultApiBaseUrl;
        [Tooltip("Optional GenPresso key (gp_...). WARNING: stored in plain text in this asset, committed with your project " +
                 "and shipped in builds. Prefer the GENPRESSO_API_KEY environment variable or Project Settings > SplatPresso " +
                 "(saved to %USERPROFILE%/.splatpresso/keys.json).")]
        public string apiKey = "";
        [Tooltip("Where the image / 3D models run. GenPresso needs only the GenPresso key; FalDirect needs FAL_KEY.")]
        public MediaProvider mediaProvider = MediaProvider.Genpresso;
        [Tooltip("If every GenPresso path of a step returns 404 and a fal.ai key exists, run that step on fal.ai instead.")]
        public bool falFallbackWhenMissing = true;
        [Tooltip("fal.ai queue root (FalDirect provider and fal fallback).")]
        public string falQueueBaseUrl = "https://queue.fal.run";

        [Header("Language models (GenPresso chat/completions)")]
        [Tooltip("Model slug as published by its maker (no provider prefix). Used for DECIDE, VERIFY and voice turns.")]
        public string chatModel = "google/gemini-3.5-flash-lite";
        [Tooltip("Used once if chatModel is rejected with a model-not-found error (400/404).")]
        public string chatFallbackModel = "google/gemini-3.8-flash";
        [Tooltip("Timeout per chat request in seconds (audio turns need more than text).")]
        public int chatTimeoutSec = 90;

        [Header("Media models (ordered GenPresso path candidates; first that exists wins)")]
        public ModelRoute edit = DefaultRoute(MediaRouteKeys.Edit);
        public ModelRoute editFallback = DefaultRoute(MediaRouteKeys.EditFallback);
        public ModelRoute enhance = DefaultRoute(MediaRouteKeys.Enhance);
        public ModelRoute textToImage = DefaultRoute(MediaRouteKeys.TextToImage);
        public ModelRoute segment = DefaultRoute(MediaRouteKeys.Segment);
        public ModelRoute removeBackground = DefaultRoute(MediaRouteKeys.RemoveBackground);
        public ModelRoute depth = DefaultRoute(MediaRouteKeys.Depth);
        public ModelRoute imageToSplat = DefaultRoute(MediaRouteKeys.ImageToSplat);
        public ModelRoute imageToMesh = DefaultRoute(MediaRouteKeys.ImageToMesh);
        public ModelRoute textToMesh = DefaultRoute(MediaRouteKeys.TextToMesh);

        [Header("Generation")]
        [Tooltip("SceneContextual edits your view so objects match the scene; DirectTextTo3D generates objects from text only.")]
        public GenerationMode defaultMode = GenerationMode.SceneContextual;
        [Tooltip("GaussianSplat uses TripoSplat; Mesh uses Rodin (needs glTFast).")]
        public ObjectRepresentation representation = ObjectRepresentation.GaussianSplat;
        [Tooltip("Re-render each cutout as a clean white-background product shot before 3D generation (much better 3D quality).")]
        public bool enhanceObjectImages = true;
        [Tooltip("Segment objects with SAM-3. Off: crop the verified box locally and remove the background instead.")]
        public bool useSegmentation = true;
        [Tooltip("Estimate the edited image's depth (used to anchor objects). Off: placement uses box math only.")]
        public bool useDepthEstimation = true;
        [Tooltip("Gaussian count requested from TripoSplat (lower = faster/lighter).")]
        [Range(32768, 262144)] public int numGaussians = 262144;
        [Tooltip("Captured RGB is downscaled to this long side before upload (keeps requests under GenPresso's 4 MB cap).")]
        public int maxImageLongSide = 1280;
        [Range(1, 100)] public int jpegQuality = 85;
        [Tooltip("Timeout for downloading generated files (seconds).")]
        public int downloadTimeoutSec = 180;
        [Tooltip("Rodin quality tier (v2.5 routes).")]
        public string rodinTier = "Gen-2.5-Extreme-Low";
        [Tooltip("Rodin material: PBR, Shaded, All or None.")]
        public string rodinMaterial = "PBR";

        [Header("Budget & concurrency")]
        [Tooltip("Estimated credits a single run may spend; stops runaway retry loops (not accounting).")]
        public float maxCostPerRun = 25f;
        [Tooltip("Objects processed in parallel within one run.")]
        [Range(1, 8)] public int maxConcurrentObjects = 4;
        [Tooltip("Placement requests that may generate at the same time.")]
        [Range(1, 8)] public int maxConcurrentRuns = 3;
        [Tooltip("Media jobs in flight at once across all runs (GenPresso enforces a media concurrency limit).")]
        [Range(1, 16)] public int maxConcurrentMediaJobs = 6;
        [Tooltip("Max generated objects in the scene (VRAM guard); the oldest active object is removed beyond this.")]
        [Range(1, 64)] public int maxSpawnedObjects = 24;

        [Header("Voice")]
        public VoiceBackendKind voiceBackend = VoiceBackendKind.GenpressoChat;
        public ReplyLanguage replyLanguage = ReplyLanguage.Auto;
        [Tooltip("Reply language when Auto cannot tell which language the user speaks.")]
        public string fallbackLanguage = "English";
        public KeyCode pushToTalkKey = KeyCode.Space;
        [Tooltip("Microphone device name; empty = system default.")]
        public string micDeviceName = "";
        [Tooltip("Multiplier applied to mic samples (raise if the level bar barely moves).")]
        [Range(0.5f, 8f)] public float micGain = 1f;
        [Tooltip("Save each utterance as SplatPresso/mic_last.wav under persistentDataPath.")]
        public bool debugSaveMicWav = false;
        [Tooltip("Attach a snapshot of the current view to each spoken turn so the agent understands 'this/here'.")]
        public bool sendFrameWithSpeech = true;
        [Tooltip("Long side of the snapshot sent with speech (smaller = cheaper/faster).")]
        public int voiceFrameMaxLongSide = 768;
        [Tooltip("Longest utterance kept. 16 kHz WAV grows ~42.7 KB/s after base64, so > ~90 s would exceed the 4 MB request cap.")]
        [Range(5f, 85f)] public float maxUtteranceSeconds = 60f;
        [Tooltip("Utterances shorter than this are discarded.")]
        public float minUtteranceSeconds = 0.4f;
        [Tooltip("Previous turns sent as context with each voice turn.")]
        [Range(0, 20)] public int voiceHistoryTurns = 6;
        public NarrationMode narrationMode = NarrationMode.Llm;
        [Tooltip("Extra instructions appended to the agent's system prompt.")]
        [TextArea(2, 6)] public string customInstructions = "";
        [Tooltip("OpenAI Realtime model (OpenAIRealtime backend only).")]
        public string realtimeModel = "gpt-realtime-2.1";
        [Tooltip("OpenAI Realtime voice (OpenAIRealtime backend only).")]
        public string realtimeVoice = "cedar";
        [Tooltip("false = push-to-talk (recommended with speakers), true = semantic VAD (headphones).")]
        public bool useSemanticVad = false;

        [Header("Placement (calibrated for TripoSplat)")]
        public PlacementTuning placement = new PlacementTuning();

        [Header("Sessions")]
        [Tooltip("Where run artifacts are stored. Empty = <persistentDataPath>/SplatPresso/sessions; relative paths resolve against persistentDataPath.")]
        public string sessionsFolder = "";

        [Header("Runtime")]
        [Tooltip("Keep the player loop running while the window is unfocused (otherwise polling stalls).")]
        public bool runInBackground = true;
        [Tooltip("Lower the frame rate while the window is unfocused.")]
        public bool capFrameRateWhenUnfocused = false;

        // ------------------------------------------------------------------------------------------
        // Helpers

        /// <summary>Absolute GenPresso URL for a path relative to <see cref="apiBaseUrl"/>.</summary>
        public string ApiUrl(string relativePath) => JoinUrl(apiBaseUrl, relativePath, DefaultApiBaseUrl);

        /// <summary>Joins a base URL (or <paramref name="fallbackBase"/> when empty) and a relative path with one slash.</summary>
        public static string JoinUrl(string baseUrl, string relativePath, string fallbackBase = DefaultApiBaseUrl)
        {
            string root = string.IsNullOrWhiteSpace(baseUrl) ? fallbackBase : baseUrl.Trim();
            root = root.TrimEnd('/');
            return root + "/" + (relativePath ?? "").Trim().TrimStart('/');
        }

        /// <summary>The route for a <see cref="MediaRouteKeys"/> key, or null.</summary>
        public ModelRoute GetRoute(string routeKey)
        {
            switch (routeKey)
            {
                case MediaRouteKeys.Edit: return edit;
                case MediaRouteKeys.EditFallback: return editFallback;
                case MediaRouteKeys.Enhance: return enhance;
                case MediaRouteKeys.TextToImage: return textToImage;
                case MediaRouteKeys.Segment: return segment;
                case MediaRouteKeys.RemoveBackground: return removeBackground;
                case MediaRouteKeys.Depth: return depth;
                case MediaRouteKeys.ImageToSplat: return imageToSplat;
                case MediaRouteKeys.ImageToMesh: return imageToMesh;
                case MediaRouteKeys.TextToMesh: return textToMesh;
                default: return null;
            }
        }

        /// <summary>Every media route with its key, in pipeline order.</summary>
        public List<KeyValuePair<string, ModelRoute>> EnumerateRoutes()
        {
            var list = new List<KeyValuePair<string, ModelRoute>>();
            foreach (var key in MediaRouteKeys.All)
                list.Add(new KeyValuePair<string, ModelRoute>(key, GetRoute(key)));
            return list;
        }

        /// <summary>Resolved sessions root (see <see cref="PipelineSession.ResolveSessionsRoot"/>).</summary>
        public string ResolveSessionsRoot() => PipelineSession.ResolveSessionsRoot(this);

        /// <summary>Factory defaults of a media route (a fresh copy), or null for an unknown key.</summary>
        public static ModelRoute DefaultRoute(string routeKey)
        {
            switch (routeKey)
            {
                case MediaRouteKeys.Edit:
                    return new ModelRoute(new[] { "google/nano-banana-2-lite/edit", "gp/nano-banana-2/edit" }, "google/nano-banana-2-lite/edit", 1.3f, 120);
                case MediaRouteKeys.EditFallback:
                    return new ModelRoute(new[] { "gp/nano-banana-2/edit", "gp/nano-banana-pro/edit" }, "fal-ai/nano-banana-2/edit", 1.3f, 150);
                case MediaRouteKeys.Enhance:
                    return new ModelRoute(new[] { "google/nano-banana-2-lite/edit", "gp/nano-banana-2/edit" }, "google/nano-banana-2-lite/edit", 1.3f, 120);
                case MediaRouteKeys.TextToImage:
                    return new ModelRoute(new[] { "google/nano-banana-2-lite", "gp/nano-banana-2", "gp/nano-banana-pro" }, "google/nano-banana-2-lite", 1.3f, 120);
                case MediaRouteKeys.Segment:
                    return new ModelRoute(new[] { "gp/sam-3/image", "gp/sam-3-1/image" }, "fal-ai/sam-3/image", 0.2f, 60);
                case MediaRouteKeys.RemoveBackground:
                    return new ModelRoute(new[] { "gp/birefnet/v2", "gp/birefnet" }, "fal-ai/birefnet/v2", 0.1f, 60);
                case MediaRouteKeys.Depth:
                    return new ModelRoute(new[] { "gp/image-preprocessors/depth-anything/v2" }, "fal-ai/image-preprocessors/depth-anything/v2", 0.2f, 60);
                case MediaRouteKeys.ImageToSplat:
                    return new ModelRoute(new[] { "tripo3d/triposplat", "gp/tripo3d/triposplat", "gp/triposplat" }, "tripo3d/triposplat", 1.5f, 300);
                case MediaRouteKeys.ImageToMesh:
                    return new ModelRoute(new[] { "gp/hyper3d/rodin/v2.5/fast", "gp/hyper3d/rodin/v2.5" }, "fal-ai/hyper3d/rodin/v2.5/fast", 3.0f, 600);
                case MediaRouteKeys.TextToMesh:
                    return new ModelRoute(new[] { "gp/hyper3d/rodin/v2.5/text-to-3d/fast" }, "fal-ai/hyper3d/rodin/v2.5/text-to-3d/fast", 3.0f, 600);
                default:
                    return null;
            }
        }

        void OnValidate()
        {
            // routes / tuning can only be null when an asset was edited by hand; restore the defaults
            edit ??= DefaultRoute(MediaRouteKeys.Edit);
            editFallback ??= DefaultRoute(MediaRouteKeys.EditFallback);
            enhance ??= DefaultRoute(MediaRouteKeys.Enhance);
            textToImage ??= DefaultRoute(MediaRouteKeys.TextToImage);
            segment ??= DefaultRoute(MediaRouteKeys.Segment);
            removeBackground ??= DefaultRoute(MediaRouteKeys.RemoveBackground);
            depth ??= DefaultRoute(MediaRouteKeys.Depth);
            imageToSplat ??= DefaultRoute(MediaRouteKeys.ImageToSplat);
            imageToMesh ??= DefaultRoute(MediaRouteKeys.ImageToMesh);
            textToMesh ??= DefaultRoute(MediaRouteKeys.TextToMesh);
            placement ??= new PlacementTuning();
            maxImageLongSide = Mathf.Max(0, maxImageLongSide);
            chatTimeoutSec = Mathf.Max(5, chatTimeoutSec);
            downloadTimeoutSec = Mathf.Max(10, downloadTimeoutSec);
        }

        // ------------------------------------------------------------------------------------------
        // Static access

        static SplatPressoSettings s_Active;        // explicitly assigned
        static SplatPressoSettings s_Loaded;        // found via Resources.Load
        static SplatPressoSettings s_Fallback;      // defaults when there is no asset
        static bool s_WarnedFallback;
        static double s_NextLookupTime;             // throttles Resources.Load while no asset exists

        /// <summary>
        /// The settings in use: the explicitly assigned instance, else the Resources asset, else a default
        /// instance (with a one-time warning). Assign null to go back to automatic lookup.
        /// </summary>
        public static SplatPressoSettings Active
        {
            get
            {
                var found = FindActive();
                if (found != null)
                    return found;
                if (s_Fallback == null)
                {
                    s_Fallback = CreateInstance<SplatPressoSettings>();
                    s_Fallback.name = "SplatPressoSettings (defaults)";
                    s_Fallback.hideFlags = HideFlags.DontSave;
                    if (!s_WarnedFallback)
                    {
                        s_WarnedFallback = true;
                        Debug.LogWarning("[SplatPresso] No SplatPressoSettings asset in Resources; using defaults. Run SplatPresso > Setup Scene to create one.");
                    }
                }
                return s_Fallback;
            }
            set
            {
                s_Active = value;
                if (value == null)
                {
                    s_Loaded = null;
                    s_NextLookupTime = 0;
                }
            }
        }

        /// <summary>The assigned or Resources settings asset, or null when there is none (never creates defaults).</summary>
        public static SplatPressoSettings FindActive()
        {
            if (s_Active != null)
                return s_Active;
            if (s_Loaded == null)
            {
                // UI code may ask every frame; while there is no asset, look again at most once per second.
                double now = Time.realtimeSinceStartupAsDouble;
                if (now < s_NextLookupTime)
                    return null;
                s_Loaded = Resources.Load<SplatPressoSettings>(ResourceName);
                if (s_Loaded == null)
                    s_NextLookupTime = now + 1.0;
            }
            return s_Loaded;
        }

        // Projects often disable domain reload, so statics survive play sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Active = null;
            s_Loaded = null;
            s_Fallback = null;
            s_WarnedFallback = false;
            s_NextLookupTime = 0;
        }
    }
}
