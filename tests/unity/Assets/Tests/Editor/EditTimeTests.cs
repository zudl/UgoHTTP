using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UgoHttp;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public class EditTimeTests
{
    static UgoClient liveClient;
    int benchPid;
    string benchUrl;
    bool previousOptionsEnabled;
    EnterPlayModeOptions previousOptions;

    [SetUp]
    public void StartClientAndBench()
    {
        previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        previousOptions = EditorSettings.enterPlayModeOptions;
        liveClient = new UgoClient(new UgoClientOptions(2) { DrainMode = UgoDrainMode.Manual });
        benchPid = Bench.Start("edit-time", out benchUrl);
    }

    [UnityTearDown]
    public IEnumerator RestoreSettingsAndStopClientAndBench()
    {
        if (EditorApplication.isPlaying) yield return new ExitPlayMode();
        try
        {
            liveClient?.Dispose();
            liveClient = null;
            if (benchPid != 0) Bench.Stop(benchPid);
            benchPid = 0;
        }
        finally
        {
            EditorSettings.enterPlayModeOptions = previousOptions;
            EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
        }
    }

    [Test]
    public void GetCompletesWithoutPlayModeOrPumping()
    {
        Assert.That(EditorApplication.isPlaying, Is.False);
        AssertGet(liveClient, benchUrl);
    }

    [UnityTest]
    public IEnumerator ClientSurvivesEnteringAndLeavingPlayModeWithoutDomainReload()
    {
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        UgoClient before = liveClient;
        using (var background = new UgoClient(new UgoClientOptions(1)))
        {
            Task<UgoResponse> inFlight = background.SendAsync(new UgoRequest(benchUrl + "/slow-body/2048?bps=1024")).AsTask();
            yield return new EnterPlayMode(false);
            Assert.That(EditorApplication.isPlaying, Is.True);
            yield return new ExitPlayMode();
            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(inFlight.Wait(10000), Is.True);
            using (UgoResponse response = inFlight.Result)
            {
                Assert.That(response.IsSuccess, Is.True, response.Error.Kind + " " + response.Error.Reason);
                Assert.That(response.Body.Length, Is.EqualTo(2048));
            }
        }
        Assert.That(liveClient, Is.SameAs(before));
        AssertGet(liveClient, benchUrl);
    }

    [Test]
    public void BackgroundSendCompletesWithoutPlayModeOrPumping()
    {
        Assert.That(EditorApplication.isPlaying, Is.False);
        using (var client = new UgoClient(new UgoClientOptions(2)))
        {
            Task<UgoResponse> pending = client.SendAsync(new UgoRequest(benchUrl + "/ok")).AsTask();
            Assert.That(pending.Wait(10000), Is.True);
            using (UgoResponse response = pending.Result)
            {
                Assert.That(response.IsSuccess, Is.True, response.Error.Kind.ToString());
                Assert.That(response.GetBodyAsString(), Is.EqualTo("UgoHTTP fault bench\n"));
            }
        }
    }

    [Test]
    public void WebSocketEchoesAndClosesWithoutPlayModeOrPumping([Values] UgoDrainMode mode, [Values] bool listener)
    {
        Assert.That(EditorApplication.isPlaying, Is.False);
        using (var client = new UgoClient(new UgoClientOptions(2) { DrainMode = mode }))
        {
            if (listener)
            {
                var recorder = new Recorder();
                using (UgoWebSocket socket = client.OpenWebSocket(Bench.WebSocket(benchUrl, "echo"), recorder))
                {
                    WaitOpen(client, recorder);
                    EchoAndClose(client, socket, recorder);
                }
                return;
            }
            using (UgoWebSocket socket = client.OpenWebSocket(Bench.WebSocket(benchUrl, "echo")))
            {
                Assert.That(Await(client, socket.WaitOpenAsync().AsTask()), Is.True);
                Assert.That(socket.Send("hello"), Is.EqualTo(UgoWebSocketSendStatus.Accepted));
                using (UgoWebSocketMessage message = Await(client, socket.ReceiveAsync().AsTask()))
                    Assert.That(message.GetText(), Is.EqualTo("hello"));
                Assert.That(socket.Send(Binary, UgoWebSocketMessageType.Binary), Is.EqualTo(UgoWebSocketSendStatus.Accepted));
                using (UgoWebSocketMessage message = Await(client, socket.ReceiveAsync().AsTask()))
                {
                    Assert.That(message.MessageType, Is.EqualTo(UgoWebSocketMessageType.Binary));
                    Assert.That(message.Data.ToArray(), Is.EqualTo(Binary));
                    Assert.Throws<InvalidOperationException>(() => message.GetText());
                }
                Assert.That(socket.Close(1000, "bye"), Is.True);
                Assert.That(Await(client, socket.ReceiveAsync().AsTask()).IsEnd, Is.True);
                Assert.That(socket.TryGetResult(out UgoWebSocketResult result), Is.True);
                Assert.That(Describe(in result), Is.EqualTo("None WebSocketClosedByLocal 1000 bye"));
            }
        }
    }

    [UnityTest]
    public IEnumerator WebSocketSurvivesEnteringAndLeavingPlayModeWithoutDomainReload()
    {
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        var recorder = new Recorder();
        using (var client = new UgoClient(new UgoClientOptions(1)))
        using (UgoWebSocket socket = client.OpenWebSocket(Bench.WebSocket(benchUrl, "echo"), recorder))
        {
            WaitOpen(client, recorder);
            Assert.That(socket.Send("before"), Is.EqualTo(UgoWebSocketSendStatus.Accepted));
            yield return new EnterPlayMode(false);
            Assert.That(EditorApplication.isPlaying, Is.True);
            yield return new ExitPlayMode();
            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(socket.State, Is.EqualTo(UgoWebSocketState.Open), recorder.Closed);
            Pump(client, () => recorder.Messages.Contains("before"));
            EchoAndClose(client, socket, recorder);
            Assert.That(recorder.Messages, Is.EqualTo(new[] { "before", "hello", "binary 00-FF-C0-80" }));
        }
    }

    sealed class Recorder : UgoWebSocketListener
    {
        public readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
        public volatile bool Opened;
        public volatile string Closed;

        public override void OnOpen(UgoWebSocket socket, in UgoHeadersEvent handshake) => Opened = handshake.StatusCode == 101;
        public override void OnMessage(UgoWebSocket socket, in UgoWebSocketMessage message) =>
            Messages.Enqueue(message.MessageType == UgoWebSocketMessageType.Binary ? "binary " + BitConverter.ToString(message.Data.ToArray()) : message.GetText());
        public override void OnClosed(UgoWebSocket socket, in UgoWebSocketResult result) => Closed = Describe(in result);
    }

    static string Describe(in UgoWebSocketResult result) =>
        result.Error.Kind + " " + result.Error.Reason + " " + result.CloseCode + " " + result.GetReason();

    static void WaitOpen(UgoClient client, Recorder recorder)
    {
        Pump(client, () => recorder.Opened || recorder.Closed != null);
        Assert.That(recorder.Opened, Is.True, recorder.Closed ?? "not open within 10 s");
    }

    static readonly byte[] Binary = { 0x00, 0xff, 0xc0, 0x80 };

    static void EchoAndClose(UgoClient client, UgoWebSocket socket, Recorder recorder)
    {
        Assert.That(socket.Send("hello"), Is.EqualTo(UgoWebSocketSendStatus.Accepted));
        Pump(client, () => recorder.Messages.Contains("hello") || recorder.Closed != null);
        Assert.That(recorder.Messages, Does.Contain("hello"), recorder.Closed);
        Assert.That(socket.Send(Binary, UgoWebSocketMessageType.Binary), Is.EqualTo(UgoWebSocketSendStatus.Accepted));
        Pump(client, () => recorder.Messages.Contains("binary 00-FF-C0-80") || recorder.Closed != null);
        Assert.That(recorder.Messages, Does.Contain("binary 00-FF-C0-80"), recorder.Closed);
        Assert.That(socket.Close(1000, "bye"), Is.True);
        Pump(client, () => recorder.Closed != null);
        Assert.That(recorder.Closed, Is.EqualTo("None WebSocketClosedByLocal 1000 bye"));
    }

    static T Await<T>(UgoClient client, Task<T> task)
    {
        Pump(client, () => task.IsCompleted);
        Assert.That(task.IsCompleted, Is.True, "not completed within 10 s");
        return task.Result;
    }

    [Test]
    public void ContinuationSeesItsOperationCompleted([Values] bool webSocket, [Values] bool registeredFirst)
    {
        using (var client = new UgoClient(new UgoClientOptions(1) { DrainMode = UgoDrainMode.Manual }))
        using (UgoWebSocket socket = webSocket ? client.OpenWebSocket(Bench.WebSocket(benchUrl, "echo")) : null)
        {
            if (socket != null)
            {
                ValueTask<bool> open = socket.WaitOpenAsync();
                Pump(client, () => open.IsCompleted);
                Assert.That(open.Result, Is.True);
            }
            for (int round = 0; round < 2; round++)
                if (socket == null)
                    AssertContinuationSeesCompleted(client, client.SendAsync(new UgoRequest(benchUrl + "/ok")), registeredFirst, response =>
                    {
                        using (response) Assert.That(response.StatusCode, Is.EqualTo(200));
                    });
                else
                {
                    ValueTask<UgoWebSocketMessage> receive = socket.ReceiveAsync();
                    Assert.That(socket.Send("echo"), Is.EqualTo(UgoWebSocketSendStatus.Accepted));
                    AssertContinuationSeesCompleted(client, receive, registeredFirst, message =>
                    {
                        using (message) Assert.That(message.GetText(), Is.EqualTo("echo"));
                    });
                }
        }
    }

    static void AssertContinuationSeesCompleted<T>(UgoClient client, ValueTask<T> pending, bool registeredFirst, Action<T> check)
    {
        ConfiguredValueTaskAwaitable<T>.ConfiguredValueTaskAwaiter awaiter = pending.ConfigureAwait(false).GetAwaiter();
        int ran = 0;
        bool completedInside = false;
        Action continuation = () =>
        {
            // Bounded: a callback that waits for IsCompleted must not wait on the SetResult that invoked it.
            completedInside = SpinWait.SpinUntil(() => awaiter.IsCompleted, 500);
            check(awaiter.GetResult());
            Interlocked.Increment(ref ran);
        };
        if (registeredFirst) awaiter.UnsafeOnCompleted(continuation);
        Pump(client, () => registeredFirst ? Volatile.Read(ref ran) != 0 : pending.IsCompleted);
        if (!registeredFirst)
        {
            awaiter.UnsafeOnCompleted(continuation);
            SpinWait.SpinUntil(() => Volatile.Read(ref ran) != 0, 10000);
        }
        Assert.That(Volatile.Read(ref ran), Is.EqualTo(1));
        Assert.That(completedInside, Is.True, "IsCompleted was false inside the continuation SetResult ran");
    }

    static void Pump(UgoClient client, Func<bool> done)
    {
        var events = new UgoEvent[8];
        long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (!done() && UgoClient.MonotonicMicroseconds < deadline)
        {
            if (client.DrainMode == UgoDrainMode.Manual) Assert.That(client.TryWaitEvents(events, 50, out _), Is.True);
            else Thread.Sleep(10);
        }
    }

    static void AssertGet(UgoClient client, string url)
    {
        var request = new UgoRequest(url + "/ok");
        Assert.That(client.Submit(in request, out UgoId id), Is.EqualTo(UgoSubmitStatus.Accepted));
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        bool completed = false;
        long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (!completed && UgoClient.MonotonicMicroseconds < deadline)
        {
            Assert.That(client.TryWaitEvents(events, 100, out int count), Is.True);
            for (int i = 0; i < count; i++)
            {
                if (events[i].Kind != UgoEventKind.Completed) continue;
                Assert.That(events[i].Id, Is.EqualTo(id));
                completed = true;
            }
        }
        Assert.That(completed, Is.True);
        Assert.That(client.TryGetResponse(id, out UgoResponse response), Is.True);
        using (response)
        {
            Assert.That(response.IsSuccess, Is.True);
            Assert.That(response.GetBodyAsString(), Is.EqualTo("UgoHTTP fault bench\n"));
        }
    }
}
