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
            bool orientation = m_Settings.askVlmForOrientation;
            return await m_Chat.CompleteJsonAsync<DecisionResult>(
                m_Settings.chatModel, DecidePromptFor(orientation), null, parts, BuildDecideSchema(orientation), "placement_decision",
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
            bool orientation = m_Settings.askVlmForOrientation;
            return await m_Chat.CompleteJsonAsync<VerificationResult>(
                m_Settings.chatModel, VerifyPromptFor(orientation), null, parts, BuildVerifySchema(orientation), "placement_verification",
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

        /// <summary>
        /// DECIDE prompt with orientation hints (<see cref="SplatPressoSettings.askVlmForOrientation"/>): the legacy prompt
        /// with wall hanging allowed, and rules for resting_surface 'wall' and against_wall.
        /// </summary>
        public const string DecideSystemPromptOrientation =
            "You are a 3D scene-augmentation planner. You see one image: the user's current camera view of a real, photoreal reconstructed 3D scene. You also get the user's request. Plan how to insert the requested object(s) into this exact view.\n" +
            "Rules:\n" +
            "- ONLY ADD objects. Never remove, move, restyle or relight anything that exists. The camera must stay identical.\n" +
            "- Every object must be FULLY inside the frame, not cropped by image edges, resting plausibly on a visible support surface (ground, table) or hanging on a visible wall. Prefer empty regions; do not occlude the main existing subjects.\n" +
            "- Objects must not overlap each other and each must be clearly separable for later segmentation.\n" +
            "- target_bbox_norm is [x, y, w, h], normalized 0-1, origin top-left, a tight box where the object should appear.\n" +
            "- size_hint_m is the object's largest real-world dimension in meters.\n" +
            "- resting_surface: 'wall' ONLY for objects that hang on a wall with nothing under them (paintings, posters, mirrors, clocks, mounted heads, wall shelves, wall-mounted TVs); furniture that stands on the floor is 'ground' even when it stands against a wall.\n" +
            "- against_wall: 'yes' if the object belongs with its back to a wall (every wall-hung object; bookshelves, cabinets, wardrobes, dressers, sideboards, TV stands, desks, beds, pianos) or the user asked for it on, along or against a wall - then place it at that wall; otherwise 'no'.\n" +
            "- description_for_image_edit: how the object should look (color/material/style), consistent with scene lighting.\n" +
            "- description_for_segmentation: a short literal noun phrase a segmentation model can match, e.g. 'red folding camping chair'.\n" +
            "- edit_prompt: ONE instruction for an image-editing model that adds ALL objects, formatted as: 'Add <object 1 description> <location phrase relative to visible landmarks>. Add <object 2 ...>. Keep everything else exactly the same: same camera angle, same framing, same lighting, same photographic style. Do not modify or remove any existing content.'\n" +
            "- If the request cannot be satisfied in this view, set feasible=false and explain in infeasible_reason.\n" +
            "Output ONLY JSON matching the provided schema.";

        /// <summary>
        /// VERIFY prompt with orientation hints (<see cref="SplatPressoSettings.askVlmForOrientation"/>): the legacy prompt
        /// plus support / back_against_wall / front_faces per object, judged on the edited image.
        /// </summary>
        public const string VerifySystemPromptOrientation =
            "You compare two images of the same scene: image 1 is the ORIGINAL, image 2 is an EDITED version where objects were supposed to be added. For each requested object, report whether it was actually added, and if so its TIGHT bounding box in the EDITED image ([x,y,w,h] normalized 0-1, top-left origin). Also report whether the camera viewpoint and pre-existing content are otherwise unchanged, and describe any unexpected changes (removed/moved objects, relighting, style shift) in unexpected_changes (empty string if none). Boxes must be precise - they will drive a segmentation model. " +
            "For each FOUND object also describe how it sits in the EDITED image (judge the image, not the request): support = what holds it up (floor, table_or_furniture, wall_mounted, other); back_against_wall = 'yes' if its back touches or almost touches a wall, 'no' if it stands free in the room, 'unsure' if you cannot tell; front_faces = the direction its front points - the side people look at or use (a painting's picture side, a shelf's open side, a screen, a seat, an animal's face) - in IMAGE directions: toward_viewer, toward_viewer_left, toward_viewer_right, image_left, image_right, away_from_viewer, or no_clear_front for round or symmetric objects (plants, vases, round lamps). left/right always mean the left/right side of the IMAGE, never the object's own left/right. An object that hangs on or stands flush against a wall faces out of that wall into the room. For objects that were not found use support 'other', back_against_wall 'unsure', front_faces 'no_clear_front'. Output ONLY JSON matching the schema.";

        /// <summary>The DECIDE system prompt used: with orientation hints, or the legacy one byte for byte.</summary>
        public static string DecidePromptFor(bool orientation) => orientation ? DecideSystemPromptOrientation : DecideSystemPrompt;

        /// <summary>The VERIFY system prompt used: with orientation hints, or the legacy one byte for byte.</summary>
        public static string VerifyPromptFor(bool orientation) => orientation ? VerifySystemPromptOrientation : VerifySystemPrompt;

        // ------------------------------------------------------------------------------------------
        // JSON schemas (strict; property names match the [JsonProperty] names on DecisionResult /
        // VerificationResult exactly)

        /// <summary>Strict JSON schema of <see cref="DecisionResult"/>.</summary>
        /// <param name="orientation">Include against_wall (default); false = the legacy schema.</param>
        public static JObject BuildDecideSchema(bool orientation = true)
        {
            var obj = new JObject
            {
                ["id"] = SType("integer"),
                ["name"] = SType("string"),
                ["description_for_image_edit"] = SType("string"),
                ["description_for_segmentation"] = SType("string"),
                ["target_bbox_norm"] = SVec4(),
                ["size_hint_m"] = SType("number"),
                ["resting_surface"] = SEnum("ground", "table", "wall", "other"),
            };
            if (orientation)
                obj["against_wall"] = SEnum("yes", "no");
            return SObj(new JObject
            {
                ["scene_summary"] = SType("string"),
                ["feasible"] = SType("boolean"),
                ["infeasible_reason"] = SNullable("string"),
                ["objects"] = SArr(SObj(obj)),
                ["edit_prompt"] = SType("string"),
            });
        }

        /// <summary>Strict JSON schema of <see cref="VerificationResult"/>.</summary>
        /// <param name="orientation">Include support / back_against_wall / front_faces (default); false = the legacy schema.</param>
        public static JObject BuildVerifySchema(bool orientation = true)
        {
            var obj = new JObject
            {
                ["id"] = SType("integer"),
                ["name"] = SType("string"),
                ["found"] = SType("boolean"),
                ["bbox_norm"] = SVec4(),
                ["fully_visible"] = SType("boolean"),
            };
            if (orientation)
            {
                obj["support"] = SEnum("floor", "table_or_furniture", "wall_mounted", "other");
                obj["back_against_wall"] = SEnum("yes", "no", "unsure");
                obj["front_faces"] = SEnum("toward_viewer", "toward_viewer_left", "toward_viewer_right", "image_left", "image_right",
                    "away_from_viewer", "no_clear_front");
            }
            obj["notes"] = SType("string");
            return SObj(new JObject
            {
                ["camera_unchanged"] = SType("boolean"),
                ["unexpected_changes"] = SType("string"),
                ["objects"] = SArr(SObj(obj)),
            });
        }

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
