using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UgoHttp;
using UnityEngine;

// Long WebSocket run against the bench's /ws/soak: one socket per API form over wss. Each side numbers its messages and fills them
// with a pattern derived from the number (tests/bench/server.py soak_text), so every received byte is checked without a copy.
static class Soak
{
    const int SendIntervalMs = 200;
    const int ProgressIntervalS = 60;
    const int BigSend = 100_000;
    const string CloseReason = "soak done";
    const int Head = 21;
    static readonly byte[] Pattern = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz0123456789");

    public static Socket[] Sockets = Array.Empty<Socket>();
    public static long StartedMicroseconds, DurationSeconds;
    public static long LastResidentBytes;

    public sealed class Socket : UgoWebSocketListener
    {
        public readonly string Form;
        public UgoWebSocket WebSocket;
        public long Sent, SentBytes, BufferFull, FullStreak, MaxFullStreak;
        public long Pushes, Echoes, ReceivedBytes, Big, Mismatches;
        public volatile bool Opened, Closing;
        public volatile string Problem, Result;
        readonly byte[] buffer = new byte[BigSend];

        public Socket(string form) { Form = form; }

        public override void OnOpen(UgoWebSocket socket, in UgoHeadersEvent handshake) => Opened = true;
        public override void OnMessage(UgoWebSocket socket, in UgoWebSocketMessage message) => Verify(message.Data);
        public override void OnClosed(UgoWebSocket socket, in UgoWebSocketResult result) => Finish(in result);

        public void Finish(in UgoWebSocketResult result)
        {
            string closed = PlayerChecks.Describe(in result);
            if (!Closing && Problem == null) Problem = "closed while running: " + closed;
            Result = closed;
        }

        public void Verify(ReadOnlySpan<byte> m)
        {
            ReceivedBytes += m.Length;
            if (m.Length >= 65536) Big++;
            if (m.Length < Head || m[11] != ' ' || m[Head - 1] != ' ' || (m[0] != 'P' && m[0] != 'C'))
            {
                Mismatch("malformed message of " + m.Length + " bytes");
                return;
            }
            long seq = Digits(m.Slice(1, 10)), size = Digits(m.Slice(12, 8));
            bool push = m[0] == 'P';
            long expected = push ? Pushes : Echoes;
            if (seq != expected || size != m.Length)
            {
                Mismatch((push ? "push " : "echo ") + seq + " of " + m.Length + " bytes, expected " + expected);
                return;
            }
            for (int i = Head; i < m.Length; i++)
                if (m[i] != Pattern[(seq + i) % Pattern.Length])
                {
                    Mismatch((push ? "push " : "echo ") + seq + " differs at byte " + i);
                    return;
                }
            if (push) Pushes++;
            else Echoes++;
        }

        void Mismatch(string what)
        {
            if (Interlocked.Increment(ref Mismatches) == 1) Problem = what;
        }

        static long Digits(ReadOnlySpan<byte> digits)
        {
            long value = 0;
            foreach (byte d in digits)
            {
                if (d < '0' || d > '9') return -1;
                value = value * 10 + (d - '0');
            }
            return value;
        }

        // Called only from the soak thread; a full buffer keeps the same message for the next tick.
        public void SendNext()
        {
            long seq = Sent;
            int size = seq % 300 == 299 ? BigSend : 32 + (int)(seq * 61 % 2000);
            Write(seq, 1, 10);
            Write(size, 12, 8);
            buffer[0] = (byte)'C';
            buffer[11] = buffer[Head - 1] = (byte)' ';
            for (int i = Head; i < size; i++) buffer[i] = Pattern[(seq + i) % Pattern.Length];
            UgoWebSocketSendStatus status = WebSocket.Send(new ReadOnlySpan<byte>(buffer, 0, size), UgoWebSocketMessageType.Text);
            if (status == UgoWebSocketSendStatus.Accepted)
            {
                SentBytes += size;
                Volatile.Write(ref Sent, seq + 1);
                FullStreak = 0;
            }
            else if (status == UgoWebSocketSendStatus.BufferFull)
            {
                BufferFull++;
                MaxFullStreak = Math.Max(MaxFullStreak, ++FullStreak);
            }
            else if (Problem == null) Problem = "send: " + status;
        }

        void Write(long value, int offset, int width)
        {
            for (int i = offset + width - 1; i >= offset; i--, value /= 10) buffer[i] = (byte)('0' + value % 10);
        }

        public void Json(StringBuilder json)
        {
            json.Append("{\"form\":\"").Append(Form)
                .Append("\",\"sent\":").Append(Volatile.Read(ref Sent)).Append(",\"sent_bytes\":").Append(SentBytes)
                .Append(",\"pushes\":").Append(Volatile.Read(ref Pushes)).Append(",\"echoes\":").Append(Volatile.Read(ref Echoes))
                .Append(",\"received_bytes\":").Append(Volatile.Read(ref ReceivedBytes)).Append(",\"big_received\":").Append(Volatile.Read(ref Big))
                .Append(",\"mismatches\":").Append(Volatile.Read(ref Mismatches))
                .Append(",\"buffer_full\":").Append(BufferFull).Append(",\"max_buffer_full_streak\":").Append(MaxFullStreak).Append('}');
        }
    }

    public static string Run(string httpsUrl, byte[] ca, long seconds)
    {
        DurationSeconds = seconds;
        var problems = new StringBuilder();
        long lastHeap = 0;
        using (var client = new UgoClient(new UgoClientOptions(4) { CaBundlePem = ca }))
        {
            var listener = new Socket("listener");
            var awaitable = new Socket("awaitable");
            Sockets = new[] { listener, awaitable };
            UgoWebSocketRequest request = Request(httpsUrl);
            listener.WebSocket = client.OpenWebSocket(request, listener);
            awaitable.WebSocket = client.OpenWebSocket(request);
            try
            {
                Task<bool> open = awaitable.WebSocket.WaitOpenAsync().AsTask();
                long openBy = UgoClient.MonotonicMicroseconds + 20_000_000;
                while (!(listener.Opened || listener.Result != null) && UgoClient.MonotonicMicroseconds < openBy) Thread.Sleep(10);
                if (!listener.Opened) throw new Exception("listener socket not open: " + listener.Result);
                if (!open.Wait(20000) || !open.Result) throw new Exception("awaitable socket not open");
                awaitable.Opened = true;
                Task receiving = Receive(awaitable);

                StartedMicroseconds = UgoClient.MonotonicMicroseconds;
                long end = StartedMicroseconds + seconds * 1_000_000;
                long nextProgress = StartedMicroseconds;
                int minute = 0;
                while (true)
                {
                    long now = UgoClient.MonotonicMicroseconds;
                    if (now >= nextProgress)
                    {
                        lastHeap = Progress(minute++, now);
                        nextProgress += ProgressIntervalS * 1_000_000L;
                    }
                    if (now >= end || listener.Problem != null || awaitable.Problem != null) break;
                    listener.SendNext();
                    awaitable.SendNext();
                    Thread.Sleep(SendIntervalMs);
                }
                lastHeap = Progress(minute, UgoClient.MonotonicMicroseconds);
                foreach (Socket socket in Sockets)
                {
                    socket.Closing = true;
                    if (!socket.WebSocket.Close(1000, CloseReason) && socket.Problem == null) socket.Problem = "close refused";
                }
                long closeBy = UgoClient.MonotonicMicroseconds + 20_000_000;
                while ((listener.Result == null || awaitable.Result == null) && UgoClient.MonotonicMicroseconds < closeBy) Thread.Sleep(10);
                if (!receiving.Wait(5000)) problems.Append("awaitable receive loop did not end; ");
                foreach (Socket socket in Sockets)
                {
                    string expected = "None WebSocketClosedByLocal 1000 " + CloseReason;
                    if (socket.Problem == null && socket.Result != expected) socket.Problem = "closed " + (socket.Result ?? "never");
                    if (socket.Problem == null && Volatile.Read(ref socket.Echoes) != Volatile.Read(ref socket.Sent))
                        socket.Problem = "echoes " + socket.Echoes + " of " + socket.Sent + " sent";
                    if (socket.Problem == null && socket.Pushes == 0) socket.Problem = "no pushes received";
                    if (socket.Problem == null && seconds >= 120 && socket.Big == 0) socket.Problem = "no big message received";
                    if (socket.Problem != null) problems.Append(socket.Form).Append(": ").Append(socket.Problem).Append("; ");
                }
            }
            finally
            {
                listener.WebSocket.Dispose();
                awaitable.WebSocket.Dispose();
            }
        }
        var json = new StringBuilder("\"soak\":{\"seconds\":").Append(seconds).Append(",\"sockets\":[");
        for (int i = 0; i < Sockets.Length; i++) Sockets[i].Json(i == 0 ? json : json.Append(','));
        json.Append("],\"heap_bytes\":").Append(lastHeap).Append(",\"resident_bytes\":").Append(LastResidentBytes).Append('}');
        if (problems.Length != 0) throw new SoakFailed(problems.ToString(), json.ToString());
        return json.ToString();
    }

    public sealed class SoakFailed : Exception
    {
        public readonly string Json;
        public SoakFailed(string message, string json) : base(message) { Json = json; }
    }

    static UgoWebSocketRequest Request(string httpsUrl) =>
        new UgoWebSocketRequest("wss" + httpsUrl.Substring(5) + "/ws/soak")
        {
            Headers = new[] { new UgoHeader("Authorization", "Bearer bench-token") },
            MaxMessageBytes = 256 << 10
        };

    static async Task Receive(Socket socket)
    {
        try
        {
            while (true)
            {
                using (UgoWebSocketMessage message = await socket.WebSocket.ReceiveAsync())
                {
                    if (message.IsEnd) break;
                    socket.Verify(message.Data);
                }
            }
            if (!socket.WebSocket.TryGetResult(out UgoWebSocketResult result)) throw new Exception("end without a result");
            socket.Finish(in result);
        }
        catch (Exception error)
        {
            if (socket.Problem == null) socket.Problem = "receive: " + error.Message;
            socket.Result = socket.Result ?? "receive failed";
        }
    }

    static long Progress(int minute, long now)
    {
        long heap = GC.GetTotalMemory(true);
        (long resident, long footprint) = Memory();
        LastResidentBytes = resident;
        var json = new StringBuilder("UGO-SOAK {\"minute\":").Append(minute)
            .Append(",\"elapsed_s\":").Append((now - StartedMicroseconds) / 1_000_000)
            .Append(",\"resident_bytes\":").Append(resident).Append(",\"footprint_bytes\":").Append(footprint)
            .Append(",\"heap_bytes\":").Append(heap).Append(",\"gc_count\":").Append(GC.CollectionCount(0))
            .Append(",\"sockets\":[");
        for (int i = 0; i < Sockets.Length; i++) Sockets[i].Json(i == 0 ? json : json.Append(','));
        Debug.Log(json.Append("]}").ToString());
        return heap;
    }

#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] static extern uint task_self_trap();
    static readonly uint SelfTask = task_self_trap();
    [DllImport("__Internal")] static extern unsafe int task_info(uint task, int flavor, ulong* info, ref uint count);

    // TASK_VM_INFO: resident_size at word 2, phys_footprint (what jetsam counts) at word 18.
    static unsafe (long, long) Memory()
    {
        ulong* info = stackalloc ulong[64];
        uint count = 128;
        if (task_info(SelfTask, 22, info, ref count) != 0 || count < 38) return (-1, -1);
        return ((long)info[2], (long)info[18]);
    }
#else
    static (long, long) Memory()
    {
        try
        {
            string[] statm = System.IO.File.ReadAllText("/proc/self/statm").Split(' ');
            return (long.Parse(statm[1]) * 4096, -1);
        }
        catch (Exception) { return (-1, -1); }
    }
#endif
}
