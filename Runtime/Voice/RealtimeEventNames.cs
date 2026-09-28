namespace SplatPresso.Voice
{
    /// <summary>
    /// OpenAI Realtime API event type strings. Server events carry both the GA name and, where the wire name
    /// changed, the legacy alias: the dispatcher accepts either so a server-side rename does not break audio.
    /// </summary>
    public static class RealtimeEventNames
    {
        // ---- client -> server ----
        public const string SessionUpdate = "session.update";
        public const string InputAudioBufferAppend = "input_audio_buffer.append";
        public const string InputAudioBufferCommit = "input_audio_buffer.commit";
        public const string InputAudioBufferClear = "input_audio_buffer.clear";
        public const string ConversationItemCreate = "conversation.item.create";
        public const string ResponseCreate = "response.create";
        public const string ResponseCancel = "response.cancel";

        // ---- server -> client: session / errors ----
        public const string SessionCreated = "session.created";
        public const string SessionUpdated = "session.updated";
        public const string Error = "error";
        public const string RateLimitsUpdated = "rate_limits.updated";

        // ---- server -> client: response lifecycle ----
        public const string ResponseCreated = "response.created";
        public const string ResponseDone = "response.done";
        public const string ResponseOutputItemAdded = "response.output_item.added";
        public const string ResponseOutputItemDone = "response.output_item.done";
        public const string ResponseContentPartAdded = "response.content_part.added";
        public const string ResponseContentPartDone = "response.content_part.done";

        // ---- server -> client: audio output (GA + legacy alias) ----
        public const string ResponseOutputAudioDelta = "response.output_audio.delta";
        public const string ResponseAudioDeltaLegacy = "response.audio.delta";
        public const string ResponseOutputAudioDone = "response.output_audio.done";
        public const string ResponseAudioDoneLegacy = "response.audio.done";
        public const string ResponseOutputAudioTranscriptDelta = "response.output_audio_transcript.delta";
        public const string ResponseAudioTranscriptDeltaLegacy = "response.audio_transcript.delta";
        public const string ResponseOutputAudioTranscriptDone = "response.output_audio_transcript.done";
        public const string ResponseAudioTranscriptDoneLegacy = "response.audio_transcript.done";
        public const string ResponseFunctionCallArgumentsDelta = "response.function_call_arguments.delta";
        public const string ResponseFunctionCallArgumentsDone = "response.function_call_arguments.done";

        // ---- server -> client: input audio / conversation ----
        public const string InputAudioBufferSpeechStarted = "input_audio_buffer.speech_started";
        public const string InputAudioBufferSpeechStopped = "input_audio_buffer.speech_stopped";
        public const string InputAudioBufferCommitted = "input_audio_buffer.committed";
        public const string InputAudioBufferCleared = "input_audio_buffer.cleared";
        public const string ConversationItemCreated = "conversation.item.created";
        public const string ConversationItemAdded = "conversation.item.added";
        public const string ConversationItemDone = "conversation.item.done";
        public const string InputAudioTranscriptionDelta = "conversation.item.input_audio_transcription.delta";
        public const string InputAudioTranscriptionCompleted = "conversation.item.input_audio_transcription.completed";
        public const string InputAudioTranscriptionFailed = "conversation.item.input_audio_transcription.failed";
    }
}
