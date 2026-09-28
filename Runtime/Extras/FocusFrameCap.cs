using UnityEngine;

namespace SplatPresso.Extras
{
    /// <summary>
    /// Opt-in: caps <see cref="Application.targetFrameRate"/> while the application window is unfocused and
    /// restores the previous value when focus returns (or when this component is disabled).
    /// </summary>
    /// <remarks>
    /// Why: an occluded/unfocused player window gets no compositor (DWM) throttling and can push Present at
    /// ~700 fps; on the D3D12 path the player loop was observed to hang after 2-3 minutes of that (main-thread
    /// heartbeat stops while the render thread spins). Capping to 30 fps while unfocused removes the trigger, and
    /// the generation pipeline still progresses normally at 30 fps. Focus changes are logged so a freeze can be
    /// narrowed down in Player.log.
    /// Enable it either by adding the component, or with <see cref="SplatPressoSettings.capFrameRateWhenUnfocused"/>
    /// (a persistent instance is then created after the first scene load if the scene has none).
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Extras/Focus Frame Cap")]
    public sealed class FocusFrameCap : MonoBehaviour
    {
        /// <summary>Frame rate while unfocused.</summary>
        [Min(1)] public int unfocusedFrameRate = 30;
        /// <summary>Also apply in the editor's play mode (the hang was observed in players).</summary>
        [Tooltip("Also cap in the editor's play mode (the hang was observed in standalone players).")]
        public bool applyInEditor;
        /// <summary>Log focus changes (with time and frame) to help locate freezes.</summary>
        public bool logFocusChanges = true;

        bool m_Capped;
        int m_PreviousTargetFrameRate;

        // Honors the settings flag without scene setup. Runs after Awake, so a root that assigned
        // SplatPressoSettings.Active (or added this component) in Awake is seen here.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InstallFromSettings()
        {
            var s = SplatPressoSettings.FindActive();
            if (s == null || !s.capFrameRateWhenUnfocused)
                return;
            if (FindFirstObjectByType<FocusFrameCap>(FindObjectsInactive.Include) != null)
                return;
            var go = new GameObject("SplatPresso Focus Frame Cap");
            DontDestroyOnLoad(go);
            go.AddComponent<FocusFrameCap>();
        }

        void OnApplicationFocus(bool focused)
        {
            if (!isActiveAndEnabled)
                return;
            if (logFocusChanges)
                Debug.Log($"[SplatPresso] focus={(focused ? "gained" : "LOST")} t={Time.realtimeSinceStartup:F0}s frame={Time.frameCount}");
            if (Application.isEditor && !applyInEditor)
                return;
            if (focused)
                Restore();
            else
                Cap();
        }

        void OnDisable() => Restore();

        void Cap()
        {
            if (m_Capped)
                return;
            m_PreviousTargetFrameRate = Application.targetFrameRate;
            m_Capped = true;
            Application.targetFrameRate = Mathf.Max(1, unfocusedFrameRate);
        }

        void Restore()
        {
            if (!m_Capped)
                return;
            m_Capped = false;
            // only restore if nobody changed it meanwhile
            if (Application.targetFrameRate == Mathf.Max(1, unfocusedFrameRate))
                Application.targetFrameRate = m_PreviousTargetFrameRate;
        }
    }
}
