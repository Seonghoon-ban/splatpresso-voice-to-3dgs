using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SplatPresso.Api
{
    /// <summary>
    /// Hides the 3D model's cold start. Measured on live GenPresso: TripoSplat itself needs ~5 s of inference (~15 s end to
    /// end when its worker is warm), but after a few idle minutes the first job waited 50 s to 6.5 min for a worker. A
    /// request whose input fails validation is not billed yet still makes the provider boot a worker, so SplatPresso
    /// sends one ("warm-up") when the user starts talking or typing, when a run starts, and - while the user is active -
    /// often enough to keep the worker from going idle.
    /// </summary>
    /// <remarks>
    /// Warm-ups bypass the cost ledger and the media concurrency gate, are debounced per route
    /// (<see cref="SplatPressoSettings.warmUpIntervalSec"/>), and never throw. Play mode / players only.
    /// </remarks>
    public static class ModelWarmer
    {
        // A body every supported 3D model rejects (missing image/prompt, invalid enums): the job fails validation.
        const string kWarmUpBody = "{\"output_format\":\"__warmup__\",\"tier\":\"__warmup__\"}";
        const float kPollSeconds = 3f;
        const double kMaxWaitSeconds = 480;

        static readonly Dictionary<string, double> s_LastSent = new Dictionary<string, double>();
        static readonly HashSet<string> s_InFlight = new HashSet<string>();
        static CancellationTokenSource s_Cts;
        static bool s_QuitHooked;

        /// <summary>Realtime (seconds) of the last user activity that may lead to a generation; -inf when none.</summary>
        public static double LastActivityTime { get; private set; } = double.NegativeInfinity;

        /// <summary>Raised when a warm-up job finished: route key, seconds from submit to the worker's answer.</summary>
        public static event Action<string, double> WarmedUp;

        /// <summary>Records user activity (talk, typing, a run) that keeps the model warm for a while.</summary>
        public static void NoteActivity() => LastActivityTime = Time.realtimeSinceStartupAsDouble;

        /// <summary>
        /// The 3D route the next run would use for the settings' representation and <paramref name="mode"/>.
        /// </summary>
        public static string RouteFor(SplatPressoSettings s, GenerationMode mode)
        {
            if (s != null && s.representation == ObjectRepresentation.Mesh)
                return mode == GenerationMode.DirectTextTo3D ? MediaRouteKeys.TextToMesh : MediaRouteKeys.ImageToMesh;
            return MediaRouteKeys.ImageToSplat;
        }

        /// <summary>
        /// Sends a warm-up request for <paramref name="routeKey"/> unless one was sent within
        /// <see cref="SplatPressoSettings.warmUpIntervalSec"/> or is still running. Fire and forget; returns true when sent.
        /// </summary>
        public static bool WarmUp(SplatPressoSettings s, string routeKey, string reason)
        {
            s = s != null ? s : SplatPressoSettings.Active;
            if (s == null || !s.warmUpModels || !Application.isPlaying || string.IsNullOrEmpty(routeKey))
                return false;
            var route = s.GetRoute(routeKey);
            if (route == null)
                return false;

            double now = Time.realtimeSinceStartupAsDouble;
            if (s_InFlight.Contains(routeKey))
                return false;
            if (s_LastSent.TryGetValue(routeKey, out double last) && now - last < Mathf.Max(30f, s.warmUpIntervalSec))
                return false;

            string url, auth, target;
            if (s.mediaProvider == MediaProvider.FalDirect)
            {
                string key = ApiKeys.Get(ApiKeyKind.Fal);
                target = (route.falEndpoint ?? "").Trim().Trim('/');
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(target))
                    return false;
                url = SplatPressoSettings.JoinUrl(s.falQueueBaseUrl, target, "https://queue.fal.run");
                auth = "Key " + key;
            }
            else
            {
                string key = ApiKeys.Get(ApiKeyKind.Genpresso);
                var paths = route.CleanPaths();
                if (string.IsNullOrEmpty(key) || paths.Count == 0)
                    return false;
                target = ModelPathCache.Get(s.apiBaseUrl, routeKey, paths);
                if (string.IsNullOrEmpty(target))
                    target = paths[0];
                url = s.ApiUrl("media/" + target);
                auth = "Bearer " + key;
            }

            s_LastSent[routeKey] = now;
            s_InFlight.Add(routeKey);
            HookQuit();
            s_Cts ??= new CancellationTokenSource();
            RunAsync(routeKey, target, url, auth, reason, s_Cts.Token);
            return true;
        }

        /// <summary>
        /// Called every frame by <see cref="SplatPressoRoot"/>: re-sends the warm-up while the user was active within
        /// <see cref="SplatPressoSettings.keepWarmMinutes"/>.
        /// </summary>
        public static void Tick(SplatPressoSettings s, GenerationMode mode)
        {
            if (s == null || !s.warmUpModels || s.keepWarmMinutes <= 0f || double.IsNegativeInfinity(LastActivityTime))
                return;
            if (Time.realtimeSinceStartupAsDouble - LastActivityTime > s.keepWarmMinutes * 60.0)
                return;
            WarmUp(s, RouteFor(s, mode), "keep-warm");
        }

        static async void RunAsync(string routeKey, string target, string url, string auth, string reason, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var headers = new Dictionary<string, string> { { "Authorization", auth } };
            string cancelUrl = null;
            try
            {
                var resp = await HttpJson.SendAsync("POST", url, Encoding.UTF8.GetBytes(kWarmUpBody), "application/json", headers, 30, ct,
                    throwOnHttpError: false);
                if (!resp.IsSuccess)
                {
                    // 422 at submit: validated immediately, the worker is up. 404: path missing (real runs resolve it).
                    if (resp.StatusCode != 422 && resp.StatusCode != 400)
                        Debug.Log($"[SplatPresso] Warm-up of '{target}' not sent ({GenpressoError.Describe(resp.StatusCode, resp.Error, resp.Text)})");
                    return;
                }

                string statusUrl = null;
                try
                {
                    var json = JObject.Parse(resp.Text ?? "");
                    statusUrl = (string)json["status_url"];
                    cancelUrl = (string)json["cancel_url"];
                }
                catch (JsonException) { }
                if (string.IsNullOrEmpty(statusUrl))
                    return;

                Debug.Log($"[SplatPresso] Warming up '{target}' ({reason}) so its worker is ready when the object image is");
                while (sw.Elapsed.TotalSeconds < kMaxWaitSeconds)
                {
                    await HttpJson.DelayAsync(kPollSeconds, ct);
                    HttpResponse st;
                    try { st = await HttpJson.SendAsync("GET", statusUrl, null, null, headers, 30, ct, throwOnHttpError: false); }
                    catch (GenpressoException) { continue; }
                    if (!st.IsSuccess)
                        continue;
                    string state = null;
                    try { state = ((string)JObject.Parse(st.Text ?? "")["status"])?.ToUpperInvariant(); }
                    catch (JsonException) { }
                    if (state == null || state == "IN_QUEUE" || state == "IN_PROGRESS")
                        continue;
                    // Any terminal state (FAILED/COMPLETED with a validation error): a worker picked the job up.
                    double secs = sw.Elapsed.TotalSeconds;
                    Debug.Log($"[SplatPresso] '{target}' worker is ready (warm-up answered after {secs:F0}s)");
                    try { WarmedUp?.Invoke(routeKey, secs); }
                    catch (Exception e) { Debug.LogException(e); }
                    return;
                }
                MediaJobClient.TryCancelFireAndForget(cancelUrl, auth);
            }
            catch (OperationCanceledException)
            {
                MediaJobClient.TryCancelFireAndForget(cancelUrl, auth);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Warm-up of '{target}' failed: {e.Message}");
            }
            finally
            {
                s_InFlight.Remove(routeKey);
            }
        }

        static void HookQuit()
        {
            if (s_QuitHooked)
                return;
            s_QuitHooked = true;
            Application.quitting += CancelAll;
        }

        static void CancelAll()
        {
            try { s_Cts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            CancelAll();
            s_Cts = null;
            s_LastSent.Clear();
            s_InFlight.Clear();
            LastActivityTime = double.NegativeInfinity;
            WarmedUp = null;
            if (s_QuitHooked)
                Application.quitting -= CancelAll;
            s_QuitHooked = false;
        }
    }
}
