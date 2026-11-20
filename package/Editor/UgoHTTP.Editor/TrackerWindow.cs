using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

[assembly: InternalsVisibleTo("UgoHttp.EditMode.Tests")]

namespace UgoHttp.Editor
{
    [InitializeOnLoad]
    internal static class TrackerPreferences
    {
        internal const string StackTracesKey = "UgoHttp.Tracker.StackTraces";

        static TrackerPreferences()
        {
            UgoTracking.Enabled = true;
            UgoTracking.StackTraces = EditorPrefs.GetBool(StackTracesKey, false);
        }
    }

    internal sealed class TrackerWindow : EditorWindow
    {
        const float SplitterHeight = 5, MinTableHeight = 60, MinDetailsHeight = 40;
        static readonly Color SplitterLine = new Color(0, 0, 0, 0.35f);
        static readonly GUIContent PauseLabel = new GUIContent("Pause", "Freezes the table to inspect it; new activity is not shown until resumed.");
        static readonly GUIContent ViewLabel = new GUIContent("View");

        [SerializeField] TreeViewState<int> treeState;
        [SerializeField] MultiColumnHeaderState headerState;
        [SerializeField] float tableFraction = 0.7f;
        [SerializeField] int lastRowId;
        [SerializeField] bool groupByHost = true;
        [SerializeField] bool keepHosts;

        bool paused;
        TrackerTreeView tree;
        SearchField search;
        string counts = "";
        StackTrace renderedStack;
        bool rendered, hasRendered;
        string stackText = "";
        Vector2 stackSize, stackScroll;
        GUIStyle stackStyle;
        double nextRefresh;
        bool visible;

        [MenuItem("Window/UgoHTTP/Tracker")]
        static void Open() => GetWindow<TrackerWindow>("UgoHTTP Tracker");

        internal TrackerTreeView Tree => tree;
        internal bool Paused { get => paused; set => paused = value; }
        internal float TableFraction { get => tableFraction; set => tableFraction = value; }
        internal readonly HashSet<string> ExpandedHosts = new HashSet<string>();

        internal bool GroupByHost
        {
            get => groupByHost;
            set
            {
                groupByHost = value;
                tree.Rebuild();
            }
        }

        internal bool KeepHosts
        {
            get => keepHosts;
            set
            {
                keepHosts = value;
                tree.Rebuild();
            }
        }

        internal int NextRowId() => ++lastRowId;

        internal void Refresh()
        {
            nextRefresh = EditorApplication.timeSinceStartup + 0.5;
            long now = Stopwatch.GetTimestamp();
            Show(UgoTracking.Snapshot(now, tree.Totals, ref tree.TotalsVersion), now);
        }

        internal void Refresh(List<UgoTrackedRow> snapshot, long now)
        {
            UgoTracking.Totals(tree.Totals, ref tree.TotalsVersion);
            Show(snapshot, now);
        }

        internal void Show(List<UgoTrackedRow> snapshot, long now)
        {
            if (!tree.Refresh(snapshot, now)) return;
            int live = 0, leaked = 0, closed = 0;
            foreach (UgoTrackedRow row in snapshot)
            {
                if (row.IsEvidence) leaked++;
                else if (!row.IsLive) closed++;
                else if (row.Id != 0) live++;
            }
            counts = live + " live · " + leaked + " leaked · " + closed + " closed";
        }

        internal string SelectedStackText
        {
            get
            {
                bool selected = tree.TryGetSelected(out UgoTrackedRow row);
                if (selected != hasRendered || row.Stack != renderedStack || !rendered)
                {
                    hasRendered = selected;
                    renderedStack = row.Stack;
                    rendered = true;
                    stackText = !selected ? "Select a row to see where it was created."
                        : row.Stack != null ? FormatStack(row.Stack, true)
                        : UgoTracking.StackTraces ? "No stack: this object was created before Capture Stacks was turned on."
                        : "Capture Stacks is off. Turn it on to record where new clients and operations are created.";
                    stackSize = Vector2.zero;
                }
                return stackText;
            }
        }

        internal static string FormatStack(StackTrace stack, bool links)
        {
            string root = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/') + "/";
            var text = new StringBuilder();
            for (int i = 0; i < stack.FrameCount; i++)
            {
                StackFrame frame = stack.GetFrame(i);
                MethodBase method = frame.GetMethod();
                Type type = method?.DeclaringType;
                if (type == null || type.Namespace == "UgoHttp" || (type.Namespace?.StartsWith("UgoHttp.", StringComparison.Ordinal) ?? false)) continue;
                text.Append(type.FullName).Append('.').Append(method.Name).Append("()");
                string file = frame.GetFileName();
                if (!string.IsNullOrEmpty(file))
                {
                    file = file.Replace('\\', '/');
                    if (file.StartsWith(root, StringComparison.Ordinal)) file = file.Substring(root.Length);
                    int line = frame.GetFileLineNumber();
                    if (!links) text.Append(" (at ").Append(file).Append(':').Append(line).Append(')');
                    // An unquoted link id lets Unity parse double-quoted attributes containing apostrophes.
                    else text.Append(" (at <color=#4C7EFF><link=href=\"").Append(file).Append("\" line=\"").Append(line).Append("\">")
                        .Append(file).Append(':').Append(line).Append("</link></color>)");
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        void OnEnable()
        {
            if (treeState == null) treeState = new TreeViewState<int>();
            var fresh = new MultiColumnHeaderState(TrackerTreeView.Columns());
            if (MultiColumnHeaderState.CanOverwriteSerializedFields(headerState, fresh)) MultiColumnHeaderState.OverwriteSerializedFields(headerState, fresh);
            headerState = fresh;
            var header = new MultiColumnHeader(headerState);
            tree = new TrackerTreeView(treeState, header, this);
            header.sortingChanged += _ => tree.Rebuild();
            search = new SearchField();
            search.downOrUpArrowKeyPressed += tree.SetFocusAndEnsureSelectedItem;
            EditorApplication.update += Tick;
            Refresh();
        }

        void OnDisable() => EditorApplication.update -= Tick;
        void OnBecameVisible() => visible = true;
        void OnBecameInvisible() => visible = false;

        void Tick()
        {
            if (!visible || paused || EditorApplication.timeSinceStartup < nextRefresh) return;
            Refresh();
            Repaint();
        }

        void OnGUI()
        {
            int splitter = GUIUtility.GetControlID(FocusType.Passive);
            float toolbarHeight = EditorStyles.toolbar.fixedHeight;
            DrawToolbar(new Rect(0, 0, position.width, toolbarHeight));
            var body = new Rect(0, toolbarHeight, position.width, Math.Max(0, position.height - toolbarHeight));
            float available = Math.Max(0, body.height - SplitterHeight);
            float tableHeight = Mathf.Clamp(available * tableFraction, Math.Min(MinTableHeight, available), Math.Max(Math.Min(MinTableHeight, available), available - MinDetailsHeight));
            var table = new Rect(body.x, body.y, body.width, tableHeight);
            var handle = new Rect(body.x, table.yMax, body.width, SplitterHeight);
            var details = new Rect(body.x, handle.yMax, body.width, Math.Max(0, body.yMax - handle.yMax));
            DragSplitter(splitter, handle, body.y, available);
            tree.OnGUI(table);
            DrawDetails(details);
        }

        void DrawToolbar(Rect rect)
        {
            GUILayout.BeginArea(rect, EditorStyles.toolbar);
            using (new EditorGUILayout.HorizontalScope())
            {
                paused = GUILayout.Toggle(paused, PauseLabel, EditorStyles.toolbarButton);
                if (EditorGUILayout.DropdownButton(ViewLabel, FocusType.Passive, EditorStyles.toolbarDropDown)) ViewMenu().ShowAsContext();
                GUILayout.FlexibleSpace();
                GUILayout.Label(counts, EditorStyles.miniLabel);
                string query = search.OnToolbarGUI(tree.searchString, GUILayout.MinWidth(120), GUILayout.MaxWidth(300));
                if (query != tree.searchString)
                {
                    tree.searchString = query;
                    tree.Reload();
                }
            }
            GUILayout.EndArea();
        }

        GenericMenu ViewMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Group by Host"), groupByHost, () => GroupByHost = !groupByHost);
            var keep = new GUIContent("Keep Finished Hosts");
            if (groupByHost) menu.AddItem(keep, keepHosts, () => KeepHosts = !keepHosts);
            else menu.AddDisabledItem(keep, keepHosts);
            menu.AddItem(new GUIContent("Capture Stacks"), UgoTracking.StackTraces, () =>
            {
                UgoTracking.StackTraces = !UgoTracking.StackTraces;
                EditorPrefs.SetBool(TrackerPreferences.StackTracesKey, UgoTracking.StackTraces);
                rendered = false;
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Clear Host Totals"), false, ClearHostTotals);
            return menu;
        }

        internal void ClearHostTotals()
        {
            UgoTracking.ClearTotals();
            if (!paused)
            {
                Refresh();
                return;
            }
            tree.Totals.Clear();
            tree.Rebuild();
        }

        void DragSplitter(int id, Rect handle, float top, float available)
        {
            EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeVertical);
            Event e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when e.button == 0 && handle.Contains(e.mousePosition):
                    GUIUtility.hotControl = id;
                    e.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == id && available > 0:
                    tableFraction = Mathf.Clamp((e.mousePosition.y - top - SplitterHeight / 2) / available, 0.05f, 0.95f);
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
                case EventType.Repaint:
                    EditorGUI.DrawRect(new Rect(handle.x, handle.center.y, handle.width, 1), SplitterLine);
                    break;
            }
        }

        void DrawDetails(Rect rect)
        {
            if (stackStyle == null) stackStyle = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = false, alignment = TextAnchor.UpperLeft };
            string text = SelectedStackText;
            if (stackSize == Vector2.zero) stackSize = stackStyle.CalcSize(new GUIContent(text));
            var content = new Rect(0, 0, Math.Max(stackSize.x, rect.width - 16), Math.Max(stackSize.y, rect.height - 16));
            stackScroll = GUI.BeginScrollView(rect, stackScroll, content);
            EditorGUI.SelectableLabel(content, text, stackStyle);
            GUI.EndScrollView();
        }
    }

    internal sealed class TrackerTreeView : TreeView<int>
    {
        internal enum Column { Client, Kind, Method, Url, State, Result, Bytes, Started, Elapsed }

        internal const string CopyUrl = "Copy URL", CopyRow = "Copy Row", CopyStack = "Copy Stack Trace";
        const int ColumnCount = (int)Column.Elapsed + 1;

        internal sealed class Item : TreeViewItem<int>
        {
            internal UgoTrackedRow Row;
            internal readonly string Host;
            internal readonly bool IsGroup;
            internal readonly GUIContent[] Cells = new GUIContent[ColumnCount];

            internal Item(int id, string host, bool isGroup) : base(id, 0)
            {
                Host = host;
                IsGroup = isGroup;
                for (int i = 0; i < ColumnCount; i++) Cells[i] = new GUIContent();
            }

            internal bool Greyed => !IsGroup && !Row.IsLive && !Row.IsEvidence;
            internal string Text(Column column) => Cells[(int)column].text;

            internal void Set(Column column, string text)
            {
                GUIContent cell = Cells[(int)column];
                cell.text = text;
                cell.tooltip = text;
            }
        }

        static readonly DateTime ClockOrigin = DateTime.Now;
        static readonly long TimestampOrigin = Stopwatch.GetTimestamp();
        static GUIStyle cellStyle;

        readonly TrackerWindow owner;
        readonly List<Item> leaves = new List<Item>();
        readonly Dictionary<int, Item> byId = new Dictionary<int, Item>();
        readonly Dictionary<(int, ulong), int> selectedKeys = new Dictionary<(int, ulong), int>();
        Dictionary<(int, ulong), Item> items = new Dictionary<(int, ulong), Item>();
        Dictionary<(int, ulong), Item> spare = new Dictionary<(int, ulong), Item>();
        Dictionary<string, Item> groups = new Dictionary<string, Item>();
        internal readonly Dictionary<string, UgoHostTotals> Totals = new Dictionary<string, UgoHostTotals>();
        internal int TotalsVersion = -1;
        int shownTotalsVersion = -1;
        long now;
        bool refreshing;

        internal TrackerTreeView(TreeViewState<int> state, MultiColumnHeader header, TrackerWindow owner) : base(state, header)
        {
            this.owner = owner;
            showAlternatingRowBackgrounds = true;
            rowHeight = 20;
            columnIndexForTreeFoldouts = (int)Column.Url;
        }

        internal static MultiColumnHeaderState.Column[] Columns() => new[]
        {
            Header("Client", 50), Header("Kind", 70), Header("Method", 60), Header("URL", 300, true), Header("State", 160),
            Header("Result", 80), Header("Bytes", 110), Header("Started", 90), Header("Elapsed", 65)
        };

        static MultiColumnHeaderState.Column Header(string name, float width, bool stretch = false) => new MultiColumnHeaderState.Column
        {
            headerContent = new GUIContent(name),
            width = width,
            minWidth = 24,
            autoResize = stretch,
            canSort = true,
            allowToggleVisibility = false
        };

        internal IList<TreeViewItem<int>> VisibleItems => GetRows();
        internal Rect ViewRect { set => treeViewRect = value; }

        bool Ascending
        {
            get
            {
                int column = multiColumnHeader.sortedColumnIndex;
                return column < 0 || multiColumnHeader.IsSortedAscending(column);
            }
        }

        internal bool TryGetSelected(out UgoTrackedRow row)
        {
            IList<int> selection = GetSelection();
            row = default;
            if (selection.Count == 0 || !byId.TryGetValue(selection[0], out Item item) || item.IsGroup) return false;
            row = item.Row;
            return true;
        }

        internal void Select(int client, ulong id)
        {
            if (items.TryGetValue((client, id), out Item item)) SetSelection(new[] { item.id }, TreeViewSelectionOptions.FireSelectionChanged);
        }

        protected override void SelectionChanged(IList<int> selection)
        {
            if (refreshing) return;
            selectedKeys.Clear();
            foreach (int id in selection)
                if (byId.TryGetValue(id, out Item item) && !item.IsGroup) selectedKeys[(item.Row.Client, item.Row.Id)] = id;
        }

        protected override void ExpandedStateChanged()
        {
            foreach (Item group in groups.Values)
            {
                bool expanded = IsExpanded(group.id);
                if (expanded) owner.ExpandedHosts.Add(group.Host);
                else owner.ExpandedHosts.Remove(group.Host);
                ShowLatest(group, !expanded);
            }
        }

        internal bool Refresh(List<UgoTrackedRow> snapshot, long snapshotTime)
        {
            now = snapshotTime;
            bool changed = TotalsVersion != shownTotalsVersion, liveElapsed = false;
            shownTotalsVersion = TotalsVersion;
            foreach (UgoTrackedRow row in snapshot)
            {
                (int, ulong) key = (row.Client, row.Id);
                if (spare.ContainsKey(key)) continue;
                if (items.TryGetValue(key, out Item item) && Same(in item.Row, in row))
                {
                    if (row.IsLive && row.Started != 0)
                    {
                        item.Set(Column.Elapsed, ElapsedText(in row));
                        liveElapsed = true;
                    }
                }
                else
                {
                    if (item == null)
                    {
                        if (!selectedKeys.TryGetValue(key, out int id)) id = owner.NextRowId();
                        item = new Item(id, UgoTracking.Host(row.Url), false);
                    }
                    item.Row = row;
                    Format(item);
                    changed = true;
                }
                spare.Add(key, item);
            }
            changed |= spare.Count != items.Count || !isInitialized;
            (items, spare) = (spare, items);
            spare.Clear();
            if (changed || (liveElapsed && multiColumnHeader.sortedColumnIndex == (int)Column.Elapsed)) Rebuild();
            return changed;
        }

        static bool Same(in UgoTrackedRow a, in UgoTrackedRow b) =>
            a.Ended == b.Ended && a.State == b.State && a.HeldMessages == b.HeldMessages && a.HasResult == b.HasResult
            && a.Received == b.Received && a.Sent == b.Sent;

        internal void Rebuild()
        {
            IList<TreeViewItem<int>> previous = isInitialized ? GetRows() : null;
            var selection = new List<int>(GetSelection());
            bool ascending = Ascending;
            float scrollY = state.scrollPos.y;
            bool pinned = previous != null && (ascending ? scrollY >= totalHeight - treeViewRect.height - rowHeight / 2 : scrollY < rowHeight / 2);
            int top = 0, anchor = 0;
            if (!pinned && previous != null && previous.Count > 0)
            {
                top = Mathf.Clamp((int)(scrollY / rowHeight), 0, previous.Count - 1);
                anchor = previous[top].id;
                for (int i = top; i < previous.Count && i * rowHeight < scrollY + treeViewRect.height; i++)
                    if (selection.Contains(previous[i].id))
                    {
                        anchor = previous[i].id;
                        top = i;
                        break;
                    }
            }
            refreshing = true;
            try
            {
                Reload();
                selection.RemoveAll(id => !byId.TryGetValue(id, out Item item) || !item.IsGroup);
                foreach (KeyValuePair<(int, ulong), int> selected in selectedKeys)
                    if (items.TryGetValue(selected.Key, out Item item)) selection.Add(item.id);
                SetSelection(selection);
                if (pinned)
                    // Past the end on purpose: the scroll view clamps it to the real bottom, horizontal scrollbar included.
                    state.scrollPos.y = ascending ? totalHeight : 0;
                else if (previous != null)
                {
                    IList<TreeViewItem<int>> current = GetRows();
                    for (int i = 0; i < current.Count; i++)
                        if (current[i].id == anchor)
                        {
                            state.scrollPos.y = Math.Max(0, scrollY + (i - top) * rowHeight);
                            break;
                        }
                }
            }
            finally { refreshing = false; }
        }

        protected override TreeViewItem<int> BuildRoot()
        {
            leaves.Clear();
            leaves.AddRange(items.Values);
            Sort(leaves);
            byId.Clear();
            bool group = owner.GroupByHost;
            // A group sits where its row nearest the new-rows edge would be, so a host with fresh activity moves to that edge.
            bool ascending = Ascending;
            var root = new TreeViewItem<int>(0, -1);
            var children = new List<TreeViewItem<int>>(leaves.Count);
            var nextGroups = new Dictionary<string, Item>();
            Item AddHost(string name)
            {
                if (!groups.TryGetValue(name, out Item host)) host = new Item(owner.NextRowId(), name, true);
                host.children = new List<TreeViewItem<int>>();
                host.Row = default;
                host.parent = root;
                nextGroups.Add(name, host);
                byId[host.id] = host;
                children.Add(host);
                return host;
            }
            for (int n = 0; n < leaves.Count; n++)
            {
                Item leaf = leaves[ascending ? leaves.Count - 1 - n : n];
                leaf.children = null;
                leaf.parent = root;
                byId[leaf.id] = leaf;
                if (!group || leaf.Host == null)
                {
                    children.Add(leaf);
                    continue;
                }
                if (!nextGroups.TryGetValue(leaf.Host, out Item host)) host = AddHost(leaf.Host);
                host.AddChild(leaf);
            }
            if (group && owner.KeepHosts)
                foreach (string name in Totals.Keys)
                    if (!nextGroups.ContainsKey(name)) AddHost(name);
            if (ascending) children.Reverse();
            var expanded = new List<int>();
            foreach (Item host in nextGroups.Values)
            {
                if (ascending) host.children.Reverse();
                int live = 0;
                ulong received = 0, sent = 0;
                foreach (TreeViewItem<int> child in host.children)
                {
                    ref UgoTrackedRow row = ref ((Item)child).Row;
                    if (!row.IsLive) continue;
                    live++;
                    received += row.Received;
                    sent += row.Sent;
                }
                Totals.TryGetValue(host.Host, out UgoHostTotals sum);
                host.Set(Column.Bytes, BytesText(sum.Received + received, sum.Sent + sent));
                host.Set(Column.Url, host.Host);
                host.Set(Column.State, TotalsText(live, in sum));
                bool open = owner.ExpandedHosts.Contains(host.Host);
                if (open) expanded.Add(host.id);
                ShowLatest(host, !open);
            }
            groups = nextGroups;
            expanded.Sort();
            state.expandedIDs = expanded;
            root.children = children;
            SetupDepthsFromParentsAndChildren(root);
            return root;
        }

        static string TotalsText(int live, in UgoHostTotals sum)
        {
            var text = new StringBuilder().Append(live).Append(" live · ").Append(sum.Done).Append(" done");
            if (sum.Failed > 0) text.Append(" · ").Append(sum.Failed).Append(" failed");
            if (sum.Aborted > 0) text.Append(" · ").Append(sum.Aborted).Append(" aborted");
            return text.Append(" · ").Append(sum.Connections).Append(" conn").ToString();
        }

        static void ShowLatest(Item host, bool collapsed)
        {
            Item latest = null;
            if (collapsed)
                foreach (TreeViewItem<int> child in host.children)
                    if (latest == null || ((Item)child).Row.Started > latest.Row.Started) latest = (Item)child;
            host.Set(Column.Method, latest?.Text(Column.Method) ?? "");
            host.Set(Column.Result, latest?.Text(Column.Result) ?? "");
        }

        protected override bool DoesItemMatchSearch(TreeViewItem<int> item, string search)
        {
            var row = (Item)item;
            return !row.IsGroup && (Contains(row.Text(Column.Url), search) || Contains(row.Text(Column.Kind), search) || Contains(row.Text(Column.State), search));
        }

        static bool Contains(string value, string search) => value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        void Sort(List<Item> rows)
        {
            int column = multiColumnHeader.sortedColumnIndex;
            int direction = Ascending ? 1 : -1;
            rows.Sort((a, b) =>
            {
                int order;
                switch ((Column)column)
                {
                    case Column.Client: order = a.Row.Client.CompareTo(b.Row.Client); break;
                    case Column.Started: order = a.Row.Started.CompareTo(b.Row.Started); break;
                    case Column.Bytes: order = (a.Row.Received + a.Row.Sent).CompareTo(b.Row.Received + b.Row.Sent); break;
                    case Column.Elapsed: order = Elapsed(in a.Row).CompareTo(Elapsed(in b.Row)); break;
                    default: order = column < 0 ? 0 : string.CompareOrdinal(a.Cells[column].text, b.Cells[column].text); break;
                }
                if (order != 0) return order * direction;
                order = a.Row.Started.CompareTo(b.Row.Started);
                if (order != 0) return order;
                order = a.Row.Client.CompareTo(b.Row.Client);
                return order != 0 ? order : a.Row.Id.CompareTo(b.Row.Id);
            });
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            if (cellStyle == null) cellStyle = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Ellipsis };
            var item = (Item)args.item;
            Color color = GUI.color;
            if (item.Greyed) GUI.color = new Color(color.r, color.g, color.b, color.a * 0.5f);
            for (int i = 0; i < args.GetNumVisibleColumns(); i++)
            {
                Rect cell = args.GetCellRect(i);
                if (i == columnIndexForTreeFoldouts) cell.xMin += GetContentIndent(item);
                CenterRectUsingSingleLineHeight(ref cell);
                GUI.Label(cell, item.Cells[args.GetColumn(i)], cellStyle);
            }
            GUI.color = color;
        }

        protected override void ContextClickedItem(int id)
        {
            var menu = new GenericMenu();
            foreach ((string label, string text) in CopyCommands()) menu.AddItem(new GUIContent(label), false, () => EditorGUIUtility.systemCopyBuffer = text);
            if (menu.GetItemCount() > 0) menu.ShowAsContext();
            Event.current?.Use();
        }

        protected override void CommandEventHandling()
        {
            Event e = Event.current;
            if ((e.type == EventType.ValidateCommand || e.type == EventType.ExecuteCommand) && e.commandName == "Copy" && HasFocus() && HasSelection())
            {
                if (e.type == EventType.ExecuteCommand) EditorGUIUtility.systemCopyBuffer = CopyText(CopyUrl);
                e.Use();
                return;
            }
            base.CommandEventHandling();
        }

        internal List<(string Label, string Text)> CopyCommands()
        {
            var commands = new List<(string, string)>();
            if (SelectedRows().Count == 0) return commands;
            commands.Add((CopyUrl, CopyText(CopyUrl)));
            commands.Add((CopyRow, CopyText(CopyRow)));
            string stacks = CopyText(CopyStack);
            if (stacks.Length > 0) commands.Add((CopyStack, stacks));
            return commands;
        }

        internal string CopyText(string command)
        {
            var text = new StringBuilder();
            foreach (Item item in SelectedRows())
            {
                if (command == CopyUrl && !string.IsNullOrEmpty(item.Row.Url)) text.Append(item.Row.Url).Append('\n');
                else if (command == CopyStack && item.Row.Stack != null) text.Append(TrackerWindow.FormatStack(item.Row.Stack, false)).Append('\n');
                else if (command == CopyRow)
                {
                    for (int i = 0; i < ColumnCount; i++) text.Append(i == 0 ? "" : "\t").Append(item.Cells[i].text);
                    text.Append('\n');
                }
            }
            if (text.Length > 0) text.Length--;
            return text.ToString();
        }

        List<Item> SelectedRows()
        {
            IList<int> selection = GetSelection();
            var rows = new List<Item>();
            foreach (Item leaf in leaves)
                if (selection.Contains(leaf.id) || (leaf.parent is Item group && group.IsGroup && selection.Contains(group.id))) rows.Add(leaf);
            return rows;
        }

        long Elapsed(in UgoTrackedRow row) => row.Started == 0 ? -1 : (row.IsLive ? now : row.Ended) - row.Started;

        string ElapsedText(in UgoTrackedRow row)
        {
            long elapsed = Elapsed(in row);
            return elapsed < 0 ? "" : ((double)elapsed / Stopwatch.Frequency).ToString("0.0") + " s";
        }

        void Format(Item item)
        {
            ref UgoTrackedRow row = ref item.Row;
            item.Set(Column.Client, row.Client.ToString());
            item.Set(Column.Kind, row.Kind);
            item.Set(Column.Method, row.Method ?? "");
            item.Set(Column.Url, row.Url ?? "");
            item.Set(Column.State, row.HeldMessages == 0 ? row.State : row.State + ", " + row.HeldMessages + " messages held");
            item.Set(Column.Result, Result(in row));
            item.Set(Column.Bytes, BytesText(row.Received, row.Sent));
            item.Set(Column.Started, row.Started == 0 ? "" : ClockOrigin.AddSeconds((double)(row.Started - TimestampOrigin) / Stopwatch.Frequency).ToString("HH:mm:ss.fff"));
            item.Set(Column.Elapsed, ElapsedText(in row));
        }

        static string BytesText(ulong received, ulong sent) =>
            sent == 0 ? (received == 0 ? "" : Size(received)) : "↑" + Size(sent) + " ↓" + Size(received);

        static string Size(ulong bytes) =>
            bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.0") + " KiB" : (bytes / (1024.0 * 1024)).ToString("0.0") + " MiB";

        static string Result(in UgoTrackedRow row)
        {
            if (!row.HasResult) return "";
            if (row.Error == UgoErrorKind.None || row.Error == UgoErrorKind.HttpStatus)
            {
                if (row.Kind != "WebSocket") return row.HttpStatus.ToString();
                if (row.WebSocketCloseCode != 0) return row.WebSocketCloseCode.ToString();
            }
            return row.Reason == UgoErrorReason.None ? row.Error.ToString() : row.Error + " (" + row.Reason + ")";
        }
    }
}
