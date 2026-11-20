using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UgoHttp.Native;

namespace UgoHttp
{
    // Only the Editor assembly turns tracking on, so in a player every tracker branch sees a null trace and no row is ever kept.
    internal static class UgoTracking
    {
        internal const int Capacity = 512;
        internal const string Released = "Closed";
        internal const string Aborted = "Aborted";
        internal const string Failed = "Failed";
        internal const string Disposed = "Disposed";
        internal const string ClosedWithClient = "Closed with client";
        internal const string Reclaimed = "Reclaimed at Play Mode exit";
        internal static readonly long Retention = 10 * Stopwatch.Frequency;

        internal static volatile bool Enabled;
        internal static volatile bool StackTraces;

        static readonly object Sync = new object();
        static UgoTrackedRow[] completed;
        static int next, count;
        static Dictionary<string, UgoHostTotals> totals;
        static int totalsVersion;

        internal static void Complete(in UgoTrackedRow row)
        {
            string host = row.Id == 0 ? null : Host(row.Url);
            lock (Sync)
            {
                if (completed == null) completed = new UgoTrackedRow[Capacity];
                completed[next] = row;
                next = (next + 1) % Capacity;
                if (count < Capacity) count++;
                if (host == null) return;
                if (totals == null) totals = new Dictionary<string, UgoHostTotals>();
                totals.TryGetValue(host, out UgoHostTotals sum);
                if (!row.HasResult || row.Error == UgoErrorKind.Aborted) sum.Aborted++;
                else if (row.Error == UgoErrorKind.None || row.Error == UgoErrorKind.HttpStatus) sum.Done++;
                else sum.Failed++;
                if (row.NewConnection) sum.Connections++;
                sum.Received += row.Received;
                sum.Sent += row.Sent;
                totals[host] = sum;
                totalsVersion++;
            }
        }

        internal static string Host(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : null;

        internal static void Totals(Dictionary<string, UgoHostTotals> into, ref int version)
        {
            lock (Sync) CopyTotals(into, ref version);
        }

        static void CopyTotals(Dictionary<string, UgoHostTotals> into, ref int version)
        {
            if (version == totalsVersion) return;
            version = totalsVersion;
            into.Clear();
            if (totals != null)
                foreach (KeyValuePair<string, UgoHostTotals> pair in totals) into.Add(pair.Key, pair.Value);
        }

        internal static void ClearTotals()
        {
            lock (Sync)
            {
                totals?.Clear();
                totalsVersion++;
            }
        }

        internal static string ClosedState(in ugo_ev_completed result) =>
            result.error_class == UgoAbi.UGO_CLASS_NONE || result.error_class == (uint)UgoErrorKind.HttpStatus ? Released
            : result.error_class == (uint)UgoErrorKind.Aborted ? Aborted : Failed;

        internal static List<UgoTrackedRow> Snapshot() => Snapshot(Stopwatch.GetTimestamp());

        internal static List<UgoTrackedRow> Snapshot(long now)
        {
            int version = 0;
            return Snapshot(now, null, ref version);
        }

        // Completed rows and totals are copied before the live walk: a release in between can hide a row for one snapshot but never count it twice.
        internal static List<UgoTrackedRow> Snapshot(long now, Dictionary<string, UgoHostTotals> totalsInto, ref int version)
        {
            var history = new List<UgoTrackedRow>();
            lock (Sync)
            {
                for (int i = 1; i <= count; i++)
                {
                    ref UgoTrackedRow row = ref completed[(next - i + Capacity) % Capacity];
                    if (row.IsEvidence || now - row.Ended < Retention) history.Add(row);
                }
                if (totalsInto != null) CopyTotals(totalsInto, ref version);
            }
            var clients = new List<UgoClient>();
            lock (UgoClient.DomainSync)
                foreach (WeakReference<UgoClient> live in UgoClient.Live)
                    if (live.TryGetTarget(out UgoClient client)) clients.Add(client);
            var rows = new List<UgoTrackedRow>();
            foreach (UgoClient client in clients) client.Track(rows);
            rows.AddRange(history);
            return rows;
        }
    }

    internal struct UgoHostTotals
    {
        internal long Done, Failed, Aborted, Connections;
        internal ulong Received, Sent;
    }

    internal sealed class UgoTrace
    {
        internal string Method;
        internal string Url;
        internal long Started;
        internal StackTrace Stack;
        internal ulong Received, Sent;
        internal long ResumeOffset;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static UgoTrace Capture(string method, string url, int skipFrames) => new UgoTrace
        {
            Method = method,
            Url = url,
            Started = Stopwatch.GetTimestamp(),
            Stack = UgoTracking.StackTraces ? new StackTrace(skipFrames + 1, true) : null
        };
    }

    internal readonly struct UgoTrackedRow
    {
        internal readonly int Client;
        internal readonly ulong Id;
        internal readonly string Kind;
        internal readonly string State;
        internal readonly long HeldMessages;
        internal readonly string Method;
        internal readonly string Url;
        // Stopwatch timestamps; Started is 0 without a trace, Ended is 0 while the object is live.
        internal readonly long Started;
        internal readonly long Ended;
        internal readonly bool HasResult;
        internal readonly UgoErrorKind Error;
        internal readonly UgoErrorReason Reason;
        internal readonly uint HttpStatus;
        internal readonly uint WebSocketCloseCode;
        internal readonly ulong Received, Sent;
        internal readonly bool NewConnection;
        internal readonly StackTrace Stack;

        internal UgoTrackedRow(int client, ulong id, string kind, string state, long heldMessages, UgoTrace trace, long ended,
            bool hasResult, in ugo_ev_completed result)
        {
            Client = client;
            Id = id;
            Kind = kind;
            State = state;
            HeldMessages = heldMessages;
            Method = trace?.Method;
            Url = trace?.Url;
            Started = trace?.Started ?? 0;
            Ended = ended;
            HasResult = hasResult;
            Error = hasResult ? (UgoErrorKind)result.error_class : UgoErrorKind.None;
            Reason = hasResult ? (UgoErrorReason)result.detail : UgoErrorReason.None;
            HttpStatus = hasResult ? result.http_status : 0;
            WebSocketCloseCode = hasResult ? result.ws_close_code : 0;
            Received = hasResult ? result.bytes_received : trace?.Received ?? 0;
            Sent = hasResult ? result.bytes_sent : trace?.Sent ?? 0;
            NewConnection = hasResult && result.connection_reused == 0 && result.connect_us > 0;
            Stack = trace?.Stack;
        }

        internal bool IsLive => Ended == 0;
        internal bool IsEvidence => State == UgoTracking.Reclaimed;
    }
}
