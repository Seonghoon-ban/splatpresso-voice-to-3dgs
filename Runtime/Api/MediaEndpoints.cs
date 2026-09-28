using System;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SplatPresso.Placement;
using UnityEngine;

namespace SplatPresso.Api
{
    /// <summary>
    /// Typed wrappers over the media models the pipeline uses (image edit, enhance, text-to-image, SAM-3,
    /// background removal, depth, TripoSplat, Rodin). Result JSON is parsed defensively: common field variants
    /// are tried in order and failures carry the raw JSON (truncated).
    /// </summary>
    /// <remarks>
    /// Hosted result URLs are downloaded immediately (they expire) without any auth header (they live on public
    /// CDNs; never send a key to other hosts), and should be passed to the next step instead of re-uploading bytes.
    /// </remarks>
    public sealed class MediaEndpoints
    {
        const int kDataUriSoftLimit = 3000000; // keep data URIs well under the 4 MB body cap

        readonly MediaJobClient m_Jobs;
        readonly SplatPressoSettings m_Settings;

        public MediaEndpoints(MediaJobClient jobs, SplatPressoSettings settings)
        {
            m_Jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            m_Settings = settings != null ? settings : jobs.Settings;
        }

        /// <summary>Optional sink for job progress lines ("route: IN_QUEUE (queue position 2)").</summary>
        public Action<string> OnJobStatus { get; set; }

        /// <summary>
        /// Image edit (nano-banana). Returns the edited image AND its hosted URL; every downstream call
        /// (segment / depth / 3D) must reuse that URL rather than re-uploading the bytes.
        /// </summary>
        public async Awaitable<(byte[] jpeg, string url)> EditImageAsync(byte[] sourceJpeg, string prompt, bool useFallbackRoute, int? seed, CancellationToken ct)
        {
            string routeKey = useFallbackRoute ? MediaRouteKeys.EditFallback : MediaRouteKeys.Edit;
            string source = DataUri("image/jpeg", sourceJpeg);
            var job = await RunAsync(routeKey, useFallbackRoute ? "edit-fallback" : "edit", t =>
            {
                var input = new JObject
                {
                    ["prompt"] = prompt,
                    ["image_urls"] = new JArray(source),
                    ["num_images"] = 1,
                    ["output_format"] = "jpeg",
                };
                if (seed.HasValue)
                    input["seed"] = seed.Value;
                return input;
            }, ct);
            string url = ExtractUrl(job.result["images"]) ?? ExtractUrl(job.result["image"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("images[0].url", job);
            byte[] jpeg = await DownloadBytesAsync(url, ct);
            return (jpeg, url);
        }

        /// <summary>
        /// Re-renders a (typically low-resolution) segmented cutout as one clean studio render on white: a much
        /// better 3D-generation input than the raw crop. <paramref name="imageUrlOrDataUri"/> is a hosted URL or a data URI.
        /// </summary>
        public async Awaitable<(byte[] png, string url)> EnhanceObjectImageAsync(string imageUrlOrDataUri, string name, string description, CancellationToken ct)
        {
            string prompt =
                $"Recreate the object in this image as a single high-quality professional 3D product render. " +
                $"Object: {name}. {description}. " +
                "Keep the object's identity, shape, colors, materials and proportions exactly the same. " +
                "Plain pure white background, the complete object fully in frame and centered, " +
                // consistent viewpoint so TripoSplat's canonical "front" axis is the same for every
                // asset (an unspecified 3/4 view made per-object yaw vary left/right)
                "seen directly from the front, facing the camera head-on, " +
                "even studio lighting, only a soft contact shadow, " +
                "no other objects, no text, no watermark.";
            var job = await RunAsync(MediaRouteKeys.Enhance, "enhance:" + name, t => new JObject
            {
                ["prompt"] = prompt,
                ["image_urls"] = new JArray(imageUrlOrDataUri),
                ["num_images"] = 1,
                ["output_format"] = "png",
            }, ct);
            string url = ExtractUrl(job.result["images"]) ?? ExtractUrl(job.result["image"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("images[0].url", job);
            byte[] png = await DownloadBytesAsync(url, ct);
            return (png, url);
        }

        /// <summary>
        /// Text-to-image (no reference image) for the DirectTextTo3D mode: one object as a white-background
        /// studio render from the description alone.
        /// </summary>
        public async Awaitable<(byte[] png, string url)> GenerateObjectImageAsync(string name, string description, CancellationToken ct)
        {
            string prompt =
                $"A single {name}. {description}. " +
                "High-quality professional 3D product render, plain pure white background, " +
                "the complete object fully in frame and centered, " +
                "seen directly from the front, facing the camera head-on, " +
                "even studio lighting, only a soft contact shadow, no other objects, " +
                "no text, no watermark.";
            var job = await RunAsync(MediaRouteKeys.TextToImage, "t2i:" + name, t => new JObject
            {
                ["prompt"] = prompt,
                ["num_images"] = 1,
                ["aspect_ratio"] = "1:1",
                ["output_format"] = "png",
            }, ct);
            string url = ExtractUrl(job.result["images"]) ?? ExtractUrl(job.result["image"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("images[0].url", job);
            byte[] png = await DownloadBytesAsync(url, ct);
            return (png, url);
        }

        /// <summary>
        /// SAM-3 text + box segmentation of a hosted image. Returns a masked single-object PNG (transparent
        /// background, full frame), its URL and the confidence.
        /// </summary>
        /// <remarks>
        /// SAM-3 box prompts must be INTEGER PIXEL coordinates {x_min,y_min,x_max,y_max}: the live endpoint
        /// 422-rejects fractional values ("Input should be a valid integer"), so callers pass the pixel size of the
        /// exact image being segmented.
        /// </remarks>
        public async Awaitable<(byte[] png, string url, float score)> SegmentObjectAsync(string imageUrl, string prompt, Bbox box, int imgW, int imgH, CancellationToken ct)
        {
            var job = await RunAsync(MediaRouteKeys.Segment, "segment:" + prompt, t =>
            {
                var input = new JObject
                {
                    ["image_url"] = imageUrl,
                    ["prompt"] = prompt,
                    ["apply_mask"] = true,
                    ["include_boxes"] = true,
                    ["include_scores"] = true,
                    ["max_masks"] = 1,
                };
                if (imgW > 0 && imgH > 0 && box.IsValid)
                {
                    int[] px = box.Clamp01().ToXyXyPixels(imgW, imgH);
                    input["box_prompts"] = new JArray(new JObject { ["x_min"] = px[0], ["y_min"] = px[1], ["x_max"] = px[2], ["y_max"] = px[3] });
                }
                return input;
            }, ct);
            string url = ExtractUrl(job.result["masks"]) ?? ExtractUrl(job.result["image"]) ?? ExtractUrl(job.result["images"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("masks[0].url", job);

            float score = 1f;
            if (job.result["scores"] is JArray scores && scores.Count > 0 &&
                (scores[0].Type == JTokenType.Float || scores[0].Type == JTokenType.Integer))
                score = (float)scores[0];

            byte[] png = await DownloadBytesAsync(url, ct);
            return (png, url, score);
        }

        /// <summary>
        /// Background removal (BiRefNet) of local image bytes, sent as a data URI. Images whose data URI would exceed
        /// ~3 MB are re-encoded as JPEG (q90, downscaled if still too large) to stay under the 4 MB body cap.
        /// </summary>
        public async Awaitable<(byte[] png, string url)> RemoveBackgroundAsync(byte[] image, string mime, CancellationToken ct)
        {
            if (image == null || image.Length == 0)
                throw new ArgumentException("[SplatPresso] RemoveBackgroundAsync: no image bytes");
            string mimeType = string.IsNullOrEmpty(mime) ? "image/png" : mime;
            if (Base64Length(image.Length) + 40 > kDataUriSoftLimit)
            {
                byte[] jpeg = ImageUtil.ReencodeJpeg(image, 0, 90);
                if (jpeg != null && Base64Length(jpeg.Length) + 40 > kDataUriSoftLimit)
                    jpeg = ImageUtil.ReencodeJpeg(image, 2048, 90);
                if (jpeg != null)
                {
                    image = jpeg;
                    mimeType = "image/jpeg";
                }
            }
            string source = DataUri(mimeType, image);
            var job = await RunAsync(MediaRouteKeys.RemoveBackground, "rembg", t => new JObject
            {
                ["image_url"] = source,
                ["output_format"] = "png",
            }, ct);
            string url = ExtractUrl(job.result["image"]) ?? ExtractUrl(job.result["images"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("image.url", job);
            byte[] png = await DownloadBytesAsync(url, ct);
            return (png, url);
        }

        /// <summary>Relative depth (depth-anything v2) of a hosted image. Near/far polarity is not documented; the solver fits it.</summary>
        public async Awaitable<(byte[] png, string url)> EstimateDepthAsync(string imageUrl, CancellationToken ct)
        {
            var job = await RunAsync(MediaRouteKeys.Depth, "depth", t => new JObject { ["image_url"] = imageUrl }, ct);
            string url = ExtractUrl(job.result["image"]) ?? ExtractUrl(job.result["images"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("image.url", job);
            byte[] png = await DownloadBytesAsync(url, ct);
            return (png, url);
        }

        /// <summary>TripoSplat: image -> gaussian splat .ply, streamed to <paramref name="destPlyPath"/>. Returns the path.</summary>
        public async Awaitable<string> GenerateSplatAsync(string imageUrl, int numGaussians, string destPlyPath, CancellationToken ct)
        {
            int n = Mathf.Clamp(numGaussians <= 0 ? 262144 : numGaussians, 32768, 262144);
            var job = await RunAsync(MediaRouteKeys.ImageToSplat, "triposplat", t => new JObject
            {
                ["image_url"] = imageUrl,
                ["output_format"] = "ply",
                ["num_gaussians"] = n,
            }, ct);
            // handles model_mesh.url, model_mesh as a plain string, and model_mesh.file.url
            string url = ExtractUrl(job.result["model_mesh"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("model_mesh.url", job);
            await DownloadFileAsync(url, destPlyPath, ct);
            return destPlyPath;
        }

        /// <summary>Rodin image-to-3D -> .glb streamed to <paramref name="destGlbPath"/>. Returns the path.</summary>
        public async Awaitable<string> GenerateMeshFromImageAsync(string imageUrl, string destGlbPath, CancellationToken ct)
        {
            var job = await RunAsync(MediaRouteKeys.ImageToMesh, "rodin-image", t =>
            {
                var input = RodinBase(t);
                // Rodin v2.5 (and v2.5/fast) take image_urls; v2 and v1 take input_image_urls.
                input[IsRodin25(t) ? "image_urls" : "input_image_urls"] = new JArray(imageUrl);
                return input;
            }, ct);
            string url = ExtractUrl(job.result["model_mesh"]) ?? ExtractUrl(job.result["model_meshes"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("model_mesh.url", job);
            await DownloadFileAsync(url, destGlbPath, ct);
            return destGlbPath;
        }

        /// <summary>Rodin text-to-3D -> .glb streamed to <paramref name="destGlbPath"/>. Returns the path.</summary>
        public async Awaitable<string> GenerateMeshFromTextAsync(string prompt, string destGlbPath, CancellationToken ct)
        {
            var job = await RunAsync(MediaRouteKeys.TextToMesh, "rodin-text", t =>
            {
                var input = RodinBase(t);
                input["prompt"] = prompt;
                return input;
            }, ct);
            string url = ExtractUrl(job.result["model_mesh"]) ?? ExtractUrl(job.result["model_meshes"]);
            if (string.IsNullOrEmpty(url))
                throw MissingField("model_mesh.url", job);
            await DownloadFileAsync(url, destGlbPath, ct);
            return destGlbPath;
        }

        // ------------------------------------------------------------------------------------------

        /// <summary><c>data:&lt;mime&gt;;base64,...</c> for inline uploads (there is no file-upload endpoint).</summary>
        public static string DataUri(string mime, byte[] bytes) =>
            "data:" + mime + ";base64," + Convert.ToBase64String(bytes ?? Array.Empty<byte>());

        /// <summary>
        /// Digs a URL out of the common result shapes: plain string, {url}, {file:{url}}, {image:{url}}, or the
        /// first element of an array of any of those. Null when absent.
        /// </summary>
        public static string ExtractUrl(JToken token)
        {
            switch (token)
            {
                case null:
                    return null;
                case JValue v when v.Type == JTokenType.String:
                    return (string)v;
                case JArray a:
                    return a.Count > 0 ? ExtractUrl(a[0]) : null;
                case JObject o:
                    if (o["url"] != null) return ExtractUrl(o["url"]);
                    if (o["file"] != null) return ExtractUrl(o["file"]);
                    if (o["image"] != null) return ExtractUrl(o["image"]);
                    return null;
                default:
                    return null;
            }
        }

        JObject RodinBase(MediaTarget t)
        {
            var input = new JObject
            {
                ["geometry_file_format"] = "glb",
                ["material"] = string.IsNullOrWhiteSpace(m_Settings.rodinMaterial) ? "PBR" : m_Settings.rodinMaterial.Trim(),
            };
            // The Gen-2.5 tiers only exist on the v2.5 routes (v1/v2 use different tier names).
            if (IsRodin25(t) && !string.IsNullOrWhiteSpace(m_Settings.rodinTier))
                input["tier"] = m_Settings.rodinTier.Trim();
            return input;
        }

        static bool IsRodin25(MediaTarget t) => t?.path != null && t.path.IndexOf("v2.5", StringComparison.OrdinalIgnoreCase) >= 0;

        Awaitable<MediaJobResult> RunAsync(string routeKey, string costLabel, Func<MediaTarget, JObject> buildInput, CancellationToken ct)
        {
            var route = m_Settings.GetRoute(routeKey) ?? SplatPressoSettings.DefaultRoute(routeKey);
            var sink = OnJobStatus;
            Action<string> onStatus = sink == null ? null : (Action<string>)(s => sink(routeKey + ": " + s));
            return m_Jobs.RunAsync(routeKey, route, buildInput, costLabel, onStatus, ct);
        }

        // Output URLs are downloaded right away and retried on transient errors: the job is already paid for, so
        // a flaky download must not bubble up and make the caller re-submit it. Waits grow (2 s, 4 s, 8 s) and so does
        // the timeout (UnityWebRequest.timeout covers the WHOLE transfer, so a big .ply on a slow link needs more).
        // A download that still fails is rethrown as NON-retryable (see OutputUnavailable).
        const int kDownloadAttempts = 4;

        int DownloadTimeoutSec(int attempt) => Mathf.Max(10, m_Settings.downloadTimeoutSec) * (attempt + 1);

        async Awaitable<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await HttpJson.GetBytesAsync(url, null, DownloadTimeoutSec(attempt), ct);
                }
                catch (GenpressoException e) when (IsTransientDownloadFailure(e) && attempt + 1 < kDownloadAttempts)
                {
                    await WaitBeforeDownloadRetryAsync(e, attempt, ct);
                }
                catch (GenpressoException e)
                {
                    throw OutputUnavailable(e);
                }
            }
        }

        async Awaitable DownloadFileAsync(string url, string destPath, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await HttpJson.DownloadFileAsync(url, destPath, null, DownloadTimeoutSec(attempt), ct);
                    return;
                }
                catch (GenpressoException e) when (IsTransientDownloadFailure(e) && attempt + 1 < kDownloadAttempts)
                {
                    await WaitBeforeDownloadRetryAsync(e, attempt, ct);
                }
                catch (GenpressoException e)
                {
                    throw OutputUnavailable(e);
                }
            }
        }

        // Network / 408 / 429 / 5xx, plus a transfer that broke off after the headers arrived (a whole-request
        // timeout mid-body reports the 2xx status of the response it was reading).
        static bool IsTransientDownloadFailure(GenpressoException e) =>
            e.Retryable || (e.StatusCode >= 200 && e.StatusCode < 300);

        static async Awaitable WaitBeforeDownloadRetryAsync(GenpressoException e, int attempt, CancellationToken ct)
        {
            float delay = e.RetryAfterSec > 0f ? Mathf.Min(e.RetryAfterSec, 30f) : 2f * (1 << attempt); // 2 s, 4 s, 8 s
            Debug.LogWarning($"[SplatPresso] Download failed ({e.Message}); retry {attempt + 1}/{kDownloadAttempts - 1} in {delay:F0}s");
            await HttpJson.DelayAsync(delay, ct);
        }

        // The output of a completed (already billed) job could not be downloaded. Never retryable: retrying the step
        // would re-submit the job and pay again. Status 0 so it is not mistaken for a rejected input URL
        // (PlacementOrchestrator.IsHostedUrlRejected), and never Unauthorized: output URLs are fetched without a key,
        // so a 401/403 is the host refusing the URL (expired, access rule), not a bad GenPresso key.
        static GenpressoException OutputUnavailable(GenpressoException e)
        {
            long status = e.StatusCode;
            bool rejected = status == 401 || status == 402 || status == 403 || status == 404 || status == 410;
            if (rejected)
                return new GenpressoException(
                    $"The output URL of a completed media job was rejected by its host (HTTP {status}); it may have expired. Not re-submitting the job.",
                    GenpressoErrorKind.NotFound, 0, null, retryable: false, inner: e);
            return new GenpressoException(
                $"The output of a completed media job could not be downloaded ({e.Message}); not re-submitting the job.",
                GenpressoErrorKind.Network, 0, null, retryable: false, inner: e);
        }

        static long Base64Length(long byteCount) => (byteCount + 2) / 3 * 4;

        static GenpressoException MissingField(string what, MediaJobResult job)
        {
            string raw = job.result != null ? job.result.ToString(Formatting.None) : "(null)";
            // The job completed (and was paid for); re-submitting would not change the output shape.
            return new GenpressoException($"{job.resolvedPath} result is missing {what}: {GenpressoError.Truncate(raw, 500)}",
                GenpressoErrorKind.Parse, 0, raw, retryable: false);
        }
    }
}
