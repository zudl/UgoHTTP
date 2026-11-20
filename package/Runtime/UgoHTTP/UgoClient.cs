using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using UgoHttp.Native;

[assembly: InternalsVisibleTo("UgoHttp.Unity")]
[assembly: InternalsVisibleTo("UgoHttp.Editor")]
[assembly: InternalsVisibleTo("UgoHttp.Tests")]
[assembly: InternalsVisibleTo("UgoHttp.EditMode.Tests")]
[assembly: InternalsVisibleTo("UgoHttp.Player")]

namespace UgoHttp
{
    internal struct UgoPins
    {
        MemoryHandle[] handles;
        int count;
        internal unsafe ugo_bytes Pin(ReadOnlyMemory<byte> bytes, int capacity)
        {
            if (bytes.IsEmpty) return default;
            if (handles == null) handles = ArrayPool<MemoryHandle>.Shared.Rent(capacity);
            MemoryHandle pin = bytes.Pin();
            handles[count++] = pin;
            return new ugo_bytes { ptr = (ulong)pin.Pointer, len = (ulong)bytes.Length };
        }
        internal void Free()
        {
            if (handles == null) return;
            for (int i = 0; i < count; i++) handles[i].Dispose();
            ArrayPool<MemoryHandle>.Shared.Return(handles, true);
            this = default;
        }
    }

    internal enum UgoSlotMode { Raw, Callback, Awaitable, WebSocket }

    internal struct UgoSlot
    {
        internal ulong Id;
        internal UgoSlotMode Mode;
        internal bool Received;
        internal bool Delivered;
        internal ugo_ev_completed Result;
        internal UgoPins Pins;
        internal Action<UgoResponse, object> Callback;
        internal object State;
        internal UgoEventHandler Events;
        internal CancellationTokenRegistration Registration;
        internal UgoOperation Operation;
        internal UgoWebSocket Socket;
        internal UgoTrace Trace;
    }

    // One per slot, reused by every operation in it. GetResult resets it: the slot is freed only after that, so the reset never races a pending await.
    internal sealed class UgoOperation : IValueTaskSource<UgoResponse>
    {
        internal static readonly Action<object> AbortRegistered = state =>
        {
            var operation = (UgoOperation)state;
            operation.client.Abort(new UgoId(operation.Id));
        };
        internal ManualResetValueTaskSourceCore<UgoResponse> Core;
        internal ulong Id;
        readonly UgoClient client;
        static readonly Action<object> Signalled = state => ((UgoOperation)state).Signal();
        bool signalling;
        Action<object> continuation;
        object continuationState;
        internal UgoOperation(UgoClient client) { this.client = client; }
        // See UgoWebSocket.Sources for the Unity corlib race the signalling flag closes.
        internal void Complete(UgoResponse response)
        {
            lock (this) signalling = true;
            try { Core.SetResult(response); }
            finally { lock (this) signalling = false; }
        }
        void Signal()
        {
            Action<object> next;
            object state;
            lock (this)
            {
                signalling = false;
                next = continuation;
                state = continuationState;
                continuation = null;
                continuationState = null;
            }
            next(state);
        }
        public UgoResponse GetResult(short token)
        {
            UgoResponse response = Core.GetResult(token);
            Core.Reset();
            return response;
        }
        public ValueTaskSourceStatus GetStatus(short token)
        {
            lock (this)
            {
                ValueTaskSourceStatus status = Core.GetStatus(token);
                return signalling ? ValueTaskSourceStatus.Pending : status;
            }
        }
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            // A stale token has to throw here, before it overwrites the live operation's continuation.
            Core.GetStatus(token);
            this.continuation = continuation;
            continuationState = state;
            Core.OnCompleted(Signalled, this, token, flags);
        }
    }

    internal sealed class UgoHandle : SafeHandle
    {
        internal readonly object Sync = new object();
        internal readonly UgoSlot[] Slots;
        internal UgoHandle(int slots) : base(IntPtr.Zero, true) { Slots = new UgoSlot[slots]; }
        internal void Initialize(IntPtr value) => SetHandle(value);
        public override bool IsInvalid => handle == IntPtr.Zero;
        internal void UnpinAll()
        {
            for (int i = 0; i < Slots.Length; i++) Slots[i].Pins.Free();
        }
        protected override bool ReleaseHandle()
        {
            lock (Sync)
            {
                for (int i = 0; i < Slots.Length; i++) Slots[i].Socket?.MarkClientDisposed();
                NativeMethods.ugo_client_destroy(handle);
                UnpinAll();
            }
            return true;
        }
    }

    /// <summary>
    /// An HTTP and WebSocket client with its own network thread; connections are reused across its requests. All members are safe from any
    /// thread. Every accepted request except a detached one completes exactly once with a <see cref="UgoResponse"/>, which the receiver must
    /// dispose. No retries, cache or host failover: those are the caller's policy. Always <see cref="Dispose"/> it: a started background drain
    /// thread keeps an abandoned client alive. In Unity, clients are disposed for you on domain reload, app quit and, for clients created in
    /// Play Mode, on leaving Play Mode.
    /// </summary>
    public sealed unsafe class UgoClient : IDisposable
    {
        internal static readonly object DomainSync = new object();
        internal static readonly List<WeakReference<UgoClient>> Live = new List<WeakReference<UgoClient>>();
        static readonly Action<UgoResponse, object> InvokePlain = (response, state) => ((Action<UgoResponse>)state)(response);
        static bool initialized;
        static bool closing;
        static int lastTrackingId;
        const int Disposed = int.MinValue;
        readonly UgoHandle handle;
        readonly object drainSync = new object();
        Thread drainThread;
        bool disposeStarted;
        bool disposing;
        UgoTrace trace;
        internal readonly int TrackingId = Interlocked.Increment(ref lastTrackingId);

        /// <summary>Size of the operation table, from <see cref="UgoClientOptions.OperationSlots"/>; <see cref="UgoId.Slot"/> is below it.</summary>
        public int OperationSlots { get; }
        /// <summary>From <see cref="UgoClientOptions.DrainMode"/>.</summary>
        public UgoDrainMode DrainMode { get; }
        /// <summary>
        /// True once <see cref="Dispose"/> has started, including the automatic disposal in Unity. Recreate a shared client when it is null or
        /// disposed. A snapshot: a concurrent <see cref="Dispose"/> can start right after it returns false.
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref disposeStarted);
        /// <summary>Current time on the clock of event and message timestamps, in microseconds. Keeps counting through system sleep; subtract app pause yourself.</summary>
        public static long MonotonicMicroseconds => NativeMethods.ugo_monotonic_us();

        /// <summary>Creates a client with default options.</summary>
        public UgoClient() : this(default(UgoClientOptions)) { }

        /// <summary>
        /// Creates a client and starts its network thread. Throws <see cref="ArgumentException"/> for invalid options, <see cref="OutOfMemoryException"/>
        /// when native resources are exhausted, and <see cref="InvalidOperationException"/> while the domain is unloading or when the native plugin
        /// does not match this assembly.
        /// </summary>
        public UgoClient(in UgoClientOptions options)
        {
            if (options.OperationSlots < 0) throw new ArgumentOutOfRangeException(nameof(options.OperationSlots), "Must not be negative.");
            if (options.MaxConnections < 0) throw new ArgumentOutOfRangeException(nameof(options.MaxConnections), "Must not be negative.");
            if (options.DrainMode != UgoDrainMode.Background && options.DrainMode != UgoDrainMode.Manual)
                throw new ArgumentOutOfRangeException(nameof(options.DrainMode));
            uint dnsSeconds = Units(options.DnsCacheDuration, TimeSpan.TicksPerSecond, false, nameof(options.DnsCacheDuration));
            OperationSlots = options.OperationSlots == 0 ? UgoClientOptions.DefaultOperationSlots : options.OperationSlots;
            DrainMode = options.DrainMode;
            if (UgoTracking.Enabled) trace = UgoTrace.Capture(null, null, 0);
            handle = new UgoHandle(OperationSlots);
            lock (DomainSync)
            {
                if (closing) throw new InvalidOperationException("The domain is unloading; no new clients.");
                if (!initialized)
                {
                    NativeMethods.ugo_shutdown_all();
                    initialized = true;
                }
                var config = new ugo_client_config
                {
                    abi_version = UgoAbi.UGO_ABI_VERSION,
                    max_transfers = (uint)OperationSlots,
                    max_total_connections = (uint)options.MaxConnections,
                    dns_cache_ttl_s = dnsSeconds,
                    flags = options.DangerousAcceptAnyCertificate ? UgoAbi.UGO_CLIENT_TLS_INSECURE_DEV : 0
                };
                ReadOnlySpan<byte> ca = options.CaBundlePem.Span;
                fixed (byte* start = ca)
                {
                    config.ca_bundle = new ugo_bytes { ptr = ca.IsEmpty ? 0 : (ulong)start, len = (ulong)ca.Length };
                    int status = NativeMethods.ugo_client_create(in config, out IntPtr native);
                    if (status != 0)
                    {
                        handle.Dispose();
                        switch (status)
                        {
                            case UgoAbi.UGO_E_NOMEM: throw new OutOfMemoryException("Native client resources could not be allocated.");
                            case UgoAbi.UGO_E_ABI: throw new InvalidOperationException("Native ABI version mismatch.");
                            case UgoAbi.UGO_E_INVALID_ARG:
                                throw options.DangerousAcceptAnyCertificate
                                    ? new ArgumentException("Release builds of the native library refuse it.", nameof(options.DangerousAcceptAnyCertificate))
                                    : new ArgumentException("Native client options rejected.", nameof(options));
                            default: throw new InvalidOperationException("Unknown native status: " + status);
                        }
                    }
                    handle.Initialize(native);
                }
                for (int i = Live.Count - 1; i >= 0; i--)
                    if (!Live[i].TryGetTarget(out _)) Live.RemoveAt(i);
                Live.Add(new WeakReference<UgoClient>(this));
            }
        }

        /// <summary>
        /// Sends a request. The task completes with the response for every outcome: HTTP errors, transport failures and aborts included. Dispose it.
        /// </summary>
        /// <remarks>
        /// Never throws <see cref="OperationCanceledException"/>: a cancelled request completes with <see cref="UgoErrorReason.Caller"/>, or with
        /// its real result if it finished first. Completion means the library no longer touches the request's body bytes or response file.
        /// The continuation follows the usual await rules: a captured <see cref="SynchronizationContext"/> (the Unity main thread) is used,
        /// otherwise it may run on the draining thread. In <see cref="UgoDrainMode.Manual"/> mode nothing completes until
        /// <see cref="TryWaitEvents"/> runs. Await the task once; an unawaited result keeps its slot. In a player, no managed allocation per call
        /// after warm-up, except a cancellable token's registration and posting to a captured context; the Editor tracker allocates per request.
        /// </remarks>
        /// <param name="request">Read before the call returns; only body bytes stay borrowed.</param>
        /// <param name="events">Optional; receives the headers and progress events, with <paramref name="state"/>.</param>
        /// <param name="state">Passed to <paramref name="events"/>.</param>
        /// <param name="cancellationToken">Aborts the request when cancelled, as <see cref="Abort"/> does.</param>
        /// <exception cref="UgoSubmitException">Not accepted, for example <see cref="UgoSubmitStatus.NoCapacity"/>; gate bulk sends on <see cref="OperationSlots"/> instead of catching it.</exception>
        /// <exception cref="ArgumentException">The request is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
        public ValueTask<UgoResponse> SendAsync(in UgoRequest request, UgoEventHandler events = null, object state = null, CancellationToken cancellationToken = default)
        {
            int status = Start(in request, 0, UgoSlotMode.Awaitable, null, state, events, cancellationToken, out ulong _, out UgoOperation operation, out short token);
            if (status != 0) throw new UgoSubmitException(ToStatus(status));
            return new ValueTask<UgoResponse>(operation, token);
        }

        /// <summary>
        /// Sends a request; <paramref name="completed"/> runs exactly once with the response and <paramref name="state"/>, for every outcome, unless
        /// the returned status is not <see cref="UgoSubmitStatus.Accepted"/> (then nothing runs).
        /// </summary>
        /// <remarks>
        /// The callback runs on the draining thread, without the sender's <see cref="ExecutionContext"/>, and must not block. It owns the response
        /// and must dispose it, then or later. An exception escaping it is rethrown on the thread pool. Abort, cancellation and event rules are
        /// those of <see cref="SendAsync"/>. Allocation as for <see cref="SendAsync"/>.
        /// </remarks>
        /// <exception cref="ArgumentException">The request is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
        public UgoSubmitStatus Send(in UgoRequest request, Action<UgoResponse, object> completed, object state, UgoEventHandler events = null, CancellationToken cancellationToken = default)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            return ToStatus(Start(in request, 0, UgoSlotMode.Callback, completed, state, events, cancellationToken, out _, out _, out _));
        }

        /// <summary>
        /// <see cref="Send(in UgoRequest, Action{UgoResponse, object}, object, UgoEventHandler, CancellationToken)"/> without state or events; a
        /// capturing lambda allocates its closure.
        /// </summary>
        public UgoSubmitStatus Send(in UgoRequest request, Action<UgoResponse> completed, CancellationToken cancellationToken = default)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            return Send(in request, InvokePlain, completed, null, cancellationToken);
        }

        /// <summary>
        /// Fire-and-forget: reports only acceptance. Nothing completes, the response is discarded and its slot is freed when the request finishes.
        /// Body bytes are copied, so the caller's buffer is free on return. <see cref="UgoRequest.ResponseFilePath"/> must be null. Works in both
        /// drain modes. Throws <see cref="ArgumentException"/> for an invalid request and <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>.
        /// </summary>
        public UgoSubmitStatus EnqueueDetached(in UgoRequest request) =>
            ToStatus(Start(in request, UgoAbi.UGO_REQ_DETACHED, UgoSlotMode.Raw, null, null, null, default, out _, out _, out _));

        /// <summary>
        /// <see cref="UgoDrainMode.Manual"/> mode only (else throws <see cref="InvalidOperationException"/>): starts a request whose events,
        /// completion included, come out of <see cref="TryWaitEvents"/>. After its <see cref="UgoEventKind.Completed"/> event take the response
        /// with <see cref="TryGetResponse"/> and dispose it. Body bytes stay borrowed until that event is drained.
        /// </summary>
        public UgoSubmitStatus Submit(in UgoRequest request, out UgoId id)
        {
            if (DrainMode != UgoDrainMode.Manual) throw new InvalidOperationException("Submit needs a client in manual drain mode.");
            int status = Start(in request, 0, UgoSlotMode.Raw, null, null, null, default, out ulong value, out _, out _);
            id = new UgoId(value);
            return ToStatus(status);
        }

        /// <summary>
        /// Opens a WebSocket and returns at once; the handshake runs in the background. With <paramref name="listener"/>, events go to it on
        /// the draining thread; without one, use <see cref="UgoWebSocket.WaitOpenAsync"/> and <see cref="UgoWebSocket.ReceiveAsync"/>. The socket
        /// ends exactly once, also when refused, failed, aborted or shut down, and must be disposed to free its slot.
        /// </summary>
        /// <exception cref="UgoSubmitException">Not accepted.</exception>
        /// <exception cref="ArgumentException">An invalid request: a scheme other than ws/wss, a <c>Sec-WebSocket-Key</c> or <c>Sec-WebSocket-Extensions</c> header or out-of-range limits.</exception>
        /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
        public UgoWebSocket OpenWebSocket(in UgoWebSocketRequest request, UgoWebSocketListener listener = null)
        {
            if (string.IsNullOrEmpty(request.Url)) throw new ArgumentException("Url is required.", nameof(request.Url));
            NoNul(request.Url, nameof(request.Url));
            ValidateHeaders(request.Headers.Span, "Sec-WebSocket-Key", "Sec-WebSocket-Extensions");
            uint connect = Units(request.ConnectTimeout, TimeSpan.TicksPerMillisecond, true, nameof(request.ConnectTimeout));
            TimeSpan? open = request.OpenTimeout;
            uint openMs = open.HasValue && open.Value != Timeout.InfiniteTimeSpan ? Units(open.Value, TimeSpan.TicksPerMillisecond, true, nameof(request.OpenTimeout)) : 0;
            uint close = Units(request.CloseTimeout, TimeSpan.TicksPerMillisecond, true, nameof(request.CloseTimeout));
            if (request.MaxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(request.MaxMessageBytes), "Must be above 0.");
            if (request.MaxBacklogMessages <= 0) throw new ArgumentOutOfRangeException(nameof(request.MaxBacklogMessages), "Must be above 0.");
            if (request.MaxBacklogBytes < request.MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(request.MaxBacklogBytes), "Must be at least MaxMessageBytes.");
            if (request.MaxSendBytes <= 8) throw new ArgumentOutOfRangeException(nameof(request.MaxSendBytes), "Must be above 8.");
            byte[] text = ArrayPool<byte>.Shared.Rent(Math.Max(1, checked(Utf8Size(request.Url) + HeaderBlockSize(request.Headers.Span, null))));
            UgoTrace trace = UgoTracking.Enabled ? UgoTrace.Capture(null, request.Url, 0) : default;
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                lock (handle.Sync)
                {
                    fixed (byte* start = text)
                    {
                        int offset = 0;
                        var native = new ugo_ws_request
                        {
                            url = Encode(request.Url, text, start, ref offset),
                            headers = EncodeHeaders(request.Headers.Span, null, text, start, ref offset),
                            connect_timeout_ms = connect,
                            open_timeout_ms = openMs,
                            close_timeout_ms = close,
                            max_message_bytes = (uint)request.MaxMessageBytes,
                            max_backlog_bytes = (ulong)request.MaxBacklogBytes,
                            max_backlog_count = (uint)request.MaxBacklogMessages,
                            max_send_bytes = (uint)request.MaxSendBytes
                        };
                        int status = NativeMethods.ugo_ws_open(handle, in native, out ulong id);
                        if (status == UgoAbi.UGO_E_INVALID_ARG)
                            throw new ArgumentException("Rejected by the native layer: a malformed URL, a scheme other than ws/wss or limits too large.", nameof(request.Url));
                        if (status != 0) throw new UgoSubmitException(ToStatus(status));
                        var socket = new UgoWebSocket(this, id, listener, request.MaxBacklogMessages, request.MaxSendBytes);
                        ref UgoSlot slot = ref handle.Slots[(uint)id];
                        slot.Id = id;
                        slot.Mode = UgoSlotMode.WebSocket;
                        slot.Received = false;
                        slot.Delivered = false;
                        slot.Socket = socket;
                        slot.Trace = trace;
                        if (DrainMode == UgoDrainMode.Background && drainThread == null) StartDrainThread();
                        return socket;
                    }
                }
            }
            finally
            {
                if (added) handle.DangerousRelease();
                ArrayPool<byte>.Shared.Return(text, true);
            }
        }

        internal int WebSocketSend(ulong id, uint type, byte* data, uint length)
        {
            try { return NativeMethods.ugo_ws_send(handle, id, type, data, length); }
            catch (ObjectDisposedException) { return UgoAbi.UGO_E_STALE_ID; }
        }

        internal int WebSocketClose(ulong id, uint code, byte* reason, uint length)
        {
            try { return NativeMethods.ugo_ws_close(handle, id, code, reason, length); }
            catch (ObjectDisposedException) { return UgoAbi.UGO_E_STALE_ID; }
        }

        internal void WebSocketRelease(ulong id, ulong sequence)
        {
            try { NativeMethods.ugo_ws_release(handle, id, sequence); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Requests an abort and returns at once. The operation still completes exactly once: with <see cref="UgoErrorReason.Caller"/>, or with its
        /// real result if it was already finishing. An id that is finished, released or unknown is ignored, as is a disposed client.
        /// </summary>
        public void Abort(UgoId id)
        {
            try { NativeMethods.ugo_abort(handle, id.Value); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// <see cref="UgoDrainMode.Manual"/> mode only: waits up to <paramref name="millisecondsTimeout"/> for events (0 polls) and dispatches them on
        /// this thread. Callbacks, event handlers, listeners and awaitable completions of <c>Send</c>, <c>SendAsync</c> and WebSockets run inside
        /// the call; an exception escaping one is rethrown on the thread pool, not here. Events of <see cref="Submit"/> operations are copied into
        /// <paramref name="events"/>, <paramref name="count"/> of them. No allocation.
        /// </summary>
        /// <returns>False once the client is shut down and every event was drained, or after <see cref="Dispose"/>; then stop calling.</returns>
        /// <exception cref="InvalidOperationException">
        /// Not in manual mode, or another drain of this client is running: one drainer at a time, and never from a callback of this drain. While
        /// <see cref="Dispose"/> runs, a concurrent call returns false instead.
        /// </exception>
        public bool TryWaitEvents(Span<UgoEvent> events, int millisecondsTimeout, out int count)
        {
            count = 0;
            if (DrainMode != UgoDrainMode.Manual) throw new InvalidOperationException("Draining needs a client in manual drain mode.");
            if (events.IsEmpty) throw new ArgumentException("Buffer is empty.", nameof(events));
            if (millisecondsTimeout < 0) throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            if (Monitor.IsEntered(drainSync)) throw new InvalidOperationException("Another drain of this client is running.");
            if (!Monitor.TryEnter(drainSync))
            {
                lock (handle.Sync)
                    if (disposing) return false;
                throw new InvalidOperationException("Another drain of this client is running.");
            }
            try
            {
                int drained = Wait(events, (uint)millisecondsTimeout);
                if (drained == UgoAbi.UGO_E_SHUTDOWN || drained == Disposed) return false;
                if (drained < 0) throw new InvalidOperationException("Unknown native status: " + drained);
                Absorb(events.Slice(0, drained));
                for (int i = 0; i < drained; i++)
                {
                    if (Dispatch(ref events[i])) continue;
                    if (count != i) events[count] = events[i];
                    count++;
                }
                return true;
            }
            finally { Monitor.Exit(drainSync); }
        }

        /// <summary>
        /// The response of a <see cref="Submit"/> operation once its <see cref="UgoEventKind.Completed"/> event was drained; false before that.
        /// Repeated calls return the same result.
        /// </summary>
        /// <exception cref="ArgumentException">A released or unknown id.</exception>
        /// <exception cref="InvalidOperationException">An id of a <c>Send</c>/<c>SendAsync</c> operation, whose response goes to its callback or awaiter.</exception>
        public bool TryGetResponse(UgoId id, out UgoResponse response)
        {
            response = default;
            lock (handle.Sync)
            {
                ref UgoSlot slot = ref SlotOf(id.Value);
                if (slot.Id != id.Value || id.Value == 0) throw new ArgumentException("Released or unknown id.", nameof(id));
                if (slot.Mode != UgoSlotMode.Raw) throw new InvalidOperationException("The response goes to the operation's callback or awaiter.");
                if (!slot.Received) return false;
                response = new UgoResponse(this, id.Value, in slot.Result);
                return true;
            }
        }

        /// <summary>
        /// Aborts every operation (each completes with <see cref="UgoErrorReason.Shutdown"/> unless it already finished), stops the network thread
        /// and refuses later requests with <see cref="UgoSubmitStatus.Shutdown"/>. Responses stay readable until <see cref="Dispose"/>. In
        /// background mode it returns after every completion was delivered, unless called from the draining thread; in manual mode the next
        /// <see cref="TryWaitEvents"/> calls or <see cref="Dispose"/> deliver them. Idempotent.
        /// </summary>
        public void Shutdown()
        {
            try { NativeMethods.ugo_client_shutdown(handle); }
            catch (ObjectDisposedException) { return; }
            Thread drainer;
            lock (handle.Sync)
            {
                handle.UnpinAll();
                drainer = drainThread;
            }
            if (drainer != null && drainer != Thread.CurrentThread) drainer.Join();
        }

        /// <summary>
        /// <see cref="Shutdown"/>, then delivers every completion not delivered yet and frees all native memory: spans of every response, message and
        /// handshake of this client become invalid. Callbacks and event handlers may still run during the call; headers and progress events left
        /// after shutdown are dropped. Waits for a drain running on another
        /// thread to return; safe to call from a callback, event handler or continuation. Afterwards <c>Send</c>, <c>SendAsync</c>, <see cref="EnqueueDetached"/>,
        /// <see cref="Submit"/> and <see cref="OpenWebSocket"/> throws <see cref="ObjectDisposedException"/>, and <see cref="TryWaitEvents"/> returns false.
        /// </summary>
        public void Dispose() => Close(false);

        internal void Reclaim() => Close(true);

        void Close(bool reclaimed)
        {
            Volatile.Write(ref disposeStarted, true);
            Shutdown();
            lock (handle.Sync) disposing = true;
            bool entered = false;
            if (!Monitor.IsEntered(drainSync)) Monitor.Enter(drainSync, ref entered);
            try
            {
                DrainLeftovers();
                DeliverReceived();
                if (UgoTracking.Enabled) CompleteTracked(reclaimed);
                handle.Dispose();
            }
            finally
            {
                if (entered) Monitor.Exit(drainSync);
            }
        }

        internal static void ShutdownForDomainUnload()
        {
            var clients = new List<UgoClient>();
            lock (DomainSync)
            {
                closing = true;
                foreach (WeakReference<UgoClient> live in Live)
                    if (live.TryGetTarget(out UgoClient client)) clients.Add(client);
                Live.Clear();
            }
            foreach (UgoClient client in clients) client.Dispose();
        }

        internal void Release(ulong id)
        {
            lock (handle.Sync)
            {
                int status;
                try { status = NativeMethods.ugo_release(handle, id); }
                catch (ObjectDisposedException) { return; }
                if (status == UgoAbi.UGO_E_STALE_ID) return;
                if (status != 0) throw new InvalidOperationException("Native release failed: " + status);
                ref UgoSlot slot = ref SlotOf(id);
                if (slot.Id != id) return;
                if (slot.Trace != null) UgoTracking.Complete(Row(in slot, UgoTracking.ClosedState(in slot.Result), 0, Stopwatch.GetTimestamp()));
                slot.Pins.Free();
                slot.Id = 0;
                slot.Received = false;
                slot.Delivered = false;
                slot.Mode = UgoSlotMode.Raw;
                slot.Socket = null;
                slot.Trace = default;
            }
        }

        internal void Track(List<UgoTrackedRow> rows)
        {
            lock (handle.Sync)
            {
                if (disposing || handle.IsClosed) return;
                rows.Add(new UgoTrackedRow(TrackingId, 0, "Client", "Not disposed", 0, trace, 0, false, default));
                for (int i = 0; i < handle.Slots.Length; i++)
                {
                    ref UgoSlot slot = ref handle.Slots[i];
                    if (slot.Id == 0) continue;
                    string state;
                    long held = 0;
                    switch (slot.Mode)
                    {
                        case UgoSlotMode.WebSocket:
                            UgoWebSocket socket = slot.Socket;
                            if (socket.IsDisposed) continue;
                            held = socket.HeldMessages;
                            UgoWebSocketState socketState = socket.State;
                            state = socketState == UgoWebSocketState.Connecting ? "Connecting"
                                : socketState == UgoWebSocketState.Open ? "Open" : "Closed, not disposed";
                            break;
                        case UgoSlotMode.Raw:
                            state = slot.Received ? "Response not disposed" : "In flight";
                            break;
                        default:
                            state = slot.Delivered ? "Response not disposed" : "In flight";
                            break;
                    }
                    rows.Add(Row(in slot, state, held, 0));
                }
            }
        }

        UgoTrackedRow Row(in UgoSlot slot, string state, long held, long ended)
        {
            string kind = slot.Mode == UgoSlotMode.WebSocket ? "WebSocket" : slot.Mode == UgoSlotMode.Raw ? "Submit"
                : slot.Mode == UgoSlotMode.Awaitable ? "SendAsync" : "Send";
            return new UgoTrackedRow(TrackingId, slot.Id, kind, state, held, slot.Trace, ended, slot.Received, in slot.Result);
        }

        void CompleteTracked(bool reclaimed)
        {
            lock (handle.Sync)
            {
                long now = Stopwatch.GetTimestamp();
                for (int i = 0; i < handle.Slots.Length; i++)
                {
                    ref UgoSlot slot = ref handle.Slots[i];
                    if (slot.Id == 0 || slot.Trace == null) continue;
                    string state = slot.Mode == UgoSlotMode.WebSocket && slot.Socket.IsDisposed ? UgoTracking.ClosedState(in slot.Result)
                        : reclaimed ? UgoTracking.Reclaimed : UgoTracking.ClosedWithClient;
                    UgoTracking.Complete(Row(in slot, state, 0, now));
                    slot.Trace = null;
                }
                if (trace == null) return;
                UgoTracking.Complete(new UgoTrackedRow(TrackingId, 0, "Client", reclaimed ? UgoTracking.Reclaimed : UgoTracking.Disposed, 0, trace, now, false, default));
                trace = null;
            }
        }

        internal ugo_result GetResult(ulong id, string owner)
        {
            int status = NativeMethods.ugo_get_result(handle, id, out ugo_result result);
            if (status == UgoAbi.UGO_E_STALE_ID) throw new ObjectDisposedException(owner);
            if (status != 0) throw new InvalidOperationException("Native result unavailable: " + status);
            return result;
        }

        ref UgoSlot SlotOf(ulong id)
        {
            uint slot = (uint)id;
            return ref handle.Slots[slot < (uint)handle.Slots.Length ? slot : 0];
        }

        int Start(in UgoRequest request, uint flags, UgoSlotMode mode, Action<UgoResponse, object> callback, object state, UgoEventHandler events,
            CancellationToken cancellationToken, out ulong id, out UgoOperation operation, out short token)
        {
            id = 0;
            operation = null;
            token = 0;
            bool detached = flags != 0;
            Validate(in request, detached);
            if (request.KeepPartialFile) flags |= UgoAbi.UGO_REQ_KEEP_PARTIAL;
            if (request.FinishInBackground) flags |= UgoAbi.UGO_REQ_FINISH_IN_BACKGROUND;
            if (request.Resume) flags |= UgoAbi.UGO_REQ_RESUME;
            UgoRequestBody body = request.Body;
            ReadOnlySpan<UgoFormPart> parts = body.Kind == UgoBodyKind.Multipart ? body.Parts.Span : default;
            string method = request.Method.Method;
            int bytes = checked(method.Length + Utf8Size(request.Url) + HeaderBlockSize(request.Headers.Span, request.Body.ContentType) + Utf8Size(request.ResponseFilePath) + Utf8Size(body.FilePath));
            for (int i = 0; i < parts.Length; i++)
                bytes = checked(bytes + Utf8Size(parts[i].Name) + Utf8Size(parts[i].FileName) + Utf8Size(parts[i].ContentType) + Utf8Size(parts[i].FilePath));
            byte[] text = ArrayPool<byte>.Shared.Rent(Math.Max(1, bytes));
            ugo_mime_part[] nativeParts = parts.IsEmpty ? null : ArrayPool<ugo_mime_part>.Shared.Rent(parts.Length);
            UgoPins pins = default;
            UgoTrace trace = !detached && UgoTracking.Enabled ? UgoTrace.Capture(method, request.Url, 1) : default;
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                lock (handle.Sync)
                {
                    fixed (byte* start = text)
                    fixed (ugo_mime_part* partsStart = nativeParts)
                    {
                        int offset = 0;
                        TimeSpan? total = request.TotalTimeout;
                        uint stall = StallSeconds(in request);
                        var native = new ugo_request
                        {
                            method = Encode(method, text, start, ref offset),
                            body_kind = body.Kind == UgoBodyKind.None && !Bodiless(method) ? UgoAbi.UGO_BODY_MEMORY : (uint)body.Kind,
                            url = Encode(request.Url, text, start, ref offset),
                            headers = EncodeHeaders(request.Headers.Span, request.Body.ContentType, text, start, ref offset),
                            sink_path = Encode(request.ResponseFilePath, text, start, ref offset),
                            body = body.Kind == UgoBodyKind.Memory ? pins.Pin(body.Memory, parts.Length + 1) : Encode(body.FilePath, text, start, ref offset),
                            parts = (ulong)partsStart,
                            part_count = (uint)parts.Length,
                            memory_limit = (ulong)request.MaxResponseBodyBytes,
                            max_recv_bytes_per_s = (ulong)request.MaxReceiveBytesPerSecond,
                            max_send_bytes_per_s = (ulong)request.MaxSendBytesPerSecond,
                            low_speed_bytes_per_s = stall == 0 ? 0 : (uint)Math.Max(1, request.MinimumSpeedBytesPerSecond),
                            low_speed_time_s = stall,
                            connect_timeout_ms = Units(request.ConnectTimeout, TimeSpan.TicksPerMillisecond, true, nameof(request.ConnectTimeout)),
                            total_timeout_ms = total.HasValue && total.Value != Timeout.InfiniteTimeSpan
                                ? Units(total.Value, TimeSpan.TicksPerMillisecond, true, nameof(request.TotalTimeout)) : 0,
                            max_redirects = (uint)request.MaxRedirects,
                            flags = flags
                        };
                        for (int i = 0; i < parts.Length; i++)
                        {
                            UgoFormPart p = parts[i];
                            nativeParts[i] = new ugo_mime_part
                            {
                                name = Encode(p.Name, text, start, ref offset),
                                filename = Encode(p.FileName, text, start, ref offset),
                                content_type = Encode(p.ContentType, text, start, ref offset),
                                data = p.FilePath == null ? pins.Pin(p.Bytes, parts.Length + 1) : Encode(p.FilePath, text, start, ref offset),
                                source = p.FilePath == null ? UgoAbi.UGO_PART_MEMORY : UgoAbi.UGO_PART_FILE
                            };
                        }
                        int status = NativeMethods.ugo_submit(handle, in native, out id);
                        if (status == UgoAbi.UGO_E_INVALID_ARG)
                            throw new ArgumentException("Rejected by the native layer: a malformed URL or a scheme other than http/https.", nameof(request.Url));
                        if (status != 0 || detached) return status;
                        ref UgoSlot slot = ref handle.Slots[(uint)id];
                        slot.Id = id;
                        slot.Mode = mode;
                        slot.Received = false;
                        slot.Delivered = false;
                        slot.Pins = pins;
                        pins = default;
                        slot.Callback = callback;
                        slot.State = state;
                        slot.Events = events;
                        slot.Trace = trace;
                        if (mode == UgoSlotMode.Awaitable || (mode == UgoSlotMode.Callback && cancellationToken.CanBeCanceled))
                        {
                            operation = slot.Operation ?? (slot.Operation = new UgoOperation(this));
                            operation.Id = id;
                            if (mode == UgoSlotMode.Awaitable) token = operation.Core.Version;
                            if (cancellationToken.CanBeCanceled)
                                slot.Registration = cancellationToken.Register(UgoOperation.AbortRegistered, operation);
                        }
                        if (mode != UgoSlotMode.Raw && DrainMode == UgoDrainMode.Background && drainThread == null) StartDrainThread();
                        return 0;
                    }
                }
            }
            finally
            {
                pins.Free();
                if (added) handle.DangerousRelease();
                if (nativeParts != null) ArrayPool<ugo_mime_part>.Shared.Return(nativeParts, true);
                ArrayPool<byte>.Shared.Return(text, true);
            }
        }

        void StartDrainThread()
        {
            drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "ugo-drain" };
            if (ExecutionContext.IsFlowSuppressed())
            {
                drainThread.Start();
                return;
            }
            using (ExecutionContext.SuppressFlow()) drainThread.Start();
        }

        int Wait(Span<UgoEvent> events, uint timeoutMs)
        {
            try
            {
                fixed (UgoEvent* buffer = events)
                    return NativeMethods.ugo_wait_events(handle, buffer, (uint)events.Length, timeoutMs);
            }
            catch (ObjectDisposedException) { return Disposed; }
        }

        void Absorb(Span<UgoEvent> batch)
        {
            lock (handle.Sync)
                for (int i = 0; i < batch.Length; i++)
                {
                    if (batch[i].Kind != UgoEventKind.Completed) continue;
                    ulong id = batch[i].Native.id;
                    ref UgoSlot slot = ref SlotOf(id);
                    if (slot.Id != id || slot.Received) continue;
                    slot.Pins.Free();
                    slot.Result = batch[i].Completed;
                    slot.Received = true;
                }
        }

        bool Dispatch(ref UgoEvent e)
        {
            ulong id = e.Native.id;
            bool completed = e.Kind == UgoEventKind.Completed;
            UgoSlot taken = default;
            UgoEventHandler handler = null;
            object state = null;
            UgoWebSocket socket = null;
            lock (handle.Sync)
            {
                ref UgoSlot slot = ref SlotOf(id);
                if (slot.Id != id) return true;
                if (slot.Trace != null)
                {
                    if (e.Kind == UgoEventKind.Headers) slot.Trace.ResumeOffset = e.Headers.StatusCode == 206 ? e.Headers.ResumeOffset : 0;
                    else if (e.Kind == UgoEventKind.Progress)
                    {
                        UgoProgress progress = e.Progress;
                        slot.Trace.Received = (ulong)(progress.BytesReceived - slot.Trace.ResumeOffset);
                        slot.Trace.Sent = (ulong)progress.BytesSent;
                    }
                }
                if (slot.Mode == UgoSlotMode.Raw) return false;
                if (slot.Delivered || (!completed && disposing)) return true;
                if (slot.Mode == UgoSlotMode.WebSocket && !completed)
                    socket = slot.Socket;
                else if (completed)
                {
                    slot.Delivered = true;
                    taken = Take(ref slot);
                }
                else
                {
                    handler = slot.Events;
                    state = slot.State;
                }
            }
            if (completed) Deliver(in taken, id);
            else if (socket != null) socket.Dispatch(ref e);
            else if (handler != null)
            {
                try { handler(in e, state); }
                catch (Exception error) { Unhandled(error); }
            }
            return true;
        }

        static UgoSlot Take(ref UgoSlot slot)
        {
            UgoSlot taken = slot;
            slot.Callback = null;
            slot.State = null;
            slot.Events = null;
            slot.Registration = default;
            return taken;
        }

        void Deliver(in UgoSlot taken, ulong id)
        {
            if (taken.Mode == UgoSlotMode.WebSocket)
            {
                taken.Socket.Complete(in taken.Result);
                return;
            }
            taken.Registration.Dispose();
            var response = new UgoResponse(this, id, in taken.Result);
            try
            {
                if (taken.Mode == UgoSlotMode.Awaitable) taken.Operation.Complete(response);
                else taken.Callback(response, taken.State);
            }
            catch (Exception error) { Unhandled(error); }
        }

        internal static void Unhandled(Exception error)
        {
            ThreadPool.UnsafeQueueUserWorkItem(state => ((ExceptionDispatchInfo)state).Throw(), ExceptionDispatchInfo.Capture(error));
        }

        void DrainLoop()
        {
            var events = new UgoEvent[32];
            lock (drainSync)
                while (true)
                {
                    int drained = Wait(events, 1000);
                    if (drained < 0) return;
                    Absorb(events.AsSpan(0, drained));
                    for (int i = 0; i < drained; i++) Dispatch(ref events[i]);
                }
        }

        void DrainLeftovers()
        {
            Span<UgoEvent> events = stackalloc UgoEvent[16];
            while (true)
            {
                int drained = Wait(events, 0);
                if (drained <= 0) return;
                Absorb(events.Slice(0, drained));
                for (int i = 0; i < drained; i++) Dispatch(ref events[i]);
            }
        }

        void DeliverReceived()
        {
            for (int i = 0; i < handle.Slots.Length; i++)
            {
                UgoSlot taken;
                ulong id;
                lock (handle.Sync)
                {
                    ref UgoSlot slot = ref handle.Slots[i];
                    if (slot.Id == 0 || !slot.Received || slot.Delivered || slot.Mode == UgoSlotMode.Raw) continue;
                    slot.Delivered = true;
                    id = slot.Id;
                    taken = Take(ref slot);
                }
                Deliver(in taken, id);
            }
        }

        static void Validate(in UgoRequest request, bool detached)
        {
            if (string.IsNullOrEmpty(request.Url)) throw new ArgumentException("Url is required.", nameof(request.Url));
            NoNul(request.Url, nameof(request.Url));
            NoNul(request.ResponseFilePath, nameof(request.ResponseFilePath));
            if (request.Method == null) throw new ArgumentException("Method is required.", nameof(request.Method));
            string method = request.Method.Method;
            if (method.Length > UgoAbi.UGO_METHOD_MAX)
                throw new ArgumentException("Method is not an HTTP token of at most " + UgoAbi.UGO_METHOD_MAX + " characters.", nameof(request.Method));
            if (method == "CONNECT") throw new ArgumentException("CONNECT is not supported.", nameof(request.Method));
            UgoRequestBody body = request.Body;
            if (method == "HEAD" && body.Kind != UgoBodyKind.None) throw new ArgumentException("HEAD cannot carry a body.", nameof(request.Body));
            NoNul(body.FilePath, nameof(request.Body));
            if (body.ContentType != null && !ValidValue(body.ContentType)) throw new ArgumentException("Content type contains CR, LF or NUL.", nameof(request.Body));
            ReadOnlySpan<UgoFormPart> parts = body.Parts.Span;
            for (int i = 0; i < parts.Length; i++)
            {
                UgoFormPart p = parts[i];
                if (string.IsNullOrEmpty(p.Name)) throw new ArgumentException("A form part needs a name.", nameof(request.Body));
                if (!ValidValue(p.Name) || !ValidValue(p.FileName) || !ValidValue(p.ContentType) || !ValidValue(p.FilePath))
                    throw new ArgumentException("A form part contains CR, LF or NUL.", nameof(request.Body));
            }
            if (detached && request.ResponseFilePath != null) throw new ArgumentException("A detached request has no response to write.", nameof(request.ResponseFilePath));
            if (!detached && request.ResponseFilePath == null && request.MaxResponseBodyBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(request.MaxResponseBodyBytes), "Must be above 0 for a memory response.");
            if (request.Resume && (request.ResponseFilePath == null || method == "HEAD"))
                throw new ArgumentException("Resume needs a ResponseFilePath and a method other than HEAD.", nameof(request.Resume));
            if (request.MaxReceiveBytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(request.MaxReceiveBytesPerSecond));
            if (request.MaxSendBytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(request.MaxSendBytesPerSecond));
            if (request.MinimumSpeedBytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(request.MinimumSpeedBytesPerSecond));
            if (StallSeconds(in request) == 0 && request.MinimumSpeedBytesPerSecond != 0)
                throw new ArgumentException("MinimumSpeedBytesPerSecond needs a StallTimeout.", nameof(request.MinimumSpeedBytesPerSecond));
            if (request.MaxRedirects < 0) throw new ArgumentOutOfRangeException(nameof(request.MaxRedirects));
            Units(request.ConnectTimeout, TimeSpan.TicksPerMillisecond, true, nameof(request.ConnectTimeout));
            if (request.TotalTimeout.HasValue && request.TotalTimeout.Value != Timeout.InfiniteTimeSpan) Units(request.TotalTimeout.Value, TimeSpan.TicksPerMillisecond, true, nameof(request.TotalTimeout));
            ValidateHeaders(request.Headers.Span, body.ContentType != null || body.Kind == UgoBodyKind.Multipart ? "Content-Type" : null,
                request.Resume ? "Range" : null);
        }

        static void ValidateHeaders(ReadOnlySpan<UgoHeader> headers, string reserved, string alsoReserved)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                UgoHeader header = headers[i];
                if (!ValidName(header.Name)) throw new ArgumentException("Header name at index " + i + " is not an HTTP token.", "Headers");
                if (!ValidValue(header.Value)) throw new ArgumentException("Header '" + header.Name + "' contains CR, LF or NUL.", "Headers");
                if ((reserved != null && string.Equals(header.Name, reserved, StringComparison.OrdinalIgnoreCase)) ||
                    (alsoReserved != null && string.Equals(header.Name, alsoReserved, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("This request cannot carry a caller-set " + header.Name + " header; remove it.", "Headers");
            }
        }

        static bool Bodiless(string method) =>
            method == "GET" || method == "HEAD" || method == "DELETE" || method == "OPTIONS";

        static uint StallSeconds(in UgoRequest request)
        {
            TimeSpan? stall = request.StallTimeout;
            if (!stall.HasValue || stall.Value == Timeout.InfiniteTimeSpan) return 0;
            uint seconds = Units(stall.Value, TimeSpan.TicksPerSecond, true, nameof(request.StallTimeout));
            if (seconds > 65535) throw new ArgumentOutOfRangeException(nameof(request.StallTimeout), "Must not exceed 65535 seconds after rounding.");
            return seconds;
        }

        static uint Units(TimeSpan value, long ticksPerUnit, bool positive, string field)
        {
            long ticks = value.Ticks;
            if (ticks < 0 || (positive && ticks == 0)) throw new ArgumentOutOfRangeException(field, positive ? "Must be above zero." : "Must not be negative.");
            long units = ticks / ticksPerUnit + (ticks % ticksPerUnit == 0 ? 0 : 1);
            if (units > uint.MaxValue) throw new ArgumentOutOfRangeException(field, "Too large.");
            return (uint)units;
        }

        static void NoNul(string value, string field)
        {
            if (value != null && value.IndexOf('\0') >= 0) throw new ArgumentException("Contains NUL.", field);
        }

        static bool ValidValue(string value)
        {
            if (value == null) return true;
            foreach (char c in value)
                if (c == '\r' || c == '\n' || c == '\0') return false;
            return true;
        }

        static bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
            {
                bool token = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0;
                if (!token) return false;
            }
            return true;
        }

        static int HeaderBlockSize(ReadOnlySpan<UgoHeader> headers, string contentType)
        {
            int size = 0;
            for (int i = 0; i < headers.Length; i++)
            {
                UgoHeader header = headers[i];
                size = checked(size + header.Name.Length + 3 + Utf8Size(header.Value));
            }
            return contentType == null ? size : checked(size + 15 + Utf8Size(contentType));
        }

        // curl drops a header written as "Name:" with nothing after it; "Name;" is how it sends an empty value.
        static ugo_bytes EncodeHeaders(ReadOnlySpan<UgoHeader> headers, string contentType, byte[] buffer, byte* start, ref int offset)
        {
            int begin = offset;
            for (int i = 0; i < headers.Length; i++)
            {
                UgoHeader header = headers[i];
                AppendHeader(header.Name, header.Value, buffer, ref offset);
            }
            if (contentType != null) AppendHeader("Content-Type", contentType, buffer, ref offset);
            return offset == begin ? default : new ugo_bytes { ptr = (ulong)(start + begin), len = (ulong)(offset - begin) };
        }

        static void AppendHeader(string name, string value, byte[] buffer, ref int offset)
        {
            offset += Encoding.ASCII.GetBytes(name, 0, name.Length, buffer, offset);
            if (string.IsNullOrEmpty(value))
                buffer[offset++] = (byte)';';
            else
            {
                buffer[offset++] = (byte)':';
                buffer[offset++] = (byte)' ';
                offset += Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, offset);
            }
            buffer[offset++] = (byte)'\n';
        }

        static int Utf8Size(string value) => value == null ? 0 : Encoding.UTF8.GetByteCount(value);

        static ugo_bytes Encode(string value, byte[] buffer, byte* start, ref int offset)
        {
            if (string.IsNullOrEmpty(value)) return default;
            int length = Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, offset);
            var result = new ugo_bytes { ptr = (ulong)(start + offset), len = (ulong)length };
            offset += length;
            return result;
        }

        static UgoSubmitStatus ToStatus(int code)
        {
            switch (code)
            {
                case 0: return UgoSubmitStatus.Accepted;
                case UgoAbi.UGO_E_CAPACITY: return UgoSubmitStatus.NoCapacity;
                case UgoAbi.UGO_E_SHUTDOWN: return UgoSubmitStatus.Shutdown;
                case UgoAbi.UGO_E_NOMEM: return UgoSubmitStatus.NoMemory;
                default: throw new InvalidOperationException("Unknown native status: " + code);
            }
        }
    }
}
