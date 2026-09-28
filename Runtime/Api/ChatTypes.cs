using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SplatPresso.Api
{
    /// <summary>Kind of a <see cref="ChatContentPart"/>.</summary>
    public enum ChatPartKind
    {
        Text,
        Image,
        Audio,
    }

    /// <summary>One item of a multimodal chat message (text, image or audio).</summary>
    public sealed class ChatContentPart
    {
        /// <summary>What this part carries.</summary>
        public ChatPartKind Kind { get; }
        /// <summary>Text of a text part (null otherwise).</summary>
        public string TextValue { get; }
        /// <summary>URL or data URI of an image part (null otherwise).</summary>
        public string ImageUrlValue { get; }
        /// <summary>Base64 payload of an audio part (null otherwise).</summary>
        public string AudioBase64 { get; }
        /// <summary>Audio format of an audio part ("wav").</summary>
        public string AudioFormat { get; }

        ChatContentPart(ChatPartKind kind, string text, string imageUrl, string audioBase64, string audioFormat)
        {
            Kind = kind;
            TextValue = text;
            ImageUrlValue = imageUrl;
            AudioBase64 = audioBase64;
            AudioFormat = audioFormat;
        }

        /// <summary>A text part.</summary>
        public static ChatContentPart Text(string text) => new ChatContentPart(ChatPartKind.Text, text ?? "", null, null, null);

        /// <summary>An inline JPEG image (data URI).</summary>
        public static ChatContentPart ImageJpeg(byte[] jpegBytes) =>
            new ChatContentPart(ChatPartKind.Image, null, "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes ?? Array.Empty<byte>()), null, null);

        /// <summary>An inline PNG image (data URI).</summary>
        public static ChatContentPart ImagePng(byte[] pngBytes) =>
            new ChatContentPart(ChatPartKind.Image, null, "data:image/png;base64," + Convert.ToBase64String(pngBytes ?? Array.Empty<byte>()), null, null);

        /// <summary>An image by URL (or an existing data URI).</summary>
        public static ChatContentPart ImageUrl(string url) => new ChatContentPart(ChatPartKind.Image, null, url ?? "", null, null);

        /// <summary>A WAV audio clip (sent as <c>input_audio</c>).</summary>
        public static ChatContentPart AudioWav(byte[] wavBytes) =>
            new ChatContentPart(ChatPartKind.Audio, null, null, Convert.ToBase64String(wavBytes ?? Array.Empty<byte>()), "wav");

        /// <summary>Approximate serialized size in bytes (for the 4 MB request budget).</summary>
        public int ApproxJsonBytes
        {
            get
            {
                switch (Kind)
                {
                    case ChatPartKind.Image: return (ImageUrlValue?.Length ?? 0) + 48;
                    case ChatPartKind.Audio: return (AudioBase64?.Length ?? 0) + 64;
                    default: return (TextValue?.Length ?? 0) * 2 + 32; // generous: escaping / multi-byte UTF-8
                }
            }
        }

        /// <summary>OpenAI-compatible JSON of the part.</summary>
        public JToken ToJson()
        {
            switch (Kind)
            {
                case ChatPartKind.Image:
                    return new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = ImageUrlValue } };
                case ChatPartKind.Audio:
                    return new JObject { ["type"] = "input_audio", ["input_audio"] = new JObject { ["data"] = AudioBase64, ["format"] = AudioFormat } };
                default:
                    return new JObject { ["type"] = "text", ["text"] = TextValue };
            }
        }
    }

    /// <summary>A chat message: plain <see cref="text"/> and/or multimodal <see cref="parts"/>.</summary>
    public sealed class ChatMessage
    {
        /// <summary>"system", "user" or "assistant".</summary>
        public string role;
        /// <summary>Text content (sent as a plain string when there are no parts).</summary>
        public string text;
        /// <summary>Optional multimodal parts (text is prepended as a text part when both are set).</summary>
        public List<ChatContentPart> parts;

        public static ChatMessage System(string text) => new ChatMessage { role = "system", text = text ?? "" };
        public static ChatMessage User(string text) => new ChatMessage { role = "user", text = text ?? "" };
        public static ChatMessage Assistant(string text) => new ChatMessage { role = "assistant", text = text ?? "" };
        public static ChatMessage User(IEnumerable<ChatContentPart> parts) =>
            new ChatMessage { role = "user", parts = parts != null ? new List<ChatContentPart>(parts) : new List<ChatContentPart>() };

        /// <summary>True when the message carries image or audio parts.</summary>
        public bool HasMedia
        {
            get
            {
                if (parts == null)
                    return false;
                foreach (var p in parts)
                    if (p != null && p.Kind != ChatPartKind.Text)
                        return true;
                return false;
            }
        }

        /// <summary>Approximate serialized size in bytes.</summary>
        public int ApproxJsonBytes
        {
            get
            {
                int n = 32 + (text?.Length ?? 0) * 2;
                if (parts != null)
                    foreach (var p in parts)
                        if (p != null) n += p.ApproxJsonBytes;
                return n;
            }
        }

        /// <summary>OpenAI-compatible JSON of the message.</summary>
        public JObject ToJson()
        {
            var msg = new JObject { ["role"] = role ?? "user" };
            if (parts != null && parts.Count > 0)
            {
                var content = new JArray();
                if (!string.IsNullOrEmpty(text))
                    content.Add(ChatContentPart.Text(text).ToJson());
                foreach (var p in parts)
                    if (p != null)
                        content.Add(p.ToJson());
                msg["content"] = content;
            }
            else
            {
                msg["content"] = text ?? "";
            }
            return msg;
        }
    }
}
