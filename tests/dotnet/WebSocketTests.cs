using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UgoHttp;
using Xunit;

public partial class IntegrationTests
{
    const string Pattern = "abcdefghijklmnopqrstuvwxyz0123456789";

    string Ws(string path) => "ws" + bench.Url.Substring(4) + "/ws/" + path;

    static UgoWebSocketRequest WsRequest(string url) =>
        new UgoWebSocketRequest(url) { Headers = new[] { new UgoHeader("Authorization", "Bearer bench-token") } };

    static string Header(in UgoHeadersEvent headers, string name) =>
        headers.TryGetHeader(Encoding.ASCII.GetBytes(name), out ReadOnlySpan<byte> value) ? Encoding.UTF8.GetString(value) : null;

    sealed class Recorder : UgoWebSocketListener
    {
        public readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
        public readonly TaskCompletionSource<int> Opened = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<(UgoErrorKind kind, UgoErrorReason reason, int code, bool server, string reason_text, int status, string location, string thread)> Closed =
            new TaskCompletionSource<(UgoErrorKind, UgoErrorReason, int, bool, string, int, string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Events;
        public string OpenThread;
        public int OpenThreadId;
        public long Start;
        public long Allocated = -1;
        public int Measure;

        public override void OnOpen(UgoWebSocket socket, in UgoHeadersEvent handshake)
        {
            OpenThread = Thread.CurrentThread.Name;
            OpenThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref Events);
            Opened.SetResult(handshake.StatusCode);
        }

        public override void OnMessage(UgoWebSocket socket, in UgoWebSocketMessage message)
        {
            if (Measure > 0)
            {
                if (message.Sequence == 10) Start = GC.GetAllocatedBytesForCurrentThread();
                else if (message.Sequence == Measure) Volatile.Write(ref Allocated, GC.GetAllocatedBytesForCurrentThread() - Start);
                return;
            }
            Messages.Enqueue(message.Sequence + ":" + (message.MessageType == UgoWebSocketMessageType.Binary
                ? "bin:" + Convert.ToHexString(message.Data) : message.GetText()));
        }

        public override void OnClosed(UgoWebSocket socket, in UgoWebSocketResult result)
        {
            Interlocked.Increment(ref Events);
            Closed.SetResult((result.Error.Kind, result.Error.Reason, result.CloseCode, result.ClosedByServer, result.GetReason(), result.StatusCode,
                Header(result.Handshake, "Location"), Thread.CurrentThread.Name));
        }
    }

    [Fact]
    public async Task WebSocketListenerEchoesAndClosesOnTheDrainThread()
    {
        using UgoClient client = Background();
        var recorder = new Recorder();
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")), recorder);
        Assert.Equal(101, await recorder.Opened.Task.WaitAsync(Limit));
        Assert.Equal("ugo-drain", recorder.OpenThread);
        Assert.Equal(UgoWebSocketState.Open, socket.State);
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("hello"));
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send(Encoding.UTF8.GetBytes("ж"), UgoWebSocketMessageType.Text));
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send(new string('x', 2000)));
        Assert.Throws<InvalidOperationException>(() => socket.ReceiveAsync());
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (recorder.Messages.Count < 3 && UgoClient.MonotonicMicroseconds < end) await Task.Delay(10);
        Assert.Equal(new[] { "1:hello", "2:ж", "3:" + new string('x', 2000) }, recorder.Messages.ToArray());
        Assert.True(socket.TryGetHandshake(out UgoHeadersEvent handshake));
        Assert.Equal("websocket", Header(handshake, "upgrade"));
        Assert.True(socket.Close(1000, "bye"));
        Assert.False(socket.Close());
        var closed = await recorder.Closed.Task.WaitAsync(Limit);
        Assert.Equal((UgoErrorKind.None, UgoErrorReason.WebSocketClosedByLocal, 1000, false, "bye", 101), (closed.kind, closed.reason, closed.code, closed.server, closed.reason_text, closed.status));
        Assert.Equal("ugo-drain", closed.thread);
        Assert.Equal(UgoWebSocketState.Closed, socket.State);
        Assert.Equal(2, recorder.Events);
        Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
        Assert.Equal("bye", result.GetReason());
        Assert.Equal("websocket", Header(result.Handshake, "upgrade"));
        socket.Dispose();
        Assert.Throws<ObjectDisposedException>(() => result.Handshake);
        Assert.Throws<ObjectDisposedException>(() => socket.TryGetHandshake(out _));
        Assert.Throws<ObjectDisposedException>(() => socket.Send("x"));
        socket.Dispose();
    }

    [Fact]
    public async Task WebSocketAwaitableFormReceivesInOrderAndEnds()
    {
        using UgoClient client = Background();
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("fragmented")));
        Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
        Assert.True(await socket.WaitOpenAsync());
        Assert.True(socket.TryGetHandshake(out UgoHeadersEvent handshake));
        Assert.Equal(101, handshake.StatusCode);
        UgoWebSocketMessage first = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(1, first.Sequence);
        Assert.Equal("frag-mented", first.GetText());
        Assert.True(first.TimestampMicroseconds > 0);
        UgoWebSocketMessage copy = first;
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => copy.Data.Length);
        ValueTask<UgoWebSocketMessage> pending = socket.ReceiveAsync();
        Assert.Throws<InvalidOperationException>(() => socket.ReceiveAsync());
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("again"));
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("third"));
        UgoWebSocketMessage second = await pending.AsTask().WaitAsync(Limit);
        Assert.Equal(2, second.Sequence);
        Assert.True(second.Data.SequenceEqual(Encoding.UTF8.GetBytes("again")));
        using (UgoWebSocketMessage third = await socket.ReceiveAsync().AsTask().WaitAsync(Limit))
        {
            Assert.Equal(3, third.Sequence);
            Assert.Equal("third", third.GetText());
        }
        Assert.Throws<ObjectDisposedException>(() => second.Data.Length);
        Assert.True(socket.Close(4000));
        UgoWebSocketMessage end = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
        Assert.True(end.IsEnd);
        Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
        Assert.Equal(UgoErrorKind.None, result.Error.Kind);
        Assert.Equal(4000, result.CloseCode);
        Assert.True((await socket.ReceiveAsync()).IsEnd);
    }

    [Fact]
    public async Task WebSocketRefusedHandshakeDeliversStatusAndHeaders()
    {
        using UgoClient client = Background();
        using (UgoWebSocket socket = client.OpenWebSocket(new UgoWebSocketRequest(Ws("echo"))))
        {
            Assert.False(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal(UgoErrorKind.HttpStatus, result.Error.Kind);
            Assert.Equal(401, result.StatusCode);
            Assert.Equal(1006, result.CloseCode);
            Assert.Equal(401, result.Handshake.StatusCode);
            Assert.False(socket.TryGetHandshake(out _));
            Assert.True((await socket.ReceiveAsync()).IsEnd);
        }
        using (UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("bad-accept"))))
        {
            Assert.False(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal((UgoErrorKind.Transfer, UgoErrorReason.WebSocketHandshake, 101), (result.Error.Kind, result.Error.Reason, result.StatusCode));
            Assert.NotEmpty(result.GetReason());
        }
    }

    [Fact]
    public async Task WebSocketHandshakeFollowsRedirects()
    {
        using UgoClient client = Background();
        var recorder = new Recorder();
        using (UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("redirect?code=308&hops=3&to=%2Fws%2Fecho")), recorder))
        {
            Assert.Equal(101, await recorder.Opened.Task.WaitAsync(Limit));
            Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("hop"));
            long end = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (recorder.Messages.IsEmpty && UgoClient.MonotonicMicroseconds < end) await Task.Delay(10);
            Assert.Equal(new[] { "1:hop" }, recorder.Messages.ToArray());
            Assert.True(socket.Close());
            var closed = await recorder.Closed.Task.WaitAsync(Limit);
            Assert.Equal((UgoErrorKind.None, 101, null), (closed.kind, closed.status, closed.location));
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal(3, result.RedirectCount);
            Assert.True(result.Timings.RedirectMicroseconds > 0);
        }
        using (UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("redirect?hops=11&to=%2Fws%2Fecho"))))
        {
            Assert.False(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal((UgoErrorKind.Transfer, 47, 302, 10), (result.Error.Kind, result.Error.NativeCode, result.StatusCode, result.RedirectCount));
            Assert.Equal("/ws/echo", Header(result.Handshake, "Location"));
        }
    }

    [Fact]
    public async Task WebSocketServerCloseProtocolErrorsAndAbortAreClassified()
    {
        using UgoClient client = Background();
        var recorder = new Recorder();
        using (client.OpenWebSocket(WsRequest(Ws("close")), recorder))
        {
            var closed = await recorder.Closed.Task.WaitAsync(Limit);
            Assert.Equal((UgoErrorKind.None, UgoErrorReason.WebSocketClosedByRemote, 1000, true, "bench done"),
                (closed.kind, closed.reason, closed.code, closed.server, closed.reason_text));
        }
        foreach (var (path, code) in new[] { ("bad-utf8?then=after", 1007), ("big?bytes=70000", 1009) })
        {
            var request = WsRequest(Ws(path));
            request.MaxMessageBytes = 65536;
            using UgoWebSocket socket = client.OpenWebSocket(request);
            UgoWebSocketMessage end = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
            Assert.True(end.IsEnd);
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal((UgoErrorKind.Transfer, UgoErrorReason.WebSocketClosedByLocal, code), (result.Error.Kind, result.Error.Reason, result.CloseCode));
        }
        using (UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo"))))
        {
            Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            socket.Abort();
            Assert.True((await socket.ReceiveAsync().AsTask().WaitAsync(Limit)).IsEnd);
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal((UgoErrorKind.Aborted, UgoErrorReason.Caller, 1006), (result.Error.Kind, result.Error.Reason, result.CloseCode));
        }
        var timeout = WsRequest(Ws("no-close-reply"));
        timeout.CloseTimeout = TimeSpan.FromMilliseconds(200);
        using (UgoWebSocket socket = client.OpenWebSocket(timeout))
        {
            Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            Assert.True(socket.Close(1001, "away"));
            Assert.True((await socket.ReceiveAsync().AsTask().WaitAsync(Limit)).IsEnd);
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal((UgoErrorKind.Timeout, 1001, "away"), (result.Error.Kind, result.CloseCode, result.GetReason()));
        }
    }

    [Fact]
    public async Task WebSocketSlowReceiverHoldsTheServerBackAndLosesNothing()
    {
        using UgoClient client = Background();
        var request = WsRequest(Ws("flood?count=64&size=1000"));
        request.MaxBacklogMessages = 4;
        request.MaxMessageBytes = 1000;
        request.MaxBacklogBytes = 4000;
        using UgoWebSocket socket = client.OpenWebSocket(request);
        var held = new List<UgoWebSocketMessage>();
        for (int i = 0; i < 4; i++) held.Add(await socket.ReceiveAsync().AsTask().WaitAsync(Limit));
        ValueTask<UgoWebSocketMessage> blocked = socket.ReceiveAsync();
        await Task.Delay(300);
        Assert.False(blocked.IsCompleted);
        held[1].Dispose();
        using (UgoWebSocketMessage fifth = await blocked.AsTask().WaitAsync(Limit)) Assert.Equal(5, fifth.Sequence);
        held[3].Dispose();
        for (int index = 5; index < 64; index++)
        {
            using UgoWebSocketMessage message = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(index + 1, message.Sequence);
            string text = message.GetText();
            Assert.Equal(index.ToString("D8"), text.Substring(0, 8));
            for (int k = 8; k < text.Length; k++) Assert.Equal(Pattern[k % 36], text[k]);
        }
        Assert.True(socket.Close());
        Assert.True((await socket.ReceiveAsync().AsTask().WaitAsync(Limit)).IsEnd);
    }

    [Fact]
    public async Task WebSocketListenerMessagesAllocateNothingOnTheDrainThread()
    {
        Assert.False(UgoTracking.Enabled);
        using UgoClient client = Background();
        var recorder = new Recorder { Measure = 60 };
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")), recorder);
        await recorder.Opened.Task.WaitAsync(Limit);
        for (int i = 0; i < 60; i++)
            while (socket.Send("message") == UgoWebSocketSendStatus.BufferFull) await Task.Delay(1);
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (Volatile.Read(ref recorder.Allocated) < 0 && UgoClient.MonotonicMicroseconds < end) await Task.Delay(10);
        Assert.Equal(0, Volatile.Read(ref recorder.Allocated));
    }

    [Fact]
    public async Task WebSocketBinaryMessagesKeepTheirTypeBothForms()
    {
        using UgoClient client = Background();
        byte[] raw = { 0x00, 0xff, 0xc0, 0x80, (byte)'b' };
        var recorder = new Recorder();
        using (UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")), recorder))
        {
            await recorder.Opened.Task.WaitAsync(Limit);
            Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send(raw, UgoWebSocketMessageType.Binary));
            Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("text"));
            Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send(ReadOnlySpan<byte>.Empty, UgoWebSocketMessageType.Binary));
            long end = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (recorder.Messages.Count < 3 && UgoClient.MonotonicMicroseconds < end) await Task.Delay(10);
            Assert.Equal(new[] { "1:bin:00FFC08062", "2:text", "3:bin:" }, recorder.Messages.ToArray());
        }
        using (UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("fragmented-binary"))))
        {
            using (UgoWebSocketMessage message = await socket.ReceiveAsync().AsTask().WaitAsync(Limit))
            {
                Assert.Equal(UgoWebSocketMessageType.Binary, message.MessageType);
                Assert.True(message.Data.SequenceEqual(new byte[] { 0, (byte)'f', (byte)'r', (byte)'a', (byte)'g', 0xff, (byte)'m', (byte)'e', (byte)'n', (byte)'t', (byte)'e', (byte)'d' }));
                Assert.Throws<InvalidOperationException>(() => message.GetText());
            }
            Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send(raw, UgoWebSocketMessageType.Binary));
            Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("after"));
            using (UgoWebSocketMessage echoed = await socket.ReceiveAsync().AsTask().WaitAsync(Limit))
            {
                Assert.Equal(UgoWebSocketMessageType.Binary, echoed.MessageType);
                Assert.True(echoed.Data.SequenceEqual(raw));
            }
            using (UgoWebSocketMessage text = await socket.ReceiveAsync().AsTask().WaitAsync(Limit))
            {
                Assert.Equal(UgoWebSocketMessageType.Text, text.MessageType);
                Assert.Equal("after", text.GetText());
            }
            Assert.True(socket.Close());
            UgoWebSocketMessage last = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
            Assert.True(last.IsEnd);
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal(UgoErrorKind.None, result.Error.Kind);
        }
    }

    [Fact]
    public async Task WebSocketBinaryListenerMessagesAllocateNothing()
    {
        Assert.False(UgoTracking.Enabled);
        using UgoClient client = Background();
        var recorder = new Recorder { Measure = 60 };
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")), recorder);
        await recorder.Opened.Task.WaitAsync(Limit);
        byte[] payload = { 1, 2, 3, 0xff };
        for (int i = 0; i < 60; i++)
            while (socket.Send(payload, UgoWebSocketMessageType.Binary) == UgoWebSocketSendStatus.BufferFull) await Task.Delay(1);
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (Volatile.Read(ref recorder.Allocated) < 0 && UgoClient.MonotonicMicroseconds < end) await Task.Delay(10);
        Assert.Equal(0, Volatile.Read(ref recorder.Allocated));
    }

    [Fact]
    public async Task WebSocketTextIsValidatedAndExtensionsAndForeignSubprotocolsRefused()
    {
        using UgoClient client = Background();
        var bad = WsRequest(Ws("echo"));
        bad.Headers = new[] { new UgoHeader("Authorization", "Bearer bench-token"), new UgoHeader("Sec-WebSocket-Extensions", "permessage-deflate") };
        Assert.Equal("Headers", Assert.Throws<ArgumentException>(() => client.OpenWebSocket(bad)).ParamName);
        using (UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo"))))
        {
            Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            ArgumentException invalid = Assert.Throws<ArgumentException>(() => socket.Send(new byte[] { 0xc0, 0x80 }, UgoWebSocketMessageType.Text));
            Assert.Equal("data", invalid.ParamName);
            Assert.Contains("UTF-8", invalid.Message);
            Assert.Throws<ArgumentOutOfRangeException>(() => socket.Send(new byte[1], (UgoWebSocketMessageType)7));
            Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("\ud800 lone"));
            using UgoWebSocketMessage echoed = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
            Assert.Equal("\ufffd lone", echoed.GetText());
        }
        foreach (string path in new[] { "echo?protocol=chat", "duplicate-accept", "extension?value=~" })
        {
            using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws(path)));
            Assert.False(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
            Assert.Equal((UgoErrorKind.Transfer, UgoErrorReason.WebSocketHandshake), (result.Error.Kind, result.Error.Reason));
        }
        var offered = WsRequest(Ws("echo?protocol=chat"));
        offered.Headers = new[] { new UgoHeader("Authorization", "Bearer bench-token"), new UgoHeader("Sec-WebSocket-Protocol", "superchat, chat") };
        using (UgoWebSocket socket = client.OpenWebSocket(offered))
        {
            Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            Assert.True(socket.TryGetHandshake(out UgoHeadersEvent handshake));
            Assert.Equal("chat", Header(handshake, "Sec-WebSocket-Protocol"));
        }
    }

    [Fact]
    public async Task WebSocketSendLimitsAndArguments()
    {
        using UgoClient client = Background();
        Assert.Equal("Url", Assert.Throws<ArgumentException>(() => client.OpenWebSocket(new UgoWebSocketRequest(bench.Url + "/ws/echo"))).ParamName);
        Assert.Equal("Url", Assert.Throws<ArgumentException>(() => client.OpenWebSocket(default)).ParamName);
        var bad = WsRequest(Ws("echo"));
        bad.Headers = new[] { new UgoHeader("sec-websocket-key", "AAAAAAAAAAAAAAAAAAAAAA==") };
        Assert.Equal("Headers", Assert.Throws<ArgumentException>(() => client.OpenWebSocket(bad)).ParamName);
        bad = WsRequest(Ws("echo"));
        bad.MaxBacklogBytes = bad.MaxMessageBytes - 1;
        Assert.Equal("MaxBacklogBytes", Assert.Throws<ArgumentOutOfRangeException>(() => client.OpenWebSocket(bad)).ParamName);
        bad = WsRequest(Ws("echo"));
        bad.CloseTimeout = TimeSpan.Zero;
        Assert.Equal("CloseTimeout", Assert.Throws<ArgumentOutOfRangeException>(() => client.OpenWebSocket(bad)).ParamName);
        var stalled = WsRequest(Ws("stall-handshake"));
        stalled.OpenTimeout = Limit;
        using (UgoWebSocket connecting = client.OpenWebSocket(stalled))
        {
            Assert.Equal(UgoWebSocketSendStatus.NotOpen, connecting.Send("early"));
            Assert.False(connecting.Close());
            Assert.Equal(UgoWebSocketState.Connecting, connecting.State);
            Assert.False(connecting.TryGetHandshake(out _));
        }
        var request = WsRequest(Ws("stall-read"));
        request.MaxSendBytes = 64 * 1024;
        using UgoWebSocket socket = client.OpenWebSocket(request);
        Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
        Assert.Throws<ArgumentException>(() => socket.Send(new byte[64 * 1024 - 7], UgoWebSocketMessageType.Binary));
        Assert.Throws<ArgumentOutOfRangeException>(() => socket.Close(1005));
        Assert.Throws<ArgumentException>(() => socket.Close(1000, new string('r', 124)));
        byte[] chunk = new byte[16 * 1024];
        chunk.AsSpan().Fill((byte)'a');
        UgoWebSocketSendStatus status = UgoWebSocketSendStatus.Accepted;
        long end = UgoClient.MonotonicMicroseconds + 20_000_000;
        while (status == UgoWebSocketSendStatus.Accepted && UgoClient.MonotonicMicroseconds < end)
        {
            status = socket.Send(chunk, UgoWebSocketMessageType.Binary);
            if (status == UgoWebSocketSendStatus.Accepted) await Task.Yield();
        }
        Assert.Equal(UgoWebSocketSendStatus.BufferFull, status);
        socket.Abort();
        Assert.True((await socket.ReceiveAsync().AsTask().WaitAsync(Limit)).IsEnd);
        Assert.Equal(UgoWebSocketSendStatus.NotOpen, socket.Send(chunk, UgoWebSocketMessageType.Binary));
    }

    [Fact]
    public void WebSocketManualDrainDispatchesInsideTryWaitEvents()
    {
        using UgoClient client = Manual();
        var recorder = new Recorder();
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("ping")), recorder);
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        bool sent = false, closed = false;
        while (!recorder.Closed.Task.IsCompleted && UgoClient.MonotonicMicroseconds < end)
        {
            Assert.True(client.TryWaitEvents(events, 50, out int count));
            Assert.Equal(0, count);
            if (recorder.Opened.Task.IsCompleted && !sent) sent = socket.Send("after pong") == UgoWebSocketSendStatus.Accepted;
            if (recorder.Messages.Count == 1 && !closed) { closed = true; socket.Close(); }
        }
        Assert.True(recorder.Closed.Task.IsCompleted);
        Assert.Equal(new[] { "1:after pong" }, recorder.Messages.ToArray());
        Assert.Equal(UgoErrorKind.None, recorder.Closed.Task.Result.kind);
        Assert.Equal(Environment.CurrentManagedThreadId, recorder.OpenThreadId);
    }

    [Theory]
    [InlineData(Registration.InlineBeforeCompletion)]
    [InlineData(Registration.InlineFlowingContext)]
    [InlineData(Registration.PostedBeforeCompletion)]
    [InlineData(Registration.AfterCompletion)]
    public void ReceiveContinuationSeesItsMessage(Registration registration)
    {
        using UgoClient client = Manual();
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")));
        ValueTask<bool> open = socket.WaitOpenAsync();
        DrainUntil(client, () => open.IsCompleted);
        Assert.True(open.GetAwaiter().GetResult());
        for (int round = 0; round < 2; round++)
        {
            string text = "echo-" + round;
            ValueTask<UgoWebSocketMessage> pending = socket.ReceiveAsync();
            RunRegistered(registration, pending, done =>
            {
                Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send(text));
                DrainUntil(client, done);
            }, message =>
            {
                using (message) Assert.Equal(text, message.GetText());
            });
        }
    }

    [Fact]
    public async Task WebSocketClientDisposeInvalidatesHeldMessagesAndResults()
    {
        using UgoClient client = Background();
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")));
        Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
        Assert.True(socket.TryGetHandshake(out UgoHeadersEvent handshake));
        Assert.Equal("websocket", Header(handshake, "upgrade"));
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("held"));
        using UgoWebSocketMessage message = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
        Assert.Equal("held", message.GetText());

        await Task.Run(() => client.Shutdown());
        Assert.Equal("held", message.GetText());
        Assert.True(socket.TryGetHandshake(out _));
        Assert.True(socket.TryGetResult(out UgoWebSocketResult result));
        Assert.Equal(101, result.Handshake.StatusCode);
        Assert.NotEmpty(result.GetReason());

        client.Dispose();
        Assert.Throws<ObjectDisposedException>(() => message.Data.Length);
        Assert.Throws<ObjectDisposedException>(() => message.GetText());
        Assert.Throws<ObjectDisposedException>(() => socket.TryGetHandshake(out _));
        Assert.Throws<ObjectDisposedException>(() => result.Handshake);
        Assert.Throws<ObjectDisposedException>(() => result.Reason.Length);
        Assert.Throws<ObjectDisposedException>(() => result.GetReason());
        Assert.Equal(UgoErrorReason.Shutdown, result.Error.Reason);
    }

    sealed class MessageHolder : UgoWebSocketListener
    {
        public UgoWebSocketMessage Message;
        public override void OnMessage(UgoWebSocket socket, in UgoWebSocketMessage message) => Message = message;
    }

    [Fact]
    public void WebSocketListenerHeldMessageStaysInvalidAfterClientDispose()
    {
        using UgoClient client = Manual();
        var holder = new MessageHolder();
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")), holder);
        DrainUntil(client, () => socket.State == UgoWebSocketState.Open);
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("held"));
        DrainUntil(client, () => holder.Message.Sequence != 0);
        Assert.Throws<ObjectDisposedException>(() => holder.Message.Data.Length);
        Assert.Throws<ObjectDisposedException>(() => holder.Message.GetText());

        client.Dispose();
        Assert.Throws<ObjectDisposedException>(() => holder.Message.Data.Length);
        Assert.Throws<ObjectDisposedException>(() => holder.Message.GetText());
        Assert.Throws<ObjectDisposedException>(() => socket.TryGetHandshake(out _));
        holder.Message.Dispose();
    }

    [Fact]
    public async Task WebSocketClientDisposeClosesItsSockets()
    {
        var client = Background();
        var recorder = new Recorder();
        UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")), recorder);
        UgoWebSocket waiting = client.OpenWebSocket(WsRequest(Ws("stall-handshake")));
        ValueTask<bool> open = waiting.WaitOpenAsync();
        await recorder.Opened.Task.WaitAsync(Limit);
        client.Dispose();
        var closed = await recorder.Closed.Task.WaitAsync(Limit);
        Assert.Equal((UgoErrorKind.Aborted, UgoErrorReason.Shutdown), (closed.kind, closed.reason));
        Assert.False(await open.AsTask().WaitAsync(Limit));
        Assert.Equal(UgoWebSocketState.Closed, waiting.State);
        socket.Dispose();
        waiting.Dispose();
        Assert.Throws<ObjectDisposedException>(() => client.OpenWebSocket(WsRequest(Ws("echo"))));
    }

    sealed class Thrower : UgoWebSocketListener
    {
        public readonly TaskCompletionSource<bool> Closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void OnOpen(UgoWebSocket socket, in UgoHeadersEvent handshake) => throw new ContinuationFault();
        public override void OnClosed(UgoWebSocket socket, in UgoWebSocketResult result) => Closed.SetResult(true);
    }

    [Fact]
    public async Task WebSocketListenerExceptionIsRethrownOnThePoolAndDrainingContinues()
    {
        Assert.True(faultHandler);
        int faultsBefore = Volatile.Read(ref continuationFaults);
        using UgoClient client = Background();
        var thrower = new Thrower();
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("close")), thrower);
        Assert.True(await thrower.Closed.Task.WaitAsync(Limit));
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (Volatile.Read(ref continuationFaults) == faultsBefore && UgoClient.MonotonicMicroseconds < end) await Task.Delay(10);
        Assert.Equal(faultsBefore + 1, Volatile.Read(ref continuationFaults));
    }

    [Fact]
    public async Task ThrowingWebSocketContinuationsDoNotSkipTheOthers()
    {
        Assert.True(faultHandler);
        int faultsBefore = Volatile.Read(ref continuationFaults);
        using UgoClient client = Background();
        int resumed = 0;

        UgoWebSocket stalled = client.OpenWebSocket(WsRequest(Ws("stall-handshake")));
        ValueTask<bool> open = stalled.WaitOpenAsync();
        ValueTask<UgoWebSocketMessage> end = stalled.ReceiveAsync();
        open.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => { Interlocked.Increment(ref resumed); throw new ContinuationFault(); });
        end.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => Interlocked.Increment(ref resumed));
        stalled.Dispose();
        long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (Volatile.Read(ref resumed) < 2 && UgoClient.MonotonicMicroseconds < deadline) await Task.Delay(10);
        Assert.Equal(2, Volatile.Read(ref resumed));
        Assert.False(open.GetAwaiter().GetResult());
        Assert.True(end.GetAwaiter().GetResult().IsEnd);

        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")));
        Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
        ValueTask<UgoWebSocketMessage> first = socket.ReceiveAsync();
        first.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => { Interlocked.Increment(ref resumed); throw new ContinuationFault(); });
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("one"));
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("two"));
        while (Volatile.Read(ref resumed) < 3 && UgoClient.MonotonicMicroseconds < deadline) await Task.Delay(10);
        Assert.Equal(3, Volatile.Read(ref resumed));
        first.GetAwaiter().GetResult().Dispose();
        using (UgoWebSocketMessage second = await socket.ReceiveAsync().AsTask().WaitAsync(Limit)) Assert.Equal("two", second.GetText());
        Assert.True(socket.Close());
        Assert.True((await socket.ReceiveAsync().AsTask().WaitAsync(Limit)).IsEnd);

        while (Volatile.Read(ref continuationFaults) < faultsBefore + 2 && UgoClient.MonotonicMicroseconds < deadline) await Task.Delay(10);
        Assert.Equal(faultsBefore + 2, Volatile.Read(ref continuationFaults));
    }
}
