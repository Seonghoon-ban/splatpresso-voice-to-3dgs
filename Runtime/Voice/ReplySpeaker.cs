using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;
using SplatPresso.Api;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace SplatPresso.Voice
{
    /// <summary>
    /// Speaks the GenPresso chat agent's text replies. Each reply runs through the
    /// <see cref="MediaRouteKeys.TextToSpeech"/> media route (default: MiniMax speech-02-turbo on GenPresso, asked for
    /// 24 kHz PCM16, which is exactly what <see cref="AudioStreamPlayer"/> plays) and is queued on the same player the
    /// Realtime backend uses.
    /// </summary>
    /// <remarks>
    /// Measured on live GenPresso: ~5-10 s from submit to downloaded audio for a short sentence (mostly queue
    /// overhead), a fraction of a credit per reply. Replies are spoken one after another in order; when replies pile up
    /// the oldest waiting one is dropped. <see cref="Stop"/> (push-to-talk barge-in) cancels the one being synthesized,
    /// drops the queue and silences the player. Speech jobs skip the media concurrency gate so they never wait behind
    /// 3D jobs, and are not charged to a run's cost cap. Play mode / players only.
    /// </remarks>
    public sealed class ReplySpeaker : IDisposable
    {
        /// <summary>Sample rate requested from MiniMax (the player's native rate).</summary>
        public const int PcmSampleRate = AudioStreamPlayer.SourceRate;

        const float kFastPollSec = 0.4f;
        const int kMaxWaiting = 2;
        const int kDownloadTimeoutSec = 60;

        readonly Func<SplatPressoSettings> m_Settings;
        readonly AudioStreamPlayer m_Player;
        readonly Queue<string> m_Waiting = new Queue<string>();
        CancellationTokenSource m_Cts = new CancellationTokenSource();
        int m_Generation;
        bool m_Running;
        bool m_Disposed;
        bool m_WarnedFailure;

        /// <summary>Raised on the main thread when a reply's audio was queued: the text, seconds it took to synthesize.</summary>
        public event Action<string, double> Spoken;

        /// <param name="settings">Settings provider (read per reply, so changes apply at once).</param>
        /// <param name="player">Where the audio plays.</param>
        public ReplySpeaker(Func<SplatPressoSettings> settings, AudioStreamPlayer player)
        {
            m_Settings = settings ?? (() => SplatPressoSettings.Active);
            m_Player = player;
        }

        /// <summary>A reply is being synthesized or waits to be.</summary>
        public bool IsPending => m_Running || m_Waiting.Count > 0;

        /// <summary>Queues <paramref name="text"/> to be spoken after the replies before it.</summary>
        public void Speak(string text)
        {
            if (m_Disposed || m_Player == null || string.IsNullOrWhiteSpace(text))
                return;
            while (m_Waiting.Count >= kMaxWaiting)
                m_Waiting.Dequeue(); // replies piled up: the oldest waiting one is stale by now
            m_Waiting.Enqueue(text.Trim());
            if (!m_Running)
                RunAsync(m_Generation, m_Cts.Token);
        }

        /// <summary>Stops speaking now: cancels the reply being synthesized, drops waiting ones, silences the player.</summary>
        public void Stop()
        {
            m_Waiting.Clear();
            m_Generation++;
            m_Running = false;
            var old = m_Cts;
            m_Cts = new CancellationTokenSource();
            try { old.Cancel(); }
            catch (ObjectDisposedException) { }
            if (m_Player != null)
                m_Player.Flush();
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            Stop();
            m_Disposed = true;
        }

        async void RunAsync(int generation, CancellationToken ct)
        {
            m_Running = true;
            try
            {
                while (generation == m_Generation && m_Waiting.Count > 0)
                {
                    string text = m_Waiting.Dequeue();
                    var sw = Stopwatch.StartNew();
                    var audio = await SynthesizeAsync(m_Settings() ?? SplatPressoSettings.Active, text, ct);
                    if (generation != m_Generation || ct.IsCancellationRequested)
                        return;
                    if (audio.samples == null || audio.samples.Length == 0)
                        continue;
                    m_Player.EnqueueSamples(audio.samples, audio.sampleRate);
                    double secs = sw.Elapsed.TotalSeconds;
                    Debug.Log($"[SplatPresso] Spoken reply ready after {secs:F1}s ({(float)audio.samples.Length / audio.sampleRate:F1}s of audio)");
                    try { Spoken?.Invoke(text, secs); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                // Speech is a nicety: the reply is already on screen. Warn once, then only log.
                if (!m_WarnedFailure)
                {
                    m_WarnedFailure = true;
                    Debug.LogWarning($"[SplatPresso] Could not speak the reply (the subtitle still shows it): {e.Message}. " +
                                     "Turn Voice > speakReplies off or change the textToSpeech media route in Project Settings > SplatPresso.");
                }
                else
                {
                    Debug.Log($"[SplatPresso] Spoken reply failed: {e.Message}");
                }
            }
            finally
            {
                if (generation == m_Generation)
                    m_Running = false;
            }
        }

        // ------------------------------------------------------------------------------------------
        // synthesis

        /// <summary>Decoded mono audio.</summary>
        public struct SpeechAudio
        {
            public float[] samples;
            public int sampleRate;
        }

        /// <summary>Runs one text-to-speech job and returns its decoded mono audio (throws on failure).</summary>
        public static async Awaitable<SpeechAudio> SynthesizeAsync(SplatPressoSettings s, string text, CancellationToken ct)
        {
            s = s != null ? s : SplatPressoSettings.Active;
            var route = s.GetRoute(MediaRouteKeys.TextToSpeech) ?? SplatPressoSettings.DefaultRoute(MediaRouteKeys.TextToSpeech);
            var client = new MediaJobClient(s, null) { UseConcurrencyGate = false, FirstPollIntervalSec = kFastPollSec };
            var job = await client.RunAsync(MediaRouteKeys.TextToSpeech, route, t => BuildInput(s, t, text), "spoken reply", null, ct);

            JObject result = job.result;
            var audio = result?["audio"] as JObject;
            string url = (string)audio?["url"] ?? (string)result?["audio_url"];
            string contentType = (string)audio?["content_type"] ?? "";
            if (string.IsNullOrEmpty(url))
                throw new GenpressoException($"text-to-speech ({job.resolvedPath}) returned no audio URL", GenpressoErrorKind.Parse, 200,
                    result?.ToString(Newtonsoft.Json.Formatting.None), retryable: false);

            if (IsRawPcm(url, contentType))
            {
                byte[] bytes = await HttpJson.GetBytesAsync(url, null, kDownloadTimeoutSec, ct);
                return new SpeechAudio { samples = Pcm16ToFloats(bytes, 0, bytes.Length), sampleRate = PcmSampleRate };
            }
            if (IsWav(url, contentType))
            {
                byte[] bytes = await HttpJson.GetBytesAsync(url, null, kDownloadTimeoutSec, ct);
                if (TryDecodePcm16Wav(bytes, out var samples, out int rate))
                    return new SpeechAudio { samples = samples, sampleRate = rate };
            }
            return await DecodeWithUnityAsync(url, contentType, ct);
        }

        /// <summary>The request body for one text-to-speech target (MiniMax: 24 kHz PCM; others: just the text).</summary>
        public static JObject BuildInput(SplatPressoSettings s, MediaTarget target, string text)
        {
            string path = (target?.path ?? "").ToLowerInvariant();
            if (path.Contains("minimax"))
            {
                return new JObject
                {
                    ["text"] = text,
                    ["voice_setting"] = new JObject
                    {
                        ["voice_id"] = string.IsNullOrWhiteSpace(s.ttsVoice) ? "Friendly_Person" : s.ttsVoice.Trim(),
                        ["speed"] = Mathf.Clamp(s.ttsSpeed, 0.5f, 2f),
                    },
                    ["audio_setting"] = new JObject { ["format"] = "pcm", ["sample_rate"] = PcmSampleRate },
                    ["language_boost"] = "auto",
                    ["output_format"] = "url",
                };
            }
            return new JObject { ["text"] = text };
        }

        static bool IsRawPcm(string url, string contentType) =>
            contentType.IndexOf("pcm", StringComparison.OrdinalIgnoreCase) >= 0 || UrlExtension(url) == ".pcm";

        static bool IsWav(string url, string contentType) =>
            contentType.IndexOf("wav", StringComparison.OrdinalIgnoreCase) >= 0 || UrlExtension(url) == ".wav";

        static string UrlExtension(string url)
        {
            string path = url.Split('?')[0];
            int dot = path.LastIndexOf('.');
            return dot >= 0 && dot > path.LastIndexOf('/') ? path.Substring(dot).ToLowerInvariant() : "";
        }

        /// <summary>Little-endian PCM16 mono bytes to samples in -1..1.</summary>
        public static float[] Pcm16ToFloats(byte[] bytes, int offset, int count)
        {
            int n = Math.Max(0, count) / 2;
            var samples = new float[n];
            for (int i = 0; i < n; ++i)
            {
                int b = offset + i * 2;
                samples[i] = (short)(bytes[b] | (bytes[b + 1] << 8)) / 32768f;
            }
            return samples;
        }

        /// <summary>Decodes a PCM16 WAV (any channel count; downmixed to mono). False for other encodings.</summary>
        public static bool TryDecodePcm16Wav(byte[] wav, out float[] samples, out int sampleRate)
        {
            samples = null;
            sampleRate = 0;
            if (wav == null || wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
                return false;
            int channels = 0, bits = 0, format = 0;
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > wav.Length)
                    size = wav.Length - body; // streamed WAVs may carry a bogus length
                if (id == "fmt " && size >= 16)
                {
                    format = BitConverter.ToInt16(wav, body);
                    channels = BitConverter.ToInt16(wav, body + 2);
                    sampleRate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToInt16(wav, body + 14);
                }
                else if (id == "data")
                {
                    if (format != 1 || bits != 16 || channels <= 0 || sampleRate <= 0)
                        return false;
                    var interleaved = Pcm16ToFloats(wav, body, size);
                    samples = Downmix(interleaved, channels);
                    return true;
                }
                pos = body + size + (size & 1);
            }
            return false;
        }

        static float[] Downmix(float[] interleaved, int channels)
        {
            if (channels <= 1)
                return interleaved;
            int frames = interleaved.Length / channels;
            var mono = new float[frames];
            for (int f = 0; f < frames; ++f)
            {
                float sum = 0f;
                for (int c = 0; c < channels; ++c)
                    sum += interleaved[f * channels + c];
                mono[f] = sum / channels;
            }
            return mono;
        }

        // MP3 / OGG (e.g. an ElevenLabs route): let Unity's decoder handle it.
        static async Awaitable<SpeechAudio> DecodeWithUnityAsync(string url, string contentType, CancellationToken ct)
        {
            string ext = UrlExtension(url);
            AudioType type = ext == ".ogg" || contentType.IndexOf("ogg", StringComparison.OrdinalIgnoreCase) >= 0 ? AudioType.OGGVORBIS
                : ext == ".wav" ? AudioType.WAV
                : AudioType.MPEG;
            using (var req = UnityWebRequestMultimedia.GetAudioClip(url, type))
            {
                if (req.downloadHandler is DownloadHandlerAudioClip handler)
                {
                    handler.streamAudio = false;
                    handler.compressed = false;
                }
                req.timeout = kDownloadTimeoutSec;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested)
                    {
                        req.Abort();
                        ct.ThrowIfCancellationRequested();
                    }
                    await Awaitable.NextFrameAsync(ct);
                }
                if (req.result != UnityWebRequest.Result.Success)
                    throw new GenpressoException($"Downloading the spoken reply failed: {req.error}", GenpressoErrorKind.Network, req.responseCode, null);

                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null || clip.samples <= 0)
                    throw new GenpressoException($"The spoken reply ({type}) could not be decoded on this platform", GenpressoErrorKind.Parse, 200, null, retryable: false);
                try
                {
                    if (clip.loadState != AudioDataLoadState.Loaded)
                        clip.LoadAudioData();
                    while (clip.loadState == AudioDataLoadState.Loading)
                        await Awaitable.NextFrameAsync(ct);
                    var data = new float[clip.samples * clip.channels];
                    if (!clip.GetData(data, 0))
                        throw new GenpressoException("The spoken reply could not be read back from its AudioClip", GenpressoErrorKind.Parse, 200, null, retryable: false);
                    return new SpeechAudio { samples = Downmix(data, clip.channels), sampleRate = clip.frequency };
                }
                finally
                {
                    Object.Destroy(clip);
                }
            }
        }
    }
}
