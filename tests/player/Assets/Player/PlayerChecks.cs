using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UgoHttp;
using UgoHttp.Native;
using UnityEngine;

static class PlayerChecks
{
    [Serializable] class Config { public string http; public string https; public string self_signed; public string ca_pem; public bool open_settings; public long soak; }

    static readonly byte[] Ok = Encoding.ASCII.GetBytes("UgoHTTP fault bench\n");
    const string DetachedText = "UgoHTTP detached POST: \u03c0 \u2603\n";
    static Config config;
    static string cache;
    static int mainThread;
    static SynchronizationContext main;
    static readonly StringBuilder json = new StringBuilder();
    static bool allPassed = true;
    // Read by PlayerStatus on the main thread; the checks only store references and counts they already have.
    public static volatile string Mode = "no config", Current = "";
    public static int Passed, Failed;
    public static volatile int Verdict;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Start()
    {
        mainThread = Thread.CurrentThread.ManagedThreadId;
        main = SynchronizationContext.Current;
        cache = Application.temporaryCachePath;
#if UNITY_ANDROID
        string encoded;
        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            encoded = intent.Call<string>("getStringExtra", "ugo");
#else
        // tests/ios/run passes the config as SIMCTL_CHILD_UGO on a simulator, as a launch environment variable on a device.
        string encoded = Environment.GetEnvironmentVariable("UGO");
#endif
        PlayerStatus.Show();
        if (string.IsNullOrEmpty(encoded)) return;
        config = JsonUtility.FromJson<Config>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Mode = config.soak > 0 ? "soak" : "checks";
        if (config.soak > 0) new Thread(RunSoak) { IsBackground = true, Name = "ugo-player-soak" }.Start();
        else new Thread(Run) { IsBackground = true, Name = "ugo-player-checks" }.Start();
    }

    static void Run()
    {
        try
        {
            foreach (string line in UgoAbi.Dump()) Debug.Log("UGO-LAYOUT " + line);
            Debug.Log("UGO-LAYOUT-END");
            byte[] ca = Encoding.ASCII.GetBytes(config.ca_pem);
            var options = new UgoClientOptions(8) { CaBundlePem = ca, DrainMode = UgoDrainMode.Manual };
            using (var client = new UgoClient(in options))
            {
                Check("off_main_thread", () => Require(Thread.CurrentThread.ManagedThreadId != mainThread, "checks run on the main thread"));
                Check("get_memory", () => GetMemory(client, config.http + "/ok"));
                Check("get_file", () => GetFile(client));
                Check("resume_file", () => ResumeFile(client));
                Check("post_memory", () => PostMemory(client));
                Check("detached_post", () => DetachedPost(client));
                Check("https_ca_bundle", () => GetMemory(client, config.https + "/ok"));
                Check("gzip_h2", () => Gzip(client));
                Check("untrusted_tls", () => Untrusted(client));
                Check("abort", () => Abort(client));
                Check("websocket_listener", () => WebSocketListener(client));
            }
            Check("shutdown_in_flight", () => ShutdownInFlight(options));
            Check("background_await", () => BackgroundAwait(ca));
            Check("websocket_awaitable_wss", () => WebSocketAwaitable(ca));
#if UNITY_IOS
            BackgroundTask(options);
#endif
        }
        catch (Exception error)
        {
            allPassed = false;
            Field("fatal", error.ToString());
        }
        Verdict = allPassed ? 1 : 2;
        Debug.Log("UGO-RESULT {\"all_passed\":" + (allPassed ? "true" : "false") + ",\"checks\":{" + json + "}}");
    }

    static void RunSoak()
    {
        string soak;
        try { soak = Soak.Run(config.https, Encoding.ASCII.GetBytes(config.ca_pem), config.soak); }
        catch (Soak.SoakFailed error)
        {
            allPassed = false;
            Field("problems", error.Message);
            soak = error.Json;
        }
        catch (Exception error)
        {
            allPassed = false;
            Field("problems", error.ToString());
            soak = "\"soak\":null";
        }
        Verdict = allPassed ? 1 : 2;
        Debug.Log("UGO-RESULT {\"all_passed\":" + (allPassed ? "true" : "false") + "," + soak + (json.Length != 0 ? "," + json : "") + "}");
    }

    static void Check(string name, Action check)
    {
        Current = name;
        try { check(); Field(name, "pass"); Interlocked.Increment(ref Passed); }
        catch (Exception error) { allPassed = false; Field(name, "fail: " + error.Message); Interlocked.Increment(ref Failed); }
    }

    static void Field(string name, string value)
    {
        if (json.Length != 0) json.Append(',');
        json.Append('"').Append(name).Append("\":\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') json.Append('\\').Append(c);
            else if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4"));
            else json.Append(c);
        }
        json.Append('"');
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void WaitHeaders(UgoClient client, UgoId id)
    {
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        long end = UgoClient.MonotonicMicroseconds + 20_000_000;
        while (UgoClient.MonotonicMicroseconds < end)
        {
            Require(client.TryWaitEvents(events, 100, out int count), "client stopped");
            for (int i = 0; i < count; i++)
            {
                if (events[i].Id != id) continue;
                if (events[i].Kind == UgoEventKind.Headers) return;
                if (events[i].Kind == UgoEventKind.Completed) throw new Exception("completed before headers");
            }
        }
        throw new Exception("no headers within 20 s");
    }

    static UgoResponse Wait(UgoClient client, UgoId id)
    {
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        long end = UgoClient.MonotonicMicroseconds + 20_000_000;
        while (UgoClient.MonotonicMicroseconds < end)
        {
            Require(client.TryWaitEvents(events, 100, out int count), "client stopped");
            for (int i = 0; i < count; i++)
                if (events[i].Id == id && events[i].Kind == UgoEventKind.Completed)
                {
                    Require(client.TryGetResponse(id, out UgoResponse response), "no response");
                    return response;
                }
        }
        throw new Exception("no completed within 20 s");
    }

    static void Completed(in UgoResponse response, UgoErrorKind kind, UgoErrorReason reason = UgoErrorReason.None)
    {
        UgoError error = response.Error;
        Require(error.Kind == kind && error.Reason == reason,
            $"error {error.Kind} reason {error.Reason} curl {error.NativeCode}, expected {kind} reason {reason}");
    }

    static UgoResponse Send(UgoClient client, in UgoRequest request)
    {
        Require(client.Submit(in request, out UgoId id) == UgoSubmitStatus.Accepted, "submit");
        return Wait(client, id);
    }

    static void GetMemory(UgoClient client, string url)
    {
        using (UgoResponse response = Send(client, new UgoRequest(url)))
        {
            Completed(response, UgoErrorKind.None);
            Require(response.StatusCode == 200, "status " + response.StatusCode);
            Require(response.Body.SequenceEqual(Ok), "body mismatch");
        }
    }

    static void Gzip(UgoClient client)
    {
        using (UgoResponse response = Send(client, new UgoRequest(config.https + "/encoded/gzip?bytes=200000")))
        {
            Completed(response, UgoErrorKind.None);
            Require(response.HttpVersion == UgoHttpVersion.Http2, "version " + response.HttpVersion);
            Require(response.GetHeader("content-encoding") == "gzip", "no content-encoding");
            ReadOnlySpan<byte> body = response.Body;
            Require(body.Length == 200000, "decoded length " + body.Length);
            for (int i = 0; i < body.Length; i++)
                if (body[i] != (byte)i) throw new Exception("decoded byte " + i + " differs");
        }
    }

    static void GetFile(UgoClient client)
    {
        const int size = 1 << 20;
        string path = Path.Combine(cache, "get-file.part");
        File.Delete(path);
        var request = new UgoRequest(config.http + "/big/" + size) { ResponseFilePath = path };
        using (UgoResponse response = Send(client, in request)) Completed(response, UgoErrorKind.None);
        byte[] bytes = File.ReadAllBytes(path);
        File.Delete(path);
        Require(bytes.Length == size, "file length " + bytes.Length);
        for (int i = 0; i < size; i++)
            if (bytes[i] != (byte)i) throw new Exception("file byte " + i + " differs");
    }

    static void ResumeFile(UgoClient client)
    {
        const int size = 1 << 20, cut = 300000;
        string path = Path.Combine(cache, "resume.part");
        File.Delete(path);
        var request = new UgoRequest(config.http + "/range/" + size + "?cut=" + cut) { ResponseFilePath = path, Resume = true };
        using (UgoResponse response = Send(client, in request)) Completed(response, UgoErrorKind.Transfer);
        Require(new FileInfo(path).Length == cut, "kept " + new FileInfo(path).Length);
        request.Url = config.http + "/range/" + size;
        using (UgoResponse response = Send(client, in request))
        {
            Completed(response, UgoErrorKind.None);
            Require(response.StatusCode == 206 && response.BytesReceived == size - cut, "status " + response.StatusCode + " received " + response.BytesReceived);
        }
        byte[] bytes = File.ReadAllBytes(path);
        File.Delete(path);
        Require(bytes.Length == size, "file length " + bytes.Length);
        for (int i = 0; i < size; i++)
            if (bytes[i] != (byte)i) throw new Exception("file byte " + i + " differs");
    }

    static void PostMemory(UgoClient client)
    {
        var body = new byte[65536];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(i * 7 + 3);
        var request = new UgoRequest(config.http + "/ok?post=memory") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(body) };
        using (UgoResponse response = Send(client, in request))
        {
            Completed(response, UgoErrorKind.None);
            Require(response.BytesSent == body.Length, "bytes_sent " + response.BytesSent);
        }
        using (var sha = SHA256.Create())
            Field("post_memory_sha256", BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "").ToLowerInvariant());
    }

    static void Untrusted(UgoClient client)
    {
        using (UgoResponse response = Send(client, new UgoRequest(config.self_signed + "/ok"))) Completed(response, UgoErrorKind.Tls);
    }

    static void DetachedPost(UgoClient client, bool background = false)
    {
        byte[] body = Encoding.UTF8.GetBytes(background ? new string('x', 65536) + DetachedText : DetachedText);
        var request = new UgoRequest(config.http + (background ? "/slow-body/2000?bps=200&post=detached-background" : "/ok?post=detached"))
        {
            Method = HttpMethod.Post,
            Body = UgoRequestBody.FromBytes(body),
            Headers = new[] { new UgoHeader("X-Ugo-Detached", background ? "background" : "foreground") },
            FinishInBackground = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            TotalTimeout = TimeSpan.FromSeconds(25),
            MaxSendBytesPerSecond = background ? 8192 : 0
        };
        UgoSubmitStatus status = client.EnqueueDetached(in request);
        Require(status == UgoSubmitStatus.Accepted, "detached submit " + status);
    }

    static void Abort(UgoClient client)
    {
        var request = new UgoRequest(config.http + "/stall-after-headers");
        Require(client.Submit(in request, out UgoId id) == UgoSubmitStatus.Accepted, "submit");
        WaitHeaders(client, id);
        client.Abort(id);
        using (UgoResponse response = Wait(client, id)) Completed(response, UgoErrorKind.Aborted, UgoErrorReason.Caller);
    }

    static void ShutdownInFlight(UgoClientOptions options)
    {
        using (var client = new UgoClient(in options))
        {
            var request = new UgoRequest(config.http + "/stall-after-headers");
            Require(client.Submit(in request, out UgoId id) == UgoSubmitStatus.Accepted, "submit");
            WaitHeaders(client, id);
            client.Shutdown();
            using (UgoResponse response = Wait(client, id)) Completed(response, UgoErrorKind.Aborted, UgoErrorReason.Shutdown);
            Span<UgoEvent> events = stackalloc UgoEvent[1];
            Require(!client.TryWaitEvents(events, 0, out _), "wait after shutdown");
            Require(client.Submit(in request, out _) == UgoSubmitStatus.Shutdown, "submit after shutdown");
        }
    }

#if UNITY_IOS
    // After UGO-BACKGROUND-READY the app goes to the background: tests/ios/run launches Settings on a simulator;
    // on a device nothing outside can, so the app opens Settings itself. One transfer ends while the app is in
    // the background; the other stalls until iOS expires its background task.
    static void BackgroundTask(UgoClientOptions options)
    {
        using (var client = new UgoClient(in options))
        {
            var finishing = new UgoRequest(config.http + "/slow-body/2000?bps=200") { FinishInBackground = true };
            var stalled = new UgoRequest(config.http + "/stall-after-headers") { FinishInBackground = true };
            Require(client.Submit(in finishing, out UgoId finishingId) == UgoSubmitStatus.Accepted, "submit");
            Require(client.Submit(in stalled, out UgoId stalledId) == UgoSubmitStatus.Accepted, "submit");
            WaitHeaders(client, stalledId);
            Check("background_detached_post", () => DetachedPost(client, true));
            Debug.Log("UGO-BACKGROUND-READY");
            if (config.open_settings) main.Post(_ => Application.OpenURL("app-settings:"), null);
            Span<UgoEvent> events = stackalloc UgoEvent[8];
            int left = 2;
            long end = UgoClient.MonotonicMicroseconds + 600_000_000L;
            while (left > 0 && UgoClient.MonotonicMicroseconds < end)
            {
                Require(client.TryWaitEvents(events, 1000, out int count), "client stopped");
                for (int i = 0; i < count; i++)
                {
                    if (events[i].Kind != UgoEventKind.Completed) continue;
                    UgoId id = events[i].Id;
                    Require(client.TryGetResponse(id, out UgoResponse response), "no response");
                    using (response)
                    {
                        UgoError error = response.Error;
                        bool finished = id == finishingId;
                        string name = finished ? "background_finish" : "background_expired";
                        Debug.Log((finished ? "UGO-BACKGROUND-FINISHED " : "UGO-BACKGROUND-EXPIRED ") + error.Kind + " " + error.Reason);
                        Check(name, () => Completed(response, finished ? UgoErrorKind.None : UgoErrorKind.Aborted,
                            finished ? UgoErrorReason.None : UgoErrorReason.BackgroundExpired));
                    }
                    left--;
                }
            }
            Require(left == 0, "background transfers did not complete within 600 s");
        }
    }
#endif

    static UgoWebSocketRequest WebSocketRequest(string httpUrl) =>
        new UgoWebSocketRequest("ws" + httpUrl.Substring(4) + "/ws/echo") { Headers = new[] { new UgoHeader("Authorization", "Bearer bench-token") } };

    sealed class Recorder : UgoWebSocketListener
    {
        public bool Opened;
        public string Echo;
        public string Closed;

        public override void OnOpen(UgoWebSocket socket, in UgoHeadersEvent handshake) => Opened = handshake.StatusCode == 101;
        public override void OnMessage(UgoWebSocket socket, in UgoWebSocketMessage message) =>
            Echo = message.MessageType == UgoWebSocketMessageType.Binary ? "binary " + BitConverter.ToString(message.Data.ToArray()) : message.GetText();
        public override void OnClosed(UgoWebSocket socket, in UgoWebSocketResult result) => Closed = Describe(in result);
    }

    internal static string Describe(in UgoWebSocketResult result) =>
        result.Error.Kind + " " + result.Error.Reason + " " + result.CloseCode + " " + result.GetReason();

    static void Drain(UgoClient client, Func<bool> done, string what)
    {
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        long end = UgoClient.MonotonicMicroseconds + 20_000_000;
        while (!done())
        {
            Require(UgoClient.MonotonicMicroseconds < end, what + " not within 20 s");
            Require(client.TryWaitEvents(events, 100, out _), "client stopped");
        }
    }

    static readonly byte[] Binary = { 0x00, 0xff, 0xc0, 0x80 };

    static void WebSocketListener(UgoClient client)
    {
        var recorder = new Recorder();
        using (UgoWebSocket socket = client.OpenWebSocket(WebSocketRequest(config.http), recorder))
        {
            Drain(client, () => recorder.Opened || recorder.Closed != null, "open");
            Require(recorder.Opened, "not open: " + recorder.Closed);
            Require(socket.Send("hello player") == UgoWebSocketSendStatus.Accepted, "send");
            Drain(client, () => recorder.Echo != null || recorder.Closed != null, "echo");
            Require(recorder.Echo == "hello player", "echo " + recorder.Echo + ", closed " + recorder.Closed);
            recorder.Echo = null;
            Require(socket.Send(Binary, UgoWebSocketMessageType.Binary) == UgoWebSocketSendStatus.Accepted, "send binary");
            Drain(client, () => recorder.Echo != null || recorder.Closed != null, "binary echo");
            Require(recorder.Echo == "binary 00-FF-C0-80", "binary echo " + recorder.Echo + ", closed " + recorder.Closed);
            Require(socket.Close(1000, "bye"), "close");
            Drain(client, () => recorder.Closed != null, "close");
            Require(recorder.Closed == "None WebSocketClosedByLocal 1000 bye", "closed " + recorder.Closed);
        }
    }

    static T Await<T>(System.Threading.Tasks.Task<T> task, string what)
    {
        Require(task.Wait(20000), what + " not within 20 s");
        return task.Result;
    }

    static void WebSocketAwaitable(byte[] ca)
    {
        using (var client = new UgoClient(new UgoClientOptions(2) { CaBundlePem = ca }))
        using (UgoWebSocket socket = client.OpenWebSocket(WebSocketRequest(config.https)))
        {
            if (!Await(socket.WaitOpenAsync().AsTask(), "open"))
                throw new Exception("not open: " + (socket.TryGetResult(out UgoWebSocketResult refused) ? Describe(in refused) : "no result"));
            Require(socket.Send("hello wss") == UgoWebSocketSendStatus.Accepted, "send");
            using (UgoWebSocketMessage message = Await(socket.ReceiveAsync().AsTask(), "echo"))
                Require(!message.IsEnd && message.MessageType == UgoWebSocketMessageType.Text && message.GetText() == "hello wss", "echo mismatch");
            Require(socket.Send(Binary, UgoWebSocketMessageType.Binary) == UgoWebSocketSendStatus.Accepted, "send binary");
            using (UgoWebSocketMessage message = Await(socket.ReceiveAsync().AsTask(), "binary echo"))
                Require(message.MessageType == UgoWebSocketMessageType.Binary && message.Data.SequenceEqual(Binary), "binary echo mismatch");
            Require(socket.Close(1000, "bye"), "close");
            Require(Await(socket.ReceiveAsync().AsTask(), "end").IsEnd, "message after close");
            Require(socket.TryGetResult(out UgoWebSocketResult result), "no result");
            string closed = Describe(in result);
            Require(closed == "None WebSocketClosedByLocal 1000 bye", "closed " + closed);
        }
    }

    static void BackgroundAwait(byte[] ca)
    {
        using (var client = new UgoClient(new UgoClientOptions(2) { CaBundlePem = ca }))
        {
            var pending = client.SendAsync(new UgoRequest(config.https + "/ok")).AsTask();
            Require(pending.Wait(20000), "no completion within 20 s");
            using (UgoResponse response = pending.Result)
            {
                Completed(response, UgoErrorKind.None);
                Require(response.Body.SequenceEqual(Ok), "body mismatch");
            }
        }
    }
}
