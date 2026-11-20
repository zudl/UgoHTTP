using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using UgoHttp.Native;

namespace UgoHttp
{
    /// <summary>
    /// A WebSocket open request. Create it with <see cref="UgoWebSocketRequest(string)"/> for the defaults: a <c>default</c> request fails
    /// validation. Read by <see cref="UgoClient.OpenWebSocket"/>, which allocates the receive backlog and send buffer once, in native memory.
    /// </summary>
    public struct UgoWebSocketRequest
    {
        /// <summary>
        /// Absolute <c>ws</c> or <c>wss</c> URL. A 3xx with <c>Location</c> is followed, at most 10 times (<c>http</c>/<c>https</c> targets become
        /// <c>ws</c>/<c>wss</c>); <c>Authorization</c> and <c>Cookie</c> headers go only to the original origin.
        /// </summary>
        public string Url;
        /// <summary>
        /// Handshake headers in order, for example <c>Authorization</c> or <c>Sec-WebSocket-Protocol</c>; the server must then answer with one of
        /// the offered subprotocols or none. A <c>Sec-WebSocket-Key</c> header throws (the library sets it), and so does
        /// <c>Sec-WebSocket-Extensions</c>: no extension is supported.
        /// </summary>
        public ReadOnlyMemory<UgoHeader> Headers;
        /// <summary>TCP and TLS connect timeout per connection attempt, rounded up to whole milliseconds; must be above zero. 10 s by default.</summary>
        public TimeSpan ConnectTimeout;
        /// <summary>
        /// Limit from <see cref="UgoClient.OpenWebSocket"/> until the socket is open, redirects included, rounded up to whole milliseconds; null or
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> = none. 30 s by default.
        /// </summary>
        public TimeSpan? OpenTimeout;
        /// <summary>
        /// Limit from <see cref="UgoWebSocket.Close"/> (or from the server's CLOSE) until the close handshake is done, rounded up to whole
        /// milliseconds; must be above zero. 5 s by default.
        /// </summary>
        public TimeSpan CloseTimeout;
        /// <summary>Largest incoming message in bytes, text or binary; a bigger one closes the socket with 1009 (<see cref="UgoErrorKind.Transfer"/>). 1 MiB by default.</summary>
        public int MaxMessageBytes;
        /// <summary>
        /// Received messages held at once: those waiting for delivery plus those delivered and not yet disposed. When reached, the library stops
        /// reading and TCP holds the server back; nothing is dropped. 64 by default.
        /// </summary>
        public int MaxBacklogMessages;
        /// <summary>Bytes of received messages held at once, the one being reassembled included; at least <see cref="MaxMessageBytes"/>. 4 MiB by default.</summary>
        public long MaxBacklogBytes;
        /// <summary>Send buffer in bytes, above 8; each queued message takes its length in bytes plus 8 until written. 1 MiB by default.</summary>
        public int MaxSendBytes;

        /// <summary>A request for <paramref name="url"/> with the defaults.</summary>
        public UgoWebSocketRequest(string url)
        {
            this = default;
            Url = url;
            ConnectTimeout = TimeSpan.FromSeconds(10);
            OpenTimeout = TimeSpan.FromSeconds(30);
            CloseTimeout = TimeSpan.FromSeconds(5);
            MaxMessageBytes = 1 << 20;
            MaxBacklogMessages = 64;
            MaxBacklogBytes = 4 << 20;
            MaxSendBytes = 1 << 20;
        }
    }

    /// <summary>
    /// Lifecycle of a <see cref="UgoWebSocket"/>, updated as the draining thread delivers events, so it lags the network. A close handshake in
    /// progress still reads <see cref="Open"/>; whether a message can be sent is <see cref="UgoWebSocket.Send(ReadOnlySpan{byte}, UgoWebSocketMessageType)"/>'s status.
    /// </summary>
    public enum UgoWebSocketState
    {
        /// <summary>Handshake in progress.</summary>
        Connecting = 0,
        /// <summary>The handshake passed; the socket has not finished.</summary>
        Open = 1,
        /// <summary>Finished; <see cref="UgoWebSocket.TryGetResult"/> has the outcome.</summary>
        Closed = 2
    }

    /// <summary>Type of a WebSocket message, as in <c>System.Net.WebSockets.WebSocketMessageType</c>.</summary>
    public enum UgoWebSocketMessageType
    {
        /// <summary>UTF-8 text, valid both ways.</summary>
        Text = 0,
        /// <summary>Raw bytes.</summary>
        Binary = 1
    }

    /// <summary>Outcome of <see cref="UgoWebSocket.Send(ReadOnlySpan{byte}, UgoWebSocketMessageType)"/>; nothing was queued unless <see cref="Accepted"/>.</summary>
    public enum UgoWebSocketSendStatus
    {
        /// <summary>Copied into the send buffer; goes out in order. Not a delivery confirmation: a queued message can be dropped even when the socket later closes without error.</summary>
        Accepted = 0,
        /// <summary>The send buffer has no room for it now.</summary>
        BufferFull = 1,
        /// <summary>The socket is not open: still connecting, closing, closed or its client shut down.</summary>
        NotOpen = 2
    }

    /// <summary>
    /// Callback form of a WebSocket: override what you need and pass it to <see cref="UgoClient.OpenWebSocket"/>. Methods run on the draining
    /// thread, in order: <see cref="OnOpen"/> at most once, then messages, then <see cref="OnClosed"/> exactly once, also after
    /// <see cref="UgoWebSocket.Dispose"/>. They must not block. An exception escaping one is rethrown on the thread pool; delivery continues.
    /// One instance may serve many sockets.
    /// </summary>
    public abstract class UgoWebSocketListener
    {
        /// <summary>The handshake passed. The socket may already be closing, so check what <see cref="UgoWebSocket.Send(ReadOnlySpan{byte}, UgoWebSocketMessageType)"/> returns. <paramref name="handshake"/> spans are valid until the socket or client is disposed.</summary>
        public virtual void OnOpen(UgoWebSocket socket, in UgoHeadersEvent handshake) { }
        /// <summary>A whole text or binary message. Valid only during the call: the library releases it on return, so copy what you keep.</summary>
        public virtual void OnMessage(UgoWebSocket socket, in UgoWebSocketMessage message) { }
        /// <summary>
        /// The socket finished: closed, failed, refused, aborted or shut down. <paramref name="result"/> spans are valid until the socket is
        /// disposed or the client is; if the socket was disposed while running, only until this call returns.
        /// </summary>
        public virtual void OnClosed(UgoWebSocket socket, in UgoWebSocketResult result) { }
    }

    /// <summary>
    /// A received message, text (valid UTF-8) or binary, held in native memory. Dispose it to release it and every earlier message of the socket: until then
    /// it counts against <see cref="UgoWebSocketRequest.MaxBacklogMessages"/> and <see cref="UgoWebSocketRequest.MaxBacklogBytes"/>, and a full
    /// backlog stops receiving.
    /// </summary>
    public readonly unsafe struct UgoWebSocketMessage : IDisposable
    {
        readonly UgoWebSocket socket;
        readonly ulong sequence;
        readonly ulong pointer;
        readonly int length;
        readonly UgoWebSocketMessageType type;
        readonly long time;

        internal UgoWebSocketMessage(UgoWebSocket socket, in ugo_ev_ws_message native, long time)
        {
            this.socket = socket;
            sequence = native.seq;
            pointer = native.data.ptr;
            length = checked((int)native.data.len);
            type = native.type == UgoAbi.UGO_WS_BINARY ? UgoWebSocketMessageType.Binary : UgoWebSocketMessageType.Text;
            this.time = time;
        }

        /// <summary>True for the end marker <see cref="UgoWebSocket.ReceiveAsync"/> returns once the socket closed and every message was received; it carries no data.</summary>
        public bool IsEnd => sequence == 0;
        /// <summary>1, 2, … per socket, in arrival order; 0 for the end marker.</summary>
        public long Sequence => (long)sequence;
        /// <summary>When the message was complete, in microseconds on the <see cref="UgoClient.MonotonicMicroseconds"/> clock.</summary>
        public long TimestampMicroseconds => time;
        /// <summary>Text or binary, as the server sent it; <see cref="UgoWebSocketMessageType.Text"/> for the end marker.</summary>
        public UgoWebSocketMessageType MessageType => type;
        /// <summary>
        /// The payload, no copy: UTF-8 for a text message. Valid until this or a later message is disposed (in a listener, until the callback
        /// returns), the socket is disposed after it ended, or the client is disposed; after that the getter throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        public ReadOnlySpan<byte> Data
        {
            get
            {
                if (socket != null && (long)sequence <= socket.ReleasedThrough) throw new ObjectDisposedException(nameof(UgoWebSocketMessage));
                return new ReadOnlySpan<byte>((void*)pointer, length);
            }
        }
        /// <summary>
        /// A text message as a string; allocates. Throws <see cref="InvalidOperationException"/> for a binary message and
        /// <see cref="ObjectDisposedException"/> under the same conditions as <see cref="Data"/>.
        /// </summary>
        public string GetText()
        {
            if (type == UgoWebSocketMessageType.Binary) throw new InvalidOperationException("A binary message has no text; read Data.");
            return Encoding.UTF8.GetString(Data);
        }
        /// <summary>Releases this message and every earlier one. Idempotent.</summary>
        public void Dispose()
        {
            if (sequence != 0) socket?.Release(sequence);
        }
    }

    /// <summary>
    /// How a WebSocket ended. Scalar properties stay readable; <see cref="Handshake"/> and <see cref="Reason"/> throw
    /// <see cref="ObjectDisposedException"/> once the socket's data is released (see <see cref="UgoWebSocket.Dispose"/>) or the client is disposed,
    /// and spans taken earlier must not be used after that.
    /// </summary>
    public readonly struct UgoWebSocketResult
    {
        readonly UgoClient client;
        readonly ulong id;
        readonly ugo_ev_completed completed;

        internal UgoWebSocketResult(UgoClient client, ulong id, in ugo_ev_completed completed)
        {
            this.client = client;
            this.id = id;
            this.completed = completed;
        }

        /// <summary>
        /// <see cref="UgoErrorKind.None"/> after a clean close handshake from either side; <see cref="UgoErrorKind.HttpStatus"/> when the server
        /// refused the upgrade; <see cref="UgoErrorKind.Aborted"/> after <see cref="UgoWebSocket.Abort"/>, <see cref="UgoWebSocket.Dispose"/> or
        /// client shutdown; <see cref="UgoErrorKind.Timeout"/> when the connect, open or close timeout fired; <see cref="UgoErrorKind.Transfer"/>
        /// after a protocol error, a failed handshake check or a drop; <see cref="UgoErrorKind.Dns"/>, <see cref="UgoErrorKind.Connect"/> or
        /// <see cref="UgoErrorKind.Tls"/> as for HTTP. The reason says who sent the first CLOSE frame, or why the socket was aborted.
        /// </summary>
        public UgoError Error => new UgoError((UgoErrorKind)completed.error_class, (UgoErrorReason)completed.detail, completed.curl_code);
        /// <summary>Code of the first CLOSE frame, sent or received; 1005 when it carried no code, 1006 when no CLOSE was exchanged.</summary>
        public int CloseCode => (int)completed.ws_close_code;
        /// <summary>The server sent the first CLOSE frame.</summary>
        public bool ClosedByServer => completed.detail == UgoAbi.UGO_DETAIL_WS_CLOSED_BY_REMOTE;
        /// <summary>Status of the last handshake response: 101 once the server agreed, else the refusal (401, …) whose headers are in <see cref="Handshake"/>; 0 if none arrived.</summary>
        public int StatusCode => (int)completed.http_status;
        /// <summary>Redirects followed before the last handshake (at most 10).</summary>
        public int RedirectCount => (int)completed.redirect_count;
        /// <summary>Phase timings of the last handshake; <see cref="UgoTimings.RedirectMicroseconds"/> is the time spent in the earlier ones.</summary>
        public UgoTimings Timings => new UgoTimings(completed);
        /// <summary>
        /// The last handshake response: the 101, a refusal, or the 3xx past the 10-redirect limit (then <see cref="UgoErrorKind.Transfer"/>).
        /// Empty headers if none arrived; <see cref="UgoHeadersEvent.ContentLength"/> is always -1.
        /// </summary>
        public UgoHeadersEvent Handshake => new UgoHeadersEvent(new ugo_ev_headers
        {
            http_status = completed.http_status,
            http_version = completed.http_version,
            content_length = -1,
            headers = Result().headers
        });
        /// <summary>UTF-8 reason of the first CLOSE frame, else the error text; empty after a clean close without a reason.</summary>
        public ReadOnlySpan<byte> Reason => UgoEvent.Bytes(Result().error_message);
        /// <summary><see cref="Reason"/> as a string; allocates.</summary>
        public string GetReason() => Encoding.UTF8.GetString(Reason);

        ugo_result Result()
        {
            if (client == null) throw new InvalidOperationException("Default UgoWebSocketResult has no result.");
            return client.GetResult(id, nameof(UgoWebSocket));
        }
    }

    /// <summary>
    /// A WebSocket opened by <see cref="UgoClient.OpenWebSocket"/>, carrying text and binary messages; no extension is negotiated. Members are
    /// safe from any thread. Messages go to the listener given at open, or, without one, through <see cref="ReceiveAsync"/>.
    /// The library answers pings and runs the close handshake; reconnects and application heartbeats are the caller's. Always
    /// <see cref="Dispose"/> it: that aborts a running socket and frees its slot.
    /// </summary>
    public sealed unsafe class UgoWebSocket : IDisposable
    {
        readonly UgoClient client;
        readonly ulong id;
        readonly UgoWebSocketListener listener;
        readonly int maxSendBytes;
        readonly object sync = new object();
        readonly UgoWebSocketMessage[] queue;
        readonly Sources sources;
        int head, count;
        bool openBusy, openWaiting, receiveBusy, receiveWaiting;
        bool opened, closed, disposed;
        long releasedThrough, lastSequence;
        volatile UgoWebSocketState state;
        UgoHeadersEvent handshake;
        ugo_ev_completed result;

        internal UgoWebSocket(UgoClient client, ulong id, UgoWebSocketListener listener, int backlog, int maxSendBytes)
        {
            this.client = client;
            this.id = id;
            this.listener = listener;
            this.maxSendBytes = maxSendBytes;
            if (listener != null) return;
            queue = new UgoWebSocketMessage[backlog];
            sources = new Sources(this);
        }

        /// <summary>The socket's operation id on its client.</summary>
        public UgoId Id => new UgoId(id);
        /// <summary>Current state; see <see cref="UgoWebSocketState"/> for its lag.</summary>
        public UgoWebSocketState State => state;
        internal long ReleasedThrough => Volatile.Read(ref releasedThrough);
        internal void MarkClientDisposed() => Volatile.Write(ref releasedThrough, long.MaxValue);
        internal bool IsDisposed => Volatile.Read(ref disposed);
        internal long HeldMessages
        {
            get
            {
                long released = ReleasedThrough;
                return Math.Max(0, Volatile.Read(ref lastSequence) - released);
            }
        }

        /// <summary>
        /// Queues one message of <paramref name="type"/>, copying <paramref name="data"/> into the send buffer before returning; text must be valid
        /// UTF-8. No allocation. Throws <see cref="ArgumentException"/> for invalid UTF-8 as text or a message that can never fit (longer than
        /// <see cref="UgoWebSocketRequest.MaxSendBytes"/> - 8), <see cref="ArgumentOutOfRangeException"/> for an unknown type and
        /// <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>.
        /// </summary>
        public UgoWebSocketSendStatus Send(ReadOnlySpan<byte> data, UgoWebSocketMessageType type)
        {
            uint native = type == UgoWebSocketMessageType.Text ? UgoAbi.UGO_WS_TEXT
                : type == UgoWebSocketMessageType.Binary ? UgoAbi.UGO_WS_BINARY
                : throw new ArgumentOutOfRangeException(nameof(type));
            int status;
            fixed (byte* bytes = data) status = client.WebSocketSend(id, native, bytes, (uint)data.Length);
            switch (status)
            {
                case UgoAbi.UGO_OK: return UgoWebSocketSendStatus.Accepted;
                case UgoAbi.UGO_E_CAPACITY: return UgoWebSocketSendStatus.BufferFull;
                case UgoAbi.UGO_E_STATE:
                case UgoAbi.UGO_E_SHUTDOWN: return UgoWebSocketSendStatus.NotOpen;
                case UgoAbi.UGO_E_INVALID_ARG:
                    throw new ArgumentException((long)data.Length + 8 > maxSendBytes ? "The message is larger than the send buffer allows."
                        : "The text is not valid UTF-8.", nameof(data));
                case UgoAbi.UGO_E_STALE_ID: throw new ObjectDisposedException(nameof(UgoWebSocket));
                default: throw new InvalidOperationException("Unknown native status: " + status);
            }
        }

        /// <summary>
        /// <see cref="Send(ReadOnlySpan{byte}, UgoWebSocketMessageType)"/> of <paramref name="text"/> as a text message, encoded on the stack up to
        /// 512 bytes, else into a pooled buffer. Unpaired surrogates are sent as U+FFFD.
        /// </summary>
        public UgoWebSocketSendStatus Send(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int size = Encoding.UTF8.GetByteCount(text);
            if (size <= 512)
            {
                Span<byte> small = stackalloc byte[size];
                Encoding.UTF8.GetBytes(text.AsSpan(), small);
                return Send(small, UgoWebSocketMessageType.Text);
            }
            byte[] rented = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                Encoding.UTF8.GetBytes(text.AsSpan(), rented);
                return Send(new ReadOnlySpan<byte>(rented, 0, size), UgoWebSocketMessageType.Text);
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        /// <summary>
        /// Starts the close handshake and returns at once: the CLOSE frame follows the messages already queued. <see cref="UgoWebSocketRequest.CloseTimeout"/>
        /// covers both sending them and waiting for the server's CLOSE. The end arrives as usual (<see cref="UgoWebSocketListener.OnClosed"/>
        /// or the end of <see cref="ReceiveAsync"/>). Returns false when the socket is not open, already closing included. <paramref name="code"/>:
        /// 1000–1003, 1007–1014 or 3000–4999; <paramref name="reason"/>: at most 123 UTF-8 bytes; others throw. Throws
        /// <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>.
        /// </summary>
        public bool Close(int code = 1000, string reason = null)
        {
            if (!((code >= 1000 && code <= 1003) || (code >= 1007 && code <= 1014) || (code >= 3000 && code <= 4999)))
                throw new ArgumentOutOfRangeException(nameof(code), "Not a close code an endpoint may send.");
            int size = reason == null ? 0 : Encoding.UTF8.GetByteCount(reason);
            if (size > UgoAbi.UGO_WS_CLOSE_REASON_MAX) throw new ArgumentException("Longer than 123 UTF-8 bytes.", nameof(reason));
            Span<byte> utf8 = stackalloc byte[size];
            if (size != 0) Encoding.UTF8.GetBytes(reason.AsSpan(), utf8);
            int status;
            fixed (byte* bytes = utf8) status = client.WebSocketClose(id, (uint)code, bytes, (uint)size);
            switch (status)
            {
                case UgoAbi.UGO_OK: return true;
                case UgoAbi.UGO_E_STATE:
                case UgoAbi.UGO_E_SHUTDOWN: return false;
                case UgoAbi.UGO_E_STALE_ID: throw new ObjectDisposedException(nameof(UgoWebSocket));
                default: throw new InvalidOperationException("Unknown native status: " + status);
            }
        }

        /// <summary>Drops the connection without a close handshake and returns at once. The socket still ends once, with <see cref="UgoErrorReason.Caller"/> unless it already finished.</summary>
        public void Abort() => client.Abort(new UgoId(id));

        /// <summary>
        /// Without a listener only: completes true once the handshake passed, false if the socket ended before that (see <see cref="TryGetResult"/>).
        /// One awaiter at a time; the continuation follows the usual await rules. Throws <see cref="InvalidOperationException"/> with a listener or
        /// while another wait is pending.
        /// </summary>
        public ValueTask<bool> WaitOpenAsync()
        {
            lock (sync)
            {
                if (listener != null) throw new InvalidOperationException("The socket has a listener.");
                if (opened || closed) return new ValueTask<bool>(opened);
                if (openBusy) throw new InvalidOperationException("WaitOpenAsync is already awaited.");
                openBusy = openWaiting = true;
                return new ValueTask<bool>(sources, sources.Open.Version);
            }
        }

        /// <summary>
        /// Without a listener only: the next message, or one with <see cref="UgoWebSocketMessage.IsEnd"/> once the socket ended and every message
        /// was received. Dispose each message; undisposed messages hold back the server. One receive at a time: throws
        /// <see cref="InvalidOperationException"/> with a listener or while another receive is pending. No allocation.
        /// </summary>
        public ValueTask<UgoWebSocketMessage> ReceiveAsync()
        {
            lock (sync)
            {
                if (listener != null) throw new InvalidOperationException("The socket has a listener.");
                if (receiveBusy) throw new InvalidOperationException("Another ReceiveAsync is pending.");
                if (count != 0) return new ValueTask<UgoWebSocketMessage>(Dequeue());
                if (closed) return new ValueTask<UgoWebSocketMessage>(default(UgoWebSocketMessage));
                receiveBusy = receiveWaiting = true;
                return new ValueTask<UgoWebSocketMessage>(sources, sources.Receive.Version);
            }
        }

        /// <summary>
        /// The 101 response once the socket opened; false before, or if it never opened. Throws <see cref="ObjectDisposedException"/> once the
        /// socket's data is released (see <see cref="Dispose"/>) or the client is disposed; spans taken earlier are invalid from then on.
        /// </summary>
        public bool TryGetHandshake(out UgoHeadersEvent value)
        {
            lock (sync)
            {
                if (ReleasedThrough == long.MaxValue) throw new ObjectDisposedException(nameof(UgoWebSocket));
                value = opened ? handshake : default;
                return opened;
            }
        }

        /// <summary>The outcome once <see cref="State"/> is <see cref="UgoWebSocketState.Closed"/>; false before. Its span accessors follow <see cref="UgoWebSocketResult"/>.</summary>
        public bool TryGetResult(out UgoWebSocketResult value)
        {
            lock (sync)
            {
                value = closed ? new UgoWebSocketResult(client, id, in result) : default;
                return closed;
            }
        }

        /// <summary>
        /// Aborts the socket if it is still running. Its messages, handshake and result are released at once if it already ended, else right after
        /// its end is delivered (a listener still gets <see cref="UgoWebSocketListener.OnClosed"/>); their spans are then invalid. Idempotent.
        /// </summary>
        public void Dispose()
        {
            bool release;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                release = closed;
            }
            if (release) ReleaseSlot();
            else Abort();
        }

        internal void Release(ulong sequence)
        {
            long through = (long)sequence, seen;
            while ((seen = Volatile.Read(ref releasedThrough)) < through && Interlocked.CompareExchange(ref releasedThrough, through, seen) != seen) { }
            client.WebSocketRelease(id, sequence);
        }

        void ReleaseSlot()
        {
            Volatile.Write(ref releasedThrough, long.MaxValue);
            client.Release(id);
        }

        void Resume<T>(ref ManualResetValueTaskSourceCore<T> core, ref bool signalling, T value)
        {
            try { core.SetResult(value); }
            catch (Exception error) { UgoClient.Unhandled(error); }
            lock (sync) signalling = false;
        }

        UgoWebSocketMessage Dequeue()
        {
            UgoWebSocketMessage message = queue[head];
            queue[head] = default;
            head = (head + 1) % queue.Length;
            count--;
            return message;
        }

        internal void Dispatch(ref UgoEvent e)
        {
            if (e.Kind == UgoEventKind.Headers)
            {
                UgoHeadersEvent headers = e.Headers;
                if (headers.StatusCode != 101) return;
                bool complete;
                lock (sync)
                {
                    handshake = headers;
                    opened = true;
                    if (state == UgoWebSocketState.Connecting) state = UgoWebSocketState.Open;
                    complete = openWaiting;
                    openWaiting = false;
                    if (complete) sources.OpenSignalling = true;
                }
                if (complete) Resume(ref sources.Open, ref sources.OpenSignalling, true);
                if (listener == null) return;
                try { listener.OnOpen(this, in headers); }
                catch (Exception error) { UgoClient.Unhandled(error); }
            }
            else if (e.Kind == UgoEventKind.WebSocketMessage)
            {
                var message = new UgoWebSocketMessage(this, e.WebSocketMessage, e.TimestampMicroseconds);
                // A slightly stale sequence in the Editor snapshot is acceptable; publishing it needs no barrier.
                lastSequence = message.Sequence;
                if (listener != null)
                {
                    try { listener.OnMessage(this, in message); }
                    catch (Exception error) { UgoClient.Unhandled(error); }
                    message.Dispose();
                    return;
                }
                bool complete;
                lock (sync)
                {
                    queue[(head + count) % queue.Length] = message;
                    count++;
                    complete = receiveWaiting;
                    receiveWaiting = false;
                    if (complete)
                    {
                        message = Dequeue();
                        sources.ReceiveSignalling = true;
                    }
                }
                if (complete) Resume(ref sources.Receive, ref sources.ReceiveSignalling, message);
            }
        }

        internal void Complete(in ugo_ev_completed completed)
        {
            bool completeOpen, completeReceive, release;
            lock (sync)
            {
                result = completed;
                closed = true;
                state = UgoWebSocketState.Closed;
                completeOpen = openWaiting;
                openWaiting = false;
                completeReceive = receiveWaiting;
                receiveWaiting = false;
                if (completeOpen) sources.OpenSignalling = true;
                if (completeReceive) sources.ReceiveSignalling = true;
                release = disposed;
            }
            if (completeOpen) Resume(ref sources.Open, ref sources.OpenSignalling, false);
            if (completeReceive) Resume(ref sources.Receive, ref sources.ReceiveSignalling, default(UgoWebSocketMessage));
            if (listener != null)
            {
                var value = new UgoWebSocketResult(client, id, in completed);
                try { listener.OnClosed(this, in value); }
                catch (Exception error) { UgoClient.Unhandled(error); }
            }
            if (release) ReleaseSlot();
        }

        sealed class Sources : IValueTaskSource<bool>, IValueTaskSource<UgoWebSocketMessage>
        {
            readonly UgoWebSocket socket;
            public ManualResetValueTaskSourceCore<bool> Open;
            public ManualResetValueTaskSourceCore<UgoWebSocketMessage> Receive;
            // Unity's corlib (Mono and IL2CPP) marks a core complete before SetResult publishes its continuation slot, and its GetStatus reads
            // only the mark (.NET's also needs the slot published). A consumer polling in that window takes the result and starts the next wait,
            // and the unfinished SetResult then invokes or poisons that wait's continuation. The flag keeps the status Pending until SetResult
            // is done with the core: it returns, or it hands over to the registered continuation. Continuations go through a fixed callback
            // that drops the flag first, so code run by SetResult itself sees the operation complete.
            public bool OpenSignalling, ReceiveSignalling;
            Action<object> openContinuation, receiveContinuation;
            object openState, receiveState;
            static readonly Action<object> OpenSignalled = state =>
            {
                var sources = (Sources)state;
                sources.Signal(ref sources.OpenSignalling, ref sources.openContinuation, ref sources.openState);
            };
            static readonly Action<object> ReceiveSignalled = state =>
            {
                var sources = (Sources)state;
                sources.Signal(ref sources.ReceiveSignalling, ref sources.receiveContinuation, ref sources.receiveState);
            };

            public Sources(UgoWebSocket socket) { this.socket = socket; }

            void Signal(ref bool signalling, ref Action<object> continuation, ref object state)
            {
                Action<object> next;
                object nextState;
                lock (socket.sync)
                {
                    signalling = false;
                    next = continuation;
                    nextState = state;
                    continuation = null;
                    state = null;
                }
                next(nextState);
            }

            bool IValueTaskSource<bool>.GetResult(short token)
            {
                bool value = Open.GetResult(token);
                lock (socket.sync)
                {
                    Open.Reset();
                    socket.openBusy = false;
                }
                return value;
            }

            ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
            {
                lock (socket.sync)
                {
                    ValueTaskSourceStatus status = Open.GetStatus(token);
                    return OpenSignalling ? ValueTaskSourceStatus.Pending : status;
                }
            }

            void IValueTaskSource<bool>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                Open.GetStatus(token);
                openContinuation = continuation;
                openState = state;
                Open.OnCompleted(OpenSignalled, this, token, flags);
            }

            UgoWebSocketMessage IValueTaskSource<UgoWebSocketMessage>.GetResult(short token)
            {
                UgoWebSocketMessage value = Receive.GetResult(token);
                lock (socket.sync)
                {
                    Receive.Reset();
                    socket.receiveBusy = false;
                }
                return value;
            }

            ValueTaskSourceStatus IValueTaskSource<UgoWebSocketMessage>.GetStatus(short token)
            {
                lock (socket.sync)
                {
                    ValueTaskSourceStatus status = Receive.GetStatus(token);
                    return ReceiveSignalling ? ValueTaskSourceStatus.Pending : status;
                }
            }

            void IValueTaskSource<UgoWebSocketMessage>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                Receive.GetStatus(token);
                receiveContinuation = continuation;
                receiveState = state;
                Receive.OnCompleted(ReceiveSignalled, this, token, flags);
            }
        }
    }
}
