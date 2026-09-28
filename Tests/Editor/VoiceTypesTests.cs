using NUnit.Framework;
using SplatPresso.Voice;

namespace SplatPresso.Tests
{
    /// <summary>
    /// The chat voice backend's response type must bind the snake_case names of its strict "voice_turn" schema,
    /// and create actions must be mapped to sanitized PlacementRequests: the schema can only ask for limits
    /// (named objects, at most 4 types, 1..3 copies, a placement hint), the client has to enforce them. The same
    /// path is exercised end to end in the PlayMode VoiceTextTurnTests against the mock server.
    /// </summary>
    public class VoiceTypesTests
    {
        [Test]
        public void VoiceTurnResponse_BindsTheSchemaNames()
        {
            const string json = "{\"transcript\":\"put a red chair there\",\"reply\":\"Starting a red chair.\"," +
                                "\"actions\":[{\"type\":\"create\",\"intent_summary\":\"Add a red chair next to the table.\"," +
                                "\"objects\":[{\"name\":\"red chair\",\"description\":\"red wooden chair\",\"count\":2}]," +
                                "\"placement_hint\":\"left of the table\"},{\"type\":\"cancel\",\"intent_summary\":\"\",\"objects\":[],\"placement_hint\":\"\"}]}";
            var r = JsonUtil.Deserialize<VoiceTurnResponse>(json);
            Assert.NotNull(r);
            Assert.AreEqual("put a red chair there", r.transcript);
            Assert.AreEqual("Starting a red chair.", r.reply);
            Assert.AreEqual(2, r.actions.Count);

            var create = r.actions[0];
            Assert.AreEqual("create", create.type);
            Assert.AreEqual("Add a red chair next to the table.", create.intentSummary);
            Assert.AreEqual("left of the table", create.placementHint);
            Assert.AreEqual(1, create.objects.Count);
            Assert.AreEqual("red chair", create.objects[0].name);
            Assert.AreEqual("red wooden chair", create.objects[0].description);
            Assert.AreEqual(2, create.objects[0].count);

            Assert.AreEqual("cancel", r.actions[1].type);
            Assert.IsEmpty(r.actions[1].objects);
        }

        [Test]
        public void CreateAction_MapsToASanitizedPlacementRequest()
        {
            // the strict schema can only ASK for these limits; the client must enforce them
            var action = new VoiceAction
            {
                type = " Create ",
                intentSummary = "  Add some furniture.  ",
                placementHint = "   ",
                objects = new System.Collections.Generic.List<RequestedObject>
                {
                    new RequestedObject { name = " lamp ", description = " tall ", count = 9 },
                    new RequestedObject { name = "", description = "nameless", count = 1 },
                    new RequestedObject { name = "vase", description = null, count = 0 },
                    null,
                    new RequestedObject { name = "rug", count = 2 },
                    new RequestedObject { name = "stool", count = 1 },
                    new RequestedObject { name = "plant", count = 1 },
                },
            };
            Assert.IsTrue(action.IsCreate);

            var req = action.ToPlacementRequest(out string reason);
            Assert.NotNull(req, reason);
            Assert.AreEqual("Add some furniture.", req.intentSummary);
            Assert.AreEqual(VoiceRequestSanitizer.DefaultPlacementHint, req.placementHint, "an empty hint becomes 'anywhere sensible'");
            Assert.AreEqual("anywhere sensible", req.placementHint);
            Assert.LessOrEqual(req.objects.Count, VoiceRequestSanitizer.MaxObjectTypes);
            Assert.AreEqual(4, req.objects.Count);
            foreach (var o in req.objects)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(o.name), "unnamed objects are dropped");
                Assert.That(o.count, Is.InRange(1, VoiceRequestSanitizer.MaxCountPerObject));
            }
            Assert.AreEqual("lamp", req.objects[0].name);
            Assert.AreEqual(3, req.objects[0].count, "count is clamped to 1..3");
            Assert.AreEqual("vase", req.objects[1].name);
            Assert.AreEqual(1, req.objects[1].count);
            Assert.IsTrue(req.IsValid(out _));

            // the input is not modified
            Assert.AreEqual(9, action.objects[0].count);
        }

        [Test]
        public void CreateAction_WithoutNamedObjects_IsDropped()
        {
            var action = new VoiceAction
            {
                type = VoiceAction.TypeCreate,
                intentSummary = "",
                placementHint = "x",
                objects = new System.Collections.Generic.List<RequestedObject> { new RequestedObject { name = "  " } },
            };
            Assert.IsNull(action.ToPlacementRequest(out string reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void CancelOrUnknownActions_DoNotMapToRequests()
        {
            var cancel = new VoiceAction { type = "cancel" };
            Assert.IsTrue(cancel.IsCancel);
            Assert.IsFalse(cancel.IsCreate);
            Assert.IsNull(cancel.ToPlacementRequest(out _));
            Assert.IsNull(new VoiceAction { type = "dance" }.ToPlacementRequest(out _));
        }

        [Test]
        public void Sanitizer_FillsAMissingIntentSummary()
        {
            var req = new PlacementRequest { intentSummary = null, placementHint = "on the table" };
            req.objects.Add(new RequestedObject { name = "mug", count = 1 });
            var clean = VoiceRequestSanitizer.Sanitize(req, out _);
            Assert.NotNull(clean);
            StringAssert.Contains("mug", clean.intentSummary);
            Assert.AreEqual("on the table", clean.placementHint);
            Assert.IsNull(VoiceRequestSanitizer.Sanitize(null, out _));
        }

        [Test]
        public void VoicePlacementRequest_CarriesTheSourceUtterance()
        {
            var req = new PlacementRequest { intentSummary = "Add a lamp" };
            req.objects.Add(new RequestedObject { name = "lamp", count = 1 });
            var vpr = new VoicePlacementRequest { request = req, sourceUtterance = "a lamp please" };
            Assert.AreSame(req, vpr.request);
            Assert.AreEqual("a lamp please", vpr.sourceUtterance);
        }
    }
}
