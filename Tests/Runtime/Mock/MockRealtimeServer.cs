using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SplatPresso.Tests
{
    /// <summary>One client event received by <see cref="MockRealtimeServer"/>.</summary>
    public sealed class RealtimeClientEvent
    {
        /// <summary>Arrival order across all connections (0-based).</summary>
        public int index;
        /// <summary>Index of the accepted WebSocket connection it came in on (0-based, in accept order).</summary>
        public int connection;
        /// <summary>The event's <c>type</c> (<c>&lt;invalid&gt;</c> when the text was not a JSON object).</summary>
        public string type;
        /// <summary>The parsed event (null when invalid).</summary>
        public JObject json;
        /// <summary>Seconds since the server started.</summary>
        public double time;

        /// <summary>The client's <c>event_id</c>, if any.</summary>
        public string EventId => (string)json?["event_id"];

        public override string ToString() => $"#{index} c{connection} {type}" + (EventId != null ? $" ({EventId})" : "");
    }

    /// <summary>One server event sent by <see cref="MockRealtimeServer"/>.</summary>
    public sealed class RealtimeSentEvent
    {
        public int connection;
        public string type;
        public JObject json;
        /// <summary>Seconds since the server started (taken just before the frame was written).</summary>
        public double time;

        public override string ToString() => $"c{connection} -> {type}";
    }

    /// <summary>One HTTP request (WebSocket upgrade or plain GET) seen by <see cref="MockRealtimeServer"/>.</summary>
    public sealed class RealtimeHttpRecord
    {
        public int index;
        /// <summary>True for a WebSocket upgrade request.</summary>
        public bool upgrade;
        public string method;
        public string path;
        public string query;
        /// <summary><c>model</c> query parameter of an upgrade, or the model of a <c>/v1/models/{model}</c> probe.</summary>
        public string model;
        /// <summary>The raw Authorization header (tests use a fake key).</summary>
        public string authorization;
        /// <summary>Status answered (101 = upgrade accepted).</summary>
        public int status;
        /// <summary>Index of the WebSocket connection an accepted upgrade opened, else -1.</summary>
        public int connection = -1;
        public double time;

        public override string ToString() => $"#{index} {method} {path}{(string.IsNullOrEmpty(query) ? "" : "?" + query)} -> {status}" +
                                             (upgrade ? " [upgrade" + (connection >= 0 ? " c" + connection : "") + "]" : "");
    }

    /// <summary>
    /// In-process stand-in for the OpenAI Realtime API on <c>ws://127.0.0.1:&lt;free port&gt;/v1/realtime</c>, used by
    /// the PlayMode tests of <see cref="SplatPresso.Voice.OpenAIRealtimeBackend"/> and
    /// <see cref="SplatPresso.Voice.RealtimeSocket"/>. A minimal RFC 6455 server on a raw <see cref="TcpListener"/>
    /// (HttpListener's WebSocket support is not relied on): it answers the upgrade handshake, reads masked client
    /// frames (fragmentation, 16/64-bit lengths, ping/pong, close), and sends unmasked text frames (optionally
    /// fragmented, see <see cref="FragmentMessagesAbove"/>). Plain <c>GET /v1/models/{model}</c> answers
    /// <see cref="ModelProbeStatus"/> (the socket's diagnosis of a refused upgrade).
    /// </summary>
    /// <remarks>
    /// Scripted default behaviour, per connection (knobs below; set them before the traffic they affect):
    /// <list type="bullet">
    /// <item>connect: <c>session.created</c> (<see cref="SendSessionCreated"/>);</item>
    /// <item><c>session.update</c>: <c>session.updated</c> echoing the session (<see cref="AnswerSessionUpdate"/>,
    ///   <see cref="SessionUpdatedDelayMs"/>), or an <c>error</c> carrying the update's event_id
    ///   (<see cref="RejectSessionUpdates"/>);</item>
    /// <item><c>input_audio_buffer.commit</c>: <c>input_audio_buffer.committed {item_id}</c>, then after
    ///   <see cref="TranscriptDelayMs"/> <c>conversation.item.input_audio_transcription.completed</c>;</item>
    /// <item><c>response.create</c>: <c>response.created</c>, <see cref="AudioDeltaCount"/> x
    ///   <c>response.output_audio.delta</c> (PCM16 24 kHz), <c>response.output_audio_transcript.done</c>,
    ///   <c>response.done</c>. With <see cref="FunctionCallOnFirstResponse"/> the server's first response instead ends
    ///   with a <see cref="FunctionCallName"/> function_call item, and the response that follows a
    ///   <c>function_call_output</c> speaks <see cref="AckTranscript"/>;</item>
    /// <item><c>response.cancel</c>: ends the active response with status <c>cancelled</c> (only observable with
    ///   <see cref="ResponseDoneDelayMs"/>), else the benign <c>response_cancel_not_active</c> error.</item>
    /// </list>
    /// Every client event, sent event and HTTP request is recorded (thread-safe snapshots). Connections are served on
    /// background threads; nothing here touches Unity APIs.
    /// </remarks>
    public sealed class MockRealtimeServer : IDisposable
    {
        /// <summary>Default request_placement arguments (valid for VoiceRequestSanitizer).</summary>
        public const string DefaultPlacementArguments =
            "{\"intent_summary\":\"Add a red chair next to the sofa.\"," +
            "\"objects\":[{\"name\":\"red chair\",\"description\":\"a red wooden chair\",\"count\":2}]," +
            "\"placement_hint\":\"next to the sofa\"}";

        const string kWebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        const int kMaxHeadBytes = 16 * 1024;
        const int kMaxMessageBytes = 32 * 1024 * 1024;
        const int kSampleRate = 24000;

        const int kOpContinuation = 0x0, kOpText = 0x1, kOpBinary = 0x2, kOpClose = 0x8, kOpPing = 0x9, kOpPong = 0xA;

        readonly TcpListener m_Listener;
        readonly Thread m_AcceptThread;
        readonly object m_Lock = new object();
        readonly Stopwatch m_Clock = Stopwatch.StartNew();
        readonly List<Conn> m_Conns = new List<Conn>();
        readonly List<RealtimeClientEvent> m_Events = new List<RealtimeClientEvent>();
        readonly List<RealtimeSentEvent> m_Sent = new List<RealtimeSentEvent>();
        readonly List<RealtimeHttpRecord> m_Http = new List<RealtimeHttpRecord>();
        readonly List<int> m_CloseCodes = new List<int>();
        readonly List<int> m_Committed = new List<int>();
        readonly List<string> m_ProtocolErrors = new List<string>();
        volatile bool m_Running;
        int m_NextWs;
        int m_NextId;
        int m_ResponseCount;
        int m_RejectSessionUpdates;

        // ---- scenario knobs ----

        /// <summary>Non-zero: refuse every WebSocket upgrade with this HTTP status (and <see cref="RejectUpgradeBody"/>).</summary>
        public volatile int RejectUpgradeStatus;
        /// <summary>Body of a refused upgrade (null = an OpenAI-style error for the status).</summary>
        public volatile string RejectUpgradeBody;
        /// <summary>Status of <c>GET /v1/models/{model}</c>.</summary>
        public volatile int ModelProbeStatus = 200;
        /// <summary>Body of the models probe (null = a model object for 200, else an OpenAI-style error).</summary>
        public volatile string ModelProbeBody;
        /// <summary>Send <c>session.created</c> right after the handshake.</summary>
        public volatile bool SendSessionCreated = true;
        /// <summary>Answer <c>session.update</c> with <c>session.updated</c>.</summary>
        public volatile bool AnswerSessionUpdate = true;
        /// <summary>Delay before <c>session.updated</c>.</summary>
        public volatile int SessionUpdatedDelayMs;
        /// <summary>What the user "said" in every committed turn (null = no transcription event).</summary>
        public volatile string UserTranscript = "put a red chair next to the sofa";
        /// <summary>Delay between <c>input_audio_buffer.committed</c> and the transcription event.</summary>
        public volatile int TranscriptDelayMs = 200;
        /// <summary>Answer <c>response.create</c>.</summary>
        public volatile bool AnswerResponseCreate = true;
        /// <summary>Delay before <c>response.created</c>.</summary>
        public volatile int ResponseDelayMs;
        /// <summary>Delay between the transcript and <c>response.done</c> (the response stays active; cancel ends it).</summary>
        public volatile int ResponseDoneDelayMs;
        /// <summary>Audio deltas per spoken response.</summary>
        public volatile int AudioDeltaCount = 2;
        /// <summary>Seconds of PCM16 24 kHz audio per delta (1.5 s makes a text frame above 64 KB: 64-bit length).</summary>
        public volatile float AudioDeltaSeconds = 0.5f;
        /// <summary>Transcript of a normal spoken response.</summary>
        public volatile string ReplyTranscript = "Sure.";
        /// <summary>Transcript of the response that follows a function_call_output.</summary>
        public volatile string AckTranscript = "Okay, I started on that.";
        /// <summary>The server's first response ends with a function call instead of speech.</summary>
        public volatile bool FunctionCallOnFirstResponse;
        /// <summary>Tool the scripted function call names.</summary>
        public volatile string FunctionCallName = "request_placement";
        /// <summary>Arguments (a JSON string) of the scripted function call.</summary>
        public volatile string FunctionCallArguments = DefaultPlacementArguments;
        /// <summary>Non-zero: text messages longer than this are sent as a first frame plus continuation frames.</summary>
        public volatile int FragmentMessagesAbove;

        /// <summary>How many upcoming <c>session.update</c> events are answered with an error (counts down).</summary>
        public int RejectSessionUpdates
        {
            get => Volatile.Read(ref m_RejectSessionUpdates);
            set => Interlocked.Exchange(ref m_RejectSessionUpdates, value);
        }

        sealed class ActiveResponse
        {
            public string id;
            public volatile bool cancelled;
        }

        sealed class Conn
        {
            public TcpClient client;
            public NetworkStream stream;
            public readonly object writeLock = new object();
            public readonly object stateLock = new object();
            public volatile bool closed;
            public bool closeSent;          // under writeLock
            public int ws = -1;             // WebSocket connection index once upgraded
            public string model;
            public string sessionId;
            public ActiveResponse activeResponse; // under stateLock
            public int pendingAudioBytes;   // reader thread only
            public int pendingFunctionOutputs;

            public void Abort()
            {
                closed = true;
                try { client.Client.LingerState = new LingerOption(true, 0); } catch (Exception) { /* already closed */ }
                try { client.Close(); } catch (Exception) { /* already closed */ }
            }
        }

        /// <summary>Starts listening on a free loopback port.</summary>
        public MockRealtimeServer()
        {
            m_Listener = new TcpListener(IPAddress.Loopback, 0);
            m_Listener.Start();
            int port = ((IPEndPoint)m_Listener.LocalEndpoint).Port;
            Origin = "http://127.0.0.1:" + port;
            RealtimeUrl = "ws://127.0.0.1:" + port + "/v1/realtime?model=";
            m_Running = true;
            m_AcceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MockRealtimeServer" };
            m_AcceptThread.Start();
        }

        /// <summary><c>http://127.0.0.1:port</c></summary>
        public string Origin { get; }
        /// <summary><c>ws://127.0.0.1:port/v1/realtime?model=</c> (use as <c>OpenAIRealtimeBackend.EndpointOverride</c>).</summary>
        public string RealtimeUrl { get; }
        /// <summary>Seconds since the server started (the clock of every recorded time).</summary>
        public double Now => m_Clock.Elapsed.TotalSeconds;

        // ------------------------------------------------------------------------------------------
        // Records (thread-safe snapshots)

        /// <summary>Every client event so far.</summary>
        public List<RealtimeClientEvent> Events
        {
            get { lock (m_Lock) return new List<RealtimeClientEvent>(m_Events); }
        }

        /// <summary>Number of client events so far (a marker for <see cref="EventsFrom"/>).</summary>
        public int EventCount
        {
            get { lock (m_Lock) return m_Events.Count; }
        }

        /// <summary>Client events with index &gt;= <paramref name="fromIndex"/>, optionally of one type / connection.</summary>
        public List<RealtimeClientEvent> EventsFrom(int fromIndex, string type = null, int connection = -1)
        {
            var list = new List<RealtimeClientEvent>();
            foreach (var e in Events)
                if (e.index >= fromIndex && (type == null || e.type == type) && (connection < 0 || e.connection == connection))
                    list.Add(e);
            return list;
        }

        /// <summary>All client events of one type (optionally on one connection).</summary>
        public List<RealtimeClientEvent> EventsOfType(string type, int connection = -1) => EventsFrom(0, type, connection);

        /// <summary>Number of client events of one type since <paramref name="fromIndex"/>.</summary>
        public int CountEvents(string type, int fromIndex = 0) => EventsFrom(fromIndex, type).Count;

        /// <summary>Every server event sent so far.</summary>
        public List<RealtimeSentEvent> SentEvents
        {
            get { lock (m_Lock) return new List<RealtimeSentEvent>(m_Sent); }
        }

        /// <summary>Sent server events of one type (optionally on one connection).</summary>
        public List<RealtimeSentEvent> SentOfType(string type, int connection = -1)
        {
            var list = new List<RealtimeSentEvent>();
            foreach (var e in SentEvents)
                if (e.type == type && (connection < 0 || e.connection == connection))
                    list.Add(e);
            return list;
        }

        /// <summary>Number of sent server events of one type.</summary>
        public int CountSent(string type, int connection = -1) => SentOfType(type, connection).Count;

        /// <summary>Every HTTP request (upgrades and probes).</summary>
        public List<RealtimeHttpRecord> HttpRequests
        {
            get { lock (m_Lock) return new List<RealtimeHttpRecord>(m_Http); }
        }

        /// <summary>WebSocket upgrade requests so far (accepted or refused): the client's connection attempts.</summary>
        public int UpgradeAttempts => HttpRequests.FindAll(r => r.upgrade).Count;

        /// <summary>Upgrades that were accepted (WebSocket connections opened).</summary>
        public int AcceptedConnections
        {
            get { lock (m_Lock) return m_NextWs; }
        }

        /// <summary>The <c>GET /v1/models/{model}</c> requests so far.</summary>
        public List<RealtimeHttpRecord> ModelProbes => HttpRequests.FindAll(r => !r.upgrade && r.path != null && r.path.StartsWith("/v1/models/", StringComparison.Ordinal));

        /// <summary>WebSocket connections that are still open on the server side.</summary>
        public int OpenWebSockets
        {
            get
            {
                lock (m_Lock)
                {
                    int n = 0;
                    foreach (var c in m_Conns)
                        if (c.ws >= 0 && !c.closed)
                            n++;
                    return n;
                }
            }
        }

        /// <summary>Status codes of the close frames the client sent.</summary>
        public List<int> ClientCloseCodes
        {
            get { lock (m_Lock) return new List<int>(m_CloseCodes); }
        }

        /// <summary>Decoded PCM bytes appended before each <c>input_audio_buffer.commit</c>.</summary>
        public List<int> CommittedAudioBytes
        {
            get { lock (m_Lock) return new List<int>(m_Committed); }
        }

        /// <summary>RFC 6455 / JSON violations by the client (unmasked frames, bad continuation, invalid JSON...).</summary>
        public List<string> ProtocolErrors
        {
            get { lock (m_Lock) return new List<string>(m_ProtocolErrors); }
        }

        /// <summary>HTTP requests, client events and sent event types (for assertion messages).</summary>
        public string Describe(int fromEvent = 0)
        {
            var sb = new StringBuilder();
            foreach (var r in HttpRequests)
                sb.AppendLine(r.ToString());
            foreach (var e in Events)
                if (e.index >= fromEvent)
                    sb.AppendLine(e.ToString());
            var sent = SentEvents;
            sb.Append("sent:");
            foreach (var s in sent)
                sb.Append(' ').Append(s);
            foreach (var p in ProtocolErrors)
                sb.AppendLine().Append("PROTOCOL: ").Append(p);
            return sb.ToString();
        }

        /// <summary>Base64 PCM16 little-endian mono 24 kHz: a quiet sine of <paramref name="seconds"/>.</summary>
        public static string Pcm16Base64(float seconds, float hz = 220f)
        {
            int n = Math.Max(1, (int)Math.Round(seconds * kSampleRate));
            var bytes = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                short s = (short)(Math.Sin(2.0 * Math.PI * hz * i / kSampleRate) * 0.3 * short.MaxValue);
                bytes[i * 2] = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return Convert.ToBase64String(bytes);
        }

        // ------------------------------------------------------------------------------------------
        // Control

        /// <summary>
        /// Drops every open WebSocket: <paramref name="graceful"/> sends a close frame (1001 going away) and waits for
        /// the client's reply; otherwise the TCP connection is reset (a network drop).
        /// </summary>
        public void DropAllConnections(bool graceful)
        {
            List<Conn> open;
            lock (m_Lock)
                open = m_Conns.FindAll(c => c.ws >= 0 && !c.closed);
            foreach (var c in open)
            {
                if (!graceful)
                {
                    c.Abort();
                    continue;
                }
                SendClose(c, 1001, "going away");
                var conn = c;
                Later(conn, 3000, conn.Abort); // the client never answered the close
            }
        }

        /// <summary>Sends an arbitrary server event on every open WebSocket.</summary>
        public void Broadcast(JObject serverEvent)
        {
            List<Conn> open;
            lock (m_Lock)
                open = m_Conns.FindAll(c => c.ws >= 0 && !c.closed);
            foreach (var c in open)
                SendEvent(c, (JObject)serverEvent.DeepClone());
        }

        public void Dispose()
        {
            m_Running = false;
            try { m_Listener.Stop(); } catch (Exception) { /* already stopped */ }
            List<Conn> all;
            lock (m_Lock)
                all = new List<Conn>(m_Conns);
            foreach (var c in all)
                c.Abort();
            if (m_AcceptThread.IsAlive)
                m_AcceptThread.Join(2000);
        }

        // ------------------------------------------------------------------------------------------
        // Accept / HTTP

        void AcceptLoop()
        {
            while (m_Running)
            {
                TcpClient client;
                try
                {
                    client = m_Listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    break; // stopped
                }
                var c = new Conn { client = client };
                if (!m_Running)
                {
                    c.Abort();
                    break;
                }
                lock (m_Lock)
                    m_Conns.Add(c);
                new Thread(() => Serve(c)) { IsBackground = true, Name = "MockRealtimeServer connection" }.Start();
            }
        }

        void Serve(Conn c)
        {
            try
            {
                c.client.NoDelay = true;
                c.stream = c.client.GetStream();
                string head = ReadHead(c.stream);
                if (head == null)
                    return;
                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var requestLine = lines[0].Split(' ');
                string method = requestLine[0];
                string target = requestLine.Length > 1 ? requestLine[1] : "/";
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0)
                        continue;
                    string name = lines[i].Substring(0, colon).Trim();
                    string value = lines[i].Substring(colon + 1).Trim();
                    headers[name] = headers.TryGetValue(name, out string prev) ? prev + ", " + value : value;
                }
                int q = target.IndexOf('?');
                var rec = new RealtimeHttpRecord
                {
                    method = method,
                    path = q >= 0 ? target.Substring(0, q) : target,
                    query = q >= 0 ? target.Substring(q + 1) : "",
                    authorization = headers.TryGetValue("Authorization", out string auth) ? auth : null,
                    time = Now,
                };
                rec.upgrade = headers.TryGetValue("Upgrade", out string upgrade) && upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0;

                if (rec.upgrade)
                    ServeWebSocket(c, rec, headers);
                else
                    ServeHttp(c, rec);
            }
            catch (Exception)
            {
                // the client went away or the server stopped
            }
            finally
            {
                c.closed = true;
                try { c.client.Close(); } catch (Exception) { /* already closed */ }
            }
        }

        void Record(RealtimeHttpRecord rec)
        {
            lock (m_Lock)
            {
                rec.index = m_Http.Count;
                m_Http.Add(rec);
            }
        }

        void ServeHttp(Conn c, RealtimeHttpRecord rec)
        {
            const string modelsPrefix = "/v1/models/";
            if (rec.method == "GET" && rec.path.StartsWith(modelsPrefix, StringComparison.Ordinal))
            {
                rec.model = Uri.UnescapeDataString(rec.path.Substring(modelsPrefix.Length));
                rec.status = ModelProbeStatus;
                Record(rec);
                string body = ModelProbeBody ?? (rec.status == 200
                    ? new JObject { ["id"] = rec.model, ["object"] = "model", ["created"] = 1700000000, ["owned_by"] = "system" }.ToString(Formatting.None)
                    : OpenAIError(rec.status));
                WriteHttpAndClose(c, rec.status, body);
                return;
            }
            rec.status = 404;
            Record(rec);
            WriteHttpAndClose(c, 404, OpenAIError(404));
        }

        void ServeWebSocket(Conn c, RealtimeHttpRecord rec, Dictionary<string, string> headers)
        {
            rec.model = QueryValue(rec.query, "model");
            int reject = RejectUpgradeStatus;
            if (reject != 0)
            {
                rec.status = reject;
                Record(rec);
                WriteHttpAndClose(c, reject, RejectUpgradeBody ?? OpenAIError(reject));
                return;
            }
            if (!headers.TryGetValue("Sec-WebSocket-Key", out string key) || string.IsNullOrWhiteSpace(key))
            {
                rec.status = 400;
                Record(rec);
                AddProtocolError("upgrade without Sec-WebSocket-Key");
                WriteHttpAndClose(c, 400, OpenAIError(400));
                return;
            }
            if (!headers.TryGetValue("Sec-WebSocket-Version", out string version) || version.Trim() != "13")
                AddProtocolError("upgrade without Sec-WebSocket-Version: 13");

            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + kWebSocketGuid)));
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");

            lock (m_Lock)
            {
                c.ws = m_NextWs++;
                rec.connection = c.ws;
                rec.status = 101;
                rec.index = m_Http.Count;
                m_Http.Add(rec);
            }
            c.model = rec.model;
            c.sessionId = NewId("sess");
            lock (c.writeLock)
            {
                c.stream.Write(response, 0, response.Length);
                c.stream.Flush();
            }

            if (SendSessionCreated)
            {
                SendEvent(c, new JObject
                {
                    ["type"] = "session.created",
                    ["session"] = new JObject
                    {
                        ["object"] = "realtime.session",
                        ["type"] = "realtime",
                        ["id"] = c.sessionId,
                        ["model"] = c.model,
                    },
                });
            }
            ReceiveLoop(c);
        }

        void WriteHttpAndClose(Conn c, int status, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
            byte[] head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n");
            lock (c.writeLock)
            {
                c.stream.Write(head, 0, head.Length);
                c.stream.Write(bodyBytes, 0, bodyBytes.Length);
                c.stream.Flush();
            }
            // graceful: FIN, then wait (bounded) for the client to close its side so nothing is reset
            try { c.client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { /* already closed */ }
            try
            {
                c.client.ReceiveTimeout = 1000;
                var sink = new byte[512];
                while (c.stream.Read(sink, 0, sink.Length) > 0) { }
            }
            catch (Exception)
            {
                // timeout or reset: closing anyway
            }
        }

        static string ReadHead(Stream s)
        {
            var buf = new List<byte>(512);
            while (buf.Count < kMaxHeadBytes)
            {
                int b = s.ReadByte(); // byte by byte: never read past the head
                if (b < 0)
                    return null;
                buf.Add((byte)b);
                int n = buf.Count;
                if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
                    return Encoding.ASCII.GetString(buf.ToArray(), 0, n - 4);
            }
            return null;
        }

        static string QueryValue(string query, string name)
        {
            if (string.IsNullOrEmpty(query))
                return null;
            foreach (string part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == name)
                    return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 101: return "Switching Protocols";
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Error";
            }
        }

        static string OpenAIError(int status)
        {
            string message, type, code;
            switch (status)
            {
                case 401: message = "Incorrect API key provided: sk-test***. You can find your API key at https://platform.openai.com/account/api-keys."; type = "invalid_request_error"; code = "invalid_api_key"; break;
                case 403: message = "You are not allowed to use this model."; type = "invalid_request_error"; code = "model_not_allowed"; break;
                case 404: message = "The model does not exist or you do not have access to it."; type = "invalid_request_error"; code = "model_not_found"; break;
                case 429: message = "You exceeded your current quota."; type = "insufficient_quota"; code = "insufficient_quota"; break;
                default: message = "The server is unavailable."; type = "server_error"; code = null; break;
            }
            return new JObject
            {
                ["error"] = new JObject { ["message"] = message, ["type"] = type, ["param"] = null, ["code"] = code },
            }.ToString(Formatting.None);
        }

        // ------------------------------------------------------------------------------------------
        // WebSocket frames

        sealed class Frame
        {
            public bool fin;
            public int opcode;
            public bool masked;
            public byte[] payload;
        }

        void ReceiveLoop(Conn c)
        {
            var message = new MemoryStream();
            int messageOpcode = -1;
            while (m_Running && !c.closed)
            {
                var f = ReadFrame(c);
                if (f == null)
                    return; // EOF / reset
                if (!f.masked)
                    AddProtocolError($"c{c.ws}: unmasked client frame (opcode {f.opcode})");
                switch (f.opcode)
                {
                    case kOpClose:
                    {
                        int code = f.payload.Length >= 2 ? (f.payload[0] << 8) | f.payload[1] : 1005;
                        lock (m_Lock)
                            m_CloseCodes.Add(code);
                        SendClose(c, code == 1005 ? 1000 : code, null); // echo (no-op when the server started the close)
                        return;
                    }
                    case kOpPing:
                        WriteFrame(c, kOpPong, f.payload);
                        break;
                    case kOpPong:
                        break; // keep-alive
                    case kOpText:
                    case kOpBinary:
                    case kOpContinuation:
                    {
                        if (f.opcode == kOpContinuation)
                        {
                            if (messageOpcode < 0)
                            {
                                AddProtocolError($"c{c.ws}: continuation frame without a started message");
                                SendClose(c, 1002, "protocol error");
                                return;
                            }
                        }
                        else if (messageOpcode >= 0)
                        {
                            AddProtocolError($"c{c.ws}: new data frame inside a fragmented message");
                            SendClose(c, 1002, "protocol error");
                            return;
                        }
                        else
                        {
                            messageOpcode = f.opcode;
                        }
                        message.Write(f.payload, 0, f.payload.Length);
                        if (message.Length > kMaxMessageBytes)
                        {
                            AddProtocolError($"c{c.ws}: message above {kMaxMessageBytes} bytes");
                            SendClose(c, 1009, "too big");
                            return;
                        }
                        if (!f.fin)
                            break;
                        byte[] data = message.ToArray();
                        int op = messageOpcode;
                        message.SetLength(0);
                        messageOpcode = -1;
                        if (op == kOpText)
                            OnTextMessage(c, Encoding.UTF8.GetString(data));
                        else
                            AddProtocolError($"c{c.ws}: binary message ({data.Length} bytes)");
                        break;
                    }
                    default:
                        AddProtocolError($"c{c.ws}: unknown opcode {f.opcode}");
                        SendClose(c, 1002, "protocol error");
                        return;
                }
            }
        }

        static Frame ReadFrame(Conn c)
        {
            byte[] h = ReadExact(c.stream, 2);
            if (h == null)
                return null;
            var f = new Frame { fin = (h[0] & 0x80) != 0, opcode = h[0] & 0x0F, masked = (h[1] & 0x80) != 0 };
            long len = h[1] & 0x7F;
            if (len == 126)
            {
                byte[] b = ReadExact(c.stream, 2);
                if (b == null)
                    return null;
                len = (b[0] << 8) | b[1];
            }
            else if (len == 127)
            {
                byte[] b = ReadExact(c.stream, 8);
                if (b == null)
                    return null;
                len = 0;
                for (int i = 0; i < 8; i++)
                    len = (len << 8) | b[i];
            }
            if (len < 0 || len > kMaxMessageBytes)
                throw new IOException("frame length " + len);
            byte[] mask = null;
            if (f.masked)
            {
                mask = ReadExact(c.stream, 4);
                if (mask == null)
                    return null;
            }
            f.payload = len == 0 ? Array.Empty<byte>() : ReadExact(c.stream, (int)len);
            if (f.payload == null)
                return null;
            if (mask != null)
                for (int i = 0; i < f.payload.Length; i++)
                    f.payload[i] ^= mask[i & 3];
            return f;
        }

        static byte[] ReadExact(Stream s, int count)
        {
            var buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0)
                    return null;
                got += n;
            }
            return buf;
        }

        // Writes one unmasked frame (caller holds c.writeLock).
        static void WriteFrameLocked(Conn c, int opcode, bool fin, byte[] payload, int offset, int count)
        {
            byte b0 = (byte)((fin ? 0x80 : 0) | opcode);
            byte[] header;
            if (count < 126)
            {
                header = new[] { b0, (byte)count };
            }
            else if (count <= 0xFFFF)
            {
                header = new[] { b0, (byte)126, (byte)(count >> 8), (byte)count };
            }
            else
            {
                header = new byte[10];
                header[0] = b0;
                header[1] = 127;
                long len = count;
                for (int i = 0; i < 8; i++)
                    header[9 - i] = (byte)(len >> (8 * i));
            }
            c.stream.Write(header, 0, header.Length);
            if (count > 0)
                c.stream.Write(payload, offset, count);
        }

        static bool WriteFrame(Conn c, int opcode, byte[] payload)
        {
            try
            {
                lock (c.writeLock)
                {
                    if (c.closeSent)
                        return false;
                    WriteFrameLocked(c, opcode, true, payload, 0, payload.Length);
                    c.stream.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                return false; // the client went away
            }
        }

        static void SendClose(Conn c, int code, string reason)
        {
            byte[] r = Encoding.UTF8.GetBytes(reason ?? "");
            var payload = new byte[2 + r.Length];
            payload[0] = (byte)(code >> 8);
            payload[1] = (byte)code;
            Buffer.BlockCopy(r, 0, payload, 2, r.Length);
            try
            {
                lock (c.writeLock)
                {
                    if (c.closeSent)
                        return;
                    c.closeSent = true;
                    WriteFrameLocked(c, kOpClose, true, payload, 0, payload.Length);
                    c.stream.Flush();
                }
            }
            catch (Exception)
            {
                // the client went away
            }
        }

        /// <summary>Sends a server event (event_id added) as one text message; false when the connection is gone.</summary>
        bool SendEvent(Conn c, JObject e)
        {
            if (e["event_id"] == null)
                e["event_id"] = NewId("event");
            byte[] bytes = Encoding.UTF8.GetBytes(e.ToString(Formatting.None));
            int frag = FragmentMessagesAbove;
            try
            {
                lock (c.writeLock)
                {
                    if (c.closeSent || c.closed)
                        return false;
                    lock (m_Lock)
                        m_Sent.Add(new RealtimeSentEvent { connection = c.ws, type = (string)e["type"], json = e, time = Now });
                    if (frag <= 0 || bytes.Length <= frag)
                    {
                        WriteFrameLocked(c, kOpText, true, bytes, 0, bytes.Length);
                    }
                    else
                    {
                        for (int off = 0; off < bytes.Length; off += frag)
                        {
                            int n = Math.Min(frag, bytes.Length - off);
                            WriteFrameLocked(c, off == 0 ? kOpText : kOpContinuation, off + n >= bytes.Length, bytes, off, n);
                        }
                    }
                    c.stream.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                return false; // the client went away
            }
        }

        // ------------------------------------------------------------------------------------------
        // Scripted Realtime behaviour

        void OnTextMessage(Conn c, string text)
        {
            JObject e = null;
            try
            {
                e = JObject.Parse(text);
            }
            catch (Exception ex)
            {
                AddProtocolError($"c{c.ws}: invalid JSON ({ex.Message})");
            }
            string type = e != null ? (string)e["type"] ?? "<no type>" : "<invalid>";
            lock (m_Lock)
                m_Events.Add(new RealtimeClientEvent { index = m_Events.Count, connection = c.ws, type = type, json = e, time = Now });
            if (e == null)
                return;

            switch (type)
            {
                case "session.update":
                    OnSessionUpdate(c, e);
                    break;

                case "input_audio_buffer.append":
                    try
                    {
                        c.pendingAudioBytes += Convert.FromBase64String((string)e["audio"] ?? "").Length;
                    }
                    catch (FormatException)
                    {
                        AddProtocolError($"c{c.ws}: append with invalid base64");
                    }
                    break;

                case "input_audio_buffer.clear":
                    c.pendingAudioBytes = 0;
                    SendEvent(c, new JObject { ["type"] = "input_audio_buffer.cleared" });
                    break;

                case "input_audio_buffer.commit":
                    OnCommit(c, e);
                    break;

                case "conversation.item.create":
                {
                    var item = e["item"] as JObject;
                    string itemType = (string)item?["type"];
                    if (itemType == "function_call_output")
                        Interlocked.Increment(ref c.pendingFunctionOutputs);
                    SendEvent(c, new JObject
                    {
                        ["type"] = "conversation.item.added",
                        ["previous_item_id"] = null,
                        ["item"] = new JObject
                        {
                            ["id"] = NewId("item"),
                            ["object"] = "realtime.item",
                            ["type"] = itemType,
                            ["role"] = item?["role"]?.DeepClone(),
                            ["status"] = "completed",
                        },
                    });
                    break;
                }

                case "response.create":
                    OnResponseCreate(c, e);
                    break;

                case "response.cancel":
                {
                    ActiveResponse active;
                    lock (c.stateLock)
                        active = c.activeResponse;
                    if (active != null)
                        active.cancelled = true; // its streamer ends it with status "cancelled"
                    else
                        SendError(c, "invalid_request_error", "response_cancel_not_active", "Cancellation failed: no active response found", e);
                    break;
                }
            }
        }

        void OnSessionUpdate(Conn c, JObject e)
        {
            if (TryConsumeSessionRejection())
            {
                string voice = (string)e["session"]?["audio"]?["output"]?["voice"];
                SendError(c, "invalid_request_error", "invalid_value",
                    $"Invalid value: '{voice}'. Supported values are: 'alloy', 'ash', 'ballad', 'coral', 'echo', 'sage', 'shimmer', 'verse', 'marin', and 'cedar'.",
                    e, "session.audio.output.voice");
                return;
            }
            if (!AnswerSessionUpdate)
                return;
            var session = e["session"] is JObject s ? (JObject)s.DeepClone() : new JObject();
            session["object"] = "realtime.session";
            session["id"] = c.sessionId;
            session["model"] = c.model;
            Later(c, SessionUpdatedDelayMs, () => SendEvent(c, new JObject { ["type"] = "session.updated", ["session"] = session }));
        }

        bool TryConsumeSessionRejection()
        {
            while (true)
            {
                int left = Volatile.Read(ref m_RejectSessionUpdates);
                if (left <= 0)
                    return false;
                if (Interlocked.CompareExchange(ref m_RejectSessionUpdates, left - 1, left) == left)
                    return true;
            }
        }

        void OnCommit(Conn c, JObject e)
        {
            if (c.pendingAudioBytes == 0)
            {
                SendError(c, "invalid_request_error", "input_audio_buffer_commit_empty",
                    "Error committing input audio buffer: buffer too small.", e);
                return;
            }
            lock (m_Lock)
                m_Committed.Add(c.pendingAudioBytes);
            c.pendingAudioBytes = 0;
            string itemId = NewId("item");
            SendEvent(c, new JObject { ["type"] = "input_audio_buffer.committed", ["previous_item_id"] = null, ["item_id"] = itemId });
            string transcript = UserTranscript;
            if (transcript == null)
                return;
            Later(c, TranscriptDelayMs, () => SendEvent(c, new JObject
            {
                ["type"] = "conversation.item.input_audio_transcription.completed",
                ["item_id"] = itemId,
                ["content_index"] = 0,
                ["transcript"] = transcript,
            }));
        }

        void OnResponseCreate(Conn c, JObject e)
        {
            if (!AnswerResponseCreate)
                return;
            ActiveResponse r;
            lock (c.stateLock)
            {
                if (c.activeResponse != null)
                {
                    r = null;
                }
                else
                {
                    r = new ActiveResponse { id = NewId("resp") };
                    c.activeResponse = r;
                }
            }
            if (r == null)
            {
                SendError(c, "invalid_request_error", "conversation_already_has_active_response",
                    "Conversation already has an active response in progress.", e);
                return;
            }
            int n = Interlocked.Increment(ref m_ResponseCount); // server-wide, 1-based
            bool functionCall = FunctionCallOnFirstResponse && n == 1;
            bool ack = !functionCall && Interlocked.Exchange(ref c.pendingFunctionOutputs, 0) > 0;
            if (ResponseDelayMs > 0 || ResponseDoneDelayMs > 0)
                Later(c, 0, () => StreamResponse(c, r, functionCall, ack), forceBackground: true);
            else
                StreamResponse(c, r, functionCall, ack);
        }

        void StreamResponse(Conn c, ActiveResponse r, bool functionCall, bool ack)
        {
            int delay = ResponseDelayMs;
            if (delay > 0)
                Thread.Sleep(delay);
            SendEvent(c, new JObject
            {
                ["type"] = "response.created",
                ["response"] = new JObject { ["id"] = r.id, ["object"] = "realtime.response", ["status"] = "in_progress", ["output"] = new JArray() },
            });
            string itemId = NewId("item");
            var output = new JArray();
            if (functionCall)
            {
                string callId = NewId("call");
                string name = FunctionCallName;
                string args = FunctionCallArguments;
                SendEvent(c, new JObject
                {
                    ["type"] = "response.function_call_arguments.done",
                    ["response_id"] = r.id,
                    ["item_id"] = itemId,
                    ["output_index"] = 0,
                    ["call_id"] = callId,
                    ["name"] = name,
                    ["arguments"] = args,
                });
                output.Add(new JObject
                {
                    ["id"] = itemId,
                    ["object"] = "realtime.item",
                    ["type"] = "function_call",
                    ["status"] = "completed",
                    ["name"] = name,
                    ["call_id"] = callId,
                    ["arguments"] = args,
                });
            }
            else
            {
                string transcript = ack ? AckTranscript : ReplyTranscript;
                int deltas = AudioDeltaCount;
                string delta = deltas > 0 ? Pcm16Base64(AudioDeltaSeconds) : null;
                for (int i = 0; i < deltas && !r.cancelled; i++)
                {
                    SendEvent(c, new JObject
                    {
                        ["type"] = "response.output_audio.delta",
                        ["response_id"] = r.id,
                        ["item_id"] = itemId,
                        ["output_index"] = 0,
                        ["content_index"] = 0,
                        ["delta"] = delta,
                    });
                }
                if (!r.cancelled && transcript != null)
                {
                    SendEvent(c, new JObject
                    {
                        ["type"] = "response.output_audio_transcript.done",
                        ["response_id"] = r.id,
                        ["item_id"] = itemId,
                        ["output_index"] = 0,
                        ["content_index"] = 0,
                        ["transcript"] = transcript,
                    });
                }
                var wait = Stopwatch.StartNew();
                int doneDelay = ResponseDoneDelayMs;
                while (!r.cancelled && m_Running && !c.closed && wait.ElapsedMilliseconds < doneDelay)
                    Thread.Sleep(10);
                output.Add(new JObject
                {
                    ["id"] = itemId,
                    ["object"] = "realtime.item",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["status"] = r.cancelled ? "incomplete" : "completed",
                    ["content"] = new JArray(new JObject { ["type"] = "output_audio", ["transcript"] = transcript }),
                });
            }
            // cleared before response.done goes out: the client may answer it with the next response.create at once
            lock (c.stateLock)
                if (c.activeResponse == r)
                    c.activeResponse = null;
            SendEvent(c, new JObject
            {
                ["type"] = "response.done",
                ["response"] = new JObject
                {
                    ["id"] = r.id,
                    ["object"] = "realtime.response",
                    ["status"] = r.cancelled ? "cancelled" : "completed",
                    ["output"] = output,
                },
            });
        }

        void SendError(Conn c, string type, string code, string message, JObject clientEvent, string param = null)
        {
            SendEvent(c, new JObject
            {
                ["type"] = "error",
                ["error"] = new JObject
                {
                    ["type"] = type,
                    ["code"] = code,
                    ["message"] = message,
                    ["param"] = param,
                    ["event_id"] = (string)clientEvent?["event_id"],
                },
            });
        }

        // Runs inline when delayMs <= 0 (keeps the event order of the reader thread), else on the thread pool.
        void Later(Conn c, int delayMs, Action action, bool forceBackground = false)
        {
            if (delayMs <= 0 && !forceBackground)
            {
                action();
                return;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (delayMs > 0)
                    Thread.Sleep(delayMs);
                if (!m_Running || c.closed)
                    return;
                try
                {
                    action();
                }
                catch (Exception)
                {
                    // the connection went away meanwhile
                }
            });
        }

        string NewId(string prefix) => prefix + "_" + Interlocked.Increment(ref m_NextId);

        void AddProtocolError(string what)
        {
            lock (m_Lock)
                m_ProtocolErrors.Add(what);
        }
    }
}
