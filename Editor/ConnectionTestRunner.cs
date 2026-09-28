using System;
using System.Threading;
using SplatPresso.Api;
using UnityEditor;
using UnityEngine;

namespace SplatPresso.EditorTools
{
    /// <summary>
    /// Runs <see cref="ConnectionTester.TestAsync"/> from the editor, outside play mode, and keeps the last result for
    /// the settings page.
    /// </summary>
    /// <remarks>
    /// Edit-mode async: HttpJson awaits the UnityWebRequest operation itself outside play mode (its completion
    /// callback fires on the main thread during editor updates), and its edit-mode delays use Task.Delay through
    /// Unity's synchronization context. While a test runs this class additionally calls
    /// <c>EditorApplication.QueuePlayerLoopUpdate</c> from <c>EditorApplication.update</c>, so any frame-based Awaitable
    /// continuation also keeps advancing when the editor is otherwise idle, and it enforces an overall deadline.
    /// </remarks>
    public static class ConnectionTestRunner
    {
        const double kTimeoutSec = 180;
        const double kTimeoutWithProbesSec = 900;

        static CancellationTokenSource s_Cts;
        static double s_StartTime;
        static double s_Deadline;
        static double s_NextRepaint;

        /// <summary>True while a test is running.</summary>
        public static bool IsRunning { get; private set; }
        /// <summary>Whether the running/last test probed the media model paths.</summary>
        public static bool LastWithProbes { get; private set; }
        /// <summary>The last completed report (null before the first run or after a failure).</summary>
        public static ConnectionReport LastReport { get; private set; }
        /// <summary>Why the last run did not produce a report, or null.</summary>
        public static string LastError { get; private set; }
        /// <summary>Local time the last run finished.</summary>
        public static DateTime? LastFinished { get; private set; }
        /// <summary>Seconds since the running test started.</summary>
        public static double Elapsed => IsRunning ? EditorApplication.timeSinceStartup - s_StartTime : 0;

        /// <summary>Raised when state changes and periodically while running (repaint hook).</summary>
        public static event Action Changed;

        /// <summary>Starts a test (ignored while one is running). Results are also logged to the Console (never key values).</summary>
        public static void Start(SplatPressoSettings settings, bool probeMediaModels)
        {
            if (IsRunning)
                return;
            IsRunning = true;
            LastWithProbes = probeMediaModels;
            LastError = null;
            s_Cts = new CancellationTokenSource();
            s_StartTime = EditorApplication.timeSinceStartup;
            s_Deadline = s_StartTime + (probeMediaModels ? kTimeoutWithProbesSec : kTimeoutSec);
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            ApiKeys.Reset(); // pick up keys saved a moment ago
            Changed?.Invoke();
            Run(settings, probeMediaModels, s_Cts.Token);
        }

        /// <summary>Cancels the running test.</summary>
        public static void Cancel()
        {
            try { s_Cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // async void is deliberate: this is a fire-and-forget editor entry point and every exception is caught.
        static async void Run(SplatPressoSettings settings, bool probe, CancellationToken ct)
        {
            try
            {
                var report = await ConnectionTester.TestAsync(settings, probe, ct);
                LastReport = report;
                string text = "[SplatPresso] Connection test:\n" + report;
                if (report.ok)
                    Debug.Log(text);
                else
                    Debug.LogWarning(text);
            }
            catch (OperationCanceledException)
            {
                LastReport = null;
                LastError = EditorApplication.timeSinceStartup >= s_Deadline ? "Timed out." : "Cancelled.";
            }
            catch (Exception e)
            {
                LastReport = null;
                LastError = e.Message;
                Debug.LogWarning("[SplatPresso] Connection test failed: " + e.Message);
            }
            finally
            {
                Finish();
            }
        }

        static void Finish()
        {
            IsRunning = false;
            LastFinished = DateTime.Now;
            EditorApplication.update -= Pump;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            s_Cts?.Dispose();
            s_Cts = null;
            Changed?.Invoke();
        }

        static void Pump()
        {
            if (!IsRunning)
            {
                EditorApplication.update -= Pump;
                return;
            }
            if (EditorApplication.timeSinceStartup > s_Deadline)
                Cancel();
            // Keep Awaitable frame continuations advancing while the editor would otherwise sleep.
            if (!EditorApplication.isPlaying)
                EditorApplication.QueuePlayerLoopUpdate();
            if (EditorApplication.timeSinceStartup >= s_NextRepaint)
            {
                s_NextRepaint = EditorApplication.timeSinceStartup + 0.25;
                Changed?.Invoke();
            }
        }

        static void OnBeforeReload()
        {
            // The awaiting continuation dies with the domain; abort the in-flight request instead of leaking it.
            Cancel();
        }

        /// <summary>Draws the running state and the last report (IMGUI).</summary>
        public static void DrawResultsGUI()
        {
            if (IsRunning)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"Testing{(LastWithProbes ? " (with media probes)" : "")}... {Elapsed:0}s", EditorStyles.miniBoldLabel);
                    if (GUILayout.Button("Cancel", GUILayout.Width(70)))
                        Cancel();
                }
                return;
            }
            if (!string.IsNullOrEmpty(LastError))
            {
                EditorGUILayout.HelpBox("Connection test did not finish: " + LastError, MessageType.Warning);
                return;
            }
            var r = LastReport;
            if (r == null)
                return;
            string when = LastFinished.HasValue ? $" ({LastFinished.Value:HH:mm:ss})" : "";
            EditorGUILayout.HelpBox((r.ok ? "Connection OK" : "Connection has problems") + when, r.ok ? MessageType.Info : MessageType.Warning);
            var style = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            string text = r.ToString();
            float height = style.CalcHeight(new GUIContent(text), Mathf.Max(200f, EditorGUIUtility.currentViewWidth - 60f));
            EditorGUILayout.SelectableLabel(text, style, GUILayout.Height(height));
        }
    }
}
