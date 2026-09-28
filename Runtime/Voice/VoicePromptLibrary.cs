using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SplatPresso.Voice
{
    /// <summary>
    /// Prompts, tool definitions and the structured-output schema shared by both voice backends.
    /// </summary>
    /// <remarks>
    /// The rule wording encodes past failures of the original voice agent:
    /// the model announced "I'll make it" without calling the tool (-> CREATE/TOOL RULE), told users to wait while
    /// a generation was running (-> "multiple generations can run in parallel"), and kept bringing up pipeline
    /// history (-> CONVERSATION STYLE). Keep these sentences when editing.
    /// </remarks>
    public static class VoicePromptLibrary
    {
        /// <summary>Realtime tool that starts a generation.</summary>
        public const string ToolRequestPlacement = "request_placement";
        /// <summary>Realtime tool that cancels generations.</summary>
        public const string ToolCancelGeneration = "cancel_generation";
        /// <summary>json_schema name of the chat backend's reply.</summary>
        public const string ChatSchemaName = "voice_turn";
        /// <summary>Prefix of pipeline progress notices.</summary>
        public const string PipelinePrefix = "[PIPELINE] ";

        /// <summary>User text of a narration-only chat call (appended after the pending notices).</summary>
        public const string NarrateInstruction =
            "[NARRATE] Relay the new notice(s) to the user in one short sentence. transcript must be \"\" and actions must be [].";

        /// <summary>Default language when <see cref="SplatPressoSettings.fallbackLanguage"/> is empty.</summary>
        public const string DefaultFallbackLanguage = "English";

        // ------------------------------------------------------------------------------------------
        // Language

        /// <summary>English name of a reply language (null for Auto).</summary>
        public static string LanguageName(ReplyLanguage language)
        {
            switch (language)
            {
                case ReplyLanguage.English: return "English";
                case ReplyLanguage.Korean: return "Korean";
                case ReplyLanguage.Japanese: return "Japanese";
                case ReplyLanguage.Chinese: return "Chinese";
                case ReplyLanguage.Spanish: return "Spanish";
                case ReplyLanguage.French: return "French";
                case ReplyLanguage.German: return "German";
                default: return null;
            }
        }

        /// <summary>
        /// The LANGUAGE RULE text. Auto follows the user's language; a fixed language uses the original
        /// (field-tested) wording, which for Korean is exactly the rule the source study ran with.
        /// </summary>
        public static string LanguageRule(ReplyLanguage language, string fallbackLanguage)
        {
            string lang = LanguageName(language);
            if (lang == null)
            {
                string fallback = string.IsNullOrWhiteSpace(fallbackLanguage) ? DefaultFallbackLanguage : fallbackLanguage.Trim();
                return $"Reply in the language the user spoke; if unsure, reply in {fallback}. Never mix languages within a reply.";
            }
            if (language == ReplyLanguage.English)
                return "ALWAYS speak English, even if the user used another language. Never mix languages within a reply.";
            return $"ALWAYS speak {lang}. Reply in {lang} even if unsure what language the user used. " +
                   "The ONLY exception: if the user clearly spoke English, you may reply in English. " +
                   "Never mix languages within a reply.";
        }

        static string LanguageRule(SplatPressoSettings s) =>
            LanguageRule(s != null ? s.replyLanguage : ReplyLanguage.Auto, s != null ? s.fallbackLanguage : null);

        static void AppendCustomInstructions(StringBuilder sb, SplatPressoSettings s)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.customInstructions))
                return;
            sb.Append("\nADDITIONAL INSTRUCTIONS (from the app developer): ");
            sb.Append(s.customInstructions.Trim());
        }

        // ------------------------------------------------------------------------------------------
        // OpenAI Realtime

        /// <summary>Session instructions for the OpenAI Realtime backend.</summary>
        public static string BuildRealtimeInstructions(SplatPressoSettings s)
        {
            var sb = new StringBuilder();
            sb.Append("LANGUAGE RULE (highest priority): ").Append(LanguageRule(s)).Append('\n');
            sb.Append(
                "CONVERSATION STYLE: respond ONLY to the user's current utterance. Do not bring up " +
                "earlier requests, past generations, pipeline history or previous snapshots unless the " +
                "user explicitly asks about them. Keep replies to one or two short sentences.\n");
            sb.Append(
                "TOOL RULE: when the user asks to add, create, place or make ANY object, you MUST call " +
                "request_placement immediately, in that same turn, BEFORE saying anything. Never just " +
                "say that you can do it, never announce that you are about to do it without actually " +
                "calling the tool, and never ask for confirmation of a clear request. Multiple " +
                "generations can run in parallel: if something is already generating, still call the " +
                "tool for the new request instead of telling the user to wait.\n");
            sb.Append(
                "You are a voice assistant inside a 3D scene viewer. The user is looking at a " +
                "reconstructed photoreal 3D scene (a gaussian splat capture). Your special ability: " +
                "when the user wants new object(s) added to the scene, call the request_placement " +
                "tool. Ask at most one short clarifying question if the object or rough location is " +
                "genuinely ambiguous; otherwise just call the tool. Generation takes about one to two " +
                "minutes; after calling the tool, briefly tell the user you started. You will receive " +
                "system messages prefixed [PIPELINE] about progress; briefly relay their content to " +
                "the user (in the reply language), then drop the topic. Never read JSON, URLs or technical " +
                "details aloud. If the user asks to stop or cancel, call cancel_generation. Some user " +
                "turns include a snapshot image of what the user is currently looking at: use it to " +
                "resolve references like 'this', 'here' or 'next to that', and to write better tool " +
                "arguments. Do not describe the snapshot unprompted and never mention that you " +
                "received an image.");
            AppendCustomInstructions(sb, s);
            return sb.ToString();
        }

        /// <summary>Realtime tool definitions (request_placement, cancel_generation).</summary>
        public static JArray BuildRealtimeTools()
        {
            var requestPlacement = new JObject
            {
                ["type"] = "function",
                ["name"] = ToolRequestPlacement,
                ["description"] =
                    "Start generating and placing new 3D object(s) into the scene the user is " +
                    "looking at. Call this once the desired object(s) are clear. All argument " +
                    "text MUST be in ENGLISH, regardless of the conversation language.",
                ["parameters"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["intent_summary"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "1-2 English sentences describing what the user wants added, " +
                                "written for a vision model that sees the current camera view.",
                        },
                        ["objects"] = new JObject
                        {
                            ["type"] = "array",
                            ["minItems"] = 1,
                            ["maxItems"] = VoiceRequestSanitizer.MaxObjectTypes,
                            ["description"] = "The distinct object types to add (English only).",
                            ["items"] = new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["name"] = new JObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Short English object name.",
                                    },
                                    ["description"] = new JObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "English description: color, material, style, rough size.",
                                    },
                                    ["count"] = new JObject
                                    {
                                        ["type"] = "integer",
                                        ["minimum"] = 1,
                                        ["maximum"] = VoiceRequestSanitizer.MaxCountPerObject,
                                        ["default"] = 1,
                                        ["description"] = "How many copies of this object (1-3), default 1.",
                                    },
                                },
                                ["required"] = new JArray("name", "description"),
                            },
                        },
                        ["placement_hint"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "English hint for where to place the object(s); use " +
                                "'anywhere sensible' if the user did not specify a location.",
                        },
                    },
                    ["required"] = new JArray("intent_summary", "objects", "placement_hint"),
                },
            };

            var cancelGeneration = new JObject
            {
                ["type"] = "function",
                ["name"] = ToolCancelGeneration,
                ["description"] =
                    "Cancel the in-progress object generation/placement. Call when the user asks " +
                    "to stop or cancel.",
                ["parameters"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject(),
                },
            };

            return new JArray(requestPlacement, cancelGeneration);
        }

        // ------------------------------------------------------------------------------------------
        // GenPresso chat

        /// <summary>System prompt of the GenPresso chat backend (one JSON reply per turn).</summary>
        public static string BuildChatSystemPrompt(SplatPressoSettings s)
        {
            var sb = new StringBuilder();
            sb.Append(
                "You are a voice assistant inside a 3D scene viewer. The user is looking at a reconstructed " +
                "photoreal 3D scene (a gaussian splat capture) and talks to you with push-to-talk. Each user turn " +
                "contains an AUDIO clip of what they just said and usually an IMAGE of what they are looking at " +
                "right now.\n");
            sb.Append("LANGUAGE RULE (highest priority): ").Append(LanguageRule(s)).Append('\n');
            sb.Append(
                "OUTPUT: reply with exactly ONE JSON object matching the schema, nothing else.\n" +
                "- transcript: exactly what the user said, in the language they spoke. If there is no intelligible " +
                "speech, use \"\" and politely ask them to repeat; actions must be [].\n" +
                "- reply: one or two short sentences, spoken style. Never read JSON, URLs or technical details aloud.\n");
            sb.Append(
                "TYPED TURNS: some turns are typed instead of spoken; they have no audio. Treat the quoted typed " +
                "text as what the user said and copy it verbatim into transcript.\n");
            sb.Append(
                "CONVERSATION STYLE: respond ONLY to the user's current utterance. Do not bring up earlier requests, " +
                "past generations, pipeline history or previous images unless the user explicitly asks about them.\n");
            sb.Append(
                "CREATE RULE: when the user asks to add, create, place or make ANY object, you MUST include a " +
                "\"create\" action in THIS reply. Never just say that you can do it, never announce that you are " +
                "about to do it without actually including the action, and never ask for confirmation of a clear " +
                "request. Multiple generations can run in parallel: if something is already generating, still create " +
                "the new request instead of telling the user to wait. Ask at most one short clarifying question " +
                "(with actions: []) only if the object or rough location is genuinely ambiguous. Generation takes " +
                "about one to two minutes; when you include a create action, briefly say you started - never claim " +
                "it is already done. All create fields MUST be in ENGLISH, regardless of the conversation language.\n");
            sb.Append("CANCEL RULE: if the user asks to stop or cancel, include an action of type \"cancel\".\n");
            sb.Append(
                "IMAGE RULE: use the image to resolve references like 'this', 'here' or 'next to that', and to write " +
                "better create fields. Do not describe the image unprompted and never mention that you received an image.\n");
            sb.Append(
                "PIPELINE NOTICES: lines starting with [PIPELINE] are system progress notices, not user speech. " +
                "Briefly relay their content to the user (in the reply language) when asked to, then drop the topic. " +
                "Never create or cancel because of a [PIPELINE] notice.");
            AppendCustomInstructions(sb, s);
            return sb.ToString();
        }

        /// <summary>
        /// Strict structured-output schema of a chat voice turn. Every property is required and
        /// additionalProperties is false; cancel uses empty values instead of nullable types, which is the most
        /// portable form across OpenAI-strict and Gemini's schema subset.
        /// </summary>
        public static JObject BuildChatSchema()
        {
            var objectItem = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JArray("name", "description", "count"),
                ["properties"] = new JObject
                {
                    ["name"] = new JObject { ["type"] = "string", ["description"] = "Short English object name." },
                    ["description"] = new JObject { ["type"] = "string", ["description"] = "English description: color, material, style, rough size." },
                    ["count"] = new JObject { ["type"] = "integer", ["description"] = "How many copies of this object (1-3)." },
                },
            };
            var action = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JArray("type", "intent_summary", "objects", "placement_hint"),
                ["properties"] = new JObject
                {
                    ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray(VoiceAction.TypeCreate, VoiceAction.TypeCancel) },
                    ["intent_summary"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "create: 1-2 English sentences describing what the user wants added, written for a vision model that sees the current camera view. cancel: empty string.",
                    },
                    ["objects"] = new JObject
                    {
                        ["type"] = "array",
                        ["maxItems"] = VoiceRequestSanitizer.MaxObjectTypes,
                        ["description"] = "create: the distinct object types to add (English only). cancel: [].",
                        ["items"] = objectItem,
                    },
                    ["placement_hint"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "create: English hint for where to place the object(s); 'anywhere sensible' if the user did not specify a location. cancel: empty string.",
                    },
                },
            };
            return new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JArray("transcript", "reply", "actions"),
                ["properties"] = new JObject
                {
                    ["transcript"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Verbatim transcription of what the user just said, in the language spoken. Empty string if there was no intelligible speech.",
                    },
                    ["reply"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "What you say back: one or two short spoken-style sentences in the reply language.",
                    },
                    ["actions"] = new JObject
                    {
                        ["type"] = "array",
                        ["maxItems"] = 4,
                        ["items"] = action,
                    },
                },
            };
        }

        /// <summary>User-message text of a spoken turn (pending notices first, then the turn marker).</summary>
        public static string BuildSpokenTurnText(IList<string> notices, bool hasImage)
        {
            var sb = new StringBuilder();
            AppendNotices(sb, notices);
            sb.Append(hasImage
                ? "[TURN] The audio is what I just said; the image is my current view."
                : "[TURN] The audio is what I just said.");
            return sb.ToString();
        }

        /// <summary>User-message text of a typed turn.</summary>
        public static string BuildTypedTurnText(IList<string> notices, string typedText, bool hasImage)
        {
            var sb = new StringBuilder();
            AppendNotices(sb, notices);
            sb.Append("[TURN] I typed this instead of speaking: \"").Append(typedText ?? "").Append('"');
            if (hasImage)
                sb.Append(" The image is my current view.");
            return sb.ToString();
        }

        /// <summary>User-message text of a narration call.</summary>
        public static string BuildNarrationText(IList<string> notices)
        {
            var sb = new StringBuilder();
            AppendNotices(sb, notices);
            sb.Append(NarrateInstruction);
            return sb.ToString();
        }

        /// <summary>
        /// History form of a finished turn's user side. Audio and images are sent only for the current turn; the
        /// history keeps this text so the model knows what was said without re-sending media.
        /// </summary>
        public static string HistoryUserText(IList<string> notices, string transcript, bool typed)
        {
            var sb = new StringBuilder();
            AppendNotices(sb, notices);
            if (string.IsNullOrWhiteSpace(transcript))
                sb.Append(typed ? "User typed nothing." : "User said nothing intelligible.");
            else
                sb.Append(typed ? "User typed: \"" : "User said: \"").Append(transcript.Trim()).Append('"');
            return sb.ToString();
        }

        /// <summary>History form of a narration call's user side: just the notices it relayed.</summary>
        public static string HistoryNoticeText(IList<string> notices)
        {
            var sb = new StringBuilder();
            AppendNotices(sb, notices);
            return sb.Length > 0 ? sb.ToString().TrimEnd('\n') : PipelinePrefix.Trim();
        }

        /// <summary>
        /// History form of the assistant side: the reply and a one-line summary per action, so the model remembers
        /// what it already requested (prevents duplicate creations).
        /// </summary>
        public static string HistoryAssistantText(string reply, IList<PlacementRequest> creates, bool cancel)
        {
            var actions = new JArray();
            if (cancel)
                actions.Add("cancel");
            if (creates != null)
            {
                foreach (var r in creates)
                {
                    if (r?.objects == null)
                        continue;
                    var names = new List<string>();
                    foreach (var o in r.objects)
                        names.Add($"{o.name} x{o.count}");
                    actions.Add($"create: {string.Join(", ", names)} @ '{r.placementHint}'");
                }
            }
            return new JObject { ["reply"] = reply ?? "", ["actions"] = actions }.ToString(Newtonsoft.Json.Formatting.None);
        }

        static void AppendNotices(StringBuilder sb, IList<string> notices)
        {
            if (notices == null)
                return;
            foreach (var n in notices)
            {
                if (string.IsNullOrWhiteSpace(n))
                    continue;
                sb.Append(n.Trim()).Append('\n');
            }
        }
    }
}
