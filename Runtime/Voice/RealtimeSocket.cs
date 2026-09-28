#if !UNITY_WEBGL || UNITY_EDITOR
#define SPLATPRESSO_WEBSOCKETS
#endif
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SplatPresso.Api;
using UnityEngine;
#if SPLATPRESSO_WEBSOCKETS
using System.Net.WebSockets;
#endif

namespace SplatPresso.Voice
{
    /// <summary>
    /// JSON-over-WebSocket transport for the OpenAI Realtime API on <c>System.Net.WebSockets.ClientWebSocket</c>,
    /// with automatic reconnection. Not available on WebGL (<see cref="IsSupported"/> is false there).
    /// </summary>
    /// <remarks>
    /// Threading: one background task per connection runs ConnectAsync, a receive loop and a single sender loop.
    /// Everything they produce (open, parsed server events, close) goes through one ordered queue that
    /// <see cref="Tick"/> drains on the main thread, so every callback fires on the main thread. Each connection
    /// has a generation number; items from a replaced or closed connection are dropped (this replaces the old
    /// per-callback "is this still my socket" guard).
    ///
    /// Send ordering is critical: a Realtime turn is append... then commit, then the image item, then
    /// response.create, and ClientWebSocket throws if two SendAsync calls overlap. So sends go into a queue
    /// consumed by exactly one sender loop (a semaphore is used only as a counter signal, never as a mutex around
    /// concurrent callers: waiter FIFO order is not guaranteed on Mono).
    ///
    /// Reconnect: backoff 1, 2, 4, 8, 15 s, then every 30 s for as long as it takes (network trouble heals; the
    /// original also retried forever). Failures that retrying cannot fix end in <see cref="SocketState.Failed"/> at
    /// once: a rejected key / model (<see cref="FailureKind.Auth"/>) and missing runtime support such as a stripped
    /// System.Configuration (<see cref="FailureKind.Runtime"/>). Unity's ClientWebSocket reports an HTTP rejection of
    /// the upgrade (401, 403, 404, 429...) only as a bare "Unable to connect to the remote server" WebSocketException,
    /// so on the first such rejection the socket asks <c>GET /v1/models/{model}</c> with the same key what is wrong.
    ///
    /// Lifetime: projects often disable domain reload, so a background receive task could outlive play mode and
    /// post into destroyed objects. Owners call <see cref="Abort"/> from OnDisable/OnDestroy, and every live socket
    /// is also aborted on Application.quitting and at SubsystemRegistration.
    /// </remarks>
    public sealed class RealtimeSocket : IDisposable
    {
        /// <summary>Connection state.</summary>
        public enum SocketState { Idle, Connecting, Open, Reconnecting, Closed, Failed }

        /// <summary>State names passed to <see cref="OnStateChanged"/>.</summary>
        public const string StateConnected = "connected", StateReconnected = "reconnected",
            StateReconnecting = "reconnecting", StateClosed = "closed", StateFailed = "failed";

        /// <summary>A parsed server event (main thread).</summary>
        public event Action<JObject> OnServerEvent;
        /// <summary>connected / reconnected / reconnecting / closed / failed (main thread).</summary>
        public event Action<string> OnStateChanged;

        /// <summary>Why the socket ended in <see cref="SocketState.Failed"/>.</summary>
        public enum FailureKind
        {
            /// <summary>Not failed.</summary>
            None,
            /// <summary>The key or the model was rejected (401/403/404, exhausted quota): check the key and model.</summary>
            Auth,
            /// <summary>This build cannot open WebSockets / TLS (e.g. managed code stripping removed what it needs).</summary>
            Runtime,
            /// <summary>Anything else (invalid URL, <see cref="MaxReconnectAttempts"/> reached).</summary>
            Other,
        }

        /// <summary>Consecutive failed (re)connects before giving up; 0 (default) = keep retrying (every 30 s after the fast backoff).</summary>
        public int MaxReconnectAttempts = 0;

        /// <summary>Why <see cref="State"/> is Failed (None otherwise).</summary>
        public FailureKind LastFailureKind { get; private set; }

        /// <summary>Current state.</summary>
        public SocketState State { get; private set; } = SocketState.Idle;
        /// <summary>True while the socket is open and sends go out.</summary>
        public bool IsConnected => State == SocketState.Open;

        /// <summary>True once any connection of the current <see cref="Connect"/> has opened.</summary>
        public bool EverOpened => m_EverOpened;

        /// <summary>Seconds since the open connection last received or finished sending a frame (MaxValue when not open).</summary>
        public double SecondsSinceActivity
        {
            get
            {
#if SPLATPRESSO_WEBSOCKETS
                var c = m_Conn;
                if (c == null || State != SocketState.Open)
                    return double.MaxValue;
                long ticks = Interlocked.Read(ref c.lastActivityTicks);
                return ticks == 0 ? double.MaxValue : (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
#else
                return double.MaxValue;
#endif
            }
        }
        /// <summary>Last close / failure reason (never contains the key).</summary>
        public string LastError { get; private set; }

        /// <summary>False on WebGL, where System.Net.WebSockets is unavailable.</summary>
        public static bool IsSupported
        {
            get
            {
#if SPLATPRESSO_WEBSOCKETS
                return true;
#else
                return false;
#endif
            }
        }

        static readonly int[] kBackoffSeconds = { 1, 2, 4, 8, 15 };
        const int kFastAttempts = 10;                   // then every kSlowRetrySeconds
        const int kSlowRetrySeconds = 30;
        const int kConnectTimeoutMs = 15000;            // TLS + HTTP upgrade; a stalled handshake is retried
        const double kStableConnectionSeconds = 30.0;   // a connection this old resets the failure counter
        const int kReceiveBufferBytes = 64 * 1024;

        enum ItemKind { Opened, Message, Closed, Warning }

        struct Incoming
        {
            public int gen;
            public ItemKind kind;
            public JObject evt;
            public string text;
            public FailureKind fatal;   // None = retry
            public bool rejected;       // the server refused the HTTP upgrade (status unknown on Unity's runtime)
        }

        readonly ConcurrentQueue<Incoming> m_Inbox = new ConcurrentQueue<Incoming>();
        string m_Url;
        string m_Token;
        int m_Gen;
        bool m_UserClosed;
        bool m_IsReconnect;
        bool m_ReconnectPending;
        DateTime m_ReconnectDueUtc;
        int m_ReconnectAttempt;
        DateTime m_OpenedAtUtc;
        bool m_EverOpened;          // a first open after failed attempts is not a "reconnect" (fresh session)
        bool m_RejectionProbed;     // the /v1/models diagnosis runs once per outage (reset when a connection opens)
        bool m_Diagnosing;
        int m_RejectedStreak;       // refused upgrades in a row with no open in between
        string m_LastDiagnosis;
        const int kMaxRejectedStreak = 4; // the server answers HTTP but keeps refusing: not network trouble
#pragma warning disable 0414 // only read by the WebSocket implementation (unused on WebGL)
        bool m_WarnedSendWhileClosed;
#pragma warning restore 0414

#if SPLATPRESSO_WEBSOCKETS
        sealed class Connection
        {
            public int gen;
            public ClientWebSocket ws;
            public CancellationTokenSource cts;
            public readonly ConcurrentQueue<byte[]> outbox = new ConcurrentQueue<byte[]>();
            public readonly SemaphoreSlim signal = new SemaphoreSlim(0);
            public volatile bool closeRequested;
            public int disposed;
            public long lastActivityTicks; // UTC ticks of the last frame received or sent (liveness watchdog)
        }

        Connection m_Conn;
#endif

        // ------------------------------------------------------------------------------------------
        // public API (main thread)

        /// <summary>Opens <paramref name="url"/> with an <c>Authorization: Bearer</c> header; reconnects automatically.</summary>
        public void Connect(string url, string bearerToken)
        {
            m_Url = url;
            m_Token = bearerToken;
            m_UserClosed = false;
            m_ReconnectPending = false;
            m_ReconnectAttempt = 0;
            m_EverOpened = false;
            m_RejectionProbed = false;
            m_Diagnosing = false;
            m_RejectedStreak = 0;
            m_LastDiagnosis = null;
            LastError = null;
            LastFailureKind = FailureKind.None;
#if SPLATPRESSO_WEBSOCKETS
            Open(isReconnect: false);
#else
            LastError = "WebSockets are not supported on this platform (WebGL)";
            SetFailed(LastError);
#endif
        }

        /// <summary>Queues a client event. Dropped (with a single warning) while the socket is not open.</summary>
        public void Send(JObject clientEvent)
        {
            if (clientEvent == null)
                return;
#if SPLATPRESSO_WEBSOCKETS
            var c = m_Conn;
            if (c == null || State != SocketState.Open)
            {
                if (!m_WarnedSendWhileClosed)
                {
                    m_WarnedSendWhileClosed = true;
                    Debug.LogWarning("[SplatPresso] Realtime send dropped: socket not connected");
                }
                return;
            }
            c.outbox.Enqueue(Encoding.UTF8.GetBytes(clientEvent.ToString(Formatting.None)));
            c.signal.Release();
#endif
        }

        /// <summary>Call every frame: dispatches received events and state changes, drives the reconnect timer.</summary>
        public void Tick()
        {
            while (m_Inbox.TryDequeue(out var item))
            {
                if (item.gen != m_Gen)
                    continue; // from a replaced or closed connection
                switch (item.kind)
                {
                    case ItemKind.Opened:
                    {
                        bool reconnect = m_IsReconnect && m_EverOpened;
                        m_EverOpened = true;
                        m_RejectedStreak = 0;
                        m_RejectionProbed = false; // a later outage (key revoked, quota gone) gets its own diagnosis
                        State = SocketState.Open;
                        m_OpenedAtUtc = DateTime.UtcNow;
                        m_WarnedSendWhileClosed = false;
                        LastError = null;
                        Debug.Log($"[SplatPresso] Realtime socket {(reconnect ? "reconnected" : "connected")}");
                        RaiseState(reconnect ? StateReconnected : StateConnected);
                        break;
                    }

                    case ItemKind.Message:
                        try { OnServerEvent?.Invoke(item.evt); }
                        catch (Exception e) { Debug.LogException(e); }
                        break;

                    case ItemKind.Warning:
                        Debug.LogWarning("[SplatPresso] " + item.text);
                        break;

                    case ItemKind.Closed:
                        HandleClosed(item.text, item.fatal, item.rejected);
                        break;
                }
            }

            if (m_ReconnectPending && !m_UserClosed && DateTime.UtcNow >= m_ReconnectDueUtc)
            {
                m_ReconnectPending = false;
#if SPLATPRESSO_WEBSOCKETS
                Open(isReconnect: true);
#endif
            }
        }

        /// <summary>Closes gracefully (bounded to a few seconds in the background); no reconnect.</summary>
        public void Close()
        {
            m_UserClosed = true;
            m_ReconnectPending = false;
            m_Gen++; // everything still in flight is stale now
#if SPLATPRESSO_WEBSOCKETS
            var c = m_Conn;
            m_Conn = null;
            if (c != null)
            {
                c.closeRequested = true;
                try
                {
                    c.signal.Release();
                    c.cts.CancelAfter(3000); // hard bound even if the peer never answers
                }
                catch (ObjectDisposedException) { }
            }
#endif
            Unregister(this);
            bool wasActive = State != SocketState.Idle && State != SocketState.Closed && State != SocketState.Failed;
            State = SocketState.Closed;
            if (wasActive)
                RaiseState(StateClosed);
        }

        /// <summary>Aborts immediately (no close handshake, no events). Safe to call repeatedly.</summary>
        public void Abort()
        {
            m_UserClosed = true;
            m_ReconnectPending = false;
            m_Gen++;
#if SPLATPRESSO_WEBSOCKETS
            var c = m_Conn;
            m_Conn = null;
            AbortConnection(c);
#endif
            Unregister(this);
            if (State != SocketState.Failed)
                State = SocketState.Closed;
        }

        /// <summary>Same as <see cref="Abort"/>.</summary>
        public void Dispose() => Abort();

        /// <summary>
        /// Ends the socket for good (no reconnect) with a reason, e.g. when the server accepted the connection but reported
        /// an invalid key or model and closed it (retrying would loop).
        /// </summary>
        public void Fail(string reason, FailureKind kind)
        {
            if (State == SocketState.Failed || m_UserClosed)
                return;
            m_ReconnectPending = false;
            m_Gen++; // anything still arriving from the dropped connection is stale
#if SPLATPRESSO_WEBSOCKETS
            var c = m_Conn;
            m_Conn = null;
            AbortConnection(c);
#endif
            SetFailed(reason, kind);
        }

        /// <summary>
        /// Drops the current connection as if the network had (a half-open TCP connection never reports itself);
        /// the socket reconnects as usual. Used by the owner's liveness watchdog.
        /// </summary>
        public void DropConnection()
        {
#if SPLATPRESSO_WEBSOCKETS
            if (State == SocketState.Open)
                AbortConnection(m_Conn);
#endif
        }

        // ------------------------------------------------------------------------------------------
        // main-thread internals

        void RaiseState(string state)
        {
            try { OnStateChanged?.Invoke(state); }
            catch (Exception e) { Debug.LogException(e); }
        }

        void SetFailed(string reason, FailureKind kind = FailureKind.Other)
        {
            LastError = reason;
            LastFailureKind = kind == FailureKind.None ? FailureKind.Other : kind;
            State = SocketState.Failed;
            m_ReconnectPending = false;
            Unregister(this);
            Debug.LogError("[SplatPresso] Realtime connection failed: " + reason);
            RaiseState(StateFailed);
        }

        void HandleClosed(string reason, FailureKind fatal, bool rejected)
        {
#if SPLATPRESSO_WEBSOCKETS
            m_Conn = null;
#endif
            LastError = reason;
            if (m_UserClosed)
            {
                State = SocketState.Closed;
                RaiseState(StateClosed);
                return;
            }
            if (fatal != FailureKind.None)
            {
                SetFailed(fatal == FailureKind.Runtime
                    ? reason + " (this build cannot open WebSockets / TLS; managed code stripping?)"
                    : reason, fatal);
                return;
            }
            if (rejected && ++m_RejectedStreak >= kMaxRejectedStreak)
            {
                SetFailed($"the server refused the connection {m_RejectedStreak} times in a row ({m_LastDiagnosis ?? reason}); " +
                          "is realtimeModel a Realtime model your key can use?", FailureKind.Auth);
                return;
            }
            if (rejected && !m_RejectionProbed && !m_Diagnosing)
            {
                // Unity's ClientWebSocket hides the HTTP status of a refused upgrade: ask the REST API with the same
                // key whether the key / model is the problem (401, 404, quota) before retrying for nothing.
                m_RejectionProbed = true;
                State = SocketState.Reconnecting;
                RaiseState(StateReconnecting);
                DiagnoseRejectionAsync(m_Gen, reason);
                return;
            }
            ScheduleReconnect(reason);
        }

        void ScheduleReconnect(string reason)
        {
            // a connection that stayed up for a while is a fresh start; quick open/close loops count as failures
            if (m_OpenedAtUtc != default && (DateTime.UtcNow - m_OpenedAtUtc).TotalSeconds >= kStableConnectionSeconds)
                m_ReconnectAttempt = 0;
            m_OpenedAtUtc = default;
            m_ReconnectAttempt++;
            if (MaxReconnectAttempts > 0 && m_ReconnectAttempt > MaxReconnectAttempts)
            {
                SetFailed($"gave up after {m_ReconnectAttempt - 1} reconnect attempts ({reason})");
                return;
            }
            int delay = m_ReconnectAttempt > kFastAttempts
                ? kSlowRetrySeconds
                : kBackoffSeconds[Mathf.Min(m_ReconnectAttempt - 1, kBackoffSeconds.Length - 1)];
            m_ReconnectDueUtc = DateTime.UtcNow.AddSeconds(delay);
            m_ReconnectPending = true;
            State = SocketState.Reconnecting;
            Debug.Log($"[SplatPresso] Realtime socket closed ({reason}); reconnecting in {delay}s (attempt {m_ReconnectAttempt}" +
                      (MaxReconnectAttempts > 0 ? "/" + MaxReconnectAttempts : "") + ")");
            RaiseState(StateReconnecting);
        }

        async void DiagnoseRejectionAsync(int gen, string reason)
        {
            m_Diagnosing = true;
            string probeUrl = ModelProbeUrl(m_Url, out string model);
            long status = 0;
            string body = null;
            try
            {
                var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + (m_Token ?? "").Trim() } };
                var resp = await HttpJson.SendAsync("GET", probeUrl, null, null, headers, 15, CancellationToken.None, throwOnHttpError: false);
                status = resp.StatusCode;
                body = resp.Text;
            }
            catch (Exception)
            {
                // network trouble: treat as transient below
            }
            m_Diagnosing = false;
            if (gen != m_Gen || m_UserClosed)
                return;
            var kind = InterpretModelProbe(status, body, model, out string diagnosis);
            m_LastDiagnosis = diagnosis;
            if (kind != FailureKind.None)
            {
                SetFailed(diagnosis, kind);
                return;
            }
            ScheduleReconnect(diagnosis != null ? reason + " (" + diagnosis + ")" : reason);
        }

        /// <summary>
        /// REST URL that checks the key and model of a Realtime WebSocket URL
        /// (<c>wss://host/v1/realtime?model=m</c> -> <c>https://host/v1/models/m</c>).
        /// </summary>
        public static string ModelProbeUrl(string realtimeUrl, out string model)
        {
            model = "";
            try
            {
                var u = new Uri(realtimeUrl);
                foreach (string part in u.Query.TrimStart('?').Split('&'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0 && part.Substring(0, eq) == "model")
                        model = Uri.UnescapeDataString(part.Substring(eq + 1));
                }
                string path = u.AbsolutePath;
                int i = path.LastIndexOf("/realtime", StringComparison.Ordinal);
                string prefix = i >= 0 ? path.Substring(0, i) : "/v1";
                string scheme = u.Scheme == "ws" ? "http" : "https";
                return $"{scheme}://{u.Authority}{prefix}/models/{Uri.EscapeDataString(model)}";
            }
            catch (Exception)
            {
                return "https://api.openai.com/v1/models/" + Uri.EscapeDataString(model);
            }
        }

        /// <summary>
        /// Reads a <c>GET /v1/models/{model}</c> answer given with the Realtime key: Auth when retrying cannot help
        /// (key rejected, no access to the model, quota exhausted), None when the problem is elsewhere or unknown.
        /// <paramref name="diagnosis"/> never contains the key.
        /// </summary>
        public static FailureKind InterpretModelProbe(long status, string body, string model, out string diagnosis)
        {
            body = body ?? "";
            // restricted keys may lack the models scope yet be valid for Realtime: inconclusive
            bool missingScopes = body.IndexOf("Missing scopes", StringComparison.OrdinalIgnoreCase) >= 0;
            switch (status)
            {
                case 200:
                    diagnosis = "the key and model are valid; the server refused the WebSocket";
                    return FailureKind.None;
                case 401:
                    diagnosis = missingScopes ? "the key cannot list models (restricted key)" : "the OpenAI key was rejected (401)";
                    return missingScopes ? FailureKind.None : FailureKind.Auth;
                case 403:
                    diagnosis = missingScopes ? "the key cannot list models (restricted key)" : "the OpenAI key has no access (403)";
                    return missingScopes ? FailureKind.None : FailureKind.Auth;
                case 404:
                    diagnosis = $"no access to the realtime model '{model}' (404)";
                    return FailureKind.Auth;
                case 429:
                    if (body.IndexOf("insufficient_quota", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        diagnosis = "the OpenAI account has no quota left (429 insufficient_quota)";
                        return FailureKind.Auth;
                    }
                    diagnosis = "rate limited (429)";
                    return FailureKind.None;
                default:
                    diagnosis = status == 0 ? null : $"models endpoint answered {status}";
                    return FailureKind.None;
            }
        }

#if SPLATPRESSO_WEBSOCKETS
        void Open(bool isReconnect)
        {
            AbortConnection(m_Conn);
            m_Conn = null;

            Uri uri;
            try
            {
                uri = new Uri(m_Url);
            }
            catch (Exception e)
            {
                SetFailed("invalid URL: " + e.Message);
                return;
            }

            var c = new Connection { gen = ++m_Gen, cts = new CancellationTokenSource() };
            try
            {
                c.ws = new ClientWebSocket();
                c.ws.Options.SetRequestHeader("Authorization", "Bearer " + (m_Token ?? "").Trim());
                c.ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            }
            catch (Exception e)
            {
                SetFailed("could not create the WebSocket: " + e.Message);
                return;
            }

            m_Conn = c;
            m_IsReconnect = isReconnect;
            State = isReconnect ? SocketState.Reconnecting : SocketState.Connecting;
            Register(this);
            var inbox = m_Inbox;
            // The only Task.Run in the package: the socket loops must not run on (or block) the main thread.
            Task.Run(() => RunConnectionAsync(c, uri, inbox));
        }

        static void AbortConnection(Connection c)
        {
            if (c == null)
                return;
            try { c.cts.Cancel(); } catch (ObjectDisposedException) { }
            try { c.ws?.Abort(); } catch (Exception) { }
        }

        // ------------------------------------------------------------------------------------------
        // background loops (thread pool; never touch Unity APIs or instance state here)

        static async Task RunConnectionAsync(Connection c, Uri uri, ConcurrentQueue<Incoming> inbox)
        {
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(c.cts.Token))
                {
                    connectCts.CancelAfter(kConnectTimeoutMs);
                    try
                    {
                        await c.ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        bool timedOut = connectCts.IsCancellationRequested && !c.cts.IsCancellationRequested;
                        var kind = ClassifyConnectError(e, out string detail);
                        if (timedOut)
                            kind = ConnectErrorKind.Transient;
                        inbox.Enqueue(new Incoming
                        {
                            gen = c.gen,
                            kind = ItemKind.Closed,
                            text = timedOut ? $"connect timed out after {kConnectTimeoutMs / 1000} s" : "connect failed: " + detail,
                            fatal = kind == ConnectErrorKind.Auth ? FailureKind.Auth : kind == ConnectErrorKind.Runtime ? FailureKind.Runtime : FailureKind.None,
                            rejected = kind == ConnectErrorKind.Rejected,
                        });
                        return;
                    }
                }

                Interlocked.Exchange(ref c.lastActivityTicks, DateTime.UtcNow.Ticks);
                inbox.Enqueue(new Incoming { gen = c.gen, kind = ItemKind.Opened });
                Task sender = SendLoopAsync(c, inbox);
                string reason = await ReceiveLoopAsync(c, inbox).ConfigureAwait(false);
                try { c.cts.Cancel(); } catch (ObjectDisposedException) { }
                try { await sender.ConfigureAwait(false); } catch (Exception) { }
                inbox.Enqueue(new Incoming { gen = c.gen, kind = ItemKind.Closed, text = reason });
            }
            catch (Exception e)
            {
                inbox.Enqueue(new Incoming { gen = c.gen, kind = ItemKind.Closed, text = "socket error: " + e.Message });
            }
            finally
            {
                DisposeConnection(c);
            }
        }

        static async Task<string> ReceiveLoopAsync(Connection c, ConcurrentQueue<Incoming> inbox)
        {
            var buffer = new byte[kReceiveBufferBytes];
            using (var message = new MemoryStream())
            {
                while (!c.cts.IsCancellationRequested)
                {
                    WebSocketReceiveResult r;
                    try
                    {
                        r = await c.ws.ReceiveAsync(new ArraySegment<byte>(buffer), c.cts.Token).ConfigureAwait(false);
                        Interlocked.Exchange(ref c.lastActivityTicks, DateTime.UtcNow.Ticks);
                    }
                    catch (OperationCanceledException)
                    {
                        return "aborted";
                    }
                    catch (Exception e)
                    {
                        return c.cts.IsCancellationRequested ? "aborted" : "receive failed: " + e.Message;
                    }

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        string why = $"closed by server ({(r.CloseStatus.HasValue ? (int)r.CloseStatus.Value : 0)} {r.CloseStatusDescription})";
                        try
                        {
                            using (var t = new CancellationTokenSource(1000))
                                await c.ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", t.Token).ConfigureAwait(false);
                        }
                        catch (Exception) { }
                        return why;
                    }

                    // audio deltas and response.done can span several frames: accumulate until EndOfMessage
                    message.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage)
                        continue;
                    if (r.MessageType == WebSocketMessageType.Text)
                    {
                        string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        try
                        {
                            // parsed here to keep the main thread free; the JObject is not touched again off-thread
                            inbox.Enqueue(new Incoming { gen = c.gen, kind = ItemKind.Message, evt = JObject.Parse(text) });
                        }
                        catch (Exception e)
                        {
                            inbox.Enqueue(new Incoming { gen = c.gen, kind = ItemKind.Warning, text = "Bad realtime server event: " + e.Message });
                        }
                    }
                    message.SetLength(0);
                }
            }
            return "aborted";
        }

        static async Task SendLoopAsync(Connection c, ConcurrentQueue<Incoming> inbox)
        {
            try
            {
                while (true)
                {
                    await c.signal.WaitAsync(c.cts.Token).ConfigureAwait(false);
                    if (c.outbox.TryDequeue(out byte[] bytes))
                    {
                        await c.ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, c.cts.Token).ConfigureAwait(false);
                        Interlocked.Exchange(ref c.lastActivityTicks, DateTime.UtcNow.Ticks);
                        continue;
                    }
                    if (c.closeRequested)
                    {
                        try
                        {
                            using (var t = new CancellationTokenSource(1500))
                                await c.ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closing", t.Token).ConfigureAwait(false);
                        }
                        catch (Exception) { }
                        try { c.cts.Cancel(); } catch (ObjectDisposedException) { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                inbox.Enqueue(new Incoming { gen = c.gen, kind = ItemKind.Warning, text = "Realtime send failed: " + e.Message });
                try { c.cts.Cancel(); } catch (ObjectDisposedException) { } // ends the receive loop -> reconnect
            }
        }

        static void DisposeConnection(Connection c)
        {
            if (Interlocked.Exchange(ref c.disposed, 1) != 0)
                return;
            try { c.ws?.Abort(); } catch (Exception) { }
            try { c.ws?.Dispose(); } catch (Exception) { }
            try { c.cts.Dispose(); } catch (Exception) { }
        }
#endif

        /// <summary>What a failed ConnectAsync means for retrying.</summary>
        public enum ConnectErrorKind
        {
            /// <summary>Network trouble: retry.</summary>
            Transient,
            /// <summary>A status code in the message says the key / model was rejected: do not retry.</summary>
            Auth,
            /// <summary>This build lacks what the handshake needs (stripped types, unsupported platform): do not retry.</summary>
            Runtime,
            /// <summary>The server refused the HTTP upgrade without a readable status (Unity's runtime): diagnose.</summary>
            Rejected,
        }

        /// <summary>Classifies a ConnectAsync failure (see <see cref="ConnectErrorKind"/>); <paramref name="detail"/> is the message chain.</summary>
        public static ConnectErrorKind ClassifyConnectError(Exception e, out string detail)
        {
            bool fatalHandshake = IsFatalHandshakeError(e, out detail);
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x is TypeInitializationException || x is MissingMethodException || x is TypeLoadException ||
                    x is PlatformNotSupportedException || x is NotSupportedException || x is ArgumentException || x is UriFormatException ||
                    (x.GetType().FullName ?? "").IndexOf("Configuration", StringComparison.Ordinal) >= 0)
                    return ConnectErrorKind.Runtime;
            }
            if (fatalHandshake)
                return ConnectErrorKind.Auth;
#if SPLATPRESSO_WEBSOCKETS
            // Unity's corefx-2.0 ClientWebSocket: a status line other than "101" throws a bare WebSocketException
            // (no inner exception); network failures are wrapped around their cause.
            if (e is WebSocketException && e.InnerException == null)
                return ConnectErrorKind.Rejected;
#endif
            return ConnectErrorKind.Transient;
        }

        /// <summary>
        /// True when a failed handshake message names 400/401/403/404 (bad key, no access, unknown model), as newer
        /// .NET runtimes report it ("The server returned status code '401' when status code '101' was expected.").
        /// Unity's runtime does not include the status; see <see cref="ClassifyConnectError"/>.
        /// </summary>
        public static bool IsFatalHandshakeError(Exception e, out string detail)
        {
            var sb = new StringBuilder();
            for (var x = e; x != null; x = x.InnerException)
            {
                if (sb.Length > 0)
                    sb.Append(" -> ");
                sb.Append(x.Message);
            }
            detail = sb.ToString();
            return IsFatalHandshakeMessage(detail);
        }

        /// <summary>Message-only variant of <see cref="IsFatalHandshakeError"/> (exposed for tests).</summary>
        public static bool IsFatalHandshakeMessage(string detail)
        {
            if (string.IsNullOrEmpty(detail))
                return false;
            foreach (Match m in Regex.Matches(detail, @"(?<!\d)(4\d\d)(?!\d)"))
            {
                int code = int.Parse(m.Groups[1].Value);
                if (code == 400 || code == 401 || code == 403 || code == 404)
                    return true;
            }
            return detail.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   detail.IndexOf("Forbidden", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------------------------------
        // process-wide safety net (main thread only)

        static readonly HashSet<RealtimeSocket> s_Live = new HashSet<RealtimeSocket>();
        static bool s_QuitHooked;

        static void Register(RealtimeSocket s)
        {
            s_Live.Add(s);
            if (!s_QuitHooked)
            {
                s_QuitHooked = true;
                Application.quitting += AbortAllLive;
            }
        }

        static void Unregister(RealtimeSocket s) => s_Live.Remove(s);

        static void AbortAllLive()
        {
            if (s_Live.Count == 0)
                return;
            var live = new List<RealtimeSocket>(s_Live);
            s_Live.Clear();
            foreach (var s in live)
            {
                try { s.Abort(); } catch (Exception) { }
            }
        }

        // Projects often disable domain reload: sockets from the previous play session must not survive.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            AbortAllLive();
            if (s_QuitHooked)
                Application.quitting -= AbortAllLive;
            s_QuitHooked = false;
        }
    }
}
