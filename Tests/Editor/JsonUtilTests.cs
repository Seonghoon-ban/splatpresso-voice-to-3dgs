using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SplatPresso.Tests
{
    /// <summary>
    /// JsonUtil converters and the persisted artifact formats. Unity's Vector3/Quaternion cannot go through
    /// Newtonsoft with default settings (the self-referencing <c>normalized</c> property recurses), so they are
    /// written as plain arrays; session replays depend on that format.
    /// </summary>
    public class JsonUtilTests
    {
        public sealed class Holder
        {
            public Vector3 v;
            public Quaternion q;
        }

        string m_TempDir;

        [TearDown]
        public void TearDown()
        {
            EditorTestUtil.DeleteDir(m_TempDir);
            m_TempDir = null;
        }

        [Test]
        public void Vector3AndQuaternion_AreWrittenAsArraysAndRoundTrip()
        {
            var src = new Holder { v = new Vector3(1.5f, -2.25f, 3f), q = new Quaternion(0.1f, -0.2f, 0.3f, 0.9f) };
            string json = JsonUtil.Serialize(src, indented: false);

            var obj = JObject.Parse(json);
            Assert.AreEqual(JTokenType.Array, obj["v"].Type);
            Assert.AreEqual(3, ((JArray)obj["v"]).Count);
            Assert.AreEqual(4, ((JArray)obj["q"]).Count);

            var back = JsonUtil.Deserialize<Holder>(json);
            Assert.That(Vector3.Distance(src.v, back.v), Is.LessThan(1e-6f));
            Assert.AreEqual(src.q.x, back.q.x, 1e-6f);
            Assert.AreEqual(src.q.y, back.q.y, 1e-6f);
            Assert.AreEqual(src.q.z, back.q.z, 1e-6f);
            Assert.AreEqual(src.q.w, back.q.w, 1e-6f);
        }

        [Test]
        public void Converters_AcceptIntegersAndNull()
        {
            var back = JsonUtil.Deserialize<Holder>("{\"v\":[1,2,3],\"q\":null}");
            Assert.AreEqual(new Vector3(1, 2, 3), back.v);
            Assert.AreEqual(Quaternion.identity, back.q);
        }

        [Test]
        public void Deserialize_NullOrEmpty_ReturnsDefault()
        {
            Assert.IsNull(JsonUtil.Deserialize<Holder>(null));
            Assert.IsNull(JsonUtil.Deserialize<Holder>("   "));
        }

        [Test]
        public void CaptureResult_SaveToAndLoadFrom_RoundTrip()
        {
            m_TempDir = EditorTestUtil.NewTempDir("capture");
            var cap = new CaptureResult
            {
                width = 4,
                height = 2,
                rgbJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0x01, 0x02 },
                depthMeters = new[] { 0f, 1f, 2.5f, 3f, 4f, 5f, 6f, 7.25f },
                cameraPosition = new Vector3(4.2056f, 2.1954f, 4.0524f),
                cameraRotation = new Quaternion(-0.2955f, 0.4547f, -0.1630f, -0.8243f),
                verticalFovDeg = 60f,
                nearPlane = 0.3f,
                farPlane = 1000f,
            };
            cap.SaveTo(m_TempDir);

            // the meta file stores pose values as arrays (same format as sessions written by earlier versions)
            var meta = JObject.Parse(File.ReadAllText(Path.Combine(m_TempDir, CaptureResult.MetaFileName)));
            Assert.AreEqual(JTokenType.Array, meta["cameraPosition"].Type);
            Assert.AreEqual(JTokenType.Array, meta["cameraRotation"].Type);
            Assert.IsNull(meta["rgbJpeg"], "image bytes live in capture.jpg, not in the meta file");

            var back = CaptureResult.LoadFrom(m_TempDir);
            Assert.NotNull(back);
            Assert.AreEqual(4, back.width);
            Assert.AreEqual(2, back.height);
            CollectionAssert.AreEqual(cap.rgbJpeg, back.rgbJpeg);
            CollectionAssert.AreEqual(cap.depthMeters, back.depthMeters);
            Assert.That(Vector3.Distance(cap.cameraPosition, back.cameraPosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(cap.cameraRotation, back.cameraRotation), Is.LessThan(0.01f));
            Assert.AreEqual(60f, back.verticalFovDeg);
            Assert.AreEqual(7.25f, back.DepthAt(3, 1));
            Assert.AreEqual(0f, back.DepthAt(4, 0), "outside the image");
        }

        [Test]
        public void CaptureResult_LoadFrom_MissingMeta_ReturnsNull()
        {
            m_TempDir = EditorTestUtil.NewTempDir("capture-empty");
            Assert.IsNull(CaptureResult.LoadFrom(m_TempDir));
        }

        [Test]
        public void PlacedObjectResult_UsesStableNamesAndIntegerStatus()
        {
            var o = new PlacedObjectResult
            {
                id = 3,
                name = "lamp",
                status = ObjectStatus.Skipped,
                representation = ObjectRepresentation.Mesh,
                modelPath = "objects/3/model.glb",
                runId = "not-persisted",
                bboxGeneratedNorm = new[] { 0.1f, 0.2f, 0.3f, 0.4f },
            };
            var json = JObject.Parse(JsonUtil.Serialize(o));
            Assert.AreEqual(3, (int)json["status"], "ObjectStatus is persisted as an integer; never reorder the enum");
            Assert.AreEqual(1, (int)json["representation"]);
            Assert.AreEqual("objects/3/model.glb", (string)json["modelPath"]);
            Assert.IsNull(json["runId"], "runId is not persisted");
            Assert.IsNull(json["BboxGenerated"], "computed properties are not persisted");

            var back = JsonUtil.Deserialize<PlacedObjectResult>(json.ToString());
            Assert.AreEqual(ObjectStatus.Skipped, back.status);
            Assert.AreEqual(ObjectRepresentation.Mesh, back.representation);
        }

        [Test]
        public void ObjectStatus_NumericValuesAreFrozen()
        {
            Assert.AreEqual(0, (int)ObjectStatus.Pending);
            Assert.AreEqual(1, (int)ObjectStatus.Placed);
            Assert.AreEqual(2, (int)ObjectStatus.Ready);
            Assert.AreEqual(3, (int)ObjectStatus.Skipped);
            Assert.AreEqual(0, (int)ObjectRepresentation.GaussianSplat);
            Assert.AreEqual(1, (int)ObjectRepresentation.Mesh);
        }

        [Test]
        public void PlacementRequest_UsesSnakeCaseNames()
        {
            var req = new PlacementRequest
            {
                intentSummary = "Add a chair",
                placementHint = "left of the table",
                objects = new List<RequestedObject> { new RequestedObject { name = "chair", description = "red", count = 2 } },
            };
            var json = JObject.Parse(JsonUtil.Serialize(req));
            Assert.AreEqual("Add a chair", (string)json["intent_summary"]);
            Assert.AreEqual("left of the table", (string)json["placement_hint"]);
            Assert.AreEqual(2, (int)json["objects"][0]["count"]);

            Assert.IsTrue(req.IsValid(out _));
            Assert.IsFalse(new PlacementRequest().IsValid(out string reason));
            Assert.IsNotEmpty(reason);
            Assert.IsFalse(new PlacementRequest { objects = { new RequestedObject { name = " " } } }.IsValid(out _));
        }
    }
}
