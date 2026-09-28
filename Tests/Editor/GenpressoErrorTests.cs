using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SplatPresso.Api;

namespace SplatPresso.Tests
{
    /// <summary>
    /// GenPresso error bodies come in two shapes: OpenAI-style <c>{"error":{message,code,type}}</c> and FastAPI-style
    /// <c>{"detail":[{loc,msg}]}</c> (or a string detail). Non-JSON bodies (the web app's HTML 404 page, the hosting
    /// layer's plain-text 413) must still produce a readable message.
    /// </summary>
    public class GenpressoErrorTests
    {
        [Test]
        public void Parse_OpenAIShape_WithCode()
        {
            const string body = "{\"error\":{\"message\":\"invalid api key\",\"code\":\"unauthorized\",\"type\":\"authentication_error\"}}";
            Assert.AreEqual("invalid api key (unauthorized)", GenpressoError.Parse(body));
        }

        [Test]
        public void Parse_OpenAIShape_WithoutCode_AndStringError()
        {
            Assert.AreEqual("model not found", GenpressoError.Parse("{\"error\":{\"message\":\"model not found\"}}"));
            Assert.AreEqual("something broke", GenpressoError.Parse("{\"error\":\"something broke\"}"));
        }

        [Test]
        public void Parse_FastApiDetailArray_ListsFieldAndMessage()
        {
            const string body = "{\"detail\":[{\"loc\":[\"body\",\"image_url\"],\"msg\":\"field required\",\"type\":\"missing\"}," +
                                "{\"loc\":[\"body\",\"box_prompts\",0,\"x_min\"],\"msg\":\"Input should be a valid integer\"}]}";
            Assert.AreEqual("body.image_url: field required; body.box_prompts.0.x_min: Input should be a valid integer",
                GenpressoError.Parse(body));
        }

        [Test]
        public void Parse_FastApiStringDetail_AndMessageField()
        {
            Assert.AreEqual("Request is still in progress", GenpressoError.Parse("{\"detail\":\"Request is still in progress\"}"));
            Assert.AreEqual("quota exceeded", GenpressoError.Parse("{\"message\":\"quota exceeded\"}"));
        }

        [Test]
        public void Parse_NonJsonOrEmpty_ReturnsNull()
        {
            Assert.IsNull(GenpressoError.Parse(null));
            Assert.IsNull(GenpressoError.Parse(""));
            Assert.IsNull(GenpressoError.Parse("<!DOCTYPE html><html><body>404</body></html>"));
            Assert.IsNull(GenpressoError.Parse("FUNCTION_PAYLOAD_TOO_LARGE"));
            Assert.IsNull(GenpressoError.Parse("{ broken json"));
            Assert.IsNull(GenpressoError.Parse("{\"ok\":true}"));
        }

        [Test]
        public void Describe_PrefixesTheStatus()
        {
            Assert.AreEqual("HTTP 422 - body.prompt: field required",
                GenpressoError.Describe(422, null, "{\"detail\":[{\"loc\":[\"body\",\"prompt\"],\"msg\":\"field required\"}]}"));
            StringAssert.Contains("(HTML error page)", GenpressoError.Describe(404, null, "<html><body>Not found</body></html>"));
            StringAssert.Contains("FUNCTION_PAYLOAD_TOO_LARGE", GenpressoError.Describe(413, null, "FUNCTION_PAYLOAD_TOO_LARGE"));
            StringAssert.Contains("Cannot connect", GenpressoError.Describe(0, "Cannot connect to destination host", null));
        }

        [Test]
        public void Describe_TruncatesLongBodies()
        {
            string longBody = new string('x', 2000);
            Assert.Less(GenpressoError.Describe(500, null, longBody).Length, 400);
        }

        [Test]
        public void IsStillInProgress_DetectsTheQueueLagBody()
        {
            Assert.IsTrue(GenpressoError.IsStillInProgress(JObject.Parse("{\"detail\":\"Request is still in progress\"}")));
            Assert.IsTrue(GenpressoError.IsStillInProgress(JObject.Parse("{\"detail\":\"request IN_PROGRESS\"}")));
            Assert.IsFalse(GenpressoError.IsStillInProgress(JObject.Parse("{\"detail\":[{\"msg\":\"in progress\"}]}")));
            Assert.IsFalse(GenpressoError.IsStillInProgress(JObject.Parse("{\"images\":[{\"url\":\"x\"}]}")));
            Assert.IsFalse(GenpressoError.IsStillInProgress(null));
        }

        [Test]
        public void KindFromStatus_MapsEveryDocumentedStatus()
        {
            Assert.AreEqual(GenpressoErrorKind.Network, GenpressoException.KindFromStatus(0));
            Assert.AreEqual(GenpressoErrorKind.Unauthorized, GenpressoException.KindFromStatus(401));
            Assert.AreEqual(GenpressoErrorKind.Unauthorized, GenpressoException.KindFromStatus(403));
            Assert.AreEqual(GenpressoErrorKind.InsufficientCredits, GenpressoException.KindFromStatus(402));
            Assert.AreEqual(GenpressoErrorKind.NotFound, GenpressoException.KindFromStatus(404));
            Assert.AreEqual(GenpressoErrorKind.Timeout, GenpressoException.KindFromStatus(408));
            Assert.AreEqual(GenpressoErrorKind.PayloadTooLarge, GenpressoException.KindFromStatus(413));
            Assert.AreEqual(GenpressoErrorKind.Validation, GenpressoException.KindFromStatus(400));
            Assert.AreEqual(GenpressoErrorKind.Validation, GenpressoException.KindFromStatus(422));
            Assert.AreEqual(GenpressoErrorKind.RateLimited, GenpressoException.KindFromStatus(429));
            Assert.AreEqual(GenpressoErrorKind.Server, GenpressoException.KindFromStatus(503));
        }

        [Test]
        public void TransientStatuses_AreRetryable_OthersAreNot()
        {
            foreach (long s in new long[] { 0, 408, 429, 500, 502, 503, 504 })
                Assert.IsTrue(GenpressoException.IsTransientStatus(s), "status " + s);
            foreach (long s in new long[] { 200, 400, 401, 402, 403, 404, 413, 422 })
                Assert.IsFalse(GenpressoException.IsTransientStatus(s), "status " + s);
        }

        [Test]
        public void FromHttp_CarriesKindRetryAfterAndHint()
        {
            var e429 = GenpressoException.FromHttp("submit", 429, "{\"error\":{\"message\":\"slow down\"}}", null, 3f);
            Assert.AreEqual(GenpressoErrorKind.RateLimited, e429.Kind);
            Assert.IsTrue(e429.Retryable);
            Assert.AreEqual(3f, e429.RetryAfterSec);
            StringAssert.Contains("slow down", e429.Message);

            var e402 = GenpressoException.FromHttp("submit", 402, "{\"error\":{\"message\":\"insufficient credits\"}}", null);
            Assert.AreEqual(GenpressoErrorKind.InsufficientCredits, e402.Kind);
            Assert.IsFalse(e402.Retryable);
            StringAssert.Contains("10 credits", e402.Message);

            var e422 = GenpressoException.FromHttp("result", 422, "{\"detail\":[{\"loc\":[\"body\",\"image_url\"],\"msg\":\"bad\"}]}", null);
            Assert.AreEqual(GenpressoErrorKind.Validation, e422.Kind);
            Assert.IsFalse(e422.Retryable);
            Assert.AreEqual(422, e422.StatusCode);

            var forced = GenpressoException.FromHttp("fetch", 503, null, null, retryable: false);
            Assert.IsFalse(forced.Retryable, "an explicit retryable flag wins (a paid job must never be re-submitted)");
        }
    }
}
