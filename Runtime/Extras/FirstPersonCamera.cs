using System;
using SplatPresso.Voice;
using UnityEngine;

namespace SplatPresso.Extras
{
    /// <summary>
    /// FPS-style first-person controls: the cursor is locked and the mouse always looks (no button held), WASD
    /// walks relative to the view yaw. Esc releases the cursor, left-click locks it again (on release, and not when
    /// the click went to an IMGUI control such as the VoiceHud mode chips). Shift = fast, mouse scroll = adjust speed.
    /// </summary>
    /// <remarks>
    /// Walk mode (default): movement stays on the horizontal plane at the current eye height (gaussian splat
    /// scenes have no colliders, so there is no physical ground - the current height simply persists); Q/E
    /// lowers/raises the eye height. Fly mode: movement follows the full look direction, Q/E moves down/up.
    /// Space is deliberately unused - it is the push-to-talk key. Keys are ignored while the HUD text box has
    /// focus. Works with the legacy Input Manager and the Input System (through <see cref="InputCompat"/>).
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Extras/First Person Camera")]
    public sealed class FirstPersonCamera : MonoBehaviour
    {
        /// <summary>false = walk (horizontal movement, eye height kept; Q/E adjusts height), true = fly along the look direction.</summary>
        [Tooltip("false = walk (horizontal movement, eye height kept; Q/E adjusts height), true = fly along the look direction")]
        public bool flyMode;
        /// <summary>Movement speed (m/s).</summary>
        public float moveSpeed = 2.5f;
        /// <summary>Speed multiplier while Shift is held.</summary>
        public float fastMultiplier = 3f;
        /// <summary>Mouse look sensitivity.</summary>
        public float lookSensitivity = 2f;
        /// <summary>Q/E eye-height change speed in walk mode (m/s).</summary>
        [Tooltip("Q/E eye-height change speed in walk mode (m/s)")]
        public float heightAdjustSpeed = 1.5f;
        /// <summary>Lower bound of the scroll-adjusted speed.</summary>
        public float minSpeed = 0.2f;
        /// <summary>Upper bound of the scroll-adjusted speed.</summary>
        public float maxSpeed = 50f;
        /// <summary>Lock the cursor as soon as the component is enabled (else the first left-click locks).</summary>
        [Tooltip("Lock the cursor as soon as the component is enabled (else the first left-click locks)")]
        public bool lockOnEnable = true;

        float m_Yaw;
        float m_Pitch;

        // Click-to-relock is deferred to the button release and dropped when an IMGUI control grabbed the press:
        // locking on press warps the pointer to the window centre before OnGUI sees the MouseUp, so HUD buttons
        // (VoiceHud mode chips) could never be clicked.
        bool m_RelockPending;
        bool m_RelockClaimedByGui;
        int m_RelockArmedFrame;
        int m_IdleHotControl;

        static bool IsLocked => Cursor.lockState == CursorLockMode.Locked;

        void OnEnable()
        {
            Vector3 e = transform.eulerAngles;
            m_Yaw = e.y;
            m_Pitch = e.x > 180f ? e.x - 360f : e.x;
            if (lockOnEnable && Application.isPlaying)
                LockCursor();
        }

        void OnDisable()
        {
            m_RelockPending = false;
            UnlockCursor();
        }

        void Update()
        {
            bool typing = ExtrasInput.TextInputFocused;

            // cursor lock lifecycle: Esc releases, a left click re-locks on release unless an IMGUI control took
            // the press (suppressed while a HUD panel or the text box is open, so its controls stay clickable)
            UpdatePendingRelock();
            if (!typing && InputCompat.GetKeyDown(KeyCode.Escape))
            {
                m_RelockPending = false;
                UnlockCursor();
            }
            else if (!IsLocked && !m_RelockPending && InputCompat.GetMouseButtonDown(0) && !ExtrasInput.CursorRelockBlocked)
            {
                m_RelockPending = true;
                m_RelockClaimedByGui = false;
                m_RelockArmedFrame = Time.frameCount;
            }

            float scroll = InputCompat.ScrollDelta; // notches
            if (Mathf.Abs(scroll) > 0.0001f)
                moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(1.4f, scroll), minSpeed, maxSpeed);

            if (IsLocked)
            {
                Vector2 look = InputCompat.MouseDelta;
                m_Yaw += look.x * lookSensitivity;
                m_Pitch = Mathf.Clamp(m_Pitch - look.y * lookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            }

            if (typing)
                return;

            Vector3 input = Vector3.zero;
            if (InputCompat.GetKey(KeyCode.W)) input += Vector3.forward;
            if (InputCompat.GetKey(KeyCode.S)) input += Vector3.back;
            if (InputCompat.GetKey(KeyCode.A)) input += Vector3.left;
            if (InputCompat.GetKey(KeyCode.D)) input += Vector3.right;

            bool fast = InputCompat.GetKey(KeyCode.LeftShift) || InputCompat.GetKey(KeyCode.RightShift);
            float speed = moveSpeed * (fast ? fastMultiplier : 1f);
            float dt = Time.unscaledDeltaTime;

            if (flyMode)
            {
                if (InputCompat.GetKey(KeyCode.E)) input += Vector3.up;
                if (InputCompat.GetKey(KeyCode.Q)) input += Vector3.down;
                if (input != Vector3.zero)
                    transform.position += transform.rotation * input.normalized * (speed * dt);
            }
            else
            {
                // walk: yaw-only basis so looking up/down never changes the walking plane
                Quaternion yawRot = Quaternion.Euler(0f, m_Yaw, 0f);
                Vector3 planar = yawRot * new Vector3(input.x, 0f, input.z);
                if (planar.sqrMagnitude > 0.0001f)
                    transform.position += planar.normalized * (speed * dt);

                float height = 0f;
                if (InputCompat.GetKey(KeyCode.E)) height += 1f;
                if (InputCompat.GetKey(KeyCode.Q)) height -= 1f;
                if (height != 0f)
                    transform.position += Vector3.up * (height * heightAdjustSpeed * (fast ? fastMultiplier : 1f) * dt);
            }
        }

        void UpdatePendingRelock()
        {
            if (!m_RelockPending)
                return;
            if (IsLocked || m_RelockClaimedByGui || ExtrasInput.CursorRelockBlocked)
            {
                m_RelockPending = false; // locked elsewhere, or the click belongs to a GUI control / open HUD panel
                return;
            }
            // lock once the button is up and OnGUI has seen the press (at least one frame after it)
            if (Time.frameCount > m_RelockArmedFrame && !InputCompat.GetMouseButton(0))
            {
                m_RelockPending = false;
                LockCursor();
            }
        }

        // IMGUI makes the pressed control hot (GUIUtility.hotControl) on MouseDown and releases it on MouseUp; a
        // hot control appearing after the relock press means the click is a GUI click, not a click-to-lock.
        void OnGUI()
        {
            int hot = GUIUtility.hotControl;
            if (!m_RelockPending)
                m_IdleHotControl = hot;
            else if (hot != 0 && hot != m_IdleHotControl)
                m_RelockClaimedByGui = true;
        }

        static void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        static void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    /// <summary>Input gating shared by the Extras components.</summary>
    public static class ExtrasInput
    {
        /// <summary>
        /// Optional application hook: return true while your own UI needs the cursor (click-to-lock is suppressed).
        /// Reset when play mode starts.
        /// </summary>
        public static Func<bool> CursorRelockBlocker;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => CursorRelockBlocker = null;

        /// <summary>True while the HUD text box has keyboard focus: keyboard shortcuts must be ignored.</summary>
        public static bool TextInputFocused => VoiceHud.TextInputFocused;

        /// <summary>True while a HUD panel (text box, mic picker) or the application hook needs the cursor.</summary>
        public static bool CursorRelockBlocked
        {
            get
            {
                if (VoiceHud.TextInputFocused || VoiceHud.DevicePanelOpen)
                    return true;
                var hook = CursorRelockBlocker;
                if (hook == null)
                    return false;
                try
                {
                    return hook();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    return false;
                }
            }
        }
    }
}
