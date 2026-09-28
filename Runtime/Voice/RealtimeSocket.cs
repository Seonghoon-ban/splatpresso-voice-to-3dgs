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
    /// Reconnect: backoff 1, 2, 4, 8, 15 s. A handshake rejected with 400/401/403/404 (bad key or model) is fatal
    /// and never retried, and after <see cref="MaxReconnectAttempts"/> consecutive failures the socket gives up
    /// (the original retried an invalid key every 15 s forever).
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

        /// <summary>Consecutive failed (re)connects before giving up.</summary>
        public int MaxReconnectAttempts = 10;

        /// <summary>Current state.</summary>
        public SocketState State { get; private set; } = SocketState.Idle;
        /// <summary>True while the socket is open and sends go out.</summary>
        public bool IsConnected => State == SocketState.Open;
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
        const double kStableConnectionSeconds = 30.0;   // a connection this old resets the failure counter
        const int kReceiveBufferBytes = 64 * 1024;

        enum ItemKind { Opened, Message, Closed, Warning }

        struct Incoming
        {
            public int gen;
            public ItemKind kind;
            public JObject evt;
            public string text;
            public bool fatal;
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
            LastError = null;
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
                        State = SocketState.Open;
                        m_OpenedAtUtc = DateTime.UtcNow;
                        m_WarnedSendWhileClosed = false;
                        LastError = null;
                        Debug.Log($"[SplatPresso] Realtime socket {(m_IsReconnect ? "reconnected" : "connected")}");
                        RaiseState(m_IsReconnect ? StateReconnected : StateConnected);
                        break;

                    case ItemKind.Message:
                        try { OnServerEvent?.Invoke(item.evt); }
                        catch (Exception e) { Debug.LogException(e); }
                        break;

                    case ItemKind.Warning:
                        Debug.LogWarning("[SplatPresso] " + item.text);
                        break;

                    case ItemKind.Closed:
                        HandleClosed(item.text, item.fatal);
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

        // ------------------------------------------------------------------------------------------
        // main-thread internals

        void RaiseState(string state)
        {
            try { OnStateChanged?.Invoke(state); }
            catch (Exception e) { Debug.LogException(e); }
        }

        void SetFailed(string reason)
        {
            State = SocketState.Failed;
            m_ReconnectPending = false;
            Unregister(this);
            Debug.LogError("[SplatPresso] Realtime connection failed: " + reason);
            RaiseState(StateFailed);
        }

        void HandleClosed(string reason, bool fatal)
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
            if (fatal)
            {
                SetFailed(reason);
                return;
            }
            // a connection that stayed up for a while is a fresh start; quick open/close loops count as failures
            if (m_OpenedAtUtc != default && (DateTime.UtcNow - m_OpenedAtUtc).TotalSeconds >= kStableConnectionSeconds)
                m_ReconnectAttempt = 0;
            m_OpenedAtUtc = default;
            m_ReconnectAttempt++;
            if (m_ReconnectAttempt > Mathf.Max(1, MaxReconnectAttempts))
            {
                SetFailed($"gave up after {m_ReconnectAttempt - 1} reconnect attempts ({reason})");
                return;
            }
            int delay = kBackoffSeconds[Mathf.Min(m_ReconnectAttempt - 1, kBackoffSeconds.Length - 1)];
            m_ReconnectDueUtc = DateTime.UtcNow.AddSeconds(delay);
            m_ReconnectPending = true;
            State = SocketState.Reconnecting;
            Debug.Log($"[SplatPresso] Realtime socket closed ({reason}); reconnecting in {delay}s (attempt {m_ReconnectAttempt}/{MaxReconnectAttempts})");
            RaiseState(StateReconnecting);
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
                try
                {
                    await c.ws.ConnectAsync(uri, c.cts.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    bool fatal = IsFatalHandshakeError(e, out string detail);
                    inbox.Enqueue(new Incoming { gen = c.gen, kind = ItemKind.Closed, text = "connect failed: " + detail, fatal = fatal });
                    return;
                }

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

        /// <summary>
        /// True when a failed handshake means "do not retry": 400/401/403/404 (bad key, no access, unknown
        /// model). The status code is found in the exception chain (e.g. "The server returned status code '401'
        /// when status code '101' was expected.").
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
