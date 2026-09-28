using UnityEngine;
#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace SplatPresso
{
    /// <summary>
    /// Keyboard/mouse polling that works with the legacy Input Manager, the Input System package, or both.
    /// </summary>
    /// <remarks>
    /// New Unity 6 projects may be Input-System-only ("Active Input Handling = Input System Package"), where
    /// every <c>UnityEngine.Input</c> call throws. All package code polls input through this class. When both
    /// back ends are enabled the Input System is preferred; if it has no device (or no mapping for a KeyCode)
    /// the legacy manager is used. With neither available every query returns false (warned once).
    /// </remarks>
    public static class InputCompat
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        static bool s_LegacyMouseAxesBroken;
#else
        static bool s_WarnedNoBackend;
#endif

        /// <summary>True while the key (or KeyCode.Mouse0..6 button) is held.</summary>
        public static bool GetKey(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
            var c = FindButton(key);
            if (c != null)
                return c.isPressed;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            return key != KeyCode.None && Input.GetKey(key);
#else
            return NoBackend();
#endif
        }

        /// <summary>True during the frame the key went down.</summary>
        public static bool GetKeyDown(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
            var c = FindButton(key);
            if (c != null)
                return c.wasPressedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            return key != KeyCode.None && Input.GetKeyDown(key);
#else
            return NoBackend();
#endif
        }

        /// <summary>True during the frame the key was released.</summary>
        public static bool GetKeyUp(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
            var c = FindButton(key);
            if (c != null)
                return c.wasReleasedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            return key != KeyCode.None && Input.GetKeyUp(key);
#else
            return NoBackend();
#endif
        }

        /// <summary>True while a mouse button is held (0 = left, 1 = right, 2 = middle).</summary>
        public static bool GetMouseButton(int button) => GetKey(MouseKeyCode(button));

        /// <summary>True during the frame a mouse button went down.</summary>
        public static bool GetMouseButtonDown(int button) => GetKeyDown(MouseKeyCode(button));

        /// <summary>True during the frame a mouse button was released.</summary>
        public static bool GetMouseButtonUp(int button) => GetKeyUp(MouseKeyCode(button));

        /// <summary>
        /// Mouse movement this frame, scaled to roughly match legacy <c>Input.GetAxis("Mouse X"/"Mouse Y")</c>.
        /// </summary>
        public static Vector2 MouseDelta
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
                var mouse = Mouse.current;
                if (mouse != null)
                    return mouse.delta.ReadValue() * 0.1f; // pixels -> legacy axis units (default sensitivity 0.1)
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
                if (s_LegacyMouseAxesBroken)
                    return Vector2.zero;
                try
                {
                    return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
                }
                catch (System.ArgumentException)
                {
                    // the project's Input Manager has no "Mouse X"/"Mouse Y" axes
                    s_LegacyMouseAxesBroken = true;
                    Debug.LogWarning("[SplatPresso] InputCompat: Input Manager axes 'Mouse X'/'Mouse Y' are missing; mouse look disabled.");
                    return Vector2.zero;
                }
#else
                NoBackend();
                return Vector2.zero;
#endif
            }
        }

        /// <summary>Vertical scroll this frame in notches (about +-1 per wheel click).</summary>
        public static float ScrollDelta
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    float y = mouse.scroll.ReadValue().y;
                    // Depending on the Input System version/platform the wheel reports 120 per notch or ~1 per notch.
                    return Mathf.Abs(y) >= 20f ? y / 120f : y;
                }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
                return Input.mouseScrollDelta.y;
#else
                NoBackend();
                return 0f;
#endif
            }
        }

        /// <summary>Mouse position in screen pixels (bottom-left origin), or zero when unavailable.</summary>
        public static Vector2 MousePosition
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
                var mouse = Mouse.current;
                if (mouse != null)
                    return mouse.position.ReadValue();
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
                return Input.mousePosition;
#else
                NoBackend();
                return Vector2.zero;
#endif
            }
        }

        /// <summary>True when some keyboard/mouse back end can be polled.</summary>
        public static bool AnyInputAvailable
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
                if (Keyboard.current != null || Mouse.current != null)
                    return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
                return true;
#else
                return false;
#endif
            }
        }

        static KeyCode MouseKeyCode(int button)
        {
            switch (button)
            {
                case 0: return KeyCode.Mouse0;
                case 1: return KeyCode.Mouse1;
                case 2: return KeyCode.Mouse2;
                case 3: return KeyCode.Mouse3;
                case 4: return KeyCode.Mouse4;
                default: return KeyCode.None;
            }
        }

#if !ENABLE_LEGACY_INPUT_MANAGER
        static bool NoBackend()
        {
            if (!s_WarnedNoBackend)
            {
                s_WarnedNoBackend = true;
                Debug.LogWarning("[SplatPresso] No usable input back end: the Input System package is missing and the legacy " +
                                 "Input Manager is disabled (Project Settings > Player > Active Input Handling). Keyboard/mouse " +
                                 "controls are off; use the on-screen UI or scripting API.");
            }
            return false;
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            s_LegacyMouseAxesBroken = false;
#else
            s_WarnedNoBackend = false;
#endif
        }

#if ENABLE_INPUT_SYSTEM && SPLATPRESSO_INPUTSYSTEM
        // Resolves a KeyCode to an Input System button control; null when there is no device or no mapping
        // (the caller then falls back to the legacy manager when it is enabled).
        static ButtonControl FindButton(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Mouse0: return Mouse.current?.leftButton;
                case KeyCode.Mouse1: return Mouse.current?.rightButton;
                case KeyCode.Mouse2: return Mouse.current?.middleButton;
                case KeyCode.Mouse3: return Mouse.current?.backButton;
                case KeyCode.Mouse4: return Mouse.current?.forwardButton;
            }
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return null;
            Key k = MapKey(key);
            return k == Key.None ? null : keyboard[k];
        }

        static Key MapKey(KeyCode code)
        {
            if (code >= KeyCode.A && code <= KeyCode.Z)
                return Key.A + (code - KeyCode.A);
            if (code >= KeyCode.F1 && code <= KeyCode.F12)
                return Key.F1 + (code - KeyCode.F1);
            // Key.Digit1..Digit9 are contiguous but Digit0 comes AFTER Digit9, so map it separately.
            if (code >= KeyCode.Alpha1 && code <= KeyCode.Alpha9)
                return Key.Digit1 + (code - KeyCode.Alpha1);
            if (code >= KeyCode.Keypad0 && code <= KeyCode.Keypad9)
                return Key.Numpad0 + (code - KeyCode.Keypad0);

            switch (code)
            {
                case KeyCode.Alpha0: return Key.Digit0;
                case KeyCode.Space: return Key.Space;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.KeypadEnter: return Key.NumpadEnter;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Backspace: return Key.Backspace;
                case KeyCode.Delete: return Key.Delete;
                case KeyCode.Insert: return Key.Insert;
                case KeyCode.Home: return Key.Home;
                case KeyCode.End: return Key.End;
                case KeyCode.PageUp: return Key.PageUp;
                case KeyCode.PageDown: return Key.PageDown;
                case KeyCode.UpArrow: return Key.UpArrow;
                case KeyCode.DownArrow: return Key.DownArrow;
                case KeyCode.LeftArrow: return Key.LeftArrow;
                case KeyCode.RightArrow: return Key.RightArrow;
                case KeyCode.LeftShift: return Key.LeftShift;
                case KeyCode.RightShift: return Key.RightShift;
                case KeyCode.LeftControl: return Key.LeftCtrl;
                case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftAlt: return Key.LeftAlt;
                case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.LeftCommand: return Key.LeftMeta;
                case KeyCode.RightCommand: return Key.RightMeta;
                case KeyCode.LeftBracket: return Key.LeftBracket;
                case KeyCode.RightBracket: return Key.RightBracket;
                case KeyCode.Comma: return Key.Comma;
                case KeyCode.Period: return Key.Period;
                case KeyCode.Minus: return Key.Minus;
                case KeyCode.Equals: return Key.Equals;
                case KeyCode.Slash: return Key.Slash;
                case KeyCode.Backslash: return Key.Backslash;
                case KeyCode.Semicolon: return Key.Semicolon;
                case KeyCode.Quote: return Key.Quote;
                case KeyCode.BackQuote: return Key.Backquote;
                case KeyCode.KeypadPlus: return Key.NumpadPlus;
                case KeyCode.KeypadMinus: return Key.NumpadMinus;
                case KeyCode.KeypadMultiply: return Key.NumpadMultiply;
                case KeyCode.KeypadDivide: return Key.NumpadDivide;
                case KeyCode.KeypadPeriod: return Key.NumpadPeriod;
                case KeyCode.CapsLock: return Key.CapsLock;
                default: return Key.None;
            }
        }
#endif
    }
}
