using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SplatPresso.Api;

namespace SplatPresso.Tests
{
    /// <summary>
    /// Pure helpers of the API layer and the DECIDE/VERIFY contracts: result URL extraction, Retry-After parsing,
    /// and the strict schemas, whose property names must match the [JsonProperty] names of the result classes
    /// exactly (a mismatch silently deserializes to empty fields).
    /// </summary>
    public class ApiHelpersTests
    {
        [Test]
        public void ExtractUrl_HandlesEveryResultShape()
        {
            Assert.AreEqual("u1", MediaEndpoints.ExtractUrl(JToken.Parse("\"u1\"")));
            Assert.AreEqual("u2", MediaEndpoints.ExtractUrl(JToken.Parse("{\"url\":\"u2\",\"width\":4}")));
            Assert.AreEqual("u3", MediaEndpoints.ExtractUrl(JToken.Parse("{\"file\":{\"url\":\"u3\"}}")));
            Assert.AreEqual("u4", MediaEndpoints.ExtractUrl(JToken.Parse("{\"image\":{\"url\":\"u4\"}}")));
            Assert.AreEqual("u5", MediaEndpoints.ExtractUrl(JToken.Parse("[{\"url\":\"u5\"},{\"url\":\"other\"}]")));
            Assert.AreEqual("u6", MediaEndpoints.ExtractUrl(JToken.Parse("[\"u6\"]")));
            Assert.IsNull(MediaEndpoints.ExtractUrl(JToken.Parse("[]")));
            Assert.IsNull(MediaEndpoints.ExtractUrl(JToken.Parse("{\"content_type\":\"x\"}")));
            Assert.IsNull(MediaEndpoints.ExtractUrl(JToken.Parse("42")));
            Assert.IsNull(MediaEndpoints.ExtractUrl(null));

            // TripoSplat output shape (model_mesh is a File object)
            var result = JObject.Parse("{\"model_mesh\":{\"url\":\"https://cdn/x.ply\",\"content_type\":\"application/octet-stream\",\"file_name\":\"x.ply\",\"file_size\":10}}");
            Assert.AreEqual("https://cdn/x.ply", MediaEndpoints.ExtractUrl(result["model_mesh"]));
        }

        [Test]
        public void ModelPathCache_AReorderDropsTheEntry_AndAFallbackPathIsNotKeptAlive()
        {
            string dir = EditorTestUtil.NewTempDir("modelpaths");
            string file = Path.Combine(dir, "model_paths.json");
            string savedOverride = ModelPathCache.FilePathOverride;
            bool savedPersist = ModelPathCache.PersistToDisk;
            try
            {
                ModelPathCache.PersistToDisk = true;
                ModelPathCache.FilePathOverride = file;
                const string baseUrl = "https://example.invalid/api/v1";
                var order = new List<string> { "gp/preferred", "gp/fallback" };

                // editing the candidate order in the settings must win over the cached path
                ModelPathCache.Set(baseUrl, "route", "gp/fallback", order);
                Assert.AreEqual("gp/fallback", ModelPathCache.Get(baseUrl, "route", order));
                Assert.IsNull(ModelPathCache.Get(baseUrl, "route", new List<string> { "gp/fallback", "gp/preferred" }));
                Assert.IsNull(ModelPathCache.Get(baseUrl, "route", order), "the stale entry is removed");

                // back-date two entries by 3 days, then use both again
                ModelPathCache.Set(baseUrl, "a", "gp/fallback", order);
                ModelPathCache.Set(baseUrl, "b", "gp/preferred", order);
                string old = DateTime.UtcNow.AddDays(-3).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                var entries = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file));
                foreach (var e in entries.Values)
                    e["savedUtc"] = old;
                File.WriteAllText(file, JsonConvert.SerializeObject(entries));
                ModelPathCache.FilePathOverride = file; // reload

                ModelPathCache.Set(baseUrl, "a", "gp/fallback", order);
                ModelPathCache.Set(baseUrl, "b", "gp/preferred", order);
                entries = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file));
                string SavedOf(string route) => entries.First(kv => kv.Key.EndsWith("|" + route, StringComparison.Ordinal)).Value["savedUtc"];
                Assert.AreEqual(old, SavedOf("a"), "using a fallback candidate does not refresh its age (it expires, then the preferred one is retried)");
                Assert.AreNotEqual(old, SavedOf("b"), "the preferred candidate is refreshed on use");
                Assert.AreEqual("gp/fallback", ModelPathCache.Get(baseUrl, "a", order));
            }
            finally
            {
                ModelPathCache.FilePathOverride = savedOverride;
                ModelPathCache.PersistToDisk = savedPersist;
                EditorTestUtil.DeleteDir(dir);
            }
        }

        [Test]
        public void DataUri_IsBase64WithMime()
        {
            Assert.AreEqual("data:image/png;base64,AQID", MediaEndpoints.DataUri("image/png", new byte[] { 1, 2, 3 }));
            Assert.AreEqual("data:image/jpeg;base64,", MediaEndpoints.DataUri("image/jpeg", null));
        }

        [Test]
        public void ParseRetryAfter_AcceptsSecondsAndHttpDates()
        {
            Assert.AreEqual(1f, HttpJson.ParseRetryAfter("1"));
            Assert.AreEqual(2.5f, HttpJson.ParseRetryAfter(" 2.5 "));
            Assert.AreEqual(0f, HttpJson.ParseRetryAfter(null));
            Assert.AreEqual(0f, HttpJson.ParseRetryAfter("soon"));
            Assert.AreEqual(300f, HttpJson.ParseRetryAfter("100000"), "clamped");
            string inTenSeconds = DateTimeOffset.UtcNow.AddSeconds(10).ToString("r");
            Assert.That(HttpJson.ParseRetryAfter(inTenSeconds), Is.InRange(5f, 11f));
            Assert.AreEqual(0f, HttpJson.ParseRetryAfter(DateTimeOffset.UtcNow.AddSeconds(-30).ToString("r")), "past dates mean now");
        }

        [Test]
        public void SanitizeUrl_DropsQueriesAndDataUris()
        {
            Assert.AreEqual("https://cdn/x.png?...", HttpJson.SanitizeUrl("https://cdn/x.png?token=secret"));
            Assert.AreEqual("https://cdn/x.png", HttpJson.SanitizeUrl("https://cdn/x.png"));
            Assert.AreEqual("data:...", HttpJson.SanitizeUrl("data:image/png;base64,AAAA"));
        }

        // ------------------------------------------------------------------------------------------
        // Strict schemas vs result classes

        static HashSet<string> JsonNames(Type t)
        {
            var names = new HashSet<string>();
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = f.GetCustomAttribute<JsonPropertyAttribute>();
                if (attr != null && f.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                    names.Add(attr.PropertyName);
            }
            return names;
        }

        static void AssertStrictObject(JObject schema, Type type, string what)
        {
            Assert.AreEqual("object", (string)schema["type"], what);
            Assert.AreEqual(false, (bool)schema["additionalProperties"], what + ": additionalProperties must be false (strict mode)");
            var props = ((JObject)schema["properties"]).Properties().Select(p => p.Name).ToList();
            var required = ((JArray)schema["required"]).Select(t => (string)t).ToList();
            CollectionAssert.AreEquivalent(props, required, what + ": strict mode requires every property");
            CollectionAssert.AreEquivalent(JsonNames(type), props, what + ": schema names must match the [JsonProperty] names");
        }

        [Test]
        public void DecideSchema_MatchesDecisionResult()
        {
            var schema = PlacementDecisionService.BuildDecideSchema();
            AssertStrictObject(schema, typeof(DecisionResult), "DecisionResult");
            var item = (JObject)schema["properties"]["objects"]["items"];
            AssertStrictObject(item, typeof(DecidedObject), "DecidedObject");
            Assert.AreEqual(4, (int)item["properties"]["target_bbox_norm"]["minItems"]);
        }

        [Test]
        public void VerifySchema_MatchesVerificationResult()
        {
            var schema = PlacementDecisionService.BuildVerifySchema();
            AssertStrictObject(schema, typeof(VerificationResult), "VerificationResult");
            AssertStrictObject((JObject)schema["properties"]["objects"]["items"], typeof(VerifiedObject), "VerifiedObject");
        }

        [Test]
        public void SystemPrompts_KeepTheMarkersTheMockServerMatchesOn()
        {
            StringAssert.Contains("scene-augmentation planner", PlacementDecisionService.DecideSystemPrompt);
            StringAssert.Contains("You compare two images", PlacementDecisionService.VerifySystemPrompt);
        }

        // ------------------------------------------------------------------------------------------
        // Fixture consistency (the mock server serves these as model output)

        [Test]
        public void FixtureDecisionAndVerification_ParseAndAgree()
        {
            var decision = JsonUtil.Deserialize<DecisionResult>(File.ReadAllText(EditorTestUtil.RequireFixture("decision.json")));
            var verification = JsonUtil.Deserialize<VerificationResult>(File.ReadAllText(EditorTestUtil.RequireFixture("verification.json")));

            Assert.IsTrue(decision.feasible);
            Assert.AreEqual(1, decision.objects.Count);
            var d = decision.objects[0];
            Assert.AreEqual(1, d.id);
            Assert.AreEqual("red chair", d.name);
            Assert.IsNotEmpty(d.descriptionForSegmentation);
            Assert.IsNotEmpty(decision.editPrompt);
            Assert.IsTrue(d.TargetBbox.IsValid);

            Assert.IsTrue(verification.cameraUnchanged);
            Assert.AreEqual(1, verification.objects.Count);
            var v = verification.objects[0];
            Assert.IsTrue(v.found);
            Assert.AreEqual(d.id, v.id);
            Assert.IsTrue(v.Bbox.IsValid);
            // the chair stands in the lower-middle of the frame (its bottom row is floor in the test scenes)
            Assert.That(v.Bbox.centerX, Is.InRange(0.4f, 0.6f));
            Assert.That(v.Bbox.y + v.Bbox.h, Is.InRange(0.8f, 0.9f));

            // every property of the strict schemas is present in the fixture JSON
            var dj = JObject.Parse(File.ReadAllText(EditorTestUtil.RequireFixture("decision.json")));
            foreach (var p in ((JObject)PlacementDecisionService.BuildDecideSchema()["properties"]).Properties())
                Assert.IsNotNull(dj.Property(p.Name), "decision.json lacks " + p.Name);
        }
    }
}
