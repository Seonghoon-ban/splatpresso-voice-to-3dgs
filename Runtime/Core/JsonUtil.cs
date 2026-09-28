using System;
using System.Globalization;
using Newtonsoft.Json;
using UnityEngine;

namespace SplatPresso
{
    /// <summary>
    /// Newtonsoft serialization helpers used for every session artifact and API payload in the package.
    /// </summary>
    /// <remarks>
    /// Unity's Vector3/Quaternion cannot be serialized by Newtonsoft directly (the self-referencing
    /// <c>normalized</c> property recurses), so dedicated converters are registered here. Always go through
    /// JsonUtil instead of calling JsonConvert with default settings.
    /// </remarks>
    public static class JsonUtil
    {
        /// <summary>Shared settings: Vector3/Quaternion converters, nulls written, unknown members ignored.</summary>
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Converters = { new Vector3Converter(), new QuaternionConverter() },
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Culture = CultureInfo.InvariantCulture,
        };

        /// <summary>Serializes <paramref name="obj"/> (indented by default).</summary>
        public static string Serialize(object obj, bool indented = true) =>
            JsonConvert.SerializeObject(obj, indented ? Formatting.Indented : Formatting.None, Settings);

        /// <summary>Deserializes <paramref name="json"/>; returns default for null/empty input.</summary>
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return default;
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        sealed class Vector3Converter : JsonConverter<Vector3>
        {
            public override void WriteJson(JsonWriter writer, Vector3 v, JsonSerializer serializer)
            {
                writer.WriteStartArray();
                writer.WriteValue(v.x); writer.WriteValue(v.y); writer.WriteValue(v.z);
                writer.WriteEndArray();
            }

            public override Vector3 ReadJson(JsonReader reader, Type objectType, Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                    return default;
                var a = ReadFloats(reader, 3);
                return new Vector3(a[0], a[1], a[2]);
            }
        }

        sealed class QuaternionConverter : JsonConverter<Quaternion>
        {
            public override void WriteJson(JsonWriter writer, Quaternion q, JsonSerializer serializer)
            {
                writer.WriteStartArray();
                writer.WriteValue(q.x); writer.WriteValue(q.y); writer.WriteValue(q.z); writer.WriteValue(q.w);
                writer.WriteEndArray();
            }

            public override Quaternion ReadJson(JsonReader reader, Type objectType, Quaternion existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                    return Quaternion.identity;
                var a = ReadFloats(reader, 4);
                return new Quaternion(a[0], a[1], a[2], a[3]);
            }
        }

        static float[] ReadFloats(JsonReader reader, int count)
        {
            var result = new float[count];
            if (reader.TokenType != JsonToken.StartArray)
                throw new JsonSerializationException($"Expected array of {count} floats, got {reader.TokenType}");
            for (int i = 0; i < count; ++i)
            {
                reader.Read();
                result[i] = Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture);
            }
            reader.Read(); // EndArray
            return result;
        }
    }
}
