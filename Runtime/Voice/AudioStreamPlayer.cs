using System;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>
    /// Streaming playback of 24 kHz mono PCM16 audio (the OpenAI Realtime output format). Decoded samples go into
    /// a lock-protected ring buffer (60 s; oldest dropped on overflow); <c>OnAudioFilterRead</c> pulls them with
    /// linear resampling to the engine output rate and zero-fills on underrun. The AudioSource plays a looping
    /// silent clip so the filter runs.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Audio Stream Player")]
    public sealed class AudioStreamPlayer : MonoBehaviour
    {
        /// <summary>Sample rate of the enqueued PCM16 stream.</summary>
        public const int SourceRate = 24000;

        // The server streams TTS much faster than real time, so the ring must hold the LONGEST expected
        // monologue: a smaller ring (10 s) overflowed on long narrations and the drop-oldest policy discarded
        // not-yet-played samples, making playback jump around mid-sentence.
        const int kRingSeconds = 60;
        const int kSilenceSamples = 1024;

        readonly object m_Lock = new object();
        readonly float[] m_Ring = new float[SourceRate * kRingSeconds];
        int m_ReadPos;
        int m_WritePos;
        int m_Count;
        double m_Phase; // fractional position between ring samples

        // Written on the main thread under m_Lock and read by the audio thread under m_Lock:
        // never touch Unity APIs inside OnAudioFilterRead.
        double m_Step = SourceRate / 48000.0;

        AudioSource m_Source;
        AudioClip m_Silence;
        int m_SilenceRate;
        bool m_WarnedBadChunk;

        /// <summary>Seconds of audio buffered and not yet played.</summary>
        public float BufferedSeconds
        {
            get
            {
                lock (m_Lock)
                    return (float)m_Count / SourceRate;
            }
        }

        /// <summary>True while buffered speech is playing.</summary>
        public bool IsPlaying => BufferedSeconds > 0.02f;

        void OnEnable()
        {
            AudioSettings.OnAudioConfigurationChanged += HandleAudioConfigurationChanged;
            ConfigureOutput();
        }

        void OnDisable()
        {
            AudioSettings.OnAudioConfigurationChanged -= HandleAudioConfigurationChanged;
            Flush();
        }

        void OnDestroy()
        {
            if (m_Silence != null)
                Destroy(m_Silence);
            m_Silence = null;
        }

        // A device or sample-rate change (headset plugged in, AudioSettings.Reset) invalidates the cached output
        // rate (wrong pitch otherwise) and stops the AudioSource that keeps the filter alive.
        void HandleAudioConfigurationChanged(bool deviceWasChanged)
        {
            if (this == null || !isActiveAndEnabled)
                return;
            ConfigureOutput();
        }

        void ConfigureOutput()
        {
            int outputRate = AudioSettings.outputSampleRate;
            if (outputRate <= 0)
                outputRate = 48000;
            lock (m_Lock)
                m_Step = (double)SourceRate / outputRate;

            if (m_Source == null)
                m_Source = GetComponent<AudioSource>();
            if (m_Source == null)
                return;

            if (m_Silence == null || m_SilenceRate != outputRate)
            {
                if (m_Silence != null)
                    Destroy(m_Silence);
                m_Silence = AudioClip.Create("SplatPressoVoiceSilence", kSilenceSamples, 1, outputRate, false);
                m_Silence.SetData(new float[kSilenceSamples], 0);
                m_SilenceRate = outputRate;
            }
            m_Source.clip = m_Silence;
            m_Source.loop = true;
            m_Source.playOnAwake = false;
            m_Source.spatialBlend = 0f;
            if (!m_Source.isPlaying)
                m_Source.Play();
        }

        /// <summary>Queues a base64 PCM16 little-endian 24 kHz mono chunk for playback (invalid chunks are ignored).</summary>
        public void EnqueueBase64Pcm16(string base64)
        {
            if (string.IsNullOrEmpty(base64))
                return;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                if (!m_WarnedBadChunk)
                {
                    m_WarnedBadChunk = true;
                    Debug.LogWarning("[SplatPresso] AudioStreamPlayer: invalid base64 audio chunk ignored");
                }
                return;
            }
            EnqueuePcm16(bytes);
        }

        /// <summary>Queues raw PCM16 little-endian 24 kHz mono bytes for playback.</summary>
        public void EnqueuePcm16(byte[] bytes)
        {
            if (bytes == null)
                return;
            int n = bytes.Length / 2;
            lock (m_Lock)
            {
                for (int i = 0; i < n; ++i)
                {
                    short s = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)); // little-endian
                    if (m_Count == m_Ring.Length) // overflow: drop oldest
                    {
                        m_ReadPos = (m_ReadPos + 1) % m_Ring.Length;
                        m_Count--;
                    }
                    m_Ring[m_WritePos] = s / 32768f;
                    m_WritePos = (m_WritePos + 1) % m_Ring.Length;
                    m_Count++;
                }
            }
        }

        /// <summary>
        /// Queues mono samples (-1..1) recorded at <paramref name="sampleRate"/>; they are linearly resampled to
        /// <see cref="SourceRate"/> (used for synthesized replies that do not come as 24 kHz PCM16).
        /// </summary>
        public void EnqueueSamples(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length == 0 || sampleRate <= 0)
                return;
            double step = (double)sampleRate / SourceRate;
            int n = sampleRate == SourceRate ? samples.Length : (int)(samples.Length / step);
            lock (m_Lock)
            {
                for (int i = 0; i < n; ++i)
                {
                    float v;
                    if (sampleRate == SourceRate)
                    {
                        v = samples[i];
                    }
                    else
                    {
                        double pos = i * step;
                        int i0 = (int)pos;
                        int i1 = Math.Min(i0 + 1, samples.Length - 1);
                        v = samples[i0] + (samples[i1] - samples[i0]) * (float)(pos - i0);
                    }
                    if (m_Count == m_Ring.Length) // overflow: drop oldest
                    {
                        m_ReadPos = (m_ReadPos + 1) % m_Ring.Length;
                        m_Count--;
                    }
                    m_Ring[m_WritePos] = Mathf.Clamp(v, -1f, 1f);
                    m_WritePos = (m_WritePos + 1) % m_Ring.Length;
                    m_Count++;
                }
            }
        }

        /// <summary>Discards all buffered audio immediately (barge-in / cancellation).</summary>
        public void Flush()
        {
            lock (m_Lock)
            {
                m_ReadPos = 0;
                m_WritePos = 0;
                m_Count = 0;
                m_Phase = 0;
            }
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            int frames = data.Length / channels;
            lock (m_Lock)
            {
                double step = m_Step;
                for (int f = 0; f < frames; ++f)
                {
                    float sample = 0f;
                    if (m_Count >= 2)
                    {
                        float s0 = m_Ring[m_ReadPos];
                        float s1 = m_Ring[(m_ReadPos + 1) % m_Ring.Length];
                        sample = s0 + (s1 - s0) * (float)m_Phase;
                        m_Phase += step;
                        while (m_Phase >= 1.0 && m_Count > 1)
                        {
                            m_ReadPos = (m_ReadPos + 1) % m_Ring.Length;
                            m_Count--;
                            m_Phase -= 1.0;
                        }
                    }
                    // else underrun: zero-fill
                    int baseIdx = f * channels;
                    for (int c = 0; c < channels; ++c)
                        data[baseIdx + c] = sample;
                }
            }
        }
    }
}
