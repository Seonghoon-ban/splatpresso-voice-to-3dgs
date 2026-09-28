using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SplatPresso
{
    /// <summary>The API keys the package can use.</summary>
    public enum ApiKeyKind
    {
        /// <summary>GenPresso (gp_...): chat, media and voice. The only key required.</summary>
        Genpresso,
        /// <summary>OpenAI: only for the optional OpenAI Realtime voice backend.</summary>
        OpenAI,
        /// <summary>fal.ai: only for MediaProvider.FalDirect or the fal fallback.</summary>
        Fal,
    }

    /// <summary>Where a resolved key came from.</summary>
    public enum KeySource
    {
        None,
        Override,
        Environment,
        UserProfileFile,
        SettingsAsset,
        StreamingAssets,
    }

    /// <summary>
    /// API key resolution. For each key the first non-empty value wins:
    /// 1. runtime override (<see cref="SetOverride"/>),
    /// 2. environment variable (GENPRESSO_API_KEY / OPENAI_API_KEY / FAL_KEY),
    /// 3. <c>%USERPROFILE%/.splatpresso/keys.json</c> (recommended: outside the project tree, so zipping or
    ///    sharing the project never leaks keys; format <c>{"genpresso":"gp_...","openai":"sk-...","fal":"..."}</c>),
    /// 4. GenPresso only: <see cref="SplatPressoSettings.apiKey"/> (plain text in an asset: committed and shipped),
    /// 5. <c>StreamingAssets/splatpresso.keys.json</c> (for handing a build to someone; keys ship in plain text).
    /// Key values are never logged; use <see cref="Mask"/> for display.
    /// </summary>
    public static class ApiKeys
    {
        const string kKeysFileName = "keys.json";
        const string kStreamingKeysFileName = "splatpresso.keys.json";
        const double kFileRecheckSeconds = 2.0;

        static readonly string[] s_Overrides = new string[3];
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        static readonly string[] s_UserEnv = new string[3];
        static DateTime s_UserEnvCheckedUtc;
#endif
        static FileKeys s_UserFile;
        static FileKeys s_StreamingFile;
        static string s_UserProfileKeysPathOverride;

        sealed class FileKeys
        {
            public string path;
            public bool exists;
            public Dictionary<string, string> values; // normalized name -> value; null when missing/unreadable
            public DateTime lastWriteUtc;
            public DateTime lastCheckUtc;
        }

        /// <summary>
        /// Replaces the user-profile keys file path (tests / tools). Null restores the default. Setting it
        /// drops the cached file contents.
        /// </summary>
        public static string UserProfileKeysPathOverride
        {
            get => s_UserProfileKeysPathOverride;
            set
            {
                s_UserProfileKeysPathOverride = value;
                Reset();
            }
        }

        /// <summary>Path of the user-profile keys file (default <c>%USERPROFILE%/.splatpresso/keys.json</c>).</summary>
        public static string UserProfileKeysPath =>
            !string.IsNullOrEmpty(s_UserProfileKeysPathOverride)
                ? s_UserProfileKeysPathOverride
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".splatpresso", kKeysFileName);

        /// <summary>Path of the optional StreamingAssets keys file.</summary>
        public static string StreamingAssetsKeysPath => Path.Combine(Application.streamingAssetsPath, kStreamingKeysFileName);

        /// <summary>Environment variable name for a key.</summary>
        public static string EnvVarName(ApiKeyKind kind)
        {
            switch (kind)
            {
                case ApiKeyKind.OpenAI: return "OPENAI_API_KEY";
                case ApiKeyKind.Fal: return "FAL_KEY";
                default: return "GENPRESSO_API_KEY";
            }
        }

        /// <summary>Canonical JSON name of a key in keys.json.</summary>
        public static string JsonKeyName(ApiKeyKind kind)
        {
            switch (kind)
            {
                case ApiKeyKind.OpenAI: return "openai";
                case ApiKeyKind.Fal: return "fal";
                default: return "genpresso";
            }
        }

        // Accepted spellings in keys.json (compared after Normalize).
        static string[] JsonAliases(ApiKeyKind kind)
        {
            switch (kind)
            {
                case ApiKeyKind.OpenAI: return new[] { "openai", "openai_api_key", "openai_key" };
                case ApiKeyKind.Fal: return new[] { "fal", "fal_key", "fal_api_key" };
                default: return new[] { "genpresso", "genpresso_api_key", "genpresso_key", "gp" };
            }
        }

        /// <summary>
        /// Sets (or with null/empty clears) a runtime override for a key; it takes priority over every other source.
        /// Overrides are cleared when play mode starts, so set them from Awake/Start or later.
        /// </summary>
        public static void SetOverride(ApiKeyKind kind, string value)
        {
            s_Overrides[(int)kind] = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>The effective key, or null when none is configured.</summary>
        public static string Get(ApiKeyKind kind) => Get(kind, out _);

        /// <summary>The effective key and where it came from.</summary>
        public static string Get(ApiKeyKind kind, out KeySource source)
        {
            string v = s_Overrides[(int)kind];
            if (!string.IsNullOrEmpty(v))
            {
                source = KeySource.Override;
                return v;
            }

            v = GetEnvironmentKey(kind);
            if (!string.IsNullOrEmpty(v))
            {
                source = KeySource.Environment;
                return v;
            }

            v = Lookup(GetUserFile(), kind);
            if (!string.IsNullOrEmpty(v))
            {
                source = KeySource.UserProfileFile;
                return v;
            }

            if (kind == ApiKeyKind.Genpresso)
            {
                var settings = SplatPressoSettings.FindActive();
                if (settings != null && !string.IsNullOrWhiteSpace(settings.apiKey))
                {
                    source = KeySource.SettingsAsset;
                    return settings.apiKey.Trim();
                }
            }

            v = Lookup(GetStreamingFile(), kind);
            if (!string.IsNullOrEmpty(v))
            {
                source = KeySource.StreamingAssets;
                return v;
            }

            source = KeySource.None;
            return null;
        }

        /// <summary>True when a non-empty key is configured.</summary>
        public static bool Has(ApiKeyKind kind) => !string.IsNullOrEmpty(Get(kind, out _));

        /// <summary>
        /// Writes a key into the user-profile keys file (merging with the entries already there; any alias of
        /// the same key is replaced). Creates the folder when needed.
        /// </summary>
        public static void SaveToUserProfile(ApiKeyKind kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ClearFromUserProfile(kind);
                return;
            }
            var obj = ReadRawUserFile();
            RemoveAliases(obj, kind);
            obj[JsonKeyName(kind)] = value.Trim();
            WriteRawUserFile(obj);
        }

        /// <summary>Removes a key (and its aliases) from the user-profile keys file.</summary>
        public static void ClearFromUserProfile(ApiKeyKind kind)
        {
            string path = UserProfileKeysPath;
            if (!File.Exists(path))
                return;
            var obj = ReadRawUserFile();
            RemoveAliases(obj, kind);
            WriteRawUserFile(obj);
        }

        /// <summary>Display form of a key: prefix kept, middle hidden, last 4 characters shown (<c>gp_****abcd</c>).</summary>
        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "";
            key = key.Trim();
            string prefix = "";
            int sep = key.IndexOfAny(new[] { '_', '-' });
            if (sep > 0 && sep <= 4)
                prefix = key.Substring(0, sep + 1);
            if (key.Length - prefix.Length <= 8)
                return prefix + "****";
            return prefix + "****" + key.Substring(key.Length - 4);
        }

        /// <summary>Diagnostics: the key NAMES found in the user-profile file (values are never exposed).</summary>
        public static string LoadedKeyNames()
        {
            var f = GetUserFile();
            if (f?.values == null || f.values.Count == 0)
                return "(none)";
            var names = new List<string>(f.values.Keys);
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names);
        }

        /// <summary>Drops the cached key files (and cached user environment) so the next lookup re-reads them.</summary>
        public static void Reset()
        {
            s_UserFile = null;
            s_StreamingFile = null;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            for (int i = 0; i < s_UserEnv.Length; i++)
                s_UserEnv[i] = null;
            s_UserEnvCheckedUtc = default;
#endif
        }

        // Projects often disable domain reload, so statics survive play sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            for (int i = 0; i < s_Overrides.Length; i++)
                s_Overrides[i] = null;
            s_UserProfileKeysPathOverride = null;
            Reset();
        }

        // ------------------------------------------------------------------------------------------
        // Environment

        static string GetEnvironmentKey(ApiKeyKind kind)
        {
            string v = Environment.GetEnvironmentVariable(EnvVarName(kind));
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            // A variable set with setx / System Properties after Unity (or the Hub) started is not in this
            // process's environment yet, only in the user registry. Read it from there (cached for 5 s).
            var now = DateTime.UtcNow;
            if ((now - s_UserEnvCheckedUtc).TotalSeconds > 5.0)
            {
                s_UserEnvCheckedUtc = now;
                for (int i = 0; i < s_UserEnv.Length; i++)
                {
                    try { s_UserEnv[i] = Environment.GetEnvironmentVariable(EnvVarName((ApiKeyKind)i), EnvironmentVariableTarget.User); }
                    catch { s_UserEnv[i] = null; }
                }
            }
            v = s_UserEnv[(int)kind];
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
#endif
            return null;
        }

        // ------------------------------------------------------------------------------------------
        // File handling

        // Compares key names ignoring case and separators: genpresso_api_key == genpressoApiKey == GENPRESSO-API-KEY.
        // Why: when a name was off by one character, the lookup silently returned null and the caller only saw
        // "no key" (a file had supabaseUrl while the code looked for supabase_url, and a whole upload path broke).
        static string Normalize(string k)
        {
            if (string.IsNullOrEmpty(k))
                return "";
            var sb = new StringBuilder(k.Length);
            foreach (char c in k)
            {
                if (c == '_' || c == '-' || c == ' ' || c == '.')
                    continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static string Lookup(FileKeys file, ApiKeyKind kind)
        {
            if (file?.values == null)
                return null;
            foreach (var alias in JsonAliases(kind))
                if (file.values.TryGetValue(Normalize(alias), out string v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            return null;
        }

        static FileKeys GetUserFile()
        {
            string path;
            try { path = UserProfileKeysPath; }
            catch { return null; }
            return Refresh(ref s_UserFile, path);
        }

        static FileKeys GetStreamingFile()
        {
            string path;
            try { path = StreamingAssetsKeysPath; }
            catch { return null; }
            return Refresh(ref s_StreamingFile, path);
        }

        // Loads the file once and re-reads it only when its timestamp changes (checked at most every 2 s),
        // so edits made outside Unity are picked up without hitting the disk every frame.
        static FileKeys Refresh(ref FileKeys cache, string path)
        {
            var now = DateTime.UtcNow;
            if (cache != null && cache.path == path && (now - cache.lastCheckUtc).TotalSeconds < kFileRecheckSeconds)
                return cache;

            DateTime stamp = DateTime.MinValue;
            bool exists = false;
            try
            {
                exists = !string.IsNullOrEmpty(path) && File.Exists(path);
                if (exists)
                    stamp = File.GetLastWriteTimeUtc(path);
            }
            catch { exists = false; }

            if (cache != null && cache.path == path && cache.lastWriteUtc == stamp && cache.exists == exists)
            {
                cache.lastCheckUtc = now;
                return cache;
            }

            cache = new FileKeys { path = path, exists = exists, lastWriteUtc = stamp, lastCheckUtc = now };
            if (!exists)
                return cache;
            try
            {
                var raw = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(path));
                var values = new Dictionary<string, string>();
                if (raw != null)
                    foreach (var kv in raw)
                        if (kv.Value != null)
                            values[Normalize(kv.Key)] = kv.Value.ToString();
                cache.values = values;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SplatPresso] Could not read keys file {path}: {e.Message}");
            }
            return cache;
        }

        static JObject ReadRawUserFile()
        {
            string path = UserProfileKeysPath;
            if (!File.Exists(path))
                return new JObject();
            try
            {
                var token = JToken.Parse(File.ReadAllText(path));
                if (token is JObject obj)
                    return obj;
            }
            catch (Exception e)
            {
                // Refuse to overwrite a file we cannot parse: it may hold other keys the user wants to keep.
                throw new InvalidOperationException($"[SplatPresso] {path} is not valid JSON ({e.Message}); fix or delete it first.", e);
            }
            return new JObject();
        }

        static void WriteRawUserFile(JObject obj)
        {
            string path = UserProfileKeysPath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, obj.ToString(Formatting.Indented));
            Reset();
        }

        static void RemoveAliases(JObject obj, ApiKeyKind kind)
        {
            var targets = new HashSet<string>();
            foreach (var alias in JsonAliases(kind))
                targets.Add(Normalize(alias));
            var remove = new List<string>();
            foreach (var prop in obj.Properties())
                if (targets.Contains(Normalize(prop.Name)))
                    remove.Add(prop.Name);
            foreach (var name in remove)
                obj.Remove(name);
        }
    }
}
