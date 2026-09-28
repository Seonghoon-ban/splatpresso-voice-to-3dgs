using UnityEngine;

namespace SplatPresso.Extras
{
    /// <summary>
    /// Simple debug fly camera: WASD + Q/E move, hold the right mouse button to look, Shift = fast, mouse scroll =
    /// adjust speed. Keys are ignored while the HUD text box has focus.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Extras/Fly Camera")]
    public sealed class FlyCamera : MonoBehaviour
    {
        /// <summary>Movement speed (m/s).</summary>
        public float moveSpeed = 3f;
        /// <summary>Speed multiplier while Shift is held.</summary>
        public float fastMultiplier = 4f;
        /// <summary>Mouse look sensitivity.</summary>
        public float lookSensitivity = 2f;
        /// <summary>Lower bound of the scroll-adjusted speed.</summary>
        public float minSpeed = 0.2f;
        /// <summary>Upper bound of the scroll-adjusted speed.</summary>
        public float maxSpeed = 50f;

        float m_Yaw;
        float m_Pitch;
        bool m_Looking;

        void OnEnable()
        {
            Vector3 e = transform.eulerAngles;
            m_Yaw = e.y;
            m_Pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        void Update()
        {
            float scroll = InputCompat.ScrollDelta; // notches
            if (Mathf.Abs(scroll) > 0.0001f)
                moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(1.4f, scroll), minSpeed, maxSpeed);

            if (InputCompat.GetMouseButtonDown(1))
            {
                m_Looking = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            if (InputCompat.GetMouseButtonUp(1))
                StopLooking();

            if (m_Looking)
            {
                Vector2 look = InputCompat.MouseDelta;
                m_Yaw += look.x * lookSensitivity;
                m_Pitch = Mathf.Clamp(m_Pitch - look.y * lookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            }

            if (ExtrasInput.TextInputFocused)
                return;

            Vector3 dir = Vector3.zero;
            if (InputCompat.GetKey(KeyCode.W)) dir += Vector3.forward;
            if (InputCompat.GetKey(KeyCode.S)) dir += Vector3.back;
            if (InputCompat.GetKey(KeyCode.A)) dir += Vector3.left;
            if (InputCompat.GetKey(KeyCode.D)) dir += Vector3.right;
            if (InputCompat.GetKey(KeyCode.E)) dir += Vector3.up;
            if (InputCompat.GetKey(KeyCode.Q)) dir += Vector3.down;
            if (dir == Vector3.zero)
                return;

            bool fast = InputCompat.GetKey(KeyCode.LeftShift) || InputCompat.GetKey(KeyCode.RightShift);
            float speed = moveSpeed * (fast ? fastMultiplier : 1f);
            transform.position += transform.rotation * dir.normalized * (speed * Time.unscaledDeltaTime);
        }

        void OnDisable() => StopLooking();

        void StopLooking()
        {
            if (!m_Looking)
                return;
            m_Looking = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
