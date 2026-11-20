using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using UgoHttp.Native;

namespace UgoHttp
{
    /// <summary>Whether the client accepted an operation. Only acceptance: the HTTP outcome comes later in <see cref="UgoResponse"/>.</summary>
    public enum UgoSubmitStatus
    {
        /// <summary>Accepted; the operation will complete exactly once.</summary>
        Accepted = 0,
        /// <summary>
        /// Every one of the client's <see cref="UgoClient.OperationSlots"/> is held: by a running request, by a finished one whose response is not
        /// disposed, or by a WebSocket not disposed. Nothing was started. Under steady load this usually means a missing <c>Dispose</c>.
        /// </summary>
        NoCapacity = 1,
        /// <summary>The client is shut down. Nothing was started.</summary>
        Shutdown = 2,
        /// <summary>Native memory for the operation could not be allocated. Nothing was started.</summary>
        NoMemory = 3
    }

    /// <summary>Which thread delivers a client's completions and events. Fixed per client.</summary>
    public enum UgoDrainMode
    {
        /// <summary>
        /// A library thread per client, started by the first <c>Send</c>, <c>SendAsync</c> or <c>OpenWebSocket</c>, runs callbacks, listeners and
        /// awaitable completions. <see cref="UgoClient.Submit"/> and <see cref="UgoClient.TryWaitEvents"/> throw.
        /// </summary>
        Background = 0,
        /// <summary>
        /// The caller calls <see cref="UgoClient.TryWaitEvents"/> on a thread it picks; callbacks, listeners and awaitable completions run inside
        /// that call. Nothing completes while nobody drains.
        /// </summary>
        Manual = 1
    }

    /// <summary>Kind of a drained event.</summary>
    public enum UgoEventKind
    {
        /// <summary>Status and headers of the final response (after redirects) are complete; the body follows.</summary>
        Headers = 1,
        /// <summary>Bytes sent or received changed. Intermediate progress updates are published at most once every 10 ms per operation; a count held by that limit is published before <see cref="Completed"/>, regardless of the interval, so the last progress carries the final byte counts.</summary>
        Progress = 2,
        /// <summary>The operation finished. For a <see cref="UgoClient.Submit"/> operation, take the response with <see cref="UgoClient.TryGetResponse"/>.</summary>
        Completed = 3,
        /// <summary>A WebSocket message. Goes to the socket; never returned by <see cref="UgoClient.TryWaitEvents"/>.</summary>
        WebSocketMessage = 4
    }

    /// <summary>Stable failure class of a finished operation; branch retry policy on it.</summary>
    public enum UgoErrorKind
    {
        /// <summary>Success: the final status is 2xx, or 416 for a <see cref="UgoRequest.Resume"/> whose file was already complete.</summary>
        None = 0,
        /// <summary>Name resolution failed.</summary>
        Dns = 1,
        /// <summary>Connection refused or host unreachable. A connect timeout is <see cref="Timeout"/>.</summary>
        Connect = 2,
        /// <summary>TLS handshake or certificate verification failed.</summary>
        Tls = 3,
        /// <summary>
        /// The connect or total timeout fired, or the speed stayed too low for <see cref="UgoRequest.StallTimeout"/>; <see cref="UgoTimings"/>
        /// shows how far it got. For a WebSocket: the connect, open or close timeout.
        /// </summary>
        Timeout = 4,
        /// <summary>
        /// The connection broke or the server violated the protocol mid-transfer, the body failed gzip/deflate decoding, or a 2xx body exceeded
        /// its memory cap. For a WebSocket also a failed handshake check, a protocol error (the library closed with 1002, 1007 or 1009) or a
        /// drop without a close handshake.
        /// </summary>
        Transfer = 5,
        /// <summary>The final status is not 2xx. Status, headers and body are still delivered (for a file request, up to 64 KiB of the body in memory).</summary>
        HttpStatus = 6,
        /// <summary>Aborted; <see cref="UgoErrorReason"/> says by whom.</summary>
        Aborted = 7,
        /// <summary>A local file could not be read or written, for example a full disk. Not the host's fault.</summary>
        LocalIO = 8,
        /// <summary>A failure inside the library, such as native memory exhaustion.</summary>
        Internal = 9
    }

    /// <summary>Narrows an <see cref="UgoErrorKind"/> where the caller's policy differs.</summary>
    public enum UgoErrorReason
    {
        /// <summary>No narrower reason.</summary>
        None = 0,
        /// <summary>Aborted by <see cref="UgoClient.Abort"/>, <see cref="UgoWebSocket.Abort"/>, <see cref="UgoWebSocket.Dispose"/> or a cancelled <see cref="System.Threading.CancellationToken"/>.</summary>
        Caller = 1,
        /// <summary>Aborted by <see cref="UgoClient.Shutdown"/> or <see cref="UgoClient.Dispose"/>, including the automatic disposal on Play Mode exit, domain reload or app quit.</summary>
        Shutdown = 2,
        /// <summary>Aborted because the iOS background time of <see cref="UgoRequest.FinishInBackground"/> expired.</summary>
        BackgroundExpired = 3,
        /// <summary>
        /// The body exceeded its memory cap: <see cref="UgoRequest.MaxResponseBodyBytes"/>, or 64 KiB for a non-2xx response to a file request.
        /// <see cref="UgoResponse.Body"/> holds the bytes that fit. The kind is <see cref="UgoErrorKind.HttpStatus"/> for a non-2xx response, else <see cref="UgoErrorKind.Transfer"/>.
        /// </summary>
        ResponseBodyLimit = 4,
        /// <summary>
        /// The server answered 101 but failed the handshake check: <c>Sec-WebSocket-Accept</c> missing, repeated or wrong, <c>Upgrade</c> or
        /// <c>Connection</c> missing, any <c>Sec-WebSocket-Extensions</c>, or a <c>Sec-WebSocket-Protocol</c> the request did not offer.
        /// </summary>
        WebSocketHandshake = 5,
        /// <summary>The client sent the first WebSocket CLOSE frame.</summary>
        WebSocketClosedByLocal = 6,
        /// <summary>The server sent the first WebSocket CLOSE frame.</summary>
        WebSocketClosedByRemote = 7,
        /// <summary>
        /// <see cref="UgoErrorKind.Transfer"/> of a <see cref="UgoRequest.Resume"/>: the response cannot continue the file. The file is deleted
        /// (unless <see cref="UgoRequest.KeepPartialFile"/>) only when the server contradicts it: a 206 starting elsewhere, or a 416 reporting a
        /// shorter resource. After a response that is inconsistent in itself (malformed or missing <c>Content-Range</c>, a body that differs from the
        /// range) the file is kept with whatever was appended.
        /// </summary>
        ResumeMismatch = 8
    }

    /// <summary>Negotiated HTTP version.</summary>
    public enum UgoHttpVersion
    {
        /// <summary>No response was received.</summary>
        None = 0,
        /// <summary>HTTP/1.0.</summary>
        Http10 = 10,
        /// <summary>HTTP/1.1.</summary>
        Http11 = 11,
        /// <summary>HTTP/2.</summary>
        Http2 = 20
    }

    /// <summary>
    /// Receives the <see cref="UgoEventKind.Headers"/> and <see cref="UgoEventKind.Progress"/> events of a <c>Send</c> or <c>SendAsync</c>
    /// operation, always before its completion. Runs on the draining thread and must not block. Spans in <paramref name="e"/> stay valid until the
    /// operation's response is disposed. It may still run during <see cref="UgoClient.Dispose"/>; events left after shutdown are dropped. An
    /// exception escaping it is rethrown on the thread pool.
    /// </summary>
    public delegate void UgoEventHandler(in UgoEvent e, object state);

    /// <summary>Thrown by <see cref="UgoClient.SendAsync"/> and <see cref="UgoClient.OpenWebSocket"/> when the client does not accept the operation; nothing was started.</summary>
    public sealed class UgoSubmitException : Exception
    {
        /// <summary>Why the operation was not accepted.</summary>
        public UgoSubmitStatus Status { get; }
        /// <summary>Creates the exception for a rejection status.</summary>
        public UgoSubmitException(UgoSubmitStatus status) : base("Request not accepted: " + status) { Status = status; }
    }

    /// <summary>Operation id, unique per client: an id whose operation was released never becomes valid again.</summary>
    public readonly struct UgoId : IEquatable<UgoId>
    {
        internal readonly ulong Value;
        internal UgoId(ulong value) { Value = value; }
        /// <summary>Index below <see cref="UgoClient.OperationSlots"/>, for indexing the caller's own per-operation array. Reused after release, unlike the id.</summary>
        public int Slot => (int)(uint)Value;
        /// <summary>False only for <c>default</c>; says nothing about whether the operation is still alive.</summary>
        public bool IsValid => Value != 0;
        /// <inheritdoc/>
        public bool Equals(UgoId other) => Value == other.Value;
        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is UgoId other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() => Value.GetHashCode();
        /// <summary>"slot/generation", for logs.</summary>
        public override string ToString() => (uint)Value + "/" + (Value >> 32);
        /// <summary>Same id.</summary>
        public static bool operator ==(UgoId left, UgoId right) => left.Equals(right);
        /// <summary>Different ids.</summary>
        public static bool operator !=(UgoId left, UgoId right) => !left.Equals(right);
    }

    /// <summary>Client settings, read once by the <see cref="UgoClient"/> constructor.</summary>
    public struct UgoClientOptions
    {
        /// <summary><see cref="OperationSlots"/> of a client created with 0 there.</summary>
        public const int DefaultOperationSlots = 64;

        /// <summary>
        /// Size of the client's fixed operation table; 0 (default) = <see cref="DefaultOperationSlots"/>. It bounds memory, not network
        /// concurrency: use <see cref="MaxConnections"/> or your own gate for that.
        /// </summary>
        /// <remarks>
        /// A slot is taken at submit and held until the operation is given back: a request until its response is disposed (a detached one until
        /// it finishes), a WebSocket until it is disposed. So finished operations count too, and a response or socket that is never disposed
        /// holds its slot for the life of the client. With every slot held, a submit fails at once with
        /// <see cref="UgoSubmitStatus.NoCapacity"/>; nothing is queued or dropped silently. A slot costs about 1 KiB of native memory, allocated when the
        /// client is created. Size it for the largest burst you start at once plus the responses and sockets you keep.
        /// </remarks>
        public int OperationSlots;
        /// <summary>
        /// Open connections across all hosts; 0 (default) = unlimited. A request waiting for a free connection is queued natively; it already
        /// holds a slot, and the wait counts towards its connect and total timeouts.
        /// </summary>
        public int MaxConnections;
        /// <summary>
        /// How long the client caches resolved addresses, rounded up to whole seconds. Zero (default) disables this cache; the OS resolver may
        /// still cache.
        /// </summary>
        public TimeSpan DnsCacheDuration;
        /// <summary>
        /// PEM roots that replace the bundled ones; copied by the constructor. Empty (default) = bundled roots. Parsed at the first TLS connection,
        /// not here: an invalid bundle fails each TLS connection as <see cref="UgoErrorKind.Tls"/> (<see cref="UgoErrorKind.Internal"/> if
        /// memory runs out). Unused with <see cref="DangerousAcceptAnyCertificate"/>.
        /// </summary>
        public ReadOnlyMemory<byte> CaBundlePem;
        /// <summary>Skips certificate and hostname verification. Development builds of the native library only: a release build makes the constructor throw <see cref="ArgumentException"/>.</summary>
        public bool DangerousAcceptAnyCertificate;
        /// <summary><see cref="UgoDrainMode.Background"/> by default.</summary>
        public UgoDrainMode DrainMode;

        /// <summary>Options with <paramref name="operationSlots"/>; everything else at its default.</summary>
        public UgoClientOptions(int operationSlots)
        {
            this = default;
            OperationSlots = operationSlots;
        }
    }

    /// <summary>One request header. The name must be an HTTP token, the value must not contain CR, LF or NUL; checked at submit.</summary>
    public readonly struct UgoHeader
    {
        /// <summary>Header name.</summary>
        public string Name { get; }
        /// <summary>Header value; null or empty sends the header with an empty value.</summary>
        public string Value { get; }
        /// <summary>Creates a header.</summary>
        public UgoHeader(string name, string value) { Name = name; Value = value; }
    }

    /// <summary>One <c>multipart/form-data</c> part, from memory or from a file.</summary>
    public readonly struct UgoFormPart
    {
        /// <summary>Form field name.</summary>
        public string Name { get; }
        /// <summary><c>filename</c> of the part, or null.</summary>
        public string FileName { get; }
        /// <summary>Part <c>Content-Type</c>, or null.</summary>
        public string ContentType { get; }
        /// <summary>Part bytes when <see cref="FilePath"/> is null. Borrowed: keep them alive and unchanged until the operation completes.</summary>
        public ReadOnlyMemory<byte> Bytes { get; }
        /// <summary>File sent as the part body, or null.</summary>
        public string FilePath { get; }

        UgoFormPart(string name, string fileName, string contentType, ReadOnlyMemory<byte> bytes, string filePath)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A form part needs a name.", nameof(name));
            Name = name;
            FileName = fileName;
            ContentType = contentType;
            Bytes = bytes;
            FilePath = filePath;
        }

        /// <summary>A text field; <paramref name="value"/> is encoded to a new UTF-8 array now, null as empty.</summary>
        public static UgoFormPart FromText(string name, string value) =>
            new UgoFormPart(name, null, null, Encoding.UTF8.GetBytes(value ?? ""), null);
        /// <summary>A part from caller memory. Borrowed: keep <paramref name="bytes"/> alive and unchanged until the operation completes.</summary>
        public static UgoFormPart FromBytes(string name, ReadOnlyMemory<byte> bytes, string fileName = null, string contentType = null) =>
            new UgoFormPart(name, fileName, contentType, bytes, null);
        /// <summary>A part whose body is the file at <paramref name="path"/>, read natively when sent and again on each redirect that keeps the body.</summary>
        public static UgoFormPart FromFile(string name, string path, string fileName = null, string contentType = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A file part needs a path.", nameof(path));
            return new UgoFormPart(name, fileName, contentType, default, path);
        }
    }

    internal enum UgoBodyKind { None, Memory, File, Multipart }

    /// <summary>
    /// Request body: none, bytes, a file, multipart or an urlencoded form. Sent exactly as given, never compressed, and sent again on each
    /// 301, 302, 307 or 308 redirect; a 303 drops it (see <see cref="UgoRequest.MaxRedirects"/>).
    /// </summary>
    public readonly struct UgoRequestBody
    {
        internal readonly UgoBodyKind Kind;
        internal readonly ReadOnlyMemory<byte> Memory;
        internal readonly string FilePath;
        internal readonly ReadOnlyMemory<UgoFormPart> Parts;
        internal readonly string ContentType;

        UgoRequestBody(UgoBodyKind kind, ReadOnlyMemory<byte> memory, string filePath, ReadOnlyMemory<UgoFormPart> parts, string contentType)
        {
            Kind = kind;
            Memory = memory;
            FilePath = filePath;
            Parts = parts;
            ContentType = contentType;
        }

        /// <summary>No body. Methods other than GET, HEAD, DELETE and OPTIONS then send <c>Content-Length: 0</c>.</summary>
        public static UgoRequestBody None => default;

        /// <summary>
        /// Bytes from caller memory. Borrowed: keep them alive and unchanged until the operation completes (<see cref="UgoClient.EnqueueDetached"/>
        /// copies them). <paramref name="contentType"/>, if set, is sent as <c>Content-Type</c>.
        /// </summary>
        public static UgoRequestBody FromBytes(ReadOnlyMemory<byte> bytes, string contentType = null) =>
            new UgoRequestBody(UgoBodyKind.Memory, bytes, null, default, contentType);

        /// <summary>The file at <paramref name="path"/>, read natively when sent. <paramref name="contentType"/>, if set, is sent as <c>Content-Type</c>.</summary>
        public static UgoRequestBody FromFile(string path, string contentType = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A file body needs a path.", nameof(path));
            return new UgoRequestBody(UgoBodyKind.File, default, path, default, contentType);
        }

        /// <summary><c>multipart/form-data</c>; boundary and <c>Content-Type</c> are generated. The part list is read at submit; part bytes are borrowed until the operation completes.</summary>
        public static UgoRequestBody FromParts(ReadOnlyMemory<UgoFormPart> parts)
        {
            if (parts.IsEmpty) throw new ArgumentException("A multipart body needs at least one part.", nameof(parts));
            return new UgoRequestBody(UgoBodyKind.Multipart, default, null, parts, null);
        }

        /// <summary><c>application/x-www-form-urlencoded</c>, encoded now into a new array (UTF-8, space as '+'). Order and duplicates are kept; a null value is sent empty.</summary>
        public static UgoRequestBody FromForm(ReadOnlyMemory<KeyValuePair<string, string>> fields)
        {
            ReadOnlySpan<KeyValuePair<string, string>> span = fields.Span;
            int bound = 0, longest = 0;
            for (int i = 0; i < span.Length; i++)
            {
                if (string.IsNullOrEmpty(span[i].Key)) throw new ArgumentException("A form field needs a name.", nameof(fields));
                int name = Encoding.UTF8.GetByteCount(span[i].Key), value = span[i].Value == null ? 0 : Encoding.UTF8.GetByteCount(span[i].Value);
                longest = Math.Max(longest, Math.Max(name, value));
                bound = checked(bound + 3 * (name + value) + 2);
            }
            byte[] utf8 = ArrayPool<byte>.Shared.Rent(Math.Max(1, longest));
            byte[] output = ArrayPool<byte>.Shared.Rent(Math.Max(1, bound));
            try
            {
                int length = 0;
                for (int i = 0; i < span.Length; i++)
                {
                    if (i != 0) output[length++] = (byte)'&';
                    length = Encode(span[i].Key, utf8, output, length);
                    output[length++] = (byte)'=';
                    length = Encode(span[i].Value, utf8, output, length);
                }
                return new UgoRequestBody(UgoBodyKind.Memory, output.AsSpan(0, length).ToArray(), null, default, "application/x-www-form-urlencoded");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(utf8, clearArray: true);
                ArrayPool<byte>.Shared.Return(output, clearArray: true);
            }
        }

        static bool Unreserved(byte b) =>
            (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '*' || b == '-' || b == '.' || b == '_';

        static int Encode(string value, byte[] utf8, byte[] target, int offset)
        {
            if (string.IsNullOrEmpty(value)) return offset;
            const string hex = "0123456789ABCDEF";
            int count = Encoding.UTF8.GetBytes(value.AsSpan(), utf8);
            for (int i = 0; i < count; i++)
            {
                byte b = utf8[i];
                if (Unreserved(b)) target[offset++] = b;
                else if (b == ' ') target[offset++] = (byte)'+';
                else
                {
                    target[offset++] = (byte)'%';
                    target[offset++] = (byte)hex[b >> 4];
                    target[offset++] = (byte)hex[b & 15];
                }
            }
            return offset;
        }
    }

    /// <summary>
    /// An HTTP request. Create it with <see cref="UgoRequest(string)"/> for the defaults: a <c>default</c> request fails validation. Read at
    /// submit, so it can be changed or reused afterwards; only body bytes stay borrowed until the operation completes.
    /// </summary>
    public struct UgoRequest
    {
        /// <summary>
        /// GET by default. Any RFC 9110 token up to 32 characters, case-sensitive; custom methods are allowed. HEAD reads no body and cannot carry
        /// one; CONNECT is rejected.
        /// </summary>
        public HttpMethod Method;
        /// <summary>Absolute <c>http</c> or <c>https</c> URL.</summary>
        public string Url;
        /// <summary>
        /// Request headers, sent in order, duplicates included. A <c>Content-Type</c> header together with a body that sets one throws. Without an
        /// <c>Accept-Encoding</c> header the library sends <c>Accept-Encoding: gzip</c> and delivers the decoded body; with one (even empty), or with
        /// <see cref="Resume"/>, the body is delivered exactly as received. No <c>User-Agent</c> is sent unless set here.
        /// </summary>
        public ReadOnlyMemory<UgoHeader> Headers;
        /// <summary>Request body; none by default. Any method but HEAD may carry one.</summary>
        public UgoRequestBody Body;
        /// <summary>
        /// Writes a 2xx body natively to this file instead of memory, decoded as described at <see cref="Headers"/>. The file is created or
        /// truncated only for a 2xx response, closed before completion and deleted on failure unless <see cref="KeepPartialFile"/> or
        /// <see cref="Resume"/>. A non-2xx response leaves the file alone; up to 64 KiB of its body goes to <see cref="UgoResponse.Body"/>, more fails with <see cref="UgoErrorReason.ResponseBodyLimit"/>.
        /// The library never renames: use a unique temporary path and move the file yourself after success.
        /// </summary>
        public string ResponseFilePath;
        /// <summary>
        /// Cap in bytes for a body kept in memory, counted after decoding; a longer body fails with <see cref="UgoErrorReason.ResponseBodyLimit"/>.
        /// 16 MiB by default; ignored with <see cref="ResponseFilePath"/>.
        /// </summary>
        public int MaxResponseBodyBytes;
        /// <summary>Receive speed cap in bytes per second; 0 (default) = unlimited.</summary>
        public long MaxReceiveBytesPerSecond;
        /// <summary>Send speed cap in bytes per second; 0 (default) = unlimited.</summary>
        public long MaxSendBytesPerSecond;
        /// <summary>
        /// Fails the request as <see cref="UgoErrorKind.Timeout"/> when it moves less than one byte per second for this long; null (default) or
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> = none. Rounded up to whole seconds, at most 65535 s.
        /// </summary>
        /// <remarks>
        /// The speed is request and response body bytes together, averaged over about the last 5 seconds and measured once connected. A
        /// server that stays silent fails the request, while it waits for the response headers too, so long polling needs a longer value or
        /// none. Because of the averaging, silence after an active transfer is noticed about 5 seconds later than this value, and a byte
        /// every few seconds does not count as progress.
        /// </remarks>
        public TimeSpan? StallTimeout;
        /// <summary>
        /// Raises the speed <see cref="StallTimeout"/> requires from one byte per second to this many, to give up on a host that is alive but
        /// too slow. 0 (default) = 1. Requires <see cref="StallTimeout"/>. Keep it below a <see cref="MaxReceiveBytesPerSecond"/> or
        /// <see cref="MaxSendBytesPerSecond"/> you set: a capped transfer is measured at its capped speed.
        /// </summary>
        public int MinimumSpeedBytesPerSecond;
        /// <summary>TCP and TLS connect timeout, rounded up to whole milliseconds; must be above zero. 10 s by default.</summary>
        public TimeSpan ConnectTimeout;
        /// <summary>Whole-request timeout, redirects included, rounded up to whole milliseconds; null (default) or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> = none.</summary>
        public TimeSpan? TotalTimeout;
        /// <summary>
        /// Redirects to follow; 0 makes a 3xx the final response. 10 by default. 301, 302, 307 and 308 send the method and body again; 303 sends
        /// a GET (a HEAD stays HEAD) without the body and without the <c>Content-Type</c>, <c>Content-Length</c>, <c>Content-Encoding</c> and
        /// <c>Transfer-Encoding</c> headers. <c>Authorization</c> and <c>Cookie</c> go only to the original origin.
        /// </summary>
        public int MaxRedirects;
        /// <summary>Keep a partially written <see cref="ResponseFilePath"/> on failure instead of deleting it.</summary>
        public bool KeepPartialFile;
        /// <summary>
        /// iOS: asks for extra time to finish after the app leaves the foreground. Best effort: iOS may refuse it; on expiry the request is aborted
        /// with <see cref="UgoErrorReason.BackgroundExpired"/>, but iOS may suspend the app first, and after resume it can fail with a network error
        /// instead. Ignored on other platforms.
        /// </summary>
        public bool FinishInBackground;
        /// <summary>
        /// Continues a download into the bytes already at <see cref="ResponseFilePath"/>: sends <c>Range: bytes=&lt;file length&gt;-</c> when the file
        /// is not empty and keeps the file on failure (see <see cref="UgoErrorReason.ResumeMismatch"/> for the exception). A 206 that continues the
        /// file is appended; if it ends before the resource's stated length, the request fails as <see cref="UgoErrorKind.Transfer"/> and the next
        /// resume continues from there. Another 2xx (the server ignored the range) rewrites the file. A 416 that reports the file's length succeeds
        /// with <see cref="UgoResponse.StatusCode"/> 416 and the file unchanged, unless reading its body fails or exceeds 64 KiB. Any other 206 or
        /// 416 fails with <see cref="UgoErrorReason.ResumeMismatch"/>;
        /// any other non-2xx leaves the file untouched. Requires <see cref="ResponseFilePath"/>, a method other than HEAD and no <c>Range</c> header.
        /// Sends <c>Accept-Encoding: identity</c> unless that header is set; the body is never decoded. The library keeps no validator: if the content
        /// at the URL can change, send the first response's strong <c>ETag</c> as <c>If-Range</c>, so a changed resource comes back as a 200 and
        /// restarts the file. To resume from an earlier point, truncate the file first.
        /// </summary>
        public bool Resume;

        /// <summary>A GET of <paramref name="url"/> with the defaults: 16 MiB memory body cap, 10 s connect timeout, no total or stall timeout, 10 redirects.</summary>
        public UgoRequest(string url)
        {
            this = default;
            Url = url;
            Method = HttpMethod.Get;
            MaxResponseBodyBytes = 16 * 1024 * 1024;
            ConnectTimeout = TimeSpan.FromSeconds(10);
            MaxRedirects = 10;
        }
    }

    /// <summary>An event from <see cref="UgoClient.TryWaitEvents"/> or a <see cref="UgoEventHandler"/>. Payload accessors throw <see cref="InvalidOperationException"/> for another <see cref="Kind"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct UgoEvent
    {
        internal ugo_event Native;
        /// <summary>What happened.</summary>
        public UgoEventKind Kind => (UgoEventKind)Native.kind;
        /// <summary>The operation it belongs to.</summary>
        public UgoId Id => new UgoId(Native.id);
        /// <summary>When it happened, in microseconds on the <see cref="UgoClient.MonotonicMicroseconds"/> clock.</summary>
        public long TimestampMicroseconds => Native.time_us;
        /// <summary>Payload of a <see cref="UgoEventKind.Headers"/> event.</summary>
        public UgoHeadersEvent Headers
        {
            get { Check(UgoEventKind.Headers); fixed (ulong* p = Native.u) return new UgoHeadersEvent(*(ugo_ev_headers*)p); }
        }
        /// <summary>Payload of a <see cref="UgoEventKind.Progress"/> event.</summary>
        public UgoProgress Progress
        {
            get { Check(UgoEventKind.Progress); fixed (ulong* p = Native.u) return new UgoProgress(*(ugo_ev_progress*)p); }
        }
        internal ugo_ev_completed Completed
        {
            get { Check(UgoEventKind.Completed); fixed (ulong* p = Native.u) return *(ugo_ev_completed*)p; }
        }
        internal ugo_ev_ws_message WebSocketMessage
        {
            get { Check(UgoEventKind.WebSocketMessage); fixed (ulong* p = Native.u) return *(ugo_ev_ws_message*)p; }
        }
        void Check(UgoEventKind expected)
        {
            if (Kind != expected) throw new InvalidOperationException("Event is " + Kind + ", not " + expected + ".");
        }
        internal static ReadOnlySpan<byte> Bytes(ugo_bytes bytes) => new ReadOnlySpan<byte>((void*)bytes.ptr, checked((int)bytes.len));
    }

    /// <summary>Status and headers of the final response, before its body; for a WebSocket, of the handshake response.</summary>
    public readonly struct UgoHeadersEvent
    {
        readonly ugo_ev_headers native;
        internal UgoHeadersEvent(ugo_ev_headers native) { this.native = native; }
        /// <summary>HTTP status code.</summary>
        public int StatusCode => (int)native.http_status;
        /// <summary>Negotiated HTTP version.</summary>
        public UgoHttpVersion HttpVersion => (UgoHttpVersion)native.http_version;
        /// <summary><c>Content-Length</c> as received (the encoded size when the body is content-encoded), or -1 when unknown.</summary>
        public long ContentLength => native.content_length;
        /// <summary>
        /// For a <see cref="UgoRequest.Resume"/>: the bytes already in the file that this response continues (a matching 206), or the file's length
        /// when a 416 found it complete; 0 when the body starts the file.
        /// </summary>
        public long ResumeOffset => (long)native.resume_offset;
        /// <summary>Header block as received: status line, then CRLF-terminated lines. Valid until the operation's response or socket is disposed, or the client is disposed.</summary>
        public ReadOnlySpan<byte> RawHeaders => UgoEvent.Bytes(native.headers);
        /// <summary>Header fields in received order, repeats included. Allocation-free.</summary>
        public UgoHeaderEnumerator EnumerateHeaders() => new UgoHeaderEnumerator(RawHeaders);
        /// <summary>First header named <paramref name="name"/> (ASCII, case-insensitive). Allocation-free; <paramref name="value"/> has the lifetime of <see cref="RawHeaders"/>.</summary>
        public bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value) => UgoHeaderEnumerator.TryFind(RawHeaders, name, out value);
    }

    /// <summary>Byte counts at the time of the event; totals are -1 when unknown. A resumed download counts the whole file: <see cref="UgoHeadersEvent.ResumeOffset"/> is included in both received values.</summary>
    public readonly struct UgoProgress
    {
        readonly ugo_ev_progress native;
        internal UgoProgress(ugo_ev_progress native) { this.native = native; }
        /// <summary>Response body bytes received over the wire, before content decoding.</summary>
        public long BytesReceived => (long)native.received;
        /// <summary>Expected response body bytes over the wire (<c>Content-Length</c>), -1 when unknown.</summary>
        public long TotalBytesToReceive => native.receive_total;
        /// <summary>Request body bytes sent.</summary>
        public long BytesSent => (long)native.sent;
        /// <summary>Request body bytes to send, -1 when unknown.</summary>
        public long TotalBytesToSend => native.send_total;
    }

    /// <summary>Why an operation failed; <see cref="Kind"/> is <see cref="UgoErrorKind.None"/> on success. Plain values, valid after the response is disposed.</summary>
    public readonly struct UgoError
    {
        /// <summary>Stable class.</summary>
        public UgoErrorKind Kind { get; }
        /// <summary>Narrower reason where one exists.</summary>
        public UgoErrorReason Reason { get; }
        /// <summary>Raw libcurl error code (<c>CURLcode</c>), for logs. Branch on <see cref="Kind"/> and <see cref="Reason"/>, not on this.</summary>
        public int NativeCode { get; }
        internal UgoError(UgoErrorKind kind, UgoErrorReason reason, int nativeCode) { Kind = kind; Reason = reason; NativeCode = nativeCode; }
    }

    /// <summary>
    /// Phase times in microseconds. <see cref="QueueMicroseconds"/> is the wait before the transfer started; the others count from that start,
    /// 0 = phase not reached, and their clock may stop during system sleep. End to end = queue + total.
    /// </summary>
    public readonly struct UgoTimings
    {
        readonly ugo_ev_completed native;
        internal UgoTimings(in ugo_ev_completed native) { this.native = native; }
        /// <summary>Time queued before the transfer started.</summary>
        public long QueueMicroseconds => native.queue_us;
        /// <summary>Until name resolution finished.</summary>
        public long NameLookupMicroseconds => native.namelookup_us;
        /// <summary>Until the TCP connection was established.</summary>
        public long ConnectMicroseconds => native.connect_us;
        /// <summary>Until the TLS handshake finished.</summary>
        public long TlsHandshakeMicroseconds => native.appconnect_us;
        /// <summary>Until the first response byte.</summary>
        public long FirstByteMicroseconds => native.starttransfer_us;
        /// <summary>Until the transfer finished.</summary>
        public long TotalMicroseconds => native.total_us;
        /// <summary>Spent in redirects before the final request.</summary>
        public long RedirectMicroseconds => native.redirect_us;
    }

    /// <summary>One response header: views into the header block.</summary>
    public readonly ref struct UgoHeaderField
    {
        /// <summary>Name as received.</summary>
        public ReadOnlySpan<byte> Name { get; }
        /// <summary>Value without surrounding whitespace.</summary>
        public ReadOnlySpan<byte> Value { get; }
        internal UgoHeaderField(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) { Name = name; Value = value; }
    }

    /// <summary>Enumerates the header fields of a block in received order, repeats included; the status line is skipped. Allocation-free.</summary>
    public ref struct UgoHeaderEnumerator
    {
        readonly ReadOnlySpan<byte> block;
        int position;
        ReadOnlySpan<byte> name, value;

        internal UgoHeaderEnumerator(ReadOnlySpan<byte> block)
        {
            this.block = block;
            int first = block.IndexOf((byte)'\n');
            position = first < 0 ? block.Length : first + 1;
            name = value = default;
        }

        /// <summary>The current field.</summary>
        public UgoHeaderField Current => new UgoHeaderField(name, value);
        /// <summary>Returns itself, for <c>foreach</c>.</summary>
        public UgoHeaderEnumerator GetEnumerator() => this;

        /// <summary>Moves to the next field.</summary>
        public bool MoveNext()
        {
            while (position < block.Length)
            {
                ReadOnlySpan<byte> rest = block.Slice(position);
                int end = rest.IndexOf((byte)'\n');
                ReadOnlySpan<byte> line = end < 0 ? rest : rest.Slice(0, end);
                position += end < 0 ? rest.Length : end + 1;
                int colon = line.IndexOf((byte)':');
                if (colon <= 0 || line[0] == ' ' || line[0] == '\t') continue;
                name = line.Slice(0, colon);
                value = Trim(line.Slice(colon + 1));
                return true;
            }
            return false;
        }

        static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
        {
            int start = 0, end = s.Length;
            while (start < end && (s[start] == ' ' || s[start] == '\t')) start++;
            while (end > start && (s[end - 1] == ' ' || s[end - 1] == '\t' || s[end - 1] == '\r')) end--;
            return s.Slice(start, end - start);
        }

        internal static bool TryFind(ReadOnlySpan<byte> block, ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
        {
            foreach (UgoHeaderField field in new UgoHeaderEnumerator(block))
                if (NameEquals(field.Name, name))
                {
                    value = field.Value;
                    return true;
                }
            value = default;
            return false;
        }

        static bool NameEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                int x = a[i], y = b[i];
                if (x >= 'A' && x <= 'Z') x += 32;
                if (y >= 'A' && y <= 'Z') y += 32;
                if (x != y) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The result of a finished request: status, error, metrics, headers and body. Dispose it to free the operation's slot; all copies of the struct
    /// share one result. Scalar properties stay readable after disposal. <see cref="Body"/>, <see cref="RawHeaders"/> and <see cref="ErrorMessage"/>
    /// then throw <see cref="ObjectDisposedException"/>, and spans taken earlier must not be used after this response or its client is disposed.
    /// Usable from any thread; reading a span while another thread disposes is the caller's race.
    /// </summary>
    public readonly struct UgoResponse : IDisposable
    {
        readonly UgoClient client;
        readonly ulong id;
        readonly ugo_ev_completed completed;

        internal UgoResponse(UgoClient client, ulong id, in ugo_ev_completed completed)
        {
            this.client = client;
            this.id = id;
            this.completed = completed;
        }

        /// <summary>The operation this response belongs to.</summary>
        public UgoId Id => new UgoId(id);
        /// <summary>
        /// The request finished without error and the final status is 2xx, or 416 for a <see cref="UgoRequest.Resume"/> whose file was already
        /// complete. A 200 whose body broke off is not a success. False for <c>default</c>.
        /// </summary>
        public bool IsSuccess => completed.error_class == UgoAbi.UGO_CLASS_NONE && client != null;
        /// <summary>Final HTTP status code; 0 if no response was received.</summary>
        public int StatusCode => (int)completed.http_status;
        /// <summary>Negotiated HTTP version.</summary>
        public UgoHttpVersion HttpVersion => (UgoHttpVersion)completed.http_version;
        /// <summary>Failure class and reason; <see cref="UgoErrorKind.None"/> on success.</summary>
        public UgoError Error => new UgoError((UgoErrorKind)completed.error_class, (UgoErrorReason)completed.detail, completed.curl_code);
        /// <summary>Body bytes received over the wire by this request, before decoding; for a resumed download, without the bytes already in the file.</summary>
        public long BytesReceived => (long)completed.bytes_received;
        /// <summary>Request body bytes sent.</summary>
        public long BytesSent => (long)completed.bytes_sent;
        /// <summary>An existing connection was reused.</summary>
        public bool ConnectionReused => completed.connection_reused != 0;
        /// <summary>Redirects followed.</summary>
        public int RedirectCount => (int)completed.redirect_count;
        /// <summary>Phase timings.</summary>
        public UgoTimings Timings => new UgoTimings(completed);

        /// <summary>
        /// The response body, also for a non-2xx status; decoded unless <see cref="UgoRequest.Headers"/> sets <c>Accept-Encoding</c> or the request
        /// resumes. For a file request, only a non-2xx body (up to 64 KiB), else empty. A view into native memory, no copy; valid until <see cref="Dispose"/>.
        /// </summary>
        public ReadOnlySpan<byte> Body => UgoEvent.Bytes(Result().body);
        /// <summary>Header block of the final response as received; empty if its headers never completed. Valid until <see cref="Dispose"/>.</summary>
        public ReadOnlySpan<byte> RawHeaders => UgoEvent.Bytes(Result().headers);
        /// <summary>Human-readable error text in UTF-8, for logs; empty on success. Valid until <see cref="Dispose"/>.</summary>
        public ReadOnlySpan<byte> ErrorMessage => UgoEvent.Bytes(Result().error_message);

        /// <summary><see cref="Body"/> as a UTF-8 string; allocates.</summary>
        public string GetBodyAsString() => Encoding.UTF8.GetString(Body);
        /// <summary><see cref="ErrorMessage"/> as a string; allocates.</summary>
        public string GetErrorMessage() => Encoding.UTF8.GetString(ErrorMessage);
        /// <summary>Header fields in received order, repeats included. Allocation-free.</summary>
        public UgoHeaderEnumerator EnumerateHeaders() => new UgoHeaderEnumerator(RawHeaders);

        /// <summary>First header named <paramref name="name"/> (ASCII, case-insensitive). Allocation-free; <paramref name="value"/> is valid until <see cref="Dispose"/>.</summary>
        public bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value) => UgoHeaderEnumerator.TryFind(RawHeaders, name, out value);

        /// <summary>First header named <paramref name="name"/> (case-insensitive) as a string, or null; allocates.</summary>
        public string GetHeader(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            byte[] bytes = Encoding.ASCII.GetBytes(name);
            return TryGetHeader(bytes, out ReadOnlySpan<byte> value) ? Encoding.UTF8.GetString(value) : null;
        }

        /// <summary>Frees the native result and the operation's slot. Idempotent, also through copies; a no-op after the client is disposed.</summary>
        public void Dispose() => client?.Release(id);

        ugo_result Result()
        {
            if (client == null) throw new InvalidOperationException("Default UgoResponse has no result.");
            return client.GetResult(id, nameof(UgoResponse));
        }
    }
}
