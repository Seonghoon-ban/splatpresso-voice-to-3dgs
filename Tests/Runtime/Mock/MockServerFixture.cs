using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SplatPresso.Api;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Base class of the PlayMode tests that talk to <see cref="MockGenpressoServer"/>: one server per fixture, the
    /// GenPresso key overridden with the mock's key, the model-path cache kept in memory, fresh settings and a
    /// temporary sessions folder per test.
    /// </summary>
    /// <remarks>
    /// Unity blocks plain-http requests by default ("Allow downloads over HTTP" = Not allowed). In the editor the
    /// fixture switches that Player setting to "Always allowed" for its duration and restores it afterwards; if
    /// requests are still refused, the tests are ignored with instructions.
    /// </remarks>
    public abstract class MockServerFixture
    {
        protected MockGenpressoServer Mock;
        protected string FixturesDir;
        protected string SessionsDir;
        protected SplatPressoSettings Settings;

        string m_CacheDir;
        string m_HttpBlocked;
        readonly List<SplatPressoSettings> m_UsedSettings = new List<SplatPressoSettings>();
        bool m_HttpChecked;
#if UNITY_EDITOR
        UnityEditor.InsecureHttpOption m_PrevHttpOption;
        bool m_ChangedHttpOption;
#endif

        [OneTimeSetUp]
        public void StartMockServer()
        {
            FixturesDir = TestEnv.FixturesDir;
            if (FixturesDir == null)
                return; // each test ignores itself in SetUp
            Mock = new MockGenpressoServer(FixturesDir);

            // keep resolved media paths in memory only: never touch the user's persisted cache
            m_CacheDir = TestEnv.NewTempDir("model-paths");
            ModelPathCache.PersistToDisk = false;
            ModelPathCache.FilePathOverride = Path.Combine(m_CacheDir, "model_paths.json");

#if UNITY_EDITOR
            m_PrevHttpOption = UnityEditor.PlayerSettings.insecureHttpOption;
            if (m_PrevHttpOption != UnityEditor.InsecureHttpOption.AlwaysAllowed)
            {
                UnityEditor.PlayerSettings.insecureHttpOption = UnityEditor.InsecureHttpOption.AlwaysAllowed;
                m_ChangedHttpOption = true;
            }
#endif
        }

        [OneTimeTearDown]
        public void StopMockServer()
        {
            Mock?.Dispose();
            Mock = null;
            foreach (var s in m_UsedSettings)
                if (s != null)
                    Object.Destroy(s);
            m_UsedSettings.Clear();
            ModelPathCache.FilePathOverride = null; // also drops the in-memory entries of the mock's base URL
            ModelPathCache.PersistToDisk = true;
            TestEnv.DeleteDir(m_CacheDir);
#if UNITY_EDITOR
            if (m_ChangedHttpOption)
                UnityEditor.PlayerSettings.insecureHttpOption = m_PrevHttpOption;
            m_ChangedHttpOption = false;
#endif
        }

        [UnitySetUp]
        public IEnumerator MockSetUp()
        {
            TestEnv.RequireFixtures();
            if (Mock == null)
                Assert.Ignore("The mock GenPresso server could not start.");

            // overrides are cleared when play mode starts, so set them per test
            ApiKeys.SetOverride(ApiKeyKind.Genpresso, MockGenpressoServer.ApiKey);
            SessionsDir = TestEnv.NewTempDir("sessions");
            Settings = TestEnv.CreateSettings(Mock.ApiBaseUrl, SessionsDir);
            SplatPressoSettings.Active = Settings;

            if (!m_HttpChecked)
            {
                m_HttpChecked = true;
                HttpResponse resp = null;
                var op = AsyncOp.Run("mock server preflight", 15f, async ct =>
                {
                    resp = await HttpJson.SendAsync("GET", Mock.ApiBaseUrl + "/models", null, null,
                        new Dictionary<string, string> { { "Authorization", "Bearer " + MockGenpressoServer.ApiKey } }, 10, ct, throwOnHttpError: false);
                });
                while (op.KeepWaiting)
                    yield return null;
                if (op.Error != null && op.Error.Message.IndexOf("could not start", StringComparison.OrdinalIgnoreCase) >= 0)
                    m_HttpBlocked = "Unity refused the plain-http request to the local mock server (" + op.Error.Message +
                                    "). Set Project Settings > Player > Allow downloads over HTTP to 'Always allowed' for the test project.";
                else
                {
                    op.AssertSucceeded(() => Mock.Describe());
                    Assert.AreEqual(200, resp.StatusCode, "mock server preflight: " + resp.Text);
                }
            }
            if (m_HttpBlocked != null)
                Assert.Ignore(m_HttpBlocked);
        }

        [TearDown]
        public void MockTearDown()
        {
            ApiKeys.SetOverride(ApiKeyKind.Genpresso, null);
            SplatPressoSettings.Active = null;
            // destroyed in OneTimeTearDown: a late continuation of a finished test may still read its settings
            if (Settings != null)
                m_UsedSettings.Add(Settings);
            Settings = null;
            if (Mock != null)
            {
                Mock.HoldJobsInQueue = false;
                Mock.FailNextJob = false;
            }
            TestEnv.DeleteDir(SessionsDir);
            SessionsDir = null;
        }

        /// <summary>Bytes of a fixture file.</summary>
        protected byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixturesDir, name));

        /// <summary>URL under which the mock serves a fixture file.</summary>
        protected string FixtureUrl(string name) => Mock.Origin + "/files/" + name;
    }
}
