using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SplatPresso.Placement;
using UnityEngine;

namespace SplatPresso.Api
{
    /// <summary>
    /// Owns the two vision-language calls of the pipeline and their prompts/schemas:
    /// DECIDE plans object placement in the captured view (<see cref="DecisionResult"/>);
    /// VERIFY compares the original and edited images and returns ground-truth boxes (<see cref="VerificationResult"/>).
    /// </summary>
    public sealed class PlacementDecisionService
    {
        readonly GenpressoChatClient m_Chat;
        readonly SplatPressoSettings m_Settings;

        public PlacementDecisionService(GenpressoChatClient chat, SplatPressoSettings settings)
        {
            m_Chat = chat;
            m_Settings = settings != null ? settings : SplatPressoSettings.Active;
        }

        /// <summary>DECIDE: where and how to add the requested objects to the captured view.</summary>
        public async Awaitable<DecisionResult> DecideAsync(PlacementRequest request, byte[] captureJpeg, CancellationToken ct)
        {
            string objectsJson = JsonUtil.Serialize(request.objects, indented: false);
            var parts = new List<ChatContentPart>
            {
                ChatContentPart.Text(
                    $"User request: {request.intentSummary}\n" +
                    $"Objects requested: {objectsJson}\n" +
                    $"Placement hint: {request.placementHint ?? "(none)"}"),
                ChatContentPart.ImageJpeg(captureJpeg),
            };
            return await m_Chat.CompleteJsonAsync<DecisionResult>(
                m_Settings.chatModel, DecideSystemPrompt, null, parts, BuildDecideSchema(), "placement_decision",
                m_Settings.chatTimeoutSec, ct, m_Settings.chatFallbackModel);
        }

        /// <summary>
        /// VERIFY: which requested objects were really added and their tight boxes in the EDITED image. The edited
        /// image is re-encoded to at most maxImageLongSide first (saves tokens and keeps the body under 4 MB).
        /// </summary>
        public async Awaitable<VerificationResult> VerifyAsync(DecisionResult decision, byte[] originalJpeg, byte[] editedJpeg, CancellationToken ct)
        {
            var requested = new JArray();
            foreach (var o in decision.objects)
                requested.Add(new JObject
                {
                    ["id"] = o.id,
                    ["name"] = o.name,
                    ["description_for_segmentation"] = o.descriptionForSegmentation,
                });

            byte[] edited = editedJpeg;
            int maxSide = m_Settings.maxImageLongSide;
            var (w, h) = ImageUtil.ImageDims(editedJpeg);
            if ((maxSide > 0 && Mathf.Max(w, h) > maxSide) || !ImageUtil.IsJpeg(editedJpeg))
                edited = ImageUtil.ReencodeJpeg(editedJpeg, maxSide, m_Settings.jpegQuality) ?? editedJpeg;

            var parts = new List<ChatContentPart>
            {
                ChatContentPart.Text(
                    $"Requested objects: {requested.ToString(Formatting.None)}. Image 1 = original, image 2 = edited."),
                ChatContentPart.ImageJpeg(originalJpeg),
                ChatContentPart.ImageJpeg(edited),
            };
            return await m_Chat.CompleteJsonAsync<VerificationResult>(
                m_Settings.chatModel, VerifySystemPrompt, null, parts, BuildVerifySchema(), "placement_verification",
                m_Settings.chatTimeoutSec, ct, m_Settings.chatFallbackModel);
        }

        // ------------------------------------------------------------------------------------------
        // Prompts (verbatim from the proven pipeline)

        /// <summary>System prompt of the DECIDE call.</summary>
        public const string DecideSystemPrompt =
            "You are a 3D scene-augmentation planner. You see one image: the user's current camera view of a real, photoreal reconstructed 3D scene. You also get the user's request. Plan how to insert the requested object(s) into this exact view.\n" +
            "Rules:\n" +
            "- ONLY ADD objects. Never remove, move, restyle or relight anything that exists. The camera must stay identical.\n" +
            "- Every object must be FULLY inside the frame, not cropped by image edges, resting plausibly on a visible support surface (ground, table). Prefer empty regions; do not occlude the main existing subjects.\n" +
            "- Objects must not overlap each other and each must be clearly separable for later segmentation.\n" +
            "- target_bbox_norm is [x, y, w, h], normalized 0-1, origin top-left, a tight box where the object should appear.\n" +
            "- size_hint_m is the object's largest real-world dimension in meters.\n" +
            "- description_for_image_edit: how the object should look (color/material/style), consistent with scene lighting.\n" +
            "- description_for_segmentation: a short literal noun phrase a segmentation model can match, e.g. 'red folding camping chair'.\n" +
            "- edit_prompt: ONE instruction for an image-editing model that adds ALL objects, formatted as: 'Add <object 1 description> <location phrase relative to visible landmarks>. Add <object 2 ...>. Keep everything else exactly the same: same camera angle, same framing, same lighting, same photographic style. Do not modify or remove any existing content.'\n" +
            "- If the request cannot be satisfied in this view, set feasible=false and explain in infeasible_reason.\n" +
            "Output ONLY JSON matching the provided schema.";

        /// <summary>System prompt of the VERIFY call.</summary>
        public const string VerifySystemPrompt =
            "You compare two images of the same scene: image 1 is the ORIGINAL, image 2 is an EDITED version where objects were supposed to be added. For each requested object, report whether it was actually added, and if so its TIGHT bounding box in the EDITED image ([x,y,w,h] normalized 0-1, top-left origin). Also report whether the camera viewpoint and pre-existing content are otherwise unchanged, and describe any unexpected changes (removed/moved objects, relighting, style shift) in unexpected_changes (empty string if none). Boxes must be precise - they will drive a segmentation model. Output ONLY JSON matching the schema.";

        // ------------------------------------------------------------------------------------------
        // JSON schemas (strict; property names match the [JsonProperty] names on DecisionResult /
        // VerificationResult exactly)

        /// <summary>Strict JSON schema of <see cref="DecisionResult"/>.</summary>
        public static JObject BuildDecideSchema() => SObj(new JObject
        {
            ["scene_summary"] = SType("string"),
            ["feasible"] = SType("boolean"),
            ["infeasible_reason"] = SNullable("string"),
            ["objects"] = SArr(SObj(new JObject
            {
                ["id"] = SType("integer"),
                ["name"] = SType("string"),
                ["description_for_image_edit"] = SType("string"),
                ["description_for_segmentation"] = SType("string"),
                ["target_bbox_norm"] = SVec4(),
                ["size_hint_m"] = SType("number"),
                ["resting_surface"] = SEnum("ground", "table", "wall", "other"),
            })),
            ["edit_prompt"] = SType("string"),
        });

        /// <summary>Strict JSON schema of <see cref="VerificationResult"/>.</summary>
        public static JObject BuildVerifySchema() => SObj(new JObject
        {
            ["camera_unchanged"] = SType("boolean"),
            ["unexpected_changes"] = SType("string"),
            ["objects"] = SArr(SObj(new JObject
            {
                ["id"] = SType("integer"),
                ["name"] = SType("string"),
                ["found"] = SType("boolean"),
                ["bbox_norm"] = SVec4(),
                ["fully_visible"] = SType("boolean"),
                ["notes"] = SType("string"),
            })),
        });

        // ---- tiny schema builders ----

        static JObject SType(string type) => new JObject { ["type"] = type };

        static JObject SNullable(string type) => new JObject { ["type"] = new JArray(type, "null") };

        static JObject SEnum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };

        static JObject SVec4() => new JObject
        {
            ["type"] = "array",
            ["items"] = SType("number"),
            ["minItems"] = 4,
            ["maxItems"] = 4,
        };

        static JObject SArr(JObject items) => new JObject { ["type"] = "array", ["items"] = items };

        // strict object: additionalProperties=false, every property required
        static JObject SObj(JObject properties)
        {
            var required = new JArray();
            foreach (var prop in properties.Properties())
                required.Add(prop.Name);
            return new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
                ["required"] = required,
            };
        }
    }
}
