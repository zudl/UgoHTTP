using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UgoHttp;
using UnityEditor;
using UnityEngine.Scripting;

public class LifetimeTests
{
    int benchPid;
    string benchUrl;

    [OneTimeSetUp]
    public void StartBench() => benchPid = Bench.Start("lifetime", out benchUrl);

    [OneTimeTearDown]
    public void StopBench() => Bench.Stop(benchPid);

    [Test]
    public void GlueAssemblyIsAlwaysLinked()
    {
        Assembly glue = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "UgoHttp.Unity");
        Assert.That(glue.IsDefined(typeof(AlwaysLinkAssemblyAttribute)), Is.True);
    }

    [Test]
    public void ReloadAndQuitHooksAreInstalledOnce()
    {
        Assert.That(UgoHooks(typeof(AssemblyReloadEvents), "m_BeforeAssemblyReloadEvent").Length, Is.EqualTo(1));
        Assert.That(UgoHooks(typeof(EditorApplication), "m_QuittingEvent").Length, Is.EqualTo(1));
    }

    [Test]
    public void QuitHookDeliversEveryCompletionOnceAndStopsDraining()
    {
        var done = new ConcurrentDictionary<string, string>();
        var duplicates = new ConcurrentQueue<string>();
        int headers = 0;
        UgoEventHandler onEvent = (in UgoEvent e, object state) =>
        {
            if (e.Kind == UgoEventKind.Headers) Interlocked.Increment(ref headers);
        };
        Action<string, UgoResponse> record = (op, response) =>
        {
            using (response)
                if (!done.TryAdd(op, response.Error.Kind + " " + response.Error.Reason)) duplicates.Enqueue(op);
        };
        var stalled = new UgoRequest(benchUrl + "/stall-after-headers");
        var background = new UgoClient(new UgoClientOptions(4));
        var manual = new UgoClient(new UgoClientOptions(4) { DrainMode = UgoDrainMode.Manual });
        try
        {
            foreach ((UgoClient client, string name) in new[] { (background, "background"), (manual, "manual") })
            {
                ValueTask<UgoResponse> pending = client.SendAsync(in stalled, onEvent);
                pending.ConfigureAwait(false).GetAwaiter().OnCompleted(() => record(name + "-async", pending.Result));
                Assert.That(client.Send(in stalled, (response, state) => record((string)state, response), name + "-callback", onEvent),
                    Is.EqualTo(UgoSubmitStatus.Accepted));
            }
            var events = new UgoEvent[8];
            long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (Volatile.Read(ref headers) < 4 && UgoClient.MonotonicMicroseconds < deadline)
                Assert.That(manual.TryWaitEvents(events, 20, out _), Is.True);
            Assert.That(Volatile.Read(ref headers), Is.EqualTo(4));
            var drainer = (Thread)typeof(UgoClient).GetField("drainThread", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(background);

            UgoHooks(typeof(EditorApplication), "m_QuittingEvent").Single().DynamicInvoke();

            Assert.That(drainer.IsAlive, Is.False);
            Assert.That(duplicates, Is.Empty);
            Assert.That(done.Keys.OrderBy(k => k), Is.EqualTo(new[] { "background-async", "background-callback", "manual-async", "manual-callback" }));
            Assert.That(done.Values.Distinct(), Is.EqualTo(new[] { "Aborted Shutdown" }));
            Assert.Throws<ObjectDisposedException>(() => background.SendAsync(in stalled));
            Assert.Throws<InvalidOperationException>(() => new UgoClient(new UgoClientOptions(1)));
        }
        finally
        {
            typeof(UgoClient).GetField("closing", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, false);
            background.Dispose();
            manual.Dispose();
        }
    }

    // Unity keeps editor events in private EventWithPerformanceTracker<T> structures whose layout changes between versions; search them for delegates.
    static Delegate[] UgoHooks(Type owner, string eventField)
    {
        object tracker = owner.GetField(eventField, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        return Delegates(tracker, 8).Distinct().Where(d => d.Method.DeclaringType == typeof(UgoClient)).ToArray();
    }

    static IEnumerable<Delegate> Delegates(object value, int depth)
    {
        if (value == null || depth < 0 || value is string || value.GetType().IsPrimitive || value is Type) return Enumerable.Empty<Delegate>();
        if (value is Delegate d) return d.GetInvocationList();
        IEnumerable<object> children = value is IEnumerable items
            ? items.Cast<object>()
            : value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.GetValue(value));
        return children.SelectMany(child => Delegates(child, depth - 1));
    }
}
