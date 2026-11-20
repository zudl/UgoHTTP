using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UgoHttp;
using UgoHttp.Editor;
using UgoHttp.Native;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using Object = UnityEngine.Object;

public class TrackerWindowTests
{
    int benchPid;
    string benchUrl;

    [OneTimeSetUp]
    public void StartBench() => benchPid = Bench.Start("tracker", out benchUrl);

    [OneTimeTearDown]
    public void StopBench() => Bench.Stop(benchPid);

    static TrackerTreeView.Item Item(TrackerWindow window, UgoClient client, ulong id) =>
        window.Tree.VisibleItems.Cast<TrackerTreeView.Item>().SingleOrDefault(item => item.Row.Client == client.TrackingId && item.Row.Id == id);

    [Test]
    public void TrackingIsOnInTheEditor() => Assert.That(UgoTracking.Enabled, Is.True);

    [Test]
    public void WindowKeepsAReleasedResponseGreyed()
    {
        bool stackTraces = UgoTracking.StackTraces;
        UgoTracking.StackTraces = true;
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            window.GroupByHost = false;
            using var client = new UgoClient(new UgoClientOptions(2) { DrainMode = UgoDrainMode.Manual });
            string url = benchUrl + "/ok";
            Assert.That(client.Submit(new UgoRequest(url), out UgoId id), Is.EqualTo(UgoSubmitStatus.Accepted));
            var events = new UgoEvent[8];
            UgoResponse response = default;
            long end = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (!client.TryGetResponse(id, out response) && UgoClient.MonotonicMicroseconds < end) client.TryWaitEvents(events, 100, out _);
            window.Refresh();
            TrackerTreeView.Item live = Item(window, client, id.Value);
            Assert.That(live.Text(TrackerTreeView.Column.Url), Is.EqualTo(url));
            Assert.That(live.Text(TrackerTreeView.Column.State), Is.EqualTo("Response not disposed"));
            Assert.That(live.Greyed, Is.False);
            Assert.That(live.Text(TrackerTreeView.Column.Bytes), Does.EndWith(" B"));

            window.Tree.Select(client.TrackingId, id.Value);
            string stack = window.SelectedStackText;
            Assert.That(stack, Does.Contain(nameof(WindowKeepsAReleasedResponseGreyed)));
            Assert.That(stack, Does.Contain("<link=href=\""));
            Assert.That(stack, Does.Contain("TrackerWindowTests.cs\" line=\""));
            Assert.That(stack, Does.Not.Contain("UgoHttp.UgoClient"));

            response.Dispose();
            window.Refresh();
            TrackerTreeView.Item released = Item(window, client, id.Value);
            Assert.That(released.id, Is.EqualTo(live.id));
            Assert.That(released.Greyed, Is.True);
            Assert.That(released.Text(TrackerTreeView.Column.State), Is.EqualTo(UgoTracking.Released));
            Assert.That(released.Text(TrackerTreeView.Column.Result), Is.EqualTo("200"));
            Assert.That(window.Tree.TryGetSelected(out UgoTrackedRow selected) && selected.Id == id.Value && !selected.IsLive, Is.True);

            window.Tree.searchString = "no such row";
            window.Tree.Reload();
            Assert.That(Item(window, client, id.Value), Is.Null);
            window.Tree.searchString = "/ok";
            window.Tree.Reload();
            Assert.That(Item(window, client, id.Value), Is.Not.Null);
        }
        finally
        {
            Object.DestroyImmediate(window);
            UgoTracking.StackTraces = stackTraces;
        }
    }

    static UgoTrackedRow Row(int client, ulong id, long started, string state = "In flight", long ended = 0, string url = "/ok", StackTrace stack = null, string method = null, ulong received = 0, ulong sent = 0) =>
        new UgoTrackedRow(client, id, "Submit", state, 0,
            new UgoTrace { Started = started, Url = url, Stack = stack, Method = method, Received = received, Sent = sent }, ended, false, default);

    static TrackerTreeView.Item[] Visible(TrackerWindow window) => window.Tree.VisibleItems.Cast<TrackerTreeView.Item>().ToArray();

    [Test]
    public void SelectionSurvivesAMissingSnapshot()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            long now = Stopwatch.GetTimestamp();
            window.Refresh(new List<UgoTrackedRow> { Row(1, 17, now) }, now);
            window.Tree.Select(1, 17);
            int id = window.Tree.GetSelection().Single();
            window.Refresh(new List<UgoTrackedRow>(), now + 1);
            Assert.That(window.Tree.TryGetSelected(out _), Is.False);
            window.Refresh(new List<UgoTrackedRow> { Row(1, 17, now, UgoTracking.Released, now + 1) }, now + 2);
            Assert.That(window.Tree.GetSelection().Single(), Is.EqualTo(id));
            Assert.That(window.Tree.TryGetSelected(out UgoTrackedRow selected), Is.True);
            Assert.That(selected.Id, Is.EqualTo(17));
            Assert.That(window.Tree.VisibleItems.Single().id, Is.EqualTo(id));
        }
        finally { Object.DestroyImmediate(window); }
    }

    [Test]
    public void RowsStayInCreationOrderAndKeepTheirScrollPosition()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            long now = Stopwatch.GetTimestamp();
            window.Refresh(new List<UgoTrackedRow> { Row(2, 7, now + 1), Row(1, 3, now), Row(1, 2, now) }, now + 2);
            ulong[] Ids() => window.Tree.VisibleItems.Cast<TrackerTreeView.Item>().Select(item => item.Row.Id).ToArray();
            Assert.That(Ids(), Is.EqualTo(new ulong[] { 2, 3, 7 }));
            window.Tree.Select(1, 3);
            window.Tree.state.scrollPos = new Vector2(11, 20);
            window.Refresh(new List<UgoTrackedRow>
            {
                Row(2, 7, now + 1), Row(1, 3, now, UgoTracking.ClosedWithClient, now + 2),
                Row(1, 2, now), Row(2, 8, now + 2), Row(1, 1, now - 1)
            }, now + 3);
            Assert.That(Ids(), Is.EqualTo(new ulong[] { 1, 2, 3, 7, 8 }));
            Assert.That(window.Tree.state.scrollPos, Is.EqualTo(new Vector2(11, 40)));
            Assert.That(window.Tree.VisibleItems.Cast<TrackerTreeView.Item>().Single(item => item.Row.Id == 3).Greyed, Is.True);

            window.Tree.multiColumnHeader.SetSorting((int)TrackerTreeView.Column.State, true);
            Assert.That(Ids().First(), Is.EqualTo(3));
            Assert.That(window.Tree.TryGetSelected(out UgoTrackedRow selected) && selected.Id == 3, Is.True);
            window.Tree.multiColumnHeader.SetSorting((int)TrackerTreeView.Column.Url, false);
            Assert.That(Ids(), Is.EqualTo(new ulong[] { 1, 2, 3, 7, 8 }));
            window.Refresh(new List<UgoTrackedRow>
            {
                Row(2, 8, now + 2), Row(2, 7, now + 1), Row(1, 3, now, UgoTracking.ClosedWithClient, now + 2)
            }, now + UgoTracking.Retention);
            Assert.That(Ids(), Is.EqualTo(new ulong[] { 3, 7, 8 }));
            Assert.That(window.Tree.state.scrollPos, Is.EqualTo(new Vector2(11, 0)));
        }
        finally { Object.DestroyImmediate(window); }
    }

    sealed class SourceFrame : StackFrame
    {
        readonly string path;
        internal SourceFrame(string path) => this.path = path;
        public override MethodBase GetMethod() => typeof(TrackerWindowTests).GetMethod(nameof(SourceLinksRoundTripThroughUnity));
        public override string GetFileName() => path;
        public override int GetFileLineNumber() => 42;
    }

    [TestCase("Assets/O'Connor/My Client.cs")]
    [TestCase("Assets/My Folder/Client.cs")]
    public void SourceLinksRoundTripThroughUnity(string path)
    {
        string text = TrackerWindow.FormatStack(new StackTrace(new SourceFrame(path)), true);
        var editor = new TextEditor
        {
            style = new GUIStyle(EditorStyles.label) { richText = true },
            position = new Rect(0, 0, 4096, 40),
            text = text
        };
        bool hit = false;
        string link = "";
        for (int x = 0; x < 4096 && !hit; x++)
            hit = editor.HasClickedOnLink(new Vector2(x, 8), out link);
        Assert.That(hit, Is.True, text);
        Assert.That(link, Is.EqualTo("href=\"" + path + "\" line=\"42\""));
        Regex Parser(string name) => (Regex)typeof(EditorGUI).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        MatchCollection attributes = Parser("s_ATagRegex").Matches(link);
        if (attributes.Count == 0) attributes = Parser("s_LinkTagRegex").Matches(link);
        Assert.That(attributes.Count, Is.EqualTo(2));
        Assert.That(attributes[0].Value, Is.EqualTo(path));
        Assert.That(attributes[1].Value, Is.EqualTo("42"));
    }

    [Test]
    public void WindowShowsWhyAStackIsMissing()
    {
        bool stackTraces = UgoTracking.StackTraces;
        UgoTracking.StackTraces = false;
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            using var client = new UgoClient(new UgoClientOptions(1));
            window.Refresh();
            Assert.That(window.SelectedStackText, Does.StartWith("Select a row"));
            window.Tree.Select(client.TrackingId, 0);
            Assert.That(window.SelectedStackText, Does.StartWith("Capture Stacks is off"));
        }
        finally
        {
            Object.DestroyImmediate(window);
            UgoTracking.StackTraces = stackTraces;
        }
    }

    [Test]
    public void ColumnsShrinkBelowTheirValues()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            long now = Stopwatch.GetTimestamp();
            string url = "https://example.test/" + new string('a', 2000);
            window.GroupByHost = false;
            window.Refresh(new List<UgoTrackedRow> { Row(1, 1, now, url: url) }, now);
            MultiColumnHeaderState.Column[] columns = window.Tree.multiColumnHeader.state.columns;
            Assert.That(columns.All(column => column.minWidth <= 30), Is.True);
            Assert.That(columns[(int)TrackerTreeView.Column.Url].width, Is.EqualTo(300));
            Assert.That(columns[(int)TrackerTreeView.Column.Url].autoResize, Is.True);
            Assert.That(columns.Count(column => column.autoResize), Is.EqualTo(1));
            Assert.That(Visible(window).Single().Cells[(int)TrackerTreeView.Column.Url].tooltip, Is.EqualTo(url));
        }
        finally { Object.DestroyImmediate(window); }
    }

    [Test]
    public void ScrollStaysPinnedAtTheNewRowsEdge()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            long now = Stopwatch.GetTimestamp();
            var rows = new List<UgoTrackedRow>();
            void Add(int count)
            {
                for (int i = 0; i < count; i++) rows.Add(Row(1, (ulong)rows.Count + 1, now + rows.Count));
                window.Refresh(new List<UgoTrackedRow>(rows), now + rows.Count);
            }
            TrackerTreeView tree = window.Tree;
            tree.ViewRect = new Rect(0, 0, 500, 100);
            Add(20);
            float Bottom() => tree.totalHeight - 100;

            tree.state.scrollPos.y = Bottom();
            Add(1);
            Assert.That(tree.state.scrollPos.y, Is.GreaterThanOrEqualTo(Bottom()));
            tree.state.scrollPos.y = 40;
            Add(1);
            Assert.That(tree.state.scrollPos.y, Is.EqualTo(40));

            tree.multiColumnHeader.SetSorting((int)TrackerTreeView.Column.Started, false);
            tree.state.scrollPos.y = 0;
            Add(2);
            Assert.That(tree.state.scrollPos.y, Is.EqualTo(0));
            Assert.That(Visible(window).First().Row.Id, Is.EqualTo(24));
            tree.state.scrollPos.y = 40;
            Add(1);
            Assert.That(tree.state.scrollPos.y, Is.EqualTo(60));
        }
        finally { Object.DestroyImmediate(window); }
    }

    [Test]
    public void CopyCommandsCopyTheSelectedRows()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            long now = Stopwatch.GetTimestamp();
            window.GroupByHost = false;
            window.Refresh(new List<UgoTrackedRow>
            {
                Row(1, 1, now, url: "https://a.test/one", stack: new StackTrace(true)),
                Row(1, 2, now + 1, url: "https://b.test/two"),
                Row(1, 3, now + 2, url: "https://c.test/three")
            }, now + 3);
            TrackerTreeView tree = window.Tree;
            Assert.That(tree.CopyCommands(), Is.Empty);

            tree.Select(1, 2);
            Assert.That(tree.CopyCommands().Select(command => command.Label), Is.EqualTo(new[] { TrackerTreeView.CopyUrl, TrackerTreeView.CopyRow }));
            Assert.That(tree.CopyText(TrackerTreeView.CopyUrl), Is.EqualTo("https://b.test/two"));
            Assert.That(tree.CopyText(TrackerTreeView.CopyRow), Is.EqualTo(string.Join("\t", Visible(window)[1].Cells.Select(cell => cell.text))));
            Assert.That(tree.CopyText(TrackerTreeView.CopyRow).Split('\t')[(int)TrackerTreeView.Column.State], Is.EqualTo("In flight"));

            tree.SetSelection(new[] { Visible(window)[2].id, Visible(window)[0].id });
            Assert.That(tree.CopyText(TrackerTreeView.CopyUrl), Is.EqualTo("https://a.test/one\nhttps://c.test/three"));
            (string Label, string Text) stack = tree.CopyCommands().Single(command => command.Label == TrackerTreeView.CopyStack);
            Assert.That(stack.Text, Does.Contain(nameof(CopyCommandsCopyTheSelectedRows)));
            Assert.That(stack.Text, Does.Not.Contain("<link"));
        }
        finally { Object.DestroyImmediate(window); }
    }

    [Test]
    public void RowsAreGroupedByHostCollapsedByDefault()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            long now = Stopwatch.GetTimestamp();
            var rows = new List<UgoTrackedRow>
            {
                new UgoTrackedRow(1, 0, "Client", "Not disposed", 0, null, 0, false, default),
                Row(1, 1, now, url: "https://a.test/one", received: 1024, sent: 100),
                Row(1, 2, now + 1, url: "https://b.test/only"),
                Row(1, 3, now + 2, UgoTracking.Released, now + 3, "https://a.test/two", method: "PUT", received: 2048)
            };
            window.Refresh(rows, now + 4);
            TrackerTreeView.Item[] visible = Visible(window);
            Assert.That(visible.Select(item => item.IsGroup ? item.Host : item.Text(TrackerTreeView.Column.Url)),
                Is.EqualTo(new[] { "", "b.test", "a.test" }));
            Assert.That(visible[2].Text(TrackerTreeView.Column.Method), Is.EqualTo("PUT"));
            Assert.That(visible[2].Text(TrackerTreeView.Column.Bytes), Is.EqualTo("↑100 B ↓1.0 KiB"));
            Assert.That(visible[1].Text(TrackerTreeView.Column.Bytes), Is.Empty);
            window.Tree.SetExpanded(visible[1].id, true);
            window.Tree.SetExpanded(visible[2].id, true);
            Assert.That(window.ExpandedHosts, Is.EquivalentTo(new[] { "a.test", "b.test" }));
            visible = Visible(window);
            Assert.That(visible.Select(item => item.IsGroup ? item.Host : item.Text(TrackerTreeView.Column.Url)),
                Is.EqualTo(new[] { "", "b.test", "https://b.test/only", "a.test", "https://a.test/one", "https://a.test/two" }));
            TrackerTreeView.Item group = visible[3];
            Assert.That(group.Text(TrackerTreeView.Column.Method), Is.Empty);
            Assert.That(group.Text(TrackerTreeView.Column.State), Is.EqualTo("1 live · 0 done · 0 conn"));
            Assert.That(visible[4].depth, Is.EqualTo(1));
            Assert.That(visible[1].depth, Is.EqualTo(0));

            window.Tree.SetSelection(new[] { group.id });
            Assert.That(window.Tree.CopyText(TrackerTreeView.CopyUrl), Is.EqualTo("https://a.test/one\nhttps://a.test/two"));

            window.Tree.SetExpanded(group.id, false);
            Assert.That(window.ExpandedHosts, Is.EquivalentTo(new[] { "b.test" }));
            rows.Add(Row(1, 4, now + 5, url: "https://a.test/three"));
            window.Refresh(new List<UgoTrackedRow>(rows), now + 6);
            visible = Visible(window);
            Assert.That(visible.Length, Is.EqualTo(4));
            Assert.That(visible[3].id, Is.EqualTo(group.id));
            Assert.That(visible[3].Text(TrackerTreeView.Column.State), Is.EqualTo("2 live · 0 done · 0 conn"));
            Assert.That(window.Tree.GetSelection(), Is.EqualTo(new[] { group.id }));

            window.GroupByHost = false;
            Assert.That(Visible(window).Length, Is.EqualTo(5));
            Assert.That(Visible(window).Any(item => item.IsGroup), Is.False);
            window.GroupByHost = true;
            Assert.That(Visible(window).Where(item => item.IsGroup).Select(item => item.Host), Is.EqualTo(new[] { "b.test", "a.test" }));
            Assert.That(window.Tree.IsExpanded(Visible(window).Single(item => item.IsGroup && item.Host == "b.test").id), Is.True);
        }
        finally { Object.DestroyImmediate(window); }
    }

    [Test]
    public void HostTotalsCountFinishedOperations()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        UgoTracking.ClearTotals();
        try
        {
            long now = Stopwatch.GetTimestamp();
            var done = new ugo_ev_completed { http_status = 200, bytes_received = 2048, bytes_sent = 10, connect_us = 5 };
            var aborted = new ugo_ev_completed { error_class = (uint)UgoErrorKind.Aborted };
            var trace = new UgoTrace { Started = now, Url = "https://k.test/x" };
            UgoTracking.Complete(new UgoTrackedRow(1, 7, "Submit", UgoTracking.Released, 0, trace, now + 1, true, in done));
            UgoTracking.Complete(new UgoTrackedRow(1, 8, "Submit", UgoTracking.Aborted, 0, trace, now + 1, true, in aborted));

            window.Refresh(new List<UgoTrackedRow> { Row(1, 9, now, url: "https://k.test/y", received: 1024) }, now);
            TrackerTreeView.Item host = Visible(window).Single();
            Assert.That(host.Text(TrackerTreeView.Column.State), Is.EqualTo("1 live · 1 done · 1 aborted · 1 conn"));
            Assert.That(host.Text(TrackerTreeView.Column.Bytes), Is.EqualTo("↑10 B ↓3.0 KiB"));

            window.Refresh(new List<UgoTrackedRow>(), now);
            Assert.That(Visible(window), Is.Empty);
            window.KeepHosts = true;
            host = Visible(window).Single();
            Assert.That(host.Host, Is.EqualTo("k.test"));
            Assert.That(host.Text(TrackerTreeView.Column.State), Is.EqualTo("0 live · 1 done · 1 aborted · 1 conn"));

            UgoTracking.ClearTotals();
            window.Refresh(new List<UgoTrackedRow>(), now);
            Assert.That(Visible(window), Is.Empty);
        }
        finally
        {
            UgoTracking.ClearTotals();
            Object.DestroyImmediate(window);
        }
    }

    [Test]
    public void ReleaseBetweenSnapshotAndRenderIsNotCountedTwice()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        UgoTracking.ClearTotals();
        try
        {
            using var client = new UgoClient(new UgoClientOptions(2) { DrainMode = UgoDrainMode.Manual });
            Assert.That(client.Submit(new UgoRequest(benchUrl + "/ok"), out UgoId id), Is.EqualTo(UgoSubmitStatus.Accepted));
            var events = new UgoEvent[8];
            UgoResponse response = default;
            long end = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (!client.TryGetResponse(id, out response) && UgoClient.MonotonicMicroseconds < end) client.TryWaitEvents(events, 100, out _);
            long now = Stopwatch.GetTimestamp();
            List<UgoTrackedRow> snapshot = UgoTracking.Snapshot(now, window.Tree.Totals, ref window.Tree.TotalsVersion);
            UgoTrackedRow live = snapshot.Single(row => row.Client == client.TrackingId && row.Id == id.Value);
            Assert.That(live.Received, Is.GreaterThan(0));
            string host = UgoTracking.Host(live.Url);
            response.Dispose();

            window.Show(snapshot, now);
            TrackerTreeView.Item group = Visible(window).Single(item => item.IsGroup && item.Host == host);
            Assert.That(group.Text(TrackerTreeView.Column.State), Does.StartWith("1 live · 0 done"));
            string bytes = group.Text(TrackerTreeView.Column.Bytes);

            window.Refresh();
            group = Visible(window).Single(item => item.IsGroup && item.Host == host);
            Assert.That(group.Text(TrackerTreeView.Column.State), Does.StartWith("0 live · 1 done"));
            Assert.That(group.Text(TrackerTreeView.Column.Bytes), Is.EqualTo(bytes));
        }
        finally
        {
            UgoTracking.ClearTotals();
            Object.DestroyImmediate(window);
        }
    }

    [Test]
    public void ClearHostTotalsKeepsAPausedTable()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        UgoTracking.ClearTotals();
        try
        {
            long now = Stopwatch.GetTimestamp();
            var done = new ugo_ev_completed { http_status = 200, bytes_received = 2048 };
            UgoTracking.Complete(new UgoTrackedRow(1, 7, "Submit", UgoTracking.Released, 0, new UgoTrace { Started = now, Url = "https://p.test/x" }, now + 1, true, in done));
            window.Refresh(new List<UgoTrackedRow> { Row(1, 9, now, url: "https://p.test/y", received: 1024) }, now);
            window.Paused = true;
            Assert.That(Visible(window).Single(item => item.IsGroup).Text(TrackerTreeView.Column.State), Does.StartWith("1 live · 1 done"));

            window.ClearHostTotals();
            TrackerTreeView.Item group = Visible(window).Single(item => item.IsGroup);
            Assert.That(group.Host, Is.EqualTo("p.test"));
            Assert.That(group.Text(TrackerTreeView.Column.State), Is.EqualTo("1 live · 0 done · 0 conn"));
            Assert.That(group.Text(TrackerTreeView.Column.Bytes), Is.EqualTo("1.0 KiB"));
        }
        finally
        {
            UgoTracking.ClearTotals();
            Object.DestroyImmediate(window);
        }
    }

    [Test]
    public void UnchangedSnapshotKeepsTheItems()
    {
        var window = ScriptableObject.CreateInstance<TrackerWindow>();
        try
        {
            long now = Stopwatch.GetTimestamp();
            var rows = new List<UgoTrackedRow> { Row(1, 1, now), Row(1, 2, now, UgoTracking.Released, now + 1) };
            window.Refresh(rows, now + 2);
            TrackerTreeView.Item[] before = Visible(window);
            string closedState = before[1].Text(TrackerTreeView.Column.State);
            window.Refresh(new List<UgoTrackedRow>(rows), now + 2 + Stopwatch.Frequency);
            TrackerTreeView.Item[] after = Visible(window);
            Assert.That(after[0], Is.SameAs(before[0]));
            Assert.That(after[1], Is.SameAs(before[1]));
            Assert.That(after[1].Text(TrackerTreeView.Column.State), Is.SameAs(closedState));
            Assert.That(after[0].Text(TrackerTreeView.Column.Elapsed), Is.EqualTo((1 + 2.0 / Stopwatch.Frequency).ToString("0.0") + " s"));
        }
        finally { Object.DestroyImmediate(window); }
    }
}
