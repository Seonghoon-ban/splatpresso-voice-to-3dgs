using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SplatPresso.Voice
{
    /// <summary>
    /// Builders for OpenAI Realtime client events (GA wire shapes) plus small parsing helpers for server events.
    /// All payloads are JObjects so callers can tweak them before sending.
    /// </summary>
    public static class RealtimeProtocol
    {
        /// <summary>Audio format of both directions: PCM16 mono at this rate.</summary>
        public const int SampleRate = 24000;
        /// <summary>Model used for input transcription ("Heard: ..." transcripts).</summary>
        public const string TranscriptionModel = "gpt-4o-mini-transcribe";

        /// <summary>
        /// session.update with instructions, tools, audio formats, voice and turn detection
        /// (null = push-to-talk; semantic_vad when <see cref="SplatPressoSettings.useSemanticVad"/>).
        /// </summary>
        public static JObject SessionUpdate(SplatPressoSettings s, string instructions, JArray tools, string voiceOverride = null)
        {
            bool vad = s != null && s.useSemanticVad;
            string voice = !string.IsNullOrWhiteSpace(voiceOverride) ? voiceOverride.Trim()
                : s != null && !string.IsNullOrWhiteSpace(s.realtimeVoice) ? s.realtimeVoice.Trim() : "cedar";
            return new JObject
            {
                ["type"] = RealtimeEventNames.SessionUpdate,
                ["session"] = new JObject
                {
                    ["type"] = "realtime",
                    ["output_modalities"] = new JArray("audio"),
                    ["instructions"] = instructions ?? "",
                    ["audio"] = new JObject
                    {
                        ["input"] = new JObject
                        {
                            ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = SampleRate },
                            ["turn_detection"] = vad
                                ? (JToken)new JObject
                                {
                                    ["type"] = "semantic_vad",
                                    // true: the server both detects the turn and creates the response.
                                    ["create_response"] = true,
                                }
                                : JValue.CreateNull(),
                            ["transcription"] = new JObject { ["model"] = TranscriptionModel },
                        },
                        ["output"] = new JObject
                        {
                            ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = SampleRate },
                            ["voice"] = voice,
                        },
                    },
                    ["tools"] = tools ?? new JArray(),
                    ["tool_choice"] = "auto",
                },
            };
        }

        /// <summary>
        /// Fallback for servers that ignore the GA-nested audio.input.transcription field: the legacy (beta)
        /// top-level session field. Sent once per session when session.updated shows no transcription config.
        /// </summary>
        public static JObject SessionUpdateLegacyTranscription() => new JObject
        {
            ["type"] = RealtimeEventNames.SessionUpdate,
            ["session"] = new JObject
            {
                ["input_audio_transcription"] = new JObject { ["model"] = TranscriptionModel },
            },
        };

        /// <summary>input_audio_buffer.append with base64 PCM16 24 kHz mono.</summary>
        public static JObject InputAudioAppend(string base64) => new JObject
        {
            ["type"] = RealtimeEventNames.InputAudioBufferAppend,
            ["audio"] = base64,
        };

        public static JObject InputAudioCommit() => new JObject { ["type"] = RealtimeEventNames.InputAudioBufferCommit };

        public static JObject InputAudioClear() => new JObject { ["type"] = RealtimeEventNames.InputAudioBufferClear };

        /// <summary>A system message item (used for [PIPELINE] notices).</summary>
        public static JObject ConversationItemCreateSystemMessage(string text) => new JObject
        {
            ["type"] = RealtimeEventNames.ConversationItemCreate,
            ["item"] = new JObject
            {
                ["type"] = "message",
                ["role"] = "system",
                ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = text ?? "" }),
            },
        };

        /// <summary>A typed user message item (text input without the microphone).</summary>
        public static JObject ConversationItemCreateUserText(string text) => new JObject
        {
            ["type"] = RealtimeEventNames.ConversationItemCreate,
            ["item"] = new JObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = text ?? "" }),
            },
        };

        /// <summary>
        /// Snapshot of the user's current view, attached as a user image. GA realtime models accept input_image
        /// content on user message items; image_url is a plain data-URI string there (not an object).
        /// </summary>
        public static JObject ConversationItemCreateUserImage(string imageDataUri) => new JObject
        {
            ["type"] = RealtimeEventNames.ConversationItemCreate,
            ["item"] = new JObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JArray(new JObject { ["type"] = "input_image", ["image_url"] = imageDataUri }),
            },
        };

        /// <summary>The acknowledgement of a function call (<paramref name="outputJson"/> is a JSON string).</summary>
        public static JObject ConversationItemCreateFunctionOutput(string callId, string outputJson) => new JObject
        {
            ["type"] = RealtimeEventNames.ConversationItemCreate,
            ["item"] = new JObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = outputJson,
            },
        };

        /// <summary>
        /// response.create. <paramref name="eventId"/> (optional) is echoed back as error.event_id if the server
        /// rejects the request, which lets the client clear its "response requested" state.
        /// </summary>
        public static JObject ResponseCreate(string eventId = null)
        {
            var e = new JObject { ["type"] = RealtimeEventNames.ResponseCreate };
            if (!string.IsNullOrEmpty(eventId))
                e["event_id"] = eventId;
            return e;
        }

        public static JObject ResponseCancel() => new JObject { ["type"] = RealtimeEventNames.ResponseCancel };

        // ---- parsing helpers ----

        /// <summary>
        /// Scans a response.done event's response.output[] for function_call items.
        /// Note: 'arguments' is a JSON *string*, not an object.
        /// </summary>
        public static List<(string name, string callId, string argumentsJson)> TryGetFunctionCalls(JObject responseDoneEvent)
        {
            var calls = new List<(string, string, string)>();
            if (!(responseDoneEvent?["response"]?["output"] is JArray output))
                return calls;
            foreach (var item in output)
            {
                if (!(item is JObject o) || (string)o["type"] != "function_call")
                    continue;
                calls.Add(((string)o["name"], (string)o["call_id"], (string)o["arguments"]));
            }
            return calls;
        }
    }
}
