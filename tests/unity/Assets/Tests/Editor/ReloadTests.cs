using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UgoHttp;
using UgoHttp.Editor;
using UgoHttp.Native;
using UnityEditor;
using UnityEngine.TestTools;

// Code after a domain reload runs in a new domain on a fresh enumerator: locals and statics are gone, so state crosses in SessionState and a file.
public class ReloadTests
{
    const string PidKey = "ugo.reload.benchPid";
    const string UrlKey = "ugo.reload.benchUrl";
    const string OpsKey = "ugo.reload.ops";
    const string TrackerWindowKey = "ugo.reload.trackerWindow";
    static readonly object LogSync = new object();
    static readonly List<Thread> Drainers = new List<Thread>();
    static int socketsOpen;

    static string LogPath => Path.Combine(Bench.ProjectTemp, "ugo-reload.log");

    [UnityTearDown]
    public IEnumerator StopBench()
    {
        if (EditorApplication.isPlaying) yield return new ExitPlayMode();
        int windowId = SessionState.GetInt(TrackerWindowKey, 0);
        if (EditorUtility.InstanceIDToObject(windowId) is EditorWindow window) window.Close();
        SessionState.EraseInt(TrackerWindowKey);
        int pid = SessionState.GetInt(PidKey, 0);
        if (pid != 0) Bench.Stop(pid);
        SessionState.EraseInt(PidKey);
        SessionState.EraseString(UrlKey);
        SessionState.EraseString(OpsKey);
        if (SessionState.GetBool("ugo.playmode.restoreOptions", false))
        {
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt("ugo.playmode.options", 0);
            EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool("ugo.playmode.optionsEnabled", false);
            SessionState.EraseBool("ugo.playmode.restoreOptions");
            SessionState.EraseInt("ugo.playmode.options");
            SessionState.EraseBool("ugo.playmode.optionsEnabled");
        }
    }

    [UnityTest]
    public IEnumerator DomainReloadPreservesOpenTrackerWindow()
    {
        TrackerWindow window = OpenTrackerWindow();
        window.TableFraction = 0.4f;
        window.Tree.multiColumnHeader.SetSorting((int)TrackerTreeView.Column.Url, false);
        window.Tree.searchString = "/ok";
        window.GroupByHost = false;

        EditorUtility.RequestScriptReload();
        yield return new WaitForDomainReload();

        window = AssertTrackerWindowSurvived();
        Assert.That(window.TableFraction, Is.EqualTo(0.4f));
        Assert.That(window.Tree.multiColumnHeader.sortedColumnIndex, Is.EqualTo((int)TrackerTreeView.Column.Url));
        Assert.That(window.Tree.multiColumnHeader.IsSortedAscending((int)TrackerTreeView.Column.Url), Is.False);
        Assert.That(window.Tree.searchString, Is.EqualTo("/ok"));
        Assert.That(window.GroupByHost, Is.False);
    }

    [UnityTest]
    public IEnumerator PlayModePreservesOpenTrackerWindow()
    {
        ConfigurePlayMode(EnterPlayModeOptions.None);
        OpenTrackerWindow();

        yield return new EnterPlayMode();

        AssertTrackerWindowSurvived();

        yield return new ExitPlayMode();

        AssertTrackerWindowSurvived();
    }

    static TrackerWindow OpenTrackerWindow()
    {
        var window = EditorWindow.GetWindow<TrackerWindow>();
        window.titleContent = new UnityEngine.GUIContent("UgoHTTP reload test");
        SessionState.SetInt(TrackerWindowKey, window.GetInstanceID());
        return window;
    }

    static TrackerWindow AssertTrackerWindowSurvived()
    {
        var window = EditorUtility.InstanceIDToObject(SessionState.GetInt(TrackerWindowKey, 0)) as TrackerWindow;
        Assert.That(window != null, Is.True);
        Assert.That(window.titleContent.text, Is.EqualTo("UgoHTTP reload test"));
        window.Refresh();
        LogAssert.NoUnexpectedReceived();
        return window;
    }

    [UnityTest]
    public IEnumerator PlayModeExitDisposesClientWithoutDomainReload()
    {
        ConfigurePlayMode(EnterPlayModeOptions.DisableDomainReload);
        yield return new EnterPlayMode(false);
        IEnumerator scenario = AssertPlayModeClientDisposed();
        while (scenario.MoveNext()) yield return scenario.Current;
    }

    [UnityTest]
    public IEnumerator PlayModeExitDisposesClientWithDomainReload()
    {
        ConfigurePlayMode(EnterPlayModeOptions.None);
        yield return new EnterPlayMode();
        IEnumerator scenario = AssertPlayModeClientDisposed();
        while (scenario.MoveNext()) yield return scenario.Current;
    }

    [UnityTest]
    public IEnumerator OnDestroyRequestIsAcceptedThenAbortedOnPlayModeExit()
    {
        ConfigurePlayMode(EnterPlayModeOptions.DisableDomainReload);
        yield return new EnterPlayMode(false);
        string url = StartBench();
        using (var client = new UgoClient(new UgoClientOptions(1)))
        {
            var request = new UgoRequest(url + "/stall-after-headers");
            UgoSubmitStatus? status = null;
            Exception error = null;
            UgoError? completed = null;
            var target = new UnityEngine.GameObject("Exit request").AddComponent<ExitRequest>();
            target.OnDestroyed = () =>
            {
                try
                {
                    status = client.Send(in request, response =>
                    {
                        using (response) completed = response.Error;
                    });
                }
                catch (Exception caught) { error = caught; }
            };

            yield return new ExitPlayMode();

            Assert.That(error, Is.Null);
            Assert.That(status, Is.EqualTo(UgoSubmitStatus.Accepted));
            Assert.That(completed.HasValue, Is.True);
            Assert.That(completed.Value.Kind, Is.EqualTo(UgoErrorKind.Aborted));
            Assert.That(completed.Value.Reason, Is.EqualTo(UgoErrorReason.Shutdown));
        }
    }

    static void ConfigurePlayMode(EnterPlayModeOptions options)
    {
        SessionState.SetInt("ugo.playmode.options", (int)EditorSettings.enterPlayModeOptions);
        SessionState.SetBool("ugo.playmode.optionsEnabled", EditorSettings.enterPlayModeOptionsEnabled);
        SessionState.SetBool("ugo.playmode.restoreOptions", true);
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = options;
    }

    static IEnumerator AssertPlayModeClientDisposed()
    {
        Assert.That(EditorApplication.isPlaying, Is.True);
        string url = StartBench();
        using (var client = new UgoClient(new UgoClientOptions(2) { DrainMode = UgoDrainMode.Manual }))
        using (UgoWebSocket socket = client.OpenWebSocket(Bench.WebSocket(url, "echo")))
        {
            int headers = 0;
            var request = new UgoRequest(url + "/stall-after-headers");
            Task<UgoResponse> transfer = client.SendAsync(in request, (in UgoEvent e, object state) =>
            {
                if (e.Kind == UgoEventKind.Headers) Interlocked.Increment(ref headers);
            }).AsTask();
            Task<bool> opened = socket.WaitOpenAsync().AsTask();
            var events = new UgoEvent[8];
            long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
            while ((!opened.IsCompleted || headers == 0) && UgoClient.MonotonicMicroseconds < deadline)
                Assert.That(client.TryWaitEvents(events, 50, out _), Is.True);
            Assert.That(opened.IsCompleted, Is.True);
            Assert.That(opened.Result, Is.True);
            Assert.That(headers, Is.GreaterThan(0));
            Task<UgoWebSocketMessage> end = socket.ReceiveAsync().AsTask();
            Assert.That(transfer.IsCompleted, Is.False);
            Assert.That(socket.State, Is.EqualTo(UgoWebSocketState.Open));

            yield return new ExitPlayMode();

            Assert.That(EditorApplication.isPlaying, Is.False);
            UgoTrackedRow[] rows = UgoTracking.Snapshot().Where(row => row.Client == client.TrackingId).ToArray();
            Assert.That(rows.Length, Is.EqualTo(3));
            Assert.That(rows.All(row => row.State == UgoTracking.Reclaimed), Is.True);
            Assert.That(transfer.IsCompleted, Is.True);
            using (UgoResponse response = transfer.Result)
            {
                Assert.That(response.Error.Kind, Is.EqualTo(UgoErrorKind.Aborted));
                Assert.That(response.Error.Reason, Is.EqualTo(UgoErrorReason.Shutdown));
            }
            Assert.That(end.IsCompleted, Is.True);
            Assert.That(end.Result.IsEnd, Is.True);
            Assert.That(socket.TryGetResult(out UgoWebSocketResult result), Is.True);
            Assert.That(result.Error.Kind, Is.EqualTo(UgoErrorKind.Aborted));
            Assert.That(result.Error.Reason, Is.EqualTo(UgoErrorReason.Shutdown));
            Assert.Throws<ObjectDisposedException>(() => client.Submit(in request, out _));
        }
    }

    [UnityTest]
    public IEnumerator DomainReloadCompletesEveryTransferOnceBeforeUnload()
    {
        string url = StartBench();
        File.WriteAllText(LogPath, "");
        Drainers.Clear();
        socketsOpen = 0;
        var ops = new List<string>();
        int headers = 0;
        UgoEventHandler onEvent = (in UgoEvent e, object state) =>
        {
            if (e.Kind == UgoEventKind.Headers) Interlocked.Increment(ref headers);
        };
        var stalled = new UgoRequest(url + "/stall-after-headers");

        var background = new UgoClient(new UgoClientOptions(4));
        Issue(background, "background", stalled, onEvent, ops);
        IssueWebSockets(background, "background", url, ops);

        var drained = new UgoClient(new UgoClientOptions(4) { DrainMode = UgoDrainMode.Manual });
        var drainer = new Thread(() =>
        {
            var events = new UgoEvent[8];
            try { while (drained.TryWaitEvents(events, 50, out _)) { } }
            catch (Exception error) { Record("drainer-error " + error.GetType().Name); }
        }) { IsBackground = true };
        drainer.Start();
        Issue(drained, "drained", stalled, onEvent, ops);
        IssueWebSockets(drained, "drained", url, ops);

        var undrained = new UgoClient(new UgoClientOptions(4) { DrainMode = UgoDrainMode.Manual });
        int undrainedHeaders = 0;
        UgoEventHandler onUndrained = (in UgoEvent e, object state) =>
        {
            if (e.Kind == UgoEventKind.Headers) Interlocked.Increment(ref undrainedHeaders);
        };
        Issue(undrained, "undrained", stalled, onUndrained, ops);
        IssueWebSockets(undrained, "undrained", url, ops);

        long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
        var buffer = new UgoEvent[8];
        while ((Volatile.Read(ref undrainedHeaders) < 2 || Volatile.Read(ref socketsOpen) < 6) && UgoClient.MonotonicMicroseconds < deadline)
            Assert.That(undrained.TryWaitEvents(buffer, 50, out _), Is.True);
        while (Volatile.Read(ref headers) < 4 && UgoClient.MonotonicMicroseconds < deadline) Thread.Sleep(10);
        Assert.That(Volatile.Read(ref headers) + Volatile.Read(ref undrainedHeaders), Is.EqualTo(6), "Every transfer reaches its stall before the reload.");
        Assert.That(Volatile.Read(ref socketsOpen), Is.EqualTo(6), "Every WebSocket is open before the reload.");

        Drainers.Add((Thread)typeof(UgoClient).GetField("drainThread", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(background));
        Drainers.Add(drainer);
        AppDomain.CurrentDomain.DomainUnload += RecordUnload;
        SessionState.SetString(OpsKey, string.Join(",", ops));

        EditorUtility.RequestScriptReload();
        yield return new WaitForDomainReload();

        string[] lines = File.ReadAllLines(LogPath);
        UnityEngine.Debug.Log("UGO-RELOAD\n" + string.Join("\n", lines));
        int unload = Array.FindIndex(lines, l => l.StartsWith("unload "));
        Assert.That(unload, Is.GreaterThanOrEqualTo(0), "The old domain recorded its unload:\n" + string.Join("\n", lines));
        Assert.That(lines[unload], Is.EqualTo("unload drainers-alive=0"), "No drain thread outlives the reload hook.");
        foreach (string op in SessionState.GetString(OpsKey, "").Split(','))
        {
            string[] done = lines.Where(l => l.StartsWith("done " + op + " ")).ToArray();
            Assert.That(done, Is.EqualTo(new[] { "done " + op + " Aborted Shutdown" }), op);
            Assert.That(Array.IndexOf(lines, done[0]), Is.LessThan(unload), op + " completes before the domain unloads.");
        }
        Assert.That(lines.Where(l => l.StartsWith("drainer-error") || l.StartsWith("ws-")), Is.Empty);
        Assert.That(NativeMethods.ugo_shutdown_all(), Is.Zero, "The reload hook destroyed every native client.");
        AssertGetSucceeds(SessionState.GetString(UrlKey, ""));
    }

    [UnityTest]
    public IEnumerator OrphanedClientIsShutDownOnNextInit()
    {
        string url = StartBench();
        var orphan = new UgoClient(new UgoClientOptions(2) { DrainMode = UgoDrainMode.Manual });
        var request = new UgoRequest(url + "/stall-after-headers");
        Assert.That(orphan.Submit(in request, out UgoId _), Is.EqualTo(UgoSubmitStatus.Accepted));
        var events = new UgoEvent[4];
        bool stalled = false;
        long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (!stalled && UgoClient.MonotonicMicroseconds < deadline)
        {
            Assert.That(orphan.TryWaitEvents(events, 50, out int count), Is.True);
            for (int i = 0; i < count; i++) stalled |= events[i].Kind == UgoEventKind.Headers;
        }
        Assert.That(stalled, Is.True);
        // Detaches the managed side without telling the native one, as a domain whose hook never ran would leave it.
        var handle = (SafeHandle)typeof(UgoClient).GetField("handle", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(orphan);
        handle.SetHandleAsInvalid();

        EditorUtility.RequestScriptReload();
        yield return new WaitForDomainReload();

        new UgoClient(new UgoClientOptions(1)).Dispose();
        Assert.That(NativeMethods.ugo_shutdown_all(), Is.Zero, "The first client of the new domain shut the orphan down.");
        AssertGetSucceeds(SessionState.GetString(UrlKey, ""));
    }

    static string StartBench()
    {
        int pid = Bench.Start("reload", out string url);
        SessionState.SetInt(PidKey, pid);
        SessionState.SetString(UrlKey, url);
        return url;
    }

    static void Issue(UgoClient client, string name, UgoRequest request, UgoEventHandler onEvent, List<string> ops)
    {
        string awaited = name + "-async";
        ValueTask<UgoResponse> pending = client.SendAsync(in request, onEvent);
        pending.ConfigureAwait(false).GetAwaiter().OnCompleted(() => RecordDone(awaited, pending.Result));
        Assert.That(client.Send(in request, (response, state) => RecordDone((string)state, response), name + "-callback", onEvent), Is.EqualTo(UgoSubmitStatus.Accepted));
        ops.Add(awaited);
        ops.Add(name + "-callback");
    }

    static void IssueWebSockets(UgoClient client, string name, string url, List<string> ops)
    {
        string awaited = name + "-ws-async";
        UgoWebSocket socket = client.OpenWebSocket(Bench.WebSocket(url, "echo"));
        ValueTask<bool> opening = socket.WaitOpenAsync();
        opening.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
        {
            if (opening.Result) Interlocked.Increment(ref socketsOpen);
        });
        ValueTask<UgoWebSocketMessage> end = socket.ReceiveAsync();
        end.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
        {
            if (!end.Result.IsEnd) Record("ws-message " + awaited);
            else if (socket.TryGetResult(out UgoWebSocketResult result)) RecordDone(awaited, in result);
            else Record("ws-no-result " + awaited);
        });
        client.OpenWebSocket(Bench.WebSocket(url, "echo"), new Listener(name + "-ws-listener"));
        ops.Add(awaited);
        ops.Add(name + "-ws-listener");
    }

    sealed class Listener : UgoWebSocketListener
    {
        readonly string op;

        public Listener(string op) { this.op = op; }

        public override void OnOpen(UgoWebSocket socket, in UgoHeadersEvent handshake) => Interlocked.Increment(ref socketsOpen);
        public override void OnClosed(UgoWebSocket socket, in UgoWebSocketResult result) => RecordDone(op, in result);
    }

    static void RecordDone(string op, in UgoWebSocketResult result) => Record("done " + op + " " + result.Error.Kind + " " + result.Error.Reason);

    static void RecordDone(string op, UgoResponse response)
    {
        using (response) Record("done " + op + " " + response.Error.Kind + " " + response.Error.Reason);
    }

    static void RecordUnload(object sender, EventArgs e) =>
        Record("unload drainers-alive=" + Drainers.Count(t => t != null && t.IsAlive));

    static void Record(string line)
    {
        lock (LogSync) File.AppendAllText(LogPath, line + "\n");
    }

    static void AssertGetSucceeds(string url)
    {
        using (var client = new UgoClient(new UgoClientOptions(1)))
        {
            Task<UgoResponse> pending = client.SendAsync(new UgoRequest(url + "/ok")).AsTask();
            Assert.That(pending.Wait(10000), Is.True);
            using (UgoResponse response = pending.Result)
                Assert.That(response.IsSuccess, Is.True, response.Error.Kind.ToString());
        }
    }
}
