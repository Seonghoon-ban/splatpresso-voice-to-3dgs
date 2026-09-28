#if UNITY_WEBGL && !UNITY_EDITOR
#define SPLATPRESSO_NO_MICROPHONE
#endif
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>
    /// Push-to-talk microphone capture shared by both voice backends. Records via <c>UnityEngine.Microphone</c>
    /// into a looping clip, reads new samples every frame (with ring wraparound), resamples to
    /// <see cref="TargetSampleRate"/> mono, and
    /// <list type="bullet">
    /// <item>streams ~100 ms base64 PCM16-LE chunks via <see cref="OnAudioChunk"/> (Realtime, 24 kHz), and/or</item>
    /// <item>buffers the utterance and hands it over via <see cref="OnUtteranceFinished"/> / <see cref="StopCapture"/> (chat, 16 kHz).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The microphone runs WARM the whole time the component is enabled: Microphone.Start has 100-500 ms device
    /// latency on Windows, so starting it on the push-to-talk keypress chops off the first syllable. While idle,
    /// resampled audio is only kept in a short local pre-roll buffer (~0.35 s, never sent anywhere);
    /// <see cref="StartCapture"/> flips to capturing and includes that pre-roll, so speech that begins right at (or
    /// a hair before) the keypress survives intact.
    ///
    /// Resampling: integer ratios (the common 48 kHz device to 24 kHz / 16 kHz) use N:1 box-averaging decimation
    /// (cheap anti-aliasing; for 48 to 24 kHz this is the original pair averaging); other rates (e.g. 44.1 kHz) use
    /// stateful linear interpolation.
    ///
    /// Diagnostics: with <see cref="saveDebugWav"/> every finished utterance is written to
    /// <c>&lt;persistentDataPath&gt;/SplatPresso/mic_last.wav</c>: play it to hear exactly what was sent.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Mic Capture")]
    public sealed class MicCapture : MonoBehaviour
    {
        /// <summary>Base64 PCM16 LE mono at <see cref="TargetSampleRate"/>, ~100 ms per chunk (only when <see cref="streamChunks"/>).</summary>
        public event Action<string> OnAudioChunk;
        /// <summary>Raised by <see cref="StopCapture"/> with the finished utterance.</summary>
        public event Action<AudioUtterance> OnUtteranceFinished;
        /// <summary>Raised when a running capture is discarded (device or rate switched mid-utterance, component disabled).</summary>
        public event Action<string> CaptureAborted;

        /// <summary>Linear gain applied to every sample (raise when the level bar barely moves).</summary>
        [NonSerialized] public float gain = 1f;
        /// <summary>Write each finished utterance to persistentDataPath/SplatPresso/mic_last.wav.</summary>
        [NonSerialized] public bool saveDebugWav;
        /// <summary>Emit <see cref="OnAudioChunk"/> while capturing (streaming backends). Off skips the base64 encoding.</summary>
        [NonSerialized] public bool streamChunks = true;
        /// <summary>Buffer the utterance samples (needed by the chat backend and the debug WAV).</summary>
        [NonSerialized] public bool keepUtterance = true;
        /// <summary>Maximum buffered utterance length; later samples are still streamed but not kept.</summary>
        [NonSerialized] public float maxUtteranceSeconds = 60f;

        const float kChunkSeconds = 0.1f;
        const float kPreRollSeconds = 0.35f;
        const int kReadBlock = 1024;      // fixed-size clip reads (AudioClip.GetData wraps at the clip end)
        const int kClipLengthSec = 30;
        const float kMicRetryIntervalSec = 2f;
        const float kLowPeakWarning = 0.15f;

        string m_DeviceName = "";            // configured; empty = system default
        int m_TargetRate = 24000;
        int m_ChunkSamples = 2400;
        int m_PreRollSamples = 8400;

        AudioClip m_Clip;
        string m_ActiveDevice;               // device string actually passed to Microphone.* (null = default)
        int m_ClipFreq;
#pragma warning disable 0414 // only read by the Microphone implementation (unused on WebGL)
        int m_ReadPos;                       // last consumed sample position in the mic clip
#pragma warning restore 0414
        readonly float[] m_ReadBuf = new float[kReadBlock];
        readonly List<float> m_Pending = new List<float>(4800);   // resampled samples (pre-roll while idle)
        readonly List<float> m_Utterance = new List<float>(16000 * 10);
        int m_UtteranceCap;
        bool m_UtteranceTruncated;
        float m_UtterancePeak;
        int m_CapturedSamples;               // all samples of the current utterance (kept or not)

        // resampler state
        int m_DecimFactor;                   // N for N:1 box averaging, 0 when not an integer ratio
        float m_DecimSum;
        int m_DecimCount;
        double m_SrcPos;                     // stateful linear resampler: source-domain cursor
        float m_PrevSample;                  // ... and one sample of history

        bool m_NoMicLogged;
        bool m_DevicesLogged;
        float m_NextMicAttemptTime;

        /// <summary>True between <see cref="StartCapture"/> and <see cref="StopCapture"/>.</summary>
        public bool IsCapturing { get; private set; }

        /// <summary>Peak level of recent input in 0..1 (after gain), for UI meters.</summary>
        public float CurrentLevel { get; private set; }

        /// <summary>Loudest sample (after gain) of the capture in progress, or of the last one.</summary>
        public float CapturePeak => m_UtterancePeak;

        /// <summary>True while the warm microphone is running.</summary>
        public bool IsMicRunning => m_Clip != null;

        /// <summary>Configured device name (empty = system default).</summary>
        public string DeviceName => m_DeviceName;

        /// <summary>Output sample rate of chunks and utterances.</summary>
        public int TargetSampleRate => m_TargetRate;

        /// <summary>Seconds captured so far in the current utterance.</summary>
        public float UtteranceSeconds => IsCapturing ? (float)m_CapturedSamples / m_TargetRate : 0f;

        /// <summary>True once the current utterance reached <see cref="maxUtteranceSeconds"/>.</summary>
        public bool UtteranceFull => IsCapturing && m_UtteranceTruncated;

        /// <summary>False on platforms without microphone support (WebGL).</summary>
        public static bool IsSupported
        {
            get
            {
#if SPLATPRESSO_NO_MICROPHONE
                return false;
#else
                return true;
#endif
            }
        }

        /// <summary>Microphone device names (empty when none or unsupported).</summary>
        public static string[] Devices
        {
            get
            {
#if SPLATPRESSO_NO_MICROPHONE
                return Array.Empty<string>();
#else
                return Microphone.devices ?? Array.Empty<string>();
#endif
            }
        }

        /// <summary>Label of the device the warm microphone is using: "(off)", the device name or "System default".</summary>
        public string ActiveDevice => m_Clip == null ? "(off)" : m_ActiveDevice ?? "System default";

        /// <summary>True when the warm microphone uses the system default device.</summary>
        public bool UsingDefaultDevice => m_Clip != null && m_ActiveDevice == null;

        /// <summary>
        /// Sets device and output rate together; restarts the microphone only when something changed. A running
        /// capture is aborted (see <see cref="CaptureAborted"/>).
        /// </summary>
        public void Configure(string deviceName, int targetSampleRate)
        {
            deviceName = deviceName ?? "";
            targetSampleRate = Mathf.Clamp(targetSampleRate, 8000, 48000);
            bool changed = deviceName != m_DeviceName || targetSampleRate != m_TargetRate;
            m_DeviceName = deviceName;
            SetRate(targetSampleRate);
            if (changed)
                Restart("microphone reconfigured");
        }

        /// <summary>Switches to another device immediately (empty/null = system default); the mic re-warms on it.</summary>
        public void SwitchDevice(string device)
        {
            m_DeviceName = device ?? "";
            Restart("microphone device switched");
        }

        /// <summary>Changes the output rate (24000 for Realtime, 16000 for chat) and re-warms the microphone.</summary>
        public void SetTargetSampleRate(int rate)
        {
            rate = Mathf.Clamp(rate, 8000, 48000);
            if (rate == m_TargetRate)
                return;
            SetRate(rate);
            Restart("sample rate changed");
        }

        /// <summary>
        /// Starts capturing (keeps the idle pre-roll as the start of the utterance). Returns false when no
        /// microphone is available.
        /// </summary>
        public bool StartCapture()
        {
            if (IsCapturing)
                return true;
            if (m_Clip == null)
            {
                TryStartMic(); // cold-start fallback; the first words may clip until the device warms up
                if (m_Clip == null)
                    return false;
            }
            // m_Pending holds the idle pre-roll: it is the start of the utterance
            m_Utterance.Clear();
            m_UtteranceCap = Mathf.Max(m_TargetRate, Mathf.RoundToInt(Mathf.Max(1f, maxUtteranceSeconds) * m_TargetRate));
            m_UtteranceTruncated = false;
            m_UtterancePeak = 0f;
            m_CapturedSamples = 0;
            IsCapturing = true;
            return true;
        }

        /// <summary>
        /// Stops capturing: drains the tail, emits every remaining chunk synchronously (so a streaming backend
        /// appends them BEFORE its commit), raises <see cref="OnUtteranceFinished"/> and returns the utterance
        /// (null when not capturing).
        /// </summary>
        public AudioUtterance StopCapture()
        {
            if (!IsCapturing)
                return null;

            // drain the tail so the last words of the utterance are not lost
            DrainAvailable(finalDrain: true);
            IsCapturing = false;
            CurrentLevel = 0f;

            // flush all remaining samples (full chunks + the final partial one)
            while (m_Pending.Count >= m_ChunkSamples)
                ConsumePending(m_ChunkSamples);
            if (m_Pending.Count > 0)
                ConsumePending(m_Pending.Count);
            // the microphone stays warm for the next press

            var utt = new AudioUtterance
            {
                samples = m_Utterance.ToArray(),
                sampleRate = m_TargetRate,
                peak = m_UtterancePeak,
                truncated = m_UtteranceTruncated,
            };
            m_Utterance.Clear();

            float durationSec = (float)m_CapturedSamples / m_TargetRate;
            string wavNote = "";
            if (saveDebugWav && utt.samples.Length > 0)
            {
                try
                {
                    string path = Path.Combine(Application.persistentDataPath, "SplatPresso", "mic_last.wav");
                    WavUtility.WriteFile(path, utt.samples, utt.samples.Length, m_TargetRate);
                    wavNote = $" -> {path}";
                }
                catch (Exception e)
                {
                    wavNote = $" (wav save failed: {e.Message})";
                }
            }
            Debug.Log($"[SplatPresso] Utterance captured: {durationSec:F2}s @ {m_TargetRate} Hz, peak {m_UtterancePeak:F2}" +
                      (m_UtterancePeak < kLowPeakWarning ? " (LOW - raise micGain or move closer)" : "") +
                      (utt.truncated ? $" (truncated at {maxUtteranceSeconds:F0}s)" : "") + wavNote);

            try { OnUtteranceFinished?.Invoke(utt); }
            catch (Exception e) { Debug.LogException(e); }
            return utt;
        }

        /// <summary>Discards the running capture without emitting anything (the mic stays warm).</summary>
        public void CancelCapture() => AbortCapture("capture cancelled");

        // ------------------------------------------------------------------------------------------
        // lifecycle

        void OnEnable()
        {
            // warm up on the first Update, so the owner can Configure() device and rate first
            m_NextMicAttemptTime = 0f;
        }

        void OnDisable()
        {
            AbortCapture("microphone disabled");
            m_Pending.Clear();
            CurrentLevel = 0f;
            StopMic();
        }

        void Update()
        {
            if (m_Clip == null)
            {
                // the device may have appeared / recovered: retry periodically
                if (Time.unscaledTime >= m_NextMicAttemptTime)
                    TryStartMic();
                return;
            }

            DrainAvailable(finalDrain: false);

            if (IsCapturing)
            {
                while (m_Pending.Count >= m_ChunkSamples)
                    ConsumePending(m_ChunkSamples);
            }
            else
            {
                // idle: keep only the last pre-roll samples
                int excess = m_Pending.Count - m_PreRollSamples;
                if (excess > 0)
                    m_Pending.RemoveRange(0, excess);
            }
        }

        // ------------------------------------------------------------------------------------------
        // internals

        void SetRate(int rate)
        {
            m_TargetRate = rate;
            m_ChunkSamples = Mathf.Max(1, Mathf.RoundToInt(rate * kChunkSeconds));
            m_PreRollSamples = Mathf.RoundToInt(rate * kPreRollSeconds);
        }

        // L7 fix: switching device/rate while capturing used to silently drop the capture flag, so the later
        // StopCapture was a no-op and a streaming backend committed a possibly empty buffer. Now the capture is
        // aborted explicitly and the owner gets a null utterance on release.
        void AbortCapture(string reason)
        {
            if (!IsCapturing)
                return;
            IsCapturing = false;
            m_Utterance.Clear();
            m_Pending.Clear();
            CurrentLevel = 0f;
            Debug.Log($"[SplatPresso] Voice capture aborted: {reason}");
            try { CaptureAborted?.Invoke(reason); }
            catch (Exception e) { Debug.LogException(e); }
        }

        void Restart(string reason)
        {
            AbortCapture(reason);
            m_Pending.Clear();
            CurrentLevel = 0f;
            bool wasRunning = m_Clip != null;
            StopMic();
            m_NoMicLogged = false;
            m_NextMicAttemptTime = 0f;
            if (wasRunning && isActiveAndEnabled)
                TryStartMic();
        }

        void StopMic()
        {
            if (m_Clip == null)
                return;
#if !SPLATPRESSO_NO_MICROPHONE
            Microphone.End(m_ActiveDevice);
#endif
            m_Clip = null;
        }

        void ResetResampler()
        {
            m_ReadPos = 0;
            m_SrcPos = 0;
            m_PrevSample = 0f;
            m_DecimSum = 0f;
            m_DecimCount = 0;
            m_DecimFactor = 0;
            if (m_ClipFreq > m_TargetRate && m_ClipFreq % m_TargetRate == 0)
                m_DecimFactor = m_ClipFreq / m_TargetRate;
        }

        void TryStartMic()
        {
            m_NextMicAttemptTime = Time.unscaledTime + kMicRetryIntervalSec;
#if SPLATPRESSO_NO_MICROPHONE
            if (!m_NoMicLogged)
            {
                m_NoMicLogged = true;
                Debug.LogWarning("[SplatPresso] Microphone input is not supported on this platform; voice input disabled.");
            }
#else
            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
            {
                if (!m_NoMicLogged)
                {
                    m_NoMicLogged = true;
                    Debug.LogError("[SplatPresso] No microphone device found; voice input disabled (retrying in background)");
                }
                return;
            }

            if (!m_DevicesLogged)
            {
                m_DevicesLogged = true;
                Debug.Log($"[SplatPresso] Microphone devices: {string.Join(" | ", devices)}");
            }

            // empty/unknown configured name -> system default (null)
            m_ActiveDevice = null;
            if (!string.IsNullOrEmpty(m_DeviceName))
            {
                if (Array.IndexOf(devices, m_DeviceName) >= 0)
                    m_ActiveDevice = m_DeviceName;
                else
                    Debug.LogWarning($"[SplatPresso] Mic device '{m_DeviceName}' not found; using the system default");
            }

            m_Clip = Microphone.Start(m_ActiveDevice, true, kClipLengthSec, m_TargetRate);
            if (m_Clip == null)
            {
                if (!m_NoMicLogged)
                {
                    m_NoMicLogged = true;
                    Debug.LogError("[SplatPresso] Microphone.Start failed; voice input disabled (retrying in background)");
                }
                return;
            }

            m_ClipFreq = m_Clip.frequency; // actual device rate; may differ from the requested rate
            ResetResampler();
            m_Pending.Clear();
            m_NoMicLogged = false;
            Debug.Log($"[SplatPresso] Microphone warm: '{m_ActiveDevice ?? "system default"}' @ {m_ClipFreq} Hz -> {m_TargetRate} Hz" +
                      (m_DecimFactor > 1 ? $" ({m_DecimFactor}:1 averaged decimation)" : m_ClipFreq != m_TargetRate ? " (linear resample)" : ""));
#endif
        }

        void DrainAvailable(bool finalDrain)
        {
#if !SPLATPRESSO_NO_MICROPHONE
            if (m_Clip == null)
                return;
            int clipSamples = m_Clip.samples;
            if (clipSamples <= 0)
                return;
            int pos = Microphone.GetPosition(m_ActiveDevice);
            if (pos < 0 || pos > clipSamples)
                return;
            int avail = pos - m_ReadPos;
            if (avail < 0)
                avail += clipSamples;

            while (avail >= kReadBlock)
            {
                m_Clip.GetData(m_ReadBuf, m_ReadPos); // wraps around the clip end per Unity docs
                m_ReadPos = (m_ReadPos + kReadBlock) % clipSamples;
                avail -= kReadBlock;
                ProcessBlock(m_ReadBuf, kReadBlock);
            }

            if (finalDrain && avail > 0)
            {
                var tail = new float[avail];
                m_Clip.GetData(tail, m_ReadPos);
                m_ReadPos = (m_ReadPos + avail) % clipSamples;
                ProcessBlock(tail, avail);
            }
#endif
        }

        void ProcessBlock(float[] src, int count)
        {
            float peak = 0f;
            for (int i = 0; i < count; ++i)
            {
                float a = src[i] < 0 ? -src[i] : src[i];
                if (a > peak) peak = a;
            }
            CurrentLevel = Mathf.Max(peak * gain, CurrentLevel * 0.9f);

            if (m_ClipFreq == m_TargetRate)
            {
                for (int i = 0; i < count; ++i)
                    m_Pending.Add(src[i]);
                return;
            }

            if (m_DecimFactor > 1)
            {
                // integer ratio (48 kHz -> 24 kHz / 16 kHz): N:1 decimation with box averaging (cheap
                // anti-aliasing); a partial group carries over to the next block
                float inv = 1f / m_DecimFactor;
                for (int i = 0; i < count; ++i)
                {
                    m_DecimSum += src[i];
                    if (++m_DecimCount == m_DecimFactor)
                    {
                        m_Pending.Add(m_DecimSum * inv);
                        m_DecimSum = 0f;
                        m_DecimCount = 0;
                    }
                }
                return;
            }

            // generic rate (e.g. 44.1 kHz): stateful linear resample
            double step = (double)m_ClipFreq / m_TargetRate;
            while (true)
            {
                int i0 = (int)Math.Floor(m_SrcPos);
                if (i0 + 1 >= count)
                    break; // need the next block for the second interpolation sample
                float s0 = i0 >= 0 ? src[i0] : m_PrevSample;
                float s1 = src[i0 + 1];
                float t = (float)(m_SrcPos - i0);
                m_Pending.Add(s0 + (s1 - s0) * t);
                m_SrcPos += step;
            }
            m_SrcPos -= count;
            m_PrevSample = src[count - 1];
        }

        // Moves sampleCount pending samples into the utterance (gain applied) and, when streaming, emits them as
        // one base64 PCM16 chunk.
        void ConsumePending(int sampleCount)
        {
            byte[] bytes = streamChunks ? new byte[sampleCount * 2] : null;
            for (int i = 0; i < sampleCount; ++i)
            {
                float f = Mathf.Clamp(m_Pending[i] * gain, -1f, 1f);
                float a = f < 0 ? -f : f;
                if (a > m_UtterancePeak) m_UtterancePeak = a;
                if (keepUtterance)
                {
                    // L2 fix: the buffer is capped (it grew without bound in continuous VAD mode)
                    if (m_Utterance.Count < m_UtteranceCap)
                        m_Utterance.Add(f);
                    else
                        m_UtteranceTruncated = true;
                }
                else if (m_CapturedSamples + i >= m_UtteranceCap)
                {
                    m_UtteranceTruncated = true;
                }
                if (bytes != null)
                {
                    short s = WavUtility.ToPcm16Sample(f);
                    bytes[i * 2] = (byte)(s & 0xFF);
                    bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
            }
            m_CapturedSamples += sampleCount;
            m_Pending.RemoveRange(0, sampleCount);
            if (bytes != null)
            {
                try { OnAudioChunk?.Invoke(Convert.ToBase64String(bytes)); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
