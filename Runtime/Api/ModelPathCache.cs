using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SplatPresso.Api
{
    /// <summary>
    /// Remembers which GenPresso media path answered for each capability, keyed by (apiBaseUrl, routeKey), in
    /// memory and in <c>&lt;persistentDataPath&gt;/SplatPresso/model_paths.json</c> with a 7-day TTL. A 404 during
    /// real use invalidates the entry so the path is re-resolved.
    /// </summary>
    public static class ModelPathCache
    {
        /// <summary>Entries older than this are ignored and pruned.</summary>
        public static readonly TimeSpan TimeToLive = TimeSpan.FromDays(7);

        sealed class Entry
        {
            public string path;
            public string savedUtc; // ISO 8601
        }

        static Dictionary<string, Entry> s_Entries;
        static string s_FilePathOverride;

        /// <summary>Replaces the cache file location (tests). Null restores the default; setting it reloads.</summary>
        public static string FilePathOverride
        {
            get => s_FilePathOverride;
            set
            {
                s_FilePathOverride = value;
                s_Entries = null;
            }
        }

        /// <summary>When false, nothing is read from or written to disk (memory only).</summary>
        public static bool PersistToDisk { get; set; } = true;

        /// <summary>The cache file path.</summary>
        public static string FilePath =>
            !string.IsNullOrEmpty(s_FilePathOverride)
                ? s_FilePathOverride
                : Path.Combine(Application.persistentDataPath, "SplatPresso", "model_paths.json");

        /// <summary>The cached path for a route, or null when unknown or expired.</summary>
        public static string Get(string baseUrl, string routeKey)
        {
            var entries = Load();
            string key = Key(baseUrl, routeKey);
            if (!entries.TryGetValue(key, out var e) || e == null || string.IsNullOrEmpty(e.path))
                return null;
            if (IsExpired(e))
            {
                entries.Remove(key);
                Save();
                return null;
            }
            return e.path;
        }

        /// <summary>Records the path that answered for a route.</summary>
        public static void Set(string baseUrl, string routeKey, string path)
        {
            if (string.IsNullOrEmpty(routeKey) || string.IsNullOrEmpty(path))
                return;
            var entries = Load();
            string key = Key(baseUrl, routeKey);
            if (entries.TryGetValue(key, out var existing) && existing != null &&
                string.Equals(existing.path, path, StringComparison.Ordinal) && !IsExpired(existing, TimeSpan.FromDays(1)))
                return; // fresh enough; avoid rewriting the file on every call
            entries[key] = new Entry { path = path, savedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            Save();
        }

        /// <summary>Forgets the cached path of a route.</summary>
        public static void Invalidate(string baseUrl, string routeKey)
        {
            var entries = Load();
            if (entries.Remove(Key(baseUrl, routeKey)))
                Save();
        }

        /// <summary>Forgets every cached path (and deletes the file).</summary>
        public static void Clear()
        {
            s_Entries = new Dictionary<string, Entry>();
            if (!PersistToDisk)
                return;
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Could not delete {FilePath}: {e.Message}");
            }
        }

        // ------------------------------------------------------------------------------------------

        static string Key(string baseUrl, string routeKey) =>
            (string.IsNullOrWhiteSpace(baseUrl) ? SplatPressoSettings.DefaultApiBaseUrl : baseUrl.Trim()).TrimEnd('/').ToLowerInvariant() + "|" + routeKey;

        static bool IsExpired(Entry e) => IsExpired(e, TimeToLive);

        static bool IsExpired(Entry e, TimeSpan ttl)
        {
            if (!DateTime.TryParse(e.savedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var saved))
                return true;
            return DateTime.UtcNow - saved.ToUniversalTime() > ttl;
        }

        static Dictionary<string, Entry> Load()
        {
            if (s_Entries != null)
                return s_Entries;
            s_Entries = new Dictionary<string, Entry>();
            if (!PersistToDisk)
                return s_Entries;
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(path));
                    if (loaded != null)
                        foreach (var kv in loaded)
                            if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.path) && !IsExpired(kv.Value))
                                s_Entries[kv.Key] = kv.Value;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Ignoring unreadable model path cache: {e.Message}");
            }
            return s_Entries;
        }

        static void Save()
        {
            if (!PersistToDisk || s_Entries == null)
                return;
            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(s_Entries, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Could not save the model path cache: {e.Message}");
            }
        }

        // Projects often disable domain reload, so statics survive play sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Entries = null;
            s_FilePathOverride = null;
            PersistToDisk = true;
        }
    }
}
