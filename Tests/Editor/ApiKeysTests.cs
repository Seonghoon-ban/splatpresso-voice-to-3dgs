using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// ApiKeys resolution order (override &gt; environment &gt; user-profile keys.json &gt; settings asset &gt;
    /// StreamingAssets) and the keys.json editing helpers. Environment variables cannot be changed safely from a
    /// test (they are process-wide), so the file/settings steps are only asserted for keys whose variable is unset
    /// on this machine. Every value here is fake.
    /// </summary>
    public class ApiKeysTests
    {
        static readonly ApiKeyKind[] s_Kinds = { ApiKeyKind.Genpresso, ApiKeyKind.OpenAI, ApiKeyKind.Fal };

        string m_TempDir;
        string m_KeysPath;
        SplatPressoSettings m_Settings;

        [SetUp]
        public void SetUp()
        {
            m_TempDir = EditorTestUtil.NewTempDir("keys");
            m_KeysPath = Path.Combine(m_TempDir, "keys.json");
            foreach (var k in s_Kinds)
                ApiKeys.SetOverride(k, null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var k in s_Kinds)
                ApiKeys.SetOverride(k, null);
            ApiKeys.UserProfileKeysPathOverride = null;
            SplatPressoSettings.Active = null;
            if (m_Settings != null)
                UnityEngine.Object.DestroyImmediate(m_Settings);
            m_Settings = null;
            EditorTestUtil.DeleteDir(m_TempDir);
        }

        void WriteKeysFile(string json)
        {
            File.WriteAllText(m_KeysPath, json);
            ApiKeys.UserProfileKeysPathOverride = m_KeysPath; // setting it also drops the cached file
        }

        static bool EnvironmentHas(ApiKeyKind kind)
        {
            string name = ApiKeys.EnvVarName(kind);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                return true;
            try
            {
                // ApiKeys also reads user-level variables that were set after Unity started (Windows registry)
                return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
            }
            catch (Exception)
            {
                return false;
            }
        }

        static List<ApiKeyKind> KindsWithoutEnvironment()
        {
            var list = new List<ApiKeyKind>();
            foreach (var k in s_Kinds)
                if (!EnvironmentHas(k))
                    list.Add(k);
            if (list.Count == 0)
                Assert.Inconclusive("Every key has an environment variable on this machine; file resolution cannot be observed.");
            return list;
        }

        static string FakeValue(ApiKeyKind kind, string tag) => kind == ApiKeyKind.Genpresso ? "gp_" + tag + "_0000abcd" : tag + "-0000abcd";

        [Test]
        public void Override_WinsOverEverySource()
        {
            WriteKeysFile("{\"genpresso\":\"gp_file_1111\",\"openai\":\"sk-file-1111\",\"fal\":\"fal-file-1111\"}");
            foreach (var k in s_Kinds)
            {
                string v = FakeValue(k, "override");
                ApiKeys.SetOverride(k, "  " + v + "  ");
                Assert.AreEqual(v, ApiKeys.Get(k, out KeySource src), k + " (trimmed)");
                Assert.AreEqual(KeySource.Override, src, k.ToString());
                Assert.IsTrue(ApiKeys.Has(k));
            }
        }

        [Test]
        public void Override_NullOrWhitespace_Clears()
        {
            ApiKeys.SetOverride(ApiKeyKind.Fal, "fal-override-2222");
            ApiKeys.SetOverride(ApiKeyKind.Fal, "   ");
            ApiKeys.Get(ApiKeyKind.Fal, out KeySource src);
            Assert.AreNotEqual(KeySource.Override, src);
        }

        [Test]
        public void UserProfileFile_IsUsed_WhenThereIsNoOverrideOrEnvironment()
        {
            WriteKeysFile("{\"genpresso\":\"gp_file_1111\",\"openai\":\"sk-file-1111\",\"fal\":\"fal-file-1111\"}");
            var expected = new Dictionary<ApiKeyKind, string>
            {
                { ApiKeyKind.Genpresso, "gp_file_1111" }, { ApiKeyKind.OpenAI, "sk-file-1111" }, { ApiKeyKind.Fal, "fal-file-1111" },
            };
            foreach (var k in KindsWithoutEnvironment())
            {
                Assert.AreEqual(expected[k], ApiKeys.Get(k, out KeySource src), k.ToString());
                Assert.AreEqual(KeySource.UserProfileFile, src, k.ToString());
            }
        }

        [Test]
        public void UserProfileFile_KeyNamesIgnoreCaseAndSeparators()
        {
            // a key name that is off by one separator used to resolve silently to "no key"
            WriteKeysFile("{\"GENPRESSO-API-KEY\":\"gp_alias_3333\",\"OpenAI_Key\":\"sk-alias-3333\",\"Fal.Key\":\"fal-alias-3333\"}");
            var expected = new Dictionary<ApiKeyKind, string>
            {
                { ApiKeyKind.Genpresso, "gp_alias_3333" }, { ApiKeyKind.OpenAI, "sk-alias-3333" }, { ApiKeyKind.Fal, "fal-alias-3333" },
            };
            foreach (var k in KindsWithoutEnvironment())
                Assert.AreEqual(expected[k], ApiKeys.Get(k), k.ToString());
        }

        [Test]
        public void SettingsAsset_IsTheGenpressoFallbackBelowTheFile()
        {
            if (EnvironmentHas(ApiKeyKind.Genpresso))
                Assert.Inconclusive("GENPRESSO_API_KEY is set on this machine; the settings-asset step cannot be observed.");

            WriteKeysFile("{\"fal\":\"fal-file-4444\"}");
            m_Settings = ScriptableObject.CreateInstance<SplatPressoSettings>();
            m_Settings.apiKey = " gp_settings_4444 ";
            SplatPressoSettings.Active = m_Settings;

            Assert.AreEqual("gp_settings_4444", ApiKeys.Get(ApiKeyKind.Genpresso, out KeySource src));
            Assert.AreEqual(KeySource.SettingsAsset, src);

            // the file wins over the settings asset once it has the key
            ApiKeys.SaveToUserProfile(ApiKeyKind.Genpresso, "gp_file_4444");
            Assert.AreEqual("gp_file_4444", ApiKeys.Get(ApiKeyKind.Genpresso, out src));
            Assert.AreEqual(KeySource.UserProfileFile, src);

            // the settings asset only ever supplies the GenPresso key
            if (!EnvironmentHas(ApiKeyKind.OpenAI))
            {
                ApiKeys.Get(ApiKeyKind.OpenAI, out src);
                Assert.AreNotEqual(KeySource.SettingsAsset, src);
            }
        }

        [Test]
        public void SaveToUserProfile_MergesAndReplacesAliases()
        {
            WriteKeysFile("{\"custom_entry\":\"keep-me\",\"FAL_KEY\":\"fal-old-5555\"}");

            ApiKeys.SaveToUserProfile(ApiKeyKind.Genpresso, "gp_new_5555");
            ApiKeys.SaveToUserProfile(ApiKeyKind.Fal, "fal-new-5555");

            var obj = JObject.Parse(File.ReadAllText(m_KeysPath));
            Assert.AreEqual("keep-me", (string)obj["custom_entry"], "unrelated entries are preserved");
            Assert.AreEqual("gp_new_5555", (string)obj["genpresso"]);
            Assert.AreEqual("fal-new-5555", (string)obj["fal"]);
            Assert.IsNull(obj["FAL_KEY"], "the alias spelling is replaced, not duplicated");

            if (!EnvironmentHas(ApiKeyKind.Fal))
                Assert.AreEqual("fal-new-5555", ApiKeys.Get(ApiKeyKind.Fal));
        }

        [Test]
        public void SaveToUserProfile_CreatesTheFolder()
        {
            string nested = Path.Combine(m_TempDir, "sub", "dir", "keys.json");
            ApiKeys.UserProfileKeysPathOverride = nested;
            ApiKeys.SaveToUserProfile(ApiKeyKind.OpenAI, "sk-new-6666");
            Assert.IsTrue(File.Exists(nested));
            Assert.AreEqual("sk-new-6666", (string)JObject.Parse(File.ReadAllText(nested))["openai"]);
        }

        [Test]
        public void ClearFromUserProfile_RemovesEveryAlias()
        {
            WriteKeysFile("{\"genpresso\":\"gp_a_7777\",\"GenpressoApiKey\":\"gp_b_7777\",\"fal\":\"fal-7777\"}");
            ApiKeys.ClearFromUserProfile(ApiKeyKind.Genpresso);

            var obj = JObject.Parse(File.ReadAllText(m_KeysPath));
            Assert.IsNull(obj["genpresso"]);
            Assert.IsNull(obj["GenpressoApiKey"]);
            Assert.AreEqual("fal-7777", (string)obj["fal"]);

            if (!EnvironmentHas(ApiKeyKind.Genpresso))
            {
                ApiKeys.Get(ApiKeyKind.Genpresso, out KeySource src);
                Assert.AreNotEqual(KeySource.UserProfileFile, src);
            }
        }

        [Test]
        public void SaveToUserProfile_RefusesToOverwriteAnUnreadableFile()
        {
            const string broken = "{ \"genpresso\": \"gp_x\", oops";
            File.WriteAllText(m_KeysPath, broken);
            ApiKeys.UserProfileKeysPathOverride = m_KeysPath;

            // writing into a file that cannot be parsed must not destroy what is there
            Assert.Throws<InvalidOperationException>(() => ApiKeys.SaveToUserProfile(ApiKeyKind.Fal, "fal-8888"));
            Assert.AreEqual(broken, File.ReadAllText(m_KeysPath));
        }

        [Test]
        public void UserProfileFile_WithABareStringRoot_IsIgnoredWithoutLoggingTheKey()
        {
            // a key written as a bare JSON string: Json.NET's error text echoes the value, so it must never be logged
            const string secret = "gp_BARESTRING_9999abcd";
            var kinds = KindsWithoutEnvironment();
            WriteKeysFile("\"" + secret + "\"");
            var logged = new List<string>();
            Application.LogCallback capture = (condition, stackTrace, type) => logged.Add(condition);
            Application.logMessageReceived += capture;
            try
            {
                ApiKeys.Get(kinds[0], out KeySource src);
                Assert.AreNotEqual(KeySource.UserProfileFile, src);
            }
            finally
            {
                Application.logMessageReceived -= capture;
            }
            Assert.IsTrue(logged.Exists(m => m.Contains("root must be a JSON object")), "expected a warning; got: " + string.Join(" | ", logged));
            foreach (var m in logged)
                StringAssert.DoesNotContain("BARESTRING", m, "key values are never logged");
            Assert.AreEqual("(none)", ApiKeys.LoadedKeyNames());

            // and Save refuses to overwrite it rather than silently replacing it
            var e = Assert.Throws<InvalidOperationException>(() => ApiKeys.SaveToUserProfile(ApiKeyKind.Fal, "fal-9999"));
            StringAssert.DoesNotContain("BARESTRING", e.Message);
            Assert.AreEqual("\"" + secret + "\"", File.ReadAllText(m_KeysPath));
        }

        [Test]
        public void Mask_HidesTheMiddleOfTheKey()
        {
            Assert.AreEqual("gp_****abcd", ApiKeys.Mask("gp_1234567890abcd"));
            Assert.AreEqual("sk-****wxyz", ApiKeys.Mask("sk-proj0123456789wxyz"));
            Assert.AreEqual("gp_****", ApiKeys.Mask("gp_short"));
            Assert.AreEqual("", ApiKeys.Mask(null));
            Assert.AreEqual("", ApiKeys.Mask(""));
            StringAssert.DoesNotContain("1234567890", ApiKeys.Mask("gp_1234567890abcd"));
        }

        [Test]
        public void EnvVarAndJsonNames_AreStable()
        {
            Assert.AreEqual("GENPRESSO_API_KEY", ApiKeys.EnvVarName(ApiKeyKind.Genpresso));
            Assert.AreEqual("OPENAI_API_KEY", ApiKeys.EnvVarName(ApiKeyKind.OpenAI));
            Assert.AreEqual("FAL_KEY", ApiKeys.EnvVarName(ApiKeyKind.Fal));
            Assert.AreEqual("genpresso", ApiKeys.JsonKeyName(ApiKeyKind.Genpresso));
            StringAssert.EndsWith(Path.Combine(".splatpresso", "keys.json"), DefaultUserProfilePath());
        }

        static string DefaultUserProfilePath()
        {
            string saved = ApiKeys.UserProfileKeysPathOverride;
            try
            {
                ApiKeys.UserProfileKeysPathOverride = null;
                return ApiKeys.UserProfileKeysPath;
            }
            finally
            {
                ApiKeys.UserProfileKeysPathOverride = saved;
            }
        }
    }
}
