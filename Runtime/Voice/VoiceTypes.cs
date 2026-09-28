using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace SplatPresso.Voice
{
    /// <summary>A placement request produced by the voice agent, with the utterance that produced it.</summary>
    public sealed class VoicePlacementRequest
    {
        /// <summary>What to add and where (English text, counts already clamped).</summary>
        public PlacementRequest request;
        /// <summary>What the user said (or typed) in the turn that produced the request; null when unknown.</summary>
        public string sourceUtterance;
    }

    /// <summary>One finished push-to-talk utterance (mono float samples, gain applied).</summary>
    public sealed class AudioUtterance
    {
        /// <summary>Mono samples in -1..1 at <see cref="sampleRate"/>. Empty when the mic was told not to buffer audio.</summary>
        public float[] samples;
        /// <summary>Sample rate of <see cref="samples"/> in Hz.</summary>
        public int sampleRate;
        /// <summary>Peak absolute level of the utterance (after gain).</summary>
        public float peak;
        /// <summary>True when the utterance hit the maximum length and its tail was not kept.</summary>
        public bool truncated;

        /// <summary>Length of <see cref="samples"/> in seconds.</summary>
        public float DurationSeconds => samples == null || sampleRate <= 0 ? 0f : (float)samples.Length / sampleRate;
    }

    /// <summary>Structured reply of one GenPresso chat voice turn (the <c>voice_turn</c> JSON schema).</summary>
    public sealed class VoiceTurnResponse
    {
        /// <summary>Verbatim transcription of what the user said ("" when nothing intelligible).</summary>
        [JsonProperty("transcript")] public string transcript;
        /// <summary>What the agent says back (reply language).</summary>
        [JsonProperty("reply")] public string reply;
        /// <summary>Requested effects (create / cancel).</summary>
        [JsonProperty("actions")] public List<VoiceAction> actions = new List<VoiceAction>();
    }

    /// <summary>One action of a voice turn: <c>create</c> (objects to generate) or <c>cancel</c> (stop all runs).</summary>
    public sealed class VoiceAction
    {
        public const string TypeCreate = "create";
        public const string TypeCancel = "cancel";

        [JsonProperty("type")] public string type;
        [JsonProperty("intent_summary")] public string intentSummary;
        [JsonProperty("objects")] public List<RequestedObject> objects = new List<RequestedObject>();
        [JsonProperty("placement_hint")] public string placementHint;

        /// <summary>True for a create action (case-insensitive).</summary>
        [JsonIgnore] public bool IsCreate => string.Equals(type?.Trim(), TypeCreate, StringComparison.OrdinalIgnoreCase);
        /// <summary>True for a cancel action (case-insensitive).</summary>
        [JsonIgnore] public bool IsCancel => string.Equals(type?.Trim(), TypeCancel, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The create action as a sanitized <see cref="PlacementRequest"/> (see <see cref="VoiceRequestSanitizer"/>),
        /// or null (with a reason) when it is not a create action or no valid object remains.
        /// </summary>
        public PlacementRequest ToPlacementRequest(out string reason)
        {
            if (!IsCreate)
            {
                reason = $"action type '{type}' is not '{TypeCreate}'";
                return null;
            }
            return VoiceRequestSanitizer.Sanitize(new PlacementRequest
            {
                intentSummary = intentSummary,
                objects = objects,
                placementHint = placementHint,
            }, out reason);
        }
    }

    /// <summary>
    /// Enforces in code what the model-facing schemas can only ask for: object names present, at most
    /// <see cref="MaxObjectTypes"/> object types, 1..<see cref="MaxCountPerObject"/> copies each, a placement hint.
    /// Used for both the chat backend's actions and the Realtime backend's tool arguments.
    /// </summary>
    public static class VoiceRequestSanitizer
    {
        /// <summary>Maximum distinct object types per request.</summary>
        public const int MaxObjectTypes = 4;
        /// <summary>Maximum copies of one object type.</summary>
        public const int MaxCountPerObject = 3;
        /// <summary>Hint used when the model left the placement hint empty.</summary>
        public const string DefaultPlacementHint = "anywhere sensible";

        /// <summary>
        /// A cleaned copy of <paramref name="request"/> (the input is not modified), or null with a reason when no
        /// object with a name remains.
        /// </summary>
        public static PlacementRequest Sanitize(PlacementRequest request, out string reason)
        {
            if (request == null)
            {
                reason = "no request";
                return null;
            }
            var clean = new PlacementRequest
            {
                intentSummary = request.intentSummary?.Trim(),
                placementHint = string.IsNullOrWhiteSpace(request.placementHint) ? DefaultPlacementHint : request.placementHint.Trim(),
                objects = new List<RequestedObject>(),
            };
            int dropped = 0;
            if (request.objects != null)
            {
                foreach (var o in request.objects)
                {
                    if (o == null || string.IsNullOrWhiteSpace(o.name))
                    {
                        dropped++;
                        continue;
                    }
                    if (clean.objects.Count >= MaxObjectTypes)
                    {
                        dropped++;
                        continue;
                    }
                    clean.objects.Add(new RequestedObject
                    {
                        name = o.name.Trim(),
                        description = o.description?.Trim() ?? "",
                        count = Mathf.Clamp(o.count, 1, MaxCountPerObject),
                    });
                }
            }
            if (clean.objects.Count == 0)
            {
                reason = "the request contains no named objects";
                return null;
            }
            if (string.IsNullOrWhiteSpace(clean.intentSummary))
            {
                var names = new List<string>();
                foreach (var o in clean.objects)
                    names.Add(o.name);
                clean.intentSummary = "Add " + string.Join(", ", names) + " to the scene.";
            }
            if (dropped > 0)
                Debug.LogWarning($"[SplatPresso] Voice request: dropped {dropped} object entr{(dropped == 1 ? "y" : "ies")} (unnamed or more than {MaxObjectTypes} types)");
            reason = null;
            return clean;
        }
    }

    /// <summary>What <see cref="VoiceAgent"/> hands a backend when it starts.</summary>
    public sealed class VoiceAgentContext
    {
        /// <summary>Settings in use (backends fall back to <see cref="SplatPressoSettings.Active"/> when null).</summary>
        public SplatPressoSettings settings;
        /// <summary>Shared microphone; null when voice input is unavailable (text-only).</summary>
        public MicCapture mic;
        /// <summary>Speech output for backends that return audio; may be null.</summary>
        public AudioStreamPlayer player;
        /// <summary>JPEG snapshot of the user's current view (the awaitable yields null when unavailable); may be null.</summary>
        public Func<Awaitable<byte[]>> captureJpeg;
        /// <summary>True while push-to-talk is held (narration is deferred meanwhile); may be null.</summary>
        public Func<bool> isTalkHeld;
    }
}
