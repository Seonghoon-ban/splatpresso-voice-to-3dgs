using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SplatPresso.Tests
{
    /// <summary>One request seen by <see cref="MockGenpressoServer"/>.</summary>
    public sealed class MockRequest
    {
        /// <summary>Arrival order (0-based).</summary>
        public int index;
        public string method;
        /// <summary>Absolute path, e.g. <c>/api/v1/media/gp/sam-3/image</c>.</summary>
        public string path;
        /// <summary>
        /// What the mock took it for: <c>chat:decide</c>, <c>chat:verify</c>, <c>chat:voice</c>, <c>chat:narrate</c>,
        /// <c>chat:other</c>, <c>models</c>, <c>submit</c>, <c>status</c>, <c>result</c>, <c>cancel</c>, <c>file</c>, <c>unknown</c>.
        /// </summary>
        public string kind;
        /// <summary>Media model path of a submit (e.g. <c>gp/tripo3d/triposplat</c>), or the file name of a download.</summary>
        public string target;
        /// <summary>Capability the submit was classified as (edit, enhance, t2i, segment, rembg, depth, splat, mesh-image, mesh-text).</summary>
        public string capability;
        public int status;
        public bool hadAuthorization;
        public int bodyBytes;
        public string schemaName;
        /// <summary>Seconds since the server started.</summary>
        public double time;

        public override string ToString() => $"#{index} {method} {path} -> {status} [{kind}{(string.IsNullOrEmpty(capability) ? "" : ":" + capability)}]";
    }

    /// <summary>
    /// In-process stand-in for the GenPresso API (chat + media queue) on <c>http://127.0.0.1:&lt;free port&gt;/</c>,
    /// used by the PlayMode tests. It serves the synthetic fixtures as model outputs and reproduces the queue quirks
    /// the client must survive:
    /// <list type="bullet">
    /// <item>the first TripoSplat candidate path (<c>tripo3d/triposplat</c>) answers 404 JSON (not hosted), the
    ///   next one (<c>gp/tripo3d/triposplat</c>) works;</item>
    /// <item>the first image-edit submit answers 429 with <c>Retry-After: 1</c>;</item>
    /// <item>each job reports IN_QUEUE once before COMPLETED;</item>
    /// <item>the first result fetch after COMPLETED answers 200 <c>{"detail":"Request is still in progress"}</c> once;</item>
    /// <item>submit/status/result/cancel URLs are returned in the submit body (clients must use them verbatim);</item>
    /// <item>inputs are validated like fal does (422 FastAPI detail), incl. integer SAM-3 box prompts and the 4 MB
    ///   body cap (plain-text 413).</item>
    /// </list>
    /// Chat requests are answered by inspecting the system prompt / schema name: DECIDE -> decision.json,
    /// VERIFY -> verification.json, voice turns -> a "create red chair" action. Every request is recorded.
    /// Requests are handled on thread-pool threads; nothing here touches Unity APIs.
    /// </summary>
    public sealed class MockGenpressoServer : IDisposable
    {
        /// <summary>The only API key the mock accepts (Bearer).</summary>
        public const string ApiKey = "gp_test";

        const int kMaxBody = 4000000;

        readonly string m_FixturesDir;
        readonly HttpListener m_Listener;
        readonly Thread m_Thread;
        readonly object m_Lock = new object();
        readonly List<MockRequest> m_Requests = new List<MockRequest>();
        readonly Dictionary<string, Job> m_Jobs = new Dictionary<string, Job>();
        readonly System.Diagnostics.Stopwatch m_Clock = System.Diagnostics.Stopwatch.StartNew();
        volatile bool m_Running;
        int m_NextJob = 1;
        int m_NextChat = 1;
        bool m_Edit429Sent;
        bool m_InProgressSent;

        // ---- scenario knobs (set before the requests they affect) ----

        /// <summary>Media path that answers 404 "not hosted" (null = none).</summary>
        public string MissingSplatPath = "tripo3d/triposplat";
        /// <summary>Answer the first image-edit submit with 429 + Retry-After: 1.</summary>
        public bool RateLimitFirstEditSubmit = true;
        /// <summary>How many status polls report IN_QUEUE before COMPLETED.</summary>
        public int InQueuePolls = 1;
        /// <summary>Answer the first result fetch after COMPLETED with 200 "still in progress" (once per server).</summary>
        public bool InProgressOnceAfterCompleted = true;
        /// <summary>Keep every new job IN_QUEUE forever (cancellation / timeout tests).</summary>
        public bool HoldJobsInQueue;
        /// <summary>The next accepted job ends FAILED and its result is a 422 validation error.</summary>
        public bool FailNextJob;

        sealed class Job
        {
            public string id;
            public string modelPath;
            public string capability;
            public JObject result;
            public int polls;
            public bool completed;
            public bool failed;
            public bool hold;
            public bool cancelled;
        }

        /// <summary>Starts listening on a free loopback port.</summary>
        /// <param name="fixturesDir">Folder with the fixture files served under <c>/files/</c>.</param>
        public MockGenpressoServer(string fixturesDir)
        {
            m_FixturesDir = fixturesDir ?? throw new ArgumentNullException(nameof(fixturesDir));
            int port = FreePort();
            Origin = "http://127.0.0.1:" + port;
            ApiBaseUrl = Origin + "/api/v1";
            m_Listener = new HttpListener();
            m_Listener.Prefixes.Add(Origin + "/");
            m_Listener.Start();
            m_Running = true;
            m_Thread = new Thread(AcceptLoop) { IsBackground = true, Name = "MockGenpressoServer" };
            m_Thread.Start();
        }

        /// <summary><c>http://127.0.0.1:port</c></summary>
        public string Origin { get; }
        /// <summary><c>http://127.0.0.1:port/api/v1</c> (use as <c>SplatPressoSettings.apiBaseUrl</c>).</summary>
        public string ApiBaseUrl { get; }

        /// <summary>Snapshot of every request so far.</summary>
        public List<MockRequest> Requests
        {
            get { lock (m_Lock) return new List<MockRequest>(m_Requests); }
        }

        /// <summary>Requests with index &gt;= <paramref name="fromIndex"/> matching <paramref name="predicate"/>.</summary>
        public List<MockRequest> Find(Func<MockRequest, bool> predicate, int fromIndex = 0)
        {
            var list = new List<MockRequest>();
            foreach (var r in Requests)
                if (r.index >= fromIndex && predicate(r))
                    list.Add(r);
            return list;
        }

        /// <summary>Number of requests recorded so far (use as a marker for <see cref="Find"/>).</summary>
        public int RequestCount
        {
            get { lock (m_Lock) return m_Requests.Count; }
        }

        /// <summary>One line per request (for assertion messages).</summary>
        public string Describe(int fromIndex = 0)
        {
            var sb = new StringBuilder();
            foreach (var r in Requests)
                if (r.index >= fromIndex)
                    sb.AppendLine(r.ToString());
            return sb.ToString();
        }

        public void Dispose()
        {
            m_Running = false;
            try { m_Listener.Stop(); } catch (Exception) { /* already stopped */ }
            try { m_Listener.Close(); } catch (Exception) { /* already closed */ }
            if (m_Thread != null && m_Thread.IsAlive)
                m_Thread.Join(2000);
        }

        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        void AcceptLoop()
        {
            while (m_Running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = m_Listener.GetContext();
                }
                catch (Exception)
                {
                    break; // stopped
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        // ------------------------------------------------------------------------------------------
        // Dispatch

        struct Reply
        {
            public int status;
            public string contentType;
            public byte[] body;
            public string retryAfter;
        }

        void Handle(HttpListenerContext ctx)
        {
            var rec = new MockRequest
            {
                method = ctx.Request.HttpMethod,
                path = ctx.Request.Url.AbsolutePath,
                hadAuthorization = !string.IsNullOrEmpty(ctx.Request.Headers["Authorization"]),
                time = m_Clock.Elapsed.TotalSeconds,
            };
            Reply reply;
            try
            {
                byte[] body = ReadBody(ctx.Request);
                rec.bodyBytes = body.Length;
                reply = Route(ctx.Request, body, rec);
            }
            catch (Exception e)
            {
                rec.kind ??= "error";
                reply = Json(500, new JObject { ["error"] = new JObject { ["message"] = "mock server error: " + e.Message, ["code"] = "internal" } });
            }
            rec.status = reply.status;
            lock (m_Lock)
            {
                rec.index = m_Requests.Count;
                m_Requests.Add(rec);
            }
            Send(ctx, reply);
        }

        static byte[] ReadBody(HttpListenerRequest req)
        {
            if (!req.HasEntityBody)
                return Array.Empty<byte>();
            using (var ms = new MemoryStream())
            {
                req.InputStream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static void Send(HttpListenerContext ctx, Reply reply)
        {
            try
            {
                var resp = ctx.Response;
                resp.StatusCode = reply.status;
                resp.ContentType = reply.contentType ?? "application/json";
                resp.KeepAlive = false;
                if (!string.IsNullOrEmpty(reply.retryAfter))
                    resp.AddHeader("Retry-After", reply.retryAfter);
                byte[] body = reply.body ?? Array.Empty<byte>();
                resp.ContentLength64 = body.Length;
                if (body.Length > 0)
                    resp.OutputStream.Write(body, 0, body.Length);
                resp.OutputStream.Close();
                resp.Close();
            }
            catch (Exception)
            {
                // client went away (cancelled request): nothing to do
            }
        }

        Reply Route(HttpListenerRequest req, byte[] body, MockRequest rec)
        {
            string path = rec.path;
            if (path.StartsWith("/files/", StringComparison.Ordinal))
            {
                rec.kind = "file";
                return ServeFile(path.Substring("/files/".Length), rec);
            }
            if (!path.StartsWith("/api/v1/", StringComparison.Ordinal))
            {
                rec.kind = "unknown";
                return Html404();
            }
            if (body.Length > kMaxBody)
            {
                // the hosting layer's limit: plain text, not JSON
                rec.kind = "too-large";
                return new Reply { status = 413, contentType = "text/plain", body = Encoding.UTF8.GetBytes("FUNCTION_PAYLOAD_TOO_LARGE") };
            }
            if (req.Headers["Authorization"] != "Bearer " + ApiKey)
            {
                rec.kind = "unauthorized";
                return Json(401, OpenAIError("invalid api key", "unauthorized", "authentication_error"));
            }

            string rest = path.Substring("/api/v1/".Length);
            if (rest == "chat/completions" && rec.method == "POST")
                return Chat(body, rec);
            if (rest == "models" && rec.method == "GET")
            {
                rec.kind = "models";
                return Json(200, new JObject
                {
                    ["data"] = new JArray(new JObject { ["id"] = "google/gemini-3.5-flash-lite" }, new JObject { ["id"] = "google/gemini-3.8-flash" }),
                });
            }
            if (rest.StartsWith("media/requests/", StringComparison.Ordinal))
                return JobRequest(rest.Substring("media/requests/".Length), rec);
            if (rest.StartsWith("media/", StringComparison.Ordinal) && rec.method == "POST")
                return Submit(rest.Substring("media/".Length).Trim('/'), body, rec);

            rec.kind = "unknown";
            return Html404();
        }

        // ------------------------------------------------------------------------------------------
        // Chat

        Reply Chat(byte[] body, MockRequest rec)
        {
            JObject req = JObject.Parse(Encoding.UTF8.GetString(body));
            string model = (string)req["model"] ?? "";
            string schemaName = (string)req["response_format"]?["json_schema"]?["name"];
            rec.schemaName = schemaName;
            string system = "";
            string lastUser = "";
            var allUser = new StringBuilder();
            if (req["messages"] is JArray messages)
            {
                foreach (var m in messages)
                {
                    string role = (string)m["role"];
                    string text = MessageText(m["content"]);
                    if (role == "system")
                        system += text;
                    else if (role == "user")
                    {
                        lastUser = text;
                        allUser.AppendLine(text);
                    }
                }
            }

            string content;
            if (schemaName == "placement_decision" || system.Contains("scene-augmentation planner"))
            {
                rec.kind = "chat:decide";
                content = ReadFixtureText("decision.json");
            }
            else if (schemaName == "placement_verification" || system.Contains("You compare two images"))
            {
                rec.kind = "chat:verify";
                content = ReadFixtureText("verification.json");
            }
            else if (schemaName == "voice_turn" || system.Contains("push-to-talk"))
            {
                content = VoiceTurn(lastUser, allUser.ToString(), rec);
            }
            else
            {
                rec.kind = "chat:other";
                content = "OK";
            }

            int n = Interlocked.Increment(ref m_NextChat);
            return Json(200, new JObject
            {
                ["id"] = "chatcmpl-mock-" + n,
                ["object"] = "chat.completion",
                ["created"] = 0,
                ["model"] = model,
                ["choices"] = new JArray(new JObject
                {
                    ["index"] = 0,
                    ["message"] = new JObject { ["role"] = "assistant", ["content"] = content },
                    ["finish_reason"] = "stop",
                }),
                ["usage"] = new JObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 10, ["total_tokens"] = 20 },
            });
        }

        static string MessageText(JToken content)
        {
            if (content == null)
                return "";
            if (content.Type == JTokenType.String)
                return (string)content;
            var sb = new StringBuilder();
            if (content is JArray parts)
                foreach (var p in parts)
                    if ((string)p["type"] == "text")
                        sb.Append((string)p["text"]).Append('\n');
            return sb.ToString();
        }

        string VoiceTurn(string lastUser, string allUser, MockRequest rec)
        {
            if (lastUser.Contains("[NARRATE]"))
            {
                rec.kind = "chat:narrate";
                return Voice("", "Here is a quick progress update.", new JArray());
            }
            rec.kind = "chat:voice";
            if (allUser.Contains("GARBAGE_TEST"))
                return "Sure, I will add that right away! {not json"; // invalid on the first call AND the repair call

            string said = Quoted(lastUser);
            if (said.Contains("CLAMP_TEST"))
            {
                // violates the schema limits on purpose: the client must sanitize
                var objects = new JArray(
                    VoiceObject("lamp", "tall floor lamp", 9),
                    VoiceObject("", "an object without a name", 1),
                    VoiceObject("vase", "", 0),
                    VoiceObject("rug", "round rug", 2),
                    VoiceObject("stool", "wooden stool", 1),
                    VoiceObject("plant", "potted plant", 1));
                return Voice(said, "Adding some furniture.", new JArray(
                    CreateAction("Add some furniture.", objects, ""),
                    CreateAction("", new JArray(VoiceObject("  ", "", 1)), "x")));
            }
            if (said.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                return Voice(said, "Okay, stopping.", new JArray(new JObject
                {
                    ["type"] = "cancel", ["intent_summary"] = "", ["objects"] = new JArray(), ["placement_hint"] = "",
                }));
            if (said.IndexOf("chair", StringComparison.OrdinalIgnoreCase) >= 0 && !lastUser.Contains("[PIPELINE]"))
                return Voice(said, "Okay, I'm starting a red chair for you.", new JArray(
                    CreateAction("Add a red wooden chair standing on the floor in front of the camera.",
                        new JArray(VoiceObject("red chair", "a red wooden chair with a tall backrest", 1)),
                        "on the floor in front of the camera")));
            return Voice(said, "What would you like me to add?", new JArray());
        }

        // The typed-turn text quotes the user's words after "[TURN]" (pipeline notices may come first); fall back
        // to the whole text.
        static string Quoted(string text)
        {
            int turn = text.IndexOf("[TURN]", StringComparison.Ordinal);
            int a = text.IndexOf('"', turn >= 0 ? turn : 0);
            int b = a >= 0 ? text.IndexOf('"', a + 1) : -1;
            return a >= 0 && b > a ? text.Substring(a + 1, b - a - 1) : text.Trim();
        }

        static JObject VoiceObject(string name, string description, int count) =>
            new JObject { ["name"] = name, ["description"] = description, ["count"] = count };

        static JObject CreateAction(string intent, JArray objects, string hint) => new JObject
        {
            ["type"] = "create", ["intent_summary"] = intent, ["objects"] = objects, ["placement_hint"] = hint,
        };

        static string Voice(string transcript, string reply, JArray actions) =>
            new JObject { ["transcript"] = transcript, ["reply"] = reply, ["actions"] = actions }.ToString(Formatting.None);

        // ------------------------------------------------------------------------------------------
        // Media queue

        static string Classify(string modelPath, JObject input)
        {
            string p = modelPath.ToLowerInvariant();
            if (p.Contains("triposplat")) return "splat";
            if (p.Contains("rodin")) return p.Contains("text-to-3d") ? "mesh-text" : "mesh-image";
            if (p.Contains("sam-3")) return "segment";
            if (p.Contains("birefnet")) return "rembg";
            if (p.Contains("depth-anything")) return "depth";
            if (p.Contains("nano-banana"))
            {
                if (!p.EndsWith("/edit")) return "t2i";
                string prompt = (string)input["prompt"] ?? "";
                return prompt.StartsWith("Recreate the object", StringComparison.Ordinal) ? "enhance" : "edit";
            }
            return null;
        }

        Reply Submit(string modelPath, byte[] body, MockRequest rec)
        {
            rec.kind = "submit";
            rec.target = modelPath;
            JObject input;
            try
            {
                input = JObject.Parse(Encoding.UTF8.GetString(body));
            }
            catch (JsonException)
            {
                return Json(422, Detail("body", "Input should be a valid JSON object"));
            }

            string capability = Classify(modelPath, input);
            rec.capability = capability;
            // Tag the record before answering "not hosted" so tests can see the fall-through per capability.
            if (MissingSplatPath != null && string.Equals(modelPath, MissingSplatPath, StringComparison.OrdinalIgnoreCase))
                return Json(404, OpenAIError($"Model '{modelPath}' not found", "not_found", "invalid_request_error"));
            if (capability == null)
                return Json(404, OpenAIError($"Model '{modelPath}' not found", "not_found", "invalid_request_error"));

            // probes (and any other validation failure) are rejected at submit, like a real existing path
            JObject invalid = Validate(capability, input);
            if (invalid != null)
                return Json(422, invalid);

            lock (m_Lock)
            {
                if (capability == "edit" && RateLimitFirstEditSubmit && !m_Edit429Sent)
                {
                    m_Edit429Sent = true;
                    var r = Json(429, OpenAIError("Too many concurrent media requests", "rate_limited", "rate_limit_error"));
                    r.retryAfter = "1";
                    return r;
                }
            }

            var job = new Job
            {
                id = "req_" + Interlocked.Increment(ref m_NextJob).ToString("D4") + "_" + capability,
                modelPath = modelPath,
                capability = capability,
                result = ResultFor(capability),
            };
            lock (m_Lock)
            {
                job.hold = HoldJobsInQueue;
                job.failed = FailNextJob;
                FailNextJob = false;
                m_Jobs[job.id] = job;
            }
            string baseUrl = ApiBaseUrl + "/media/requests/" + job.id;
            return Json(200, new JObject
            {
                ["request_id"] = job.id,
                ["status"] = "IN_QUEUE",
                ["queue_position"] = 0,
                ["status_url"] = baseUrl + "/status",
                ["response_url"] = baseUrl,
                ["cancel_url"] = baseUrl + "/cancel",
            });
        }

        static JObject Validate(string capability, JObject input)
        {
            if (input.ToString(Formatting.None).Contains("__probe__"))
                return Detail("output_format", "Input should be 'png', 'jpeg' or 'ply'");
            bool Has(string key) => input[key] != null && input[key].Type == JTokenType.String && ((string)input[key]).Length > 0;
            bool HasList(string key) => input[key] is JArray a && a.Count > 0 && a[0].Type == JTokenType.String;
            switch (capability)
            {
                case "edit":
                case "enhance":
                    if (!Has("prompt")) return Detail("prompt", "Field required");
                    if (!HasList("image_urls")) return Detail("image_urls", "Field required");
                    return null;
                case "t2i":
                case "mesh-text":
                    return Has("prompt") ? null : Detail("prompt", "Field required");
                case "segment":
                    if (!Has("image_url")) return Detail("image_url", "Field required");
                    if (input["box_prompts"] is JArray boxes)
                        foreach (var b in boxes)
                            foreach (var k in new[] { "x_min", "y_min", "x_max", "y_max" })
                                if (b[k] == null || b[k].Type != JTokenType.Integer)
                                    return Detail("box_prompts." + k, "Input should be a valid integer");
                    return null;
                case "rembg":
                case "depth":
                    return Has("image_url") ? null : Detail("image_url", "Field required");
                case "splat":
                    if (!Has("image_url")) return Detail("image_url", "Field required");
                    var n = input["num_gaussians"];
                    if (n != null && (n.Type != JTokenType.Integer || (long)n < 32768 || (long)n > 262144))
                        return Detail("num_gaussians", "Input should be between 32768 and 262144");
                    return null;
                case "mesh-image":
                    return HasList("image_urls") || HasList("input_image_urls") ? null : Detail("image_urls", "Field required");
                default:
                    return null;
            }
        }

        JObject ResultFor(string capability)
        {
            string Url(string name) => Origin + "/files/" + name;
            switch (capability)
            {
                case "edit":
                    return new JObject { ["images"] = new JArray(new JObject { ["url"] = Url("edited.jpg"), ["width"] = 1280, ["height"] = 720, ["content_type"] = "image/jpeg" }), ["description"] = "" };
                case "enhance":
                    return new JObject { ["images"] = new JArray(new JObject { ["url"] = Url("enhanced.png"), ["content_type"] = "image/png" }), ["description"] = "" };
                case "t2i":
                    return new JObject { ["images"] = new JArray(new JObject { ["url"] = Url("generated.png"), ["content_type"] = "image/png" }), ["description"] = "" };
                case "segment":
                    return new JObject
                    {
                        ["masks"] = new JArray(new JObject { ["url"] = Url("cutout.png"), ["content_type"] = "image/png" }),
                        ["scores"] = new JArray(0.93),
                        ["boxes"] = new JArray(new JArray(0.5, 0.66, 0.12, 0.4)),
                    };
                case "rembg":
                    return new JObject { ["image"] = new JObject { ["url"] = Url("cutout.png"), ["content_type"] = "image/png" } };
                case "depth":
                    return new JObject { ["image"] = new JObject { ["url"] = Url("depth.png"), ["content_type"] = "image/png" } };
                case "splat":
                    return new JObject
                    {
                        ["model_mesh"] = new JObject { ["url"] = Url("object.ply"), ["content_type"] = "application/octet-stream", ["file_name"] = "model.ply" },
                        ["preprocessed_image"] = new JObject { ["url"] = Url("enhanced.png") },
                        ["num_gaussians"] = 8192,
                        ["seed"] = 42,
                    };
                default: // mesh-image / mesh-text
                    return new JObject
                    {
                        ["model_mesh"] = new JObject { ["url"] = Url("object.glb"), ["content_type"] = "model/gltf-binary", ["file_name"] = "model.glb" },
                        ["textures"] = new JArray(),
                        ["seed"] = 7,
                    };
            }
        }

        Reply JobRequest(string rest, MockRequest rec)
        {
            string[] parts = rest.Trim('/').Split('/');
            Job job;
            lock (m_Lock)
                m_Jobs.TryGetValue(parts[0], out job);
            rec.target = parts[0];
            if (parts.Length == 2 && parts[1] == "cancel")
            {
                rec.kind = "cancel";
                if (rec.method != "PUT")
                    return Json(405, OpenAIError("Method not allowed", "method_not_allowed", "invalid_request_error"));
                if (job != null)
                    lock (m_Lock) job.cancelled = true;
                return Json(job != null ? 200 : 404, new JObject { ["status"] = job != null ? "CANCELLATION_REQUESTED" : "NOT_FOUND" });
            }
            if (job == null)
            {
                rec.kind = parts.Length == 2 ? "status" : "result";
                return Json(404, OpenAIError("Request not found", "not_found", "invalid_request_error"));
            }
            rec.capability = job.capability;

            if (parts.Length == 2 && parts[1] == "status")
            {
                rec.kind = "status";
                lock (m_Lock)
                {
                    if (job.cancelled)
                        return Json(200, new JObject { ["status"] = "CANCELED" });
                    job.polls++;
                    if (job.hold || job.polls <= InQueuePolls)
                        return Json(200, new JObject { ["status"] = "IN_QUEUE", ["queue_position"] = Math.Max(0, InQueuePolls - job.polls) });
                    job.completed = true;
                    return Json(200, new JObject { ["status"] = job.failed ? "FAILED" : "COMPLETED" });
                }
            }

            rec.kind = "result";
            lock (m_Lock)
            {
                if (job.failed)
                    return Json(422, Detail("image_url", "Could not download the image"));
                if (!job.completed)
                    return Json(200, new JObject { ["detail"] = "Request is still in progress" });
                if (InProgressOnceAfterCompleted && !m_InProgressSent)
                {
                    // GenPresso quirk: the result can lag the COMPLETED status for a moment
                    m_InProgressSent = true;
                    return Json(200, new JObject { ["detail"] = "Request is still in progress" });
                }
            }
            return Json(200, job.result);
        }

        // ------------------------------------------------------------------------------------------
        // Files

        Reply ServeFile(string name, MockRequest rec)
        {
            rec.target = name;
            if (name.Contains("..") || name.Contains("/") || name.Contains("\\"))
                return Html404();
            string path = Path.Combine(m_FixturesDir, name);
            if (!File.Exists(path))
                return Html404();
            string ext = Path.GetExtension(name).ToLowerInvariant();
            string type = ext == ".jpg" ? "image/jpeg" : ext == ".png" ? "image/png" : ext == ".glb" ? "model/gltf-binary" :
                ext == ".json" ? "application/json" : "application/octet-stream";
            return new Reply { status = 200, contentType = type, body = File.ReadAllBytes(path) };
        }

        string ReadFixtureText(string name) => File.ReadAllText(Path.Combine(m_FixturesDir, name));

        // ------------------------------------------------------------------------------------------
        // Bodies

        static Reply Json(int status, JToken body) => new Reply
        {
            status = status,
            contentType = "application/json",
            body = Encoding.UTF8.GetBytes(body.ToString(Formatting.None)),
        };

        static Reply Html404() => new Reply
        {
            status = 404,
            contentType = "text/html",
            body = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><h1>404</h1>This page could not be found.</body></html>"),
        };

        static JObject OpenAIError(string message, string code, string type) =>
            new JObject { ["error"] = new JObject { ["message"] = message, ["code"] = code, ["type"] = type } };

        static JObject Detail(string field, string message) => new JObject
        {
            ["detail"] = new JArray(new JObject { ["loc"] = new JArray("body", field), ["msg"] = message, ["type"] = "value_error" }),
        };
    }
}
