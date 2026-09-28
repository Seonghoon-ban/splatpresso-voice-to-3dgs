using System.Collections;
using NUnit.Framework;
using SplatPresso.Api;
using UnityEngine;
using UnityEngine.TestTools;

namespace SplatPresso.Tests
{
    /// <summary>
    /// ModelWarmer against the mock server: one small, free (validation-failing) request per route, debounced, never
    /// touching a cost ledger, and "ready" once the job reached a terminal state (a worker picked it up).
    /// </summary>
    public class WarmUpTests : MockServerFixture
    {
        [UnityTest]
        public IEnumerator WarmUp_SendsOneFreeRequestPerInterval_AndReportsTheWorkerReady()
        {
            Settings.warmUpModels = true;
            Settings.warmUpIntervalSec = 240f;
            int mark = Mock.RequestCount;
            double answeredAfter = -1, meshAnswered = -1;
            System.Action<string, double> onWarm = (route, secs) =>
            {
                if (route == MediaRouteKeys.ImageToSplat)
                    answeredAfter = secs;
                else if (route == MediaRouteKeys.TextToMesh)
                    meshAnswered = secs;
            };
            ModelWarmer.WarmedUp += onWarm;
            try
            {
                Assert.IsTrue(ModelWarmer.WarmUp(Settings, MediaRouteKeys.ImageToSplat, "test"), "first warm-up is sent");
                Assert.IsFalse(ModelWarmer.WarmUp(Settings, MediaRouteKeys.ImageToSplat, "again"), "a warm-up in flight is not duplicated");

                float until = Time.realtimeSinceStartup + 30f;
                while (answeredAfter < 0 && Time.realtimeSinceStartup < until)
                    yield return null;
                Assert.GreaterOrEqual(answeredAfter, 0.0, "the warm-up job reached a terminal state\n" + Mock.Describe(mark));

                var submits = Mock.Find(r => r.kind == "submit" && r.capability == "splat", mark);
                Assert.AreEqual(1, submits.Count, Mock.Describe(mark));
                Assert.Less(submits[0].bodyBytes, 200, "the warm-up body carries no image");
                Assert.IsTrue(submits[0].hadAuthorization);
                Assert.IsEmpty(Mock.Find(r => r.kind == "file", mark), "a warm-up downloads nothing");

                Assert.IsFalse(ModelWarmer.WarmUp(Settings, MediaRouteKeys.ImageToSplat, "debounced"),
                    "no second warm-up within warmUpIntervalSec");
                Assert.IsTrue(ModelWarmer.WarmUp(Settings, MediaRouteKeys.TextToMesh, "other route"),
                    "routes are debounced independently");
                // let that job finish too, so nothing keeps polling the mock after this test
                until = Time.realtimeSinceStartup + 30f;
                while (meshAnswered < 0 && Time.realtimeSinceStartup < until)
                    yield return null;
                Assert.GreaterOrEqual(meshAnswered, 0.0, Mock.Describe(mark));
            }
            finally
            {
                ModelWarmer.WarmedUp -= onWarm;
            }
        }

        [Test]
        public void WarmUp_IsOffWhenDisabled_AndPicksTheRouteForTheRepresentation()
        {
            Settings.warmUpModels = false;
            Assert.IsFalse(ModelWarmer.WarmUp(Settings, MediaRouteKeys.ImageToSplat, "disabled"));

            Settings.representation = ObjectRepresentation.GaussianSplat;
            Assert.AreEqual(MediaRouteKeys.ImageToSplat, ModelWarmer.RouteFor(Settings, GenerationMode.SceneContextual));
            Assert.AreEqual(MediaRouteKeys.ImageToSplat, ModelWarmer.RouteFor(Settings, GenerationMode.DirectTextTo3D));
            Settings.representation = ObjectRepresentation.Mesh;
            Assert.AreEqual(MediaRouteKeys.ImageToMesh, ModelWarmer.RouteFor(Settings, GenerationMode.SceneContextual));
            Assert.AreEqual(MediaRouteKeys.TextToMesh, ModelWarmer.RouteFor(Settings, GenerationMode.DirectTextTo3D));
            Settings.representation = ObjectRepresentation.GaussianSplat;
        }
    }
}
