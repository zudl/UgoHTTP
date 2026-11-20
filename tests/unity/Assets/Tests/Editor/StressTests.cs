using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UgoHttp;

public class StressTests
{
    const int Slots = 16;
    const int Workers = 8;
    const int OperationTimeoutMs = 30000;
    const int ThreadTimeoutMs = 120000;
    int benchPid;
    string benchUrl;

    [OneTimeSetUp]
    public void StartBench() => benchPid = Bench.Start("stress", out benchUrl);

    [OneTimeTearDown]
    public void StopBench() => Bench.Stop(benchPid);

    class Run
    {
        internal readonly int[] Completions = new int[1 << 16];
        internal readonly bool[] AcceptedOps = new bool[1 << 16];
        internal readonly ConcurrentQueue<Exception> Unexpected = new ConcurrentQueue<Exception>();
        internal int Next;
        internal int Accepted;
        internal int Completed;
        internal int Full;
        internal volatile bool Disposed;

        internal int Begin() => Interlocked.Increment(ref Next);

        internal void Accept(int op)
        {
            AcceptedOps[op] = true;
            Interlocked.Increment(ref Accepted);
        }

        internal void Complete(int op, UgoResponse response, bool cancellable = false)
        {
            using (response)
            {
                if (Interlocked.Increment(ref Completions[op]) != 1) Unexpected.Enqueue(new Exception("Second completion of op " + op));
                UgoError error = response.Error;
                bool expected = response.IsSuccess || (error.Kind == UgoErrorKind.Aborted
                    && (error.Reason == UgoErrorReason.Shutdown ? Disposed : cancellable && error.Reason == UgoErrorReason.Caller));
                if (!expected) Unexpected.Enqueue(new Exception("Op " + op + " failed: " + error.Kind + " " + error.Reason));
            }
            Interlocked.Increment(ref Completed);
        }

        internal void Rejected(UgoSubmitStatus status)
        {
            if (status == UgoSubmitStatus.NoCapacity)
            {
                Interlocked.Increment(ref Full);
                Thread.Sleep(1);
                return;
            }
            if (Disposed && status == UgoSubmitStatus.Shutdown) return;
            Unexpected.Enqueue(new Exception("Unexpected rejection: " + status));
        }

        internal void Failed(Exception error)
        {
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1) error = aggregate.InnerException;
            if (error is UgoSubmitException submit) { Rejected(submit.Status); return; }
            if (Disposed && error is ObjectDisposedException) return;
            Unexpected.Enqueue(error);
        }
    }

    [Test]
    public void ConcurrentOperationsCompleteOnceAndFreeEverySlot([Values(UgoDrainMode.Background, UgoDrainMode.Manual)] UgoDrainMode mode)
    {
        var run = new Run();
        var client = new UgoClient(new UgoClientOptions(Slots) { DrainMode = mode });
        Thread drainer = mode == UgoDrainMode.Manual ? StartDrainer(client, run) : null;
        try
        {
            Hammer(client, run, 500);
            WaitCompleted(run);
            AssertClean(run);
            AssertEverySlotFree(client, run);
        }
        finally
        {
            run.Disposed = true;
            client.Dispose();
        }
        Join(drainer);
        WaitCompleted(run);
        AssertClean(run);
    }

    [Test]
    public void DisposeRacingSubmitsAndAbortsCompletesEveryAcceptedOnce([Values(UgoDrainMode.Background, UgoDrainMode.Manual)] UgoDrainMode mode)
    {
        var run = new Run();
        var client = new UgoClient(new UgoClientOptions(Slots) { DrainMode = mode });
        Thread drainer = mode == UgoDrainMode.Manual ? StartDrainer(client, run) : null;
        var disposer = new Thread(() =>
        {
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref run.Next) >= Workers * 400 / 2, ThreadTimeoutMs))
                run.Unexpected.Enqueue(new TimeoutException("Dispose threshold not reached; " + run.Next + " ops begun"));
            run.Disposed = true;
            client.Dispose();
        }) { Name = "stress-disposer", IsBackground = true };
        disposer.Start();
        try
        {
            Hammer(client, run, 400);
        }
        catch
        {
            run.Disposed = true;
            client.Dispose();
            throw;
        }
        Join(disposer, drainer);
        WaitCompleted(run);
        AssertClean(run);
    }

    // Unity's corlib reports a completion source complete before SetResult has returned; resetting the source on that signal corrupts the next wait.
    [Test]
    public void PolledResultFollowedAtOnceByTheNextWait([Values(false, true)] bool webSocket)
    {
        const int MinimumPairs = 200;
        var failures = new ConcurrentQueue<Exception>();
        int[] pairs = new int[Workers];
        long end = 0;
        using (var ready = new CountdownEvent(Workers))
        using (var go = new ManualResetEventSlim())
        {
            Thread[] threads = Enumerable.Range(0, Workers).Select(n => new Thread(() =>
            {
                bool counted = false;
                Func<long> start = () =>
                {
                    counted = true;
                    ready.Signal();
                    go.Wait();
                    return end;
                };
                try { pairs[n] = webSocket ? PollWebSocket(start) : PollHttp(start); }
                catch (Exception error) { failures.Enqueue(error); }
                finally
                {
                    if (!counted) ready.Signal();
                }
            }) { Name = "poll-" + n, IsBackground = true }).ToArray();
            foreach (Thread thread in threads) thread.Start();
            bool allReady = ready.Wait(ThreadTimeoutMs);
            end = UgoClient.MonotonicMicroseconds + 2_000_000;
            go.Set();
            Join(threads);
            Assert.That(allReady, "workers not ready in " + ThreadTimeoutMs + " ms");
        }
        Assert.That(failures.Select(e => e.ToString()), Is.Empty);
        UnityEngine.Debug.Log("UGO-STRESS " + TestContext.CurrentContext.Test.Name + " pairs=" + string.Join(",", pairs));
        Assert.That(pairs, Has.All.GreaterThanOrEqualTo(MinimumPairs), "pairs per worker");
    }

    int PollHttp(Func<long> start)
    {
        using (var client = new UgoClient(new UgoClientOptions(1)))
        {
            var ok = new UgoRequest(benchUrl + "/ok");
            int pairs = 0;
            for (long end = start(); UgoClient.MonotonicMicroseconds < end; pairs++)
            {
                ValueTask<UgoResponse> polled = client.SendAsync(in ok);
                SpinUntilCompleted(() => polled.IsCompleted);
                using (UgoResponse response = polled.AsTask().Result) Check(response.StatusCode == 200, "polled status " + response.StatusCode);
                Task<UgoResponse> next = client.SendAsync(in ok).AsTask();
                Check(next.Wait(OperationTimeoutMs), "next response timed out");
                using (UgoResponse response = next.Result) Check(response.StatusCode == 200, "next status " + response.StatusCode);
            }
            return pairs;
        }
    }

    int PollWebSocket(Func<long> start)
    {
        using (var client = new UgoClient(new UgoClientOptions(1)))
        using (UgoWebSocket socket = client.OpenWebSocket(Bench.WebSocket(benchUrl, "echo")))
        {
            Task<bool> open = socket.WaitOpenAsync().AsTask();
            Check(open.Wait(OperationTimeoutMs) && open.Result, "not open");
            int pairs = 0;
            for (long end = start(); UgoClient.MonotonicMicroseconds < end; pairs++)
            {
                ValueTask<UgoWebSocketMessage> polled = socket.ReceiveAsync();
                Check(socket.Send("polled") == UgoWebSocketSendStatus.Accepted, "send");
                SpinUntilCompleted(() => polled.IsCompleted);
                using (UgoWebSocketMessage message = polled.AsTask().Result) Check(message.GetText() == "polled", "polled message");
                Task<UgoWebSocketMessage> next = socket.ReceiveAsync().AsTask();
                Check(socket.Send("next") == UgoWebSocketSendStatus.Accepted, "send");
                Check(next.Wait(OperationTimeoutMs), "next message timed out");
                using (UgoWebSocketMessage message = next.Result) Check(message.GetText() == "next", "next message");
            }
            Check(socket.Close(), "close");
            Task<UgoWebSocketMessage> last = socket.ReceiveAsync().AsTask();
            Check(last.Wait(OperationTimeoutMs) && last.Result.IsEnd, "no end");
            return pairs;
        }
    }

    static void SpinUntilCompleted(Func<bool> completed)
    {
        long deadline = UgoClient.MonotonicMicroseconds + OperationTimeoutMs * 1000L;
        while (!completed()) Check(UgoClient.MonotonicMicroseconds < deadline, "never completed");
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    void Hammer(UgoClient client, Run run, int iterations)
    {
        var threads = Enumerable.Range(0, Workers)
            .Select(seed => new Thread(() => Work(client, run, iterations, seed)) { Name = "stress-worker-" + seed, IsBackground = true })
            .ToArray();
        foreach (Thread thread in threads) thread.Start();
        Join(threads);
    }

    static void Join(params Thread[] threads)
    {
        long deadline = UgoClient.MonotonicMicroseconds + ThreadTimeoutMs * 1000L;
        string[] running = threads
            .Where(thread => thread != null && !thread.Join((int)Math.Max(0, (deadline - UgoClient.MonotonicMicroseconds) / 1000)))
            .Select(thread => thread.Name)
            .ToArray();
        Assert.That(running, Is.Empty, "Threads still running after " + ThreadTimeoutMs + " ms");
    }

    void Work(UgoClient client, Run run, int iterations, int seed)
    {
        var random = new Random(seed);
        var ok = new UgoRequest(benchUrl + "/ok");
        var slow = new UgoRequest(benchUrl + "/slow-body/1024?bps=40960");
        var shared = new CancellationTokenSource();
        for (int i = 0; i < iterations; i++)
        {
            if (random.Next(20) == 0)
            {
                shared.Cancel();
                shared = new CancellationTokenSource();
            }
            int op = run.Begin();
            UgoRequest request = random.Next(2) == 0 ? ok : slow;
            try
            {
                switch (random.Next(client.DrainMode == UgoDrainMode.Manual ? 6 : 5))
                {
                    case 0:
                        {
                            Task<UgoResponse> pending = client.SendAsync(in request).AsTask();
                            run.Accept(op);
                            if (pending.Wait(OperationTimeoutMs)) run.Complete(op, pending.Result);
                            else run.Unexpected.Enqueue(new TimeoutException("SendAsync op " + op + " did not complete in " + OperationTimeoutMs + " ms"));
                            break;
                        }
                    case 1:
                        {
                            var cancel = new CancellationTokenSource();
                            ValueTask<UgoResponse> pending = client.SendAsync(in request, cancellationToken: cancel.Token);
                            run.Accept(op);
                            if (random.Next(2) == 0) cancel.Cancel();
                            else cancel.CancelAfter(random.Next(1, 50));
                            pending.ConfigureAwait(false).GetAwaiter().OnCompleted(() => run.Complete(op, pending.Result, true));
                            break;
                        }
                    case 2:
                        {
                            UgoSubmitStatus status = client.Send(in request, (response, state) => run.Complete((int)state, response, true), op, null, shared.Token);
                            if (status != UgoSubmitStatus.Accepted) { run.Rejected(status); break; }
                            run.Accept(op);
                            break;
                        }
                    case 3:
                        {
                            UgoSubmitStatus status = client.Send(in request, response => run.Complete(op, response));
                            if (status != UgoSubmitStatus.Accepted) { run.Rejected(status); break; }
                            run.Accept(op);
                            break;
                        }
                    case 4:
                        {
                            UgoSubmitStatus status = client.EnqueueDetached(in ok);
                            if (status != UgoSubmitStatus.Accepted) run.Rejected(status);
                            break;
                        }
                    default:
                        {
                            UgoSubmitStatus status = client.Submit(in request, out UgoId id);
                            if (status != UgoSubmitStatus.Accepted) { run.Rejected(status); break; }
                            client.Abort(id);
                            break;
                        }
                }
            }
            catch (Exception error) { run.Failed(error); }
        }
    }

    // Raw Submit operations are aborted at once and released by the drainer; only Send/SendAsync completions are counted.
    static Thread StartDrainer(UgoClient client, Run run)
    {
        var thread = new Thread(() =>
        {
            var events = new UgoEvent[32];
            try
            {
                while (client.TryWaitEvents(events, 20, out int count))
                    for (int i = 0; i < count; i++)
                        if (events[i].Kind == UgoEventKind.Completed && client.TryGetResponse(events[i].Id, out UgoResponse response))
                            response.Dispose();
            }
            catch (Exception error) { run.Failed(error); }
        }) { Name = "stress-drainer", IsBackground = true };
        thread.Start();
        return thread;
    }

    static void WaitCompleted(Run run)
    {
        if (SpinWait.SpinUntil(() => Volatile.Read(ref run.Completed) == Volatile.Read(ref run.Accepted), OperationTimeoutMs)) return;
        int[] stuck = Enumerable.Range(0, run.AcceptedOps.Length).Where(op => run.AcceptedOps[op] && Volatile.Read(ref run.Completions[op]) == 0).Take(20).ToArray();
        Assert.Fail("Completed " + run.Completed + " of " + run.Accepted + "; not completed ops: " + string.Join(", ", stuck));
    }

    static void AssertClean(Run run)
    {
        Assert.That(run.Unexpected.Select(e => e.ToString()), Is.Empty);
        Assert.That(run.Completions.Count(c => c > 1), Is.Zero);
        Assert.That(run.Completions.Count(c => c == 1), Is.EqualTo(run.Accepted));
        UnityEngine.Debug.Log("UGO-STRESS " + TestContext.CurrentContext.Test.Name + " accepted=" + run.Accepted + " full=" + run.Full + " ops=" + run.Next);
    }

    // Detached transfers free their slots only when they finish, so the full count may take a moment to come back.
    void AssertEverySlotFree(UgoClient client, Run run)
    {
        var stalled = new UgoRequest(benchUrl + "/stall-after-headers");
        int held = 0;
        long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (held < Slots && UgoClient.MonotonicMicroseconds < deadline)
        {
            int op = run.Begin();
            UgoSubmitStatus status = client.Send(in stalled, response => run.Complete(op, response));
            if (status == UgoSubmitStatus.Accepted) { held++; run.Accept(op); }
            else
            {
                Assert.That(status, Is.EqualTo(UgoSubmitStatus.NoCapacity));
                Thread.Sleep(1);
            }
        }
        Assert.That(held, Is.EqualTo(Slots));
        Assert.That(client.Send(in stalled, response => response.Dispose()), Is.EqualTo(UgoSubmitStatus.NoCapacity));
    }
}
