using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UgoHttp;
using Xunit;

public partial class IntegrationTests
{
    static UgoTrackedRow[] Rows(UgoClient client) => UgoTracking.Snapshot().Where(row => row.Client == client.TrackingId).ToArray();
    static UgoTrackedRow[] LiveRows(UgoClient client) => Rows(client).Where(row => row.IsLive).ToArray();

    static void Track(bool enabled, bool stackTraces)
    {
        UgoTracking.Enabled = enabled;
        UgoTracking.StackTraces = stackTraces;
    }

    static UgoTrackedRow Completed(int client, ulong id, string state, long ended) =>
        new UgoTrackedRow(client, id, "Submit", state, 0, null, ended, false, default);

    [Fact]
    public async Task TrackerListsLeakedResponsesUntilDisposed()
    {
        Track(true, true);
        try
        {
            using UgoClient client = Manual();
            UgoTrackedRow own = Assert.Single(Rows(client));
            Assert.Equal(("Client", "Not disposed"), (own.Kind, own.State));
            Assert.Contains(nameof(TrackerListsLeakedResponsesUntilDisposed), own.Stack.ToString());

            string url = bench.Url + "/ok";
            UgoResponse leaked = Get(client, new UgoRequest(url) { Method = HttpMethod.Post });
            ValueTask<UgoResponse> pending = client.SendAsync(new UgoRequest(url));
            UgoTrackedRow inFlight = Rows(client).Single(row => row.Kind == "SendAsync");
            Assert.Equal("In flight", inFlight.State);
            DrainUntil(client, () => pending.IsCompleted);
            UgoResponse awaited = await pending;

            UgoTrackedRow raw = Rows(client).Single(row => row.Id == leaked.Id.Value);
            Assert.Equal(("Submit", "Response not disposed", "POST", url, 200u), (raw.Kind, raw.State, raw.Method, raw.Url, raw.HttpStatus));
            Assert.True(raw.IsLive && raw.Started != 0);
            string stack = raw.Stack.ToString();
            Assert.Contains("UgoClient.Submit", stack);
            Assert.Contains(nameof(TrackerListsLeakedResponsesUntilDisposed), stack);
            Assert.Equal("Response not disposed", Rows(client).Single(row => row.Id == awaited.Id.Value).State);

            leaked.Dispose();
            awaited.Dispose();
            Assert.Single(LiveRows(client));
            UgoTrackedRow closed = Rows(client).Single(row => row.Id == leaked.Id.Value);
            Assert.Equal((UgoTracking.Released, false, 200u, raw.Started, url), (closed.State, closed.IsLive, closed.HttpStatus, closed.Started, closed.Url));
            Assert.Same(raw.Stack, closed.Stack);
            client.Dispose();
            Assert.Empty(LiveRows(client));
            Assert.Equal(UgoTracking.Disposed, Rows(client).Single(row => row.Id == 0).State);
            client.Dispose();
            Assert.Single(Rows(client), row => row.Id == 0);
        }
        finally { Track(false, false); }
    }

    [Fact]
    public void TrackerCountsOnlyTheResumedPartAsReceived()
    {
        Track(true, false);
        try
        {
            using UgoClient client = Manual();
            string part = System.IO.Path.Combine(bench.Run, "tracker-resume.part");
            System.IO.File.WriteAllBytes(part, new byte[4000]);
            var request = new UgoRequest(bench.Url + "/range/10000") { ResponseFilePath = part, Resume = true };
            Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId id));
            var events = new UgoEvent[1];
            int progress = 0;
            UgoResponse response = default;
            long end = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (!client.TryGetResponse(id, out response) && UgoClient.MonotonicMicroseconds < end)
            {
                Assert.True(client.TryWaitEvents(events, 100, out int count));
                if (count == 0 || events[0].Kind != UgoEventKind.Progress) continue;
                UgoTrackedRow live = Rows(client).Single(row => row.Id == id.Value);
                Assert.False(live.HasResult);
                Assert.Equal((ulong)(events[0].Progress.BytesReceived - 4000), live.Received);
                progress++;
            }
            using (response)
            {
                Assert.Equal(206, response.StatusCode);
                Assert.True(progress > 0);
                Assert.Equal(6000ul, Rows(client).Single(row => row.Id == id.Value).Received);
            }
        }
        finally { Track(false, false); }
    }

    [Fact]
    public void TrackerCountsAMatchingRangeRefusalBodyAsReceived()
    {
        Track(true, false);
        try
        {
            using UgoClient client = Manual();
            string part = System.IO.Path.Combine(bench.Run, "tracker-416.part");
            System.IO.File.WriteAllBytes(part, new byte[10000]);
            var request = new UgoRequest(bench.Url + "/range/10000?mode=big-416") { ResponseFilePath = part, Resume = true };
            Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId id));
            var events = new UgoEvent[1];
            long offset = -1;
            int progress = 0;
            UgoResponse response = default;
            long end = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (!client.TryGetResponse(id, out response) && UgoClient.MonotonicMicroseconds < end)
            {
                Assert.True(client.TryWaitEvents(events, 100, out int count));
                if (count == 0) continue;
                if (events[0].Kind == UgoEventKind.Headers) offset = events[0].Headers.ResumeOffset;
                if (events[0].Kind != UgoEventKind.Progress) continue;
                UgoTrackedRow live = Rows(client).Single(row => row.Id == id.Value);
                Assert.False(live.HasResult);
                Assert.Equal((ulong)events[0].Progress.BytesReceived, live.Received);
                Assert.InRange(live.Received, 0ul, 5ul * 16384);
                progress++;
            }
            using (response)
            {
                Assert.Equal(416, response.StatusCode);
                Assert.Equal(UgoErrorReason.ResponseBodyLimit, response.Error.Reason);
                Assert.Equal(10000, offset);
                Assert.True(progress > 0);
                UgoTrackedRow done = Rows(client).Single(row => row.Id == id.Value);
                Assert.Equal((ulong)response.BytesReceived, done.Received);
                Assert.InRange(done.Received, 1ul, 5ul * 16384);
            }
            Assert.Equal(10000, new System.IO.FileInfo(part).Length);
        }
        finally { Track(false, false); }
    }

    [Fact]
    public void TrackerMarksResponsesLeftToTheClient()
    {
        Track(true, false);
        try
        {
            UgoClient client = Manual();
            UgoResponse leaked = Get(client, new UgoRequest(bench.Url + "/ok"));
            UgoResponse released = Get(client, new UgoRequest(bench.Url + "/ok"));
            released.Dispose();
            client.Dispose();
            UgoTrackedRow[] rows = Rows(client);
            Assert.Equal(UgoTracking.ClosedWithClient, rows.Single(row => row.Id == leaked.Id.Value).State);
            Assert.Equal(UgoTracking.Released, rows.Single(row => row.Id == released.Id.Value).State);
            Assert.Equal(UgoTracking.Disposed, rows.Single(row => row.Id == 0).State);
            Assert.All(rows, row => Assert.False(row.IsEvidence));

            UgoClient reclaimed = Manual();
            UgoResponse held = Get(reclaimed, new UgoRequest(bench.Url + "/ok"));
            reclaimed.Reclaim();
            rows = Rows(reclaimed);
            Assert.Equal(UgoTracking.Reclaimed, rows.Single(row => row.Id == held.Id.Value).State);
            Assert.Equal(UgoTracking.Reclaimed, rows.Single(row => row.Id == 0).State);
            Assert.All(rows, row => Assert.True(row.IsEvidence));
        }
        finally { Track(false, false); }
    }

    [Fact]
    public async Task TrackerDoesNotBlameShutdownForHeldResponses()
    {
        long start = Stopwatch.GetTimestamp();
        Track(true, false);
        try
        {
            await ShutdownCompletesPendingAwaitsInBothModes();
            await DisposeFromCallbackAndContinuation(UgoDrainMode.Background);
            await DisposeFromCallbackAndContinuation(UgoDrainMode.Manual);
            UgoTrackedRow[] rows = UgoTracking.Snapshot().Where(row => row.Started >= start).ToArray();
            Assert.Contains(rows, row => row.State == UgoTracking.ClosedWithClient);
            Assert.All(rows, row => Assert.False(row.IsEvidence));
        }
        finally { Track(false, false); }
    }

    [Fact]
    public async Task TrackerMarksAbortedOperations()
    {
        Track(true, false);
        try
        {
            using UgoClient client = Background();
            using var cancel = new CancellationTokenSource();
            ValueTask<UgoResponse> pending = client.SendAsync(new UgoRequest(bench.Url + "/stall-after-headers"), null, null, cancel.Token);
            cancel.Cancel();
            UgoResponse response = await pending.AsTask().WaitAsync(Limit);
            ulong id = response.Id.Value;
            response.Dispose();
            UgoTrackedRow row = Rows(client).Single(r => r.Id == id);
            Assert.Equal((UgoTracking.Aborted, UgoErrorKind.Aborted, UgoErrorReason.Caller), (row.State, row.Error, row.Reason));
        }
        finally { Track(false, false); }
    }

    [Fact]
    public void TrackerHidesClosedRowsAfterRetentionButKeepsEvidence()
    {
        const int client = -1;
        long ended = Stopwatch.GetTimestamp();
        UgoTracking.Complete(Completed(client, 1, UgoTracking.Released, ended));
        UgoTracking.Complete(Completed(client, 2, UgoTracking.ClosedWithClient, ended));
        UgoTracking.Complete(Completed(client, 3, UgoTracking.Reclaimed, ended));
        ulong[] Shown(long now) => UgoTracking.Snapshot(now).Where(row => row.Client == client).Select(row => row.Id).OrderBy(id => id).ToArray();
        Assert.Equal(new ulong[] { 1, 2, 3 }, Shown(ended + UgoTracking.Retention - 1));
        Assert.Equal(new ulong[] { 3 }, Shown(ended + UgoTracking.Retention));
        Assert.Equal(new ulong[] { 3 }, Shown(ended + 1000 * UgoTracking.Retention));
        Assert.Equal(10 * Stopwatch.Frequency, UgoTracking.Retention);
    }

    [Fact]
    public void TrackerRingKeepsTheNewestRows()
    {
        const int client = -2;
        long ended = Stopwatch.GetTimestamp();
        for (int i = 1; i <= UgoTracking.Capacity + 100; i++) UgoTracking.Complete(Completed(client, (ulong)i, UgoTracking.ClosedWithClient, ended));
        ulong[] ids = UgoTracking.Snapshot().Where(row => row.Client == client).Select(row => row.Id).ToArray();
        Assert.Equal(UgoTracking.Capacity, ids.Length);
        Assert.Equal((ulong)(UgoTracking.Capacity + 100), ids[0]);
        Assert.Equal(101UL, ids[ids.Length - 1]);
    }

    [Fact]
    public async Task TrackerCountsHeldWebSocketMessages()
    {
        Track(true, false);
        try
        {
            using UgoClient client = Background();
            UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")));
            Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
            Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("held"));
            UgoWebSocketMessage message = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
            UgoTrackedRow row = LiveRows(client).Single(r => r.Kind == "WebSocket");
            Assert.Equal(("Open", 1L, Ws("echo")), (row.State, row.HeldMessages, row.Url));
            Assert.Null(row.Stack);
            message.Dispose();
            Assert.Equal(0, LiveRows(client).Single(r => r.Kind == "WebSocket").HeldMessages);
            socket.Abort();
            Assert.True((await socket.ReceiveAsync().AsTask().WaitAsync(Limit)).IsEnd);
            Assert.Equal("Closed, not disposed", LiveRows(client).Single(r => r.Kind == "WebSocket").State);
            socket.Dispose();
            Assert.Single(LiveRows(client));
            Assert.Equal(UgoTracking.Aborted, Rows(client).Single(r => r.Kind == "WebSocket").State);
        }
        finally { Track(false, false); }
    }

    [Fact]
    public async Task TrackerCountsHeldWebSocketMessagesWhenOff()
    {
        using UgoClient client = Background();
        using UgoWebSocket socket = client.OpenWebSocket(WsRequest(Ws("echo")));
        Assert.True(await socket.WaitOpenAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("first"));
        Assert.Equal(UgoWebSocketSendStatus.Accepted, socket.Send("second"));
        UgoWebSocketMessage first = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
        UgoWebSocketMessage second = await socket.ReceiveAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(2, Rows(client).Single(r => r.Kind == "WebSocket").HeldMessages);
        first.Dispose();
        Assert.Equal(1, Rows(client).Single(r => r.Kind == "WebSocket").HeldMessages);
        second.Dispose();
        Assert.Equal(0, Rows(client).Single(r => r.Kind == "WebSocket").HeldMessages);
    }

    [Fact]
    public void TrackerRecordsNothingWhenOff()
    {
        UgoClient client = Manual();
        UgoResponse response = Get(client, new UgoRequest(bench.Url + "/ok"));
        UgoTrackedRow[] rows = Rows(client);
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.True(row.Url == null && row.Method == null && row.Stack == null && row.Started == 0 && row.IsLive));
        Assert.Equal("Response not disposed", rows.Single(row => row.Id == response.Id.Value).State);
        response.Dispose();
        Get(client, new UgoRequest(bench.Url + "/ok"));
        client.Dispose();
        Assert.Empty(Rows(client));
    }
}
