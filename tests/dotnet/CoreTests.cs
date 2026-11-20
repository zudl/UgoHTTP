using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UgoHttp;
using UgoHttp.Native;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public static class Paths
{
    public static readonly string Root = FindRoot();
    static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "native/include/ugo.h"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
    public static readonly string Library = Path.Combine(Root, OperatingSystem.IsWindows() ? "out/windows-x64/lib/ugo.dll" : "out/macos-arm64/lib/libugo.dylib");
    public static Process Start(string executable, params string[] args)
    {
        if (executable.Contains('/') && !Path.IsPathRooted(executable)) executable = Path.GetFullPath(Path.Combine(Root, executable));
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info);
    }
}

public class OfflineTests
{
    [Fact]
    public unsafe void SyntheticEventPayloadsDecode()
    {
        Span<ugo_event> native = stackalloc ugo_event[3];
        native.Clear();
        fixed (ugo_event* raw = native)
        {
            raw[0].kind = UgoAbi.UGO_EV_HEADERS;
            raw[0].id = 3UL << 32 | 5;
            raw[0].time_us = 1234;
            *(ugo_ev_headers*)raw[0].u = new ugo_ev_headers { http_status = 204, http_version = 20, content_length = 0 };
            raw[1].kind = UgoAbi.UGO_EV_PROGRESS;
            *(ugo_ev_progress*)raw[1].u = new ugo_ev_progress { received = 29, sent = 17, receive_total = -1, send_total = 100 };
            raw[2].kind = UgoAbi.UGO_EV_COMPLETED;
            *(ugo_ev_completed*)raw[2].u = new ugo_ev_completed { error_class = 7, detail = 2, total_us = 993, bytes_sent = 17 };
            var events = MemoryMarshal.Cast<ugo_event, UgoEvent>(native);
            Assert.Equal(5, events[0].Id.Slot);
            Assert.Equal("5/3", events[0].Id.ToString());
            Assert.Equal(1234, events[0].TimestampMicroseconds);
            Assert.Equal(204, events[0].Headers.StatusCode);
            Assert.Equal(UgoHttpVersion.Http2, events[0].Headers.HttpVersion);
            Assert.Equal(29, events[1].Progress.BytesReceived);
            Assert.Equal(-1, events[1].Progress.TotalBytesToReceive);
            var response = new UgoResponse(null, events[2].Native.id, events[2].Completed);
            Assert.Equal(993, response.Timings.TotalMicroseconds);
            Assert.Equal(UgoErrorKind.Aborted, response.Error.Kind);
            Assert.Equal(UgoErrorReason.Shutdown, response.Error.Reason);
            Assert.False(response.IsSuccess);
            response.Dispose();
            bool rejected = false;
            try { _ = events[0].Progress; } catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
        }
    }

    [Fact]
    public void HeaderEnumerationSkipsStatusLineAndKeepsRepeats()
    {
        byte[] block = Encoding.ASCII.GetBytes("HTTP/1.1 500 Error: bad\r\nSet-Cookie: a=1\r\nX-Empty:\r\nset-cookie:  b=2 \r\n\r\n");
        var names = new List<string>();
        var values = new List<string>();
        foreach (UgoHeaderField field in new UgoHeaderEnumerator(block))
        {
            names.Add(Encoding.ASCII.GetString(field.Name));
            values.Add(Encoding.ASCII.GetString(field.Value));
        }
        Assert.Equal(new[] { "Set-Cookie", "X-Empty", "set-cookie" }, names);
        Assert.Equal(new[] { "a=1", "", "b=2" }, values);
    }

    [Fact]
    public void FormUrlEncodedFollowsTheWhatwgSerializer()
    {
        UgoRequestBody body = UgoRequestBody.FromForm(new[]
        {
            KeyValuePair.Create("a b", "1+2"), KeyValuePair.Create("a b", "ж&="), KeyValuePair.Create("empty", (string)null), KeyValuePair.Create("safe", "*-._~"),
            KeyValuePair.Create("lone", "\ud800")
        });
        Assert.Equal("a+b=1%2B2&a+b=%D0%B6%26%3D&empty=&safe=*-._%7E&lone=%EF%BF%BD", Encoding.ASCII.GetString(body.Memory.Span));
        Assert.Equal("application/x-www-form-urlencoded", body.ContentType);
    }
}

public sealed class Bench : IDisposable
{
    readonly Process process;
    readonly Task<string> error;
    // The venv's python.exe on Windows is a launcher; the server is its child.
    readonly int serverPid;
    public string Url { get; }
    public string HttpsUrl { get; }
    public byte[] CaPem { get; }
    public string Run { get; }
    public Bench()
    {
        string library = Paths.Library;
        if (!File.Exists(library)) throw new FileNotFoundException("Build the real shim before integration tests.", library);
        NativeLibrary.SetDllImportResolver(typeof(UgoClient).Assembly, (name, assembly, path) => name == "ugo" ? NativeLibrary.Load(library) : IntPtr.Zero);
        process = OperatingSystem.IsWindows()
            ? Paths.Start("tests/bench/.venv/Scripts/python.exe", "tests/bench/server.py")
            : Paths.Start("./scripts/bench");
        error = process.StandardError.ReadToEndAsync();
        Task<string> line = process.StandardOutput.ReadLineAsync();
        if (!line.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Bench did not publish readiness.");
        using JsonDocument ready = JsonDocument.Parse(line.Result ?? throw new InvalidOperationException("Bench exited before readiness."));
        Url = ready.RootElement.GetProperty("endpoints").GetProperty("http").GetString();
        HttpsUrl = ready.RootElement.GetProperty("endpoints").GetProperty("https").GetString();
        CaPem = File.ReadAllBytes(ready.RootElement.GetProperty("ca_pem").GetString());
        serverPid = ready.RootElement.GetProperty("pid").GetInt32();
        Run = Path.GetDirectoryName(ready.RootElement.GetProperty("ca_pem").GetString());
    }
    public JsonElement LastRequest(string target)
    {
        JsonElement found = default;
        // The bench keeps the log open for writing; Windows refuses a reader that does not share write access.
        using var reader = new StreamReader(new FileStream(Path.Combine(Run, "requests.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        for (string line; (line = reader.ReadLine()) != null;)
        {
            using JsonDocument record = JsonDocument.Parse(line);
            if (record.RootElement.TryGetProperty("target", out JsonElement recorded) && recorded.GetString() == target) found = record.RootElement.Clone();
        }
        Assert.NotEqual(JsonValueKind.Undefined, found.ValueKind);
        return found;
    }
    public void Dispose()
    {
        if (!process.HasExited)
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows has no SIGTERM; the bench waits on this named event (tests/bench/server.py).
                using var stop = EventWaitHandle.OpenExisting("ugohttp-bench-stop-" + serverPid);
                stop.Set();
            }
            else
            {
                using Process stop = Paths.Start("kill", "-TERM", process.Id.ToString());
                stop.WaitForExit();
            }
            if (!process.WaitForExit(10000)) { process.Kill(true); process.WaitForExit(); }
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(error.GetAwaiter().GetResult());
        process.Dispose();
    }
}

public partial class IntegrationTests : IClassFixture<Bench>
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    readonly Bench bench;
    readonly ITestOutputHelper output;
    public IntegrationTests(Bench bench, ITestOutputHelper output) { this.bench = bench; this.output = output; }

    static UgoClient Manual(int slots = 4) => new UgoClient(new UgoClientOptions(slots) { DrainMode = UgoDrainMode.Manual });
    static UgoClient Background(int slots = 4) => new UgoClient(new UgoClientOptions(slots));

    static UgoResponse Complete(UgoClient client, UgoId id)
    {
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        int completed = 0;
        while (UgoClient.MonotonicMicroseconds < end && completed == 0)
        {
            Assert.True(client.TryWaitEvents(events, 100, out int count));
            for (int i = 0; i < count; i++)
                if (events[i].Id == id && events[i].Kind == UgoEventKind.Completed) completed++;
        }
        Assert.Equal(1, completed);
        Assert.True(client.TryWaitEvents(events, 0, out int rest));
        Assert.Equal(0, rest);
        Assert.True(client.TryGetResponse(id, out UgoResponse response));
        return response;
    }

    static UgoResponse Get(UgoClient client, in UgoRequest request)
    {
        Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId id));
        return Complete(client, id);
    }

    [Fact]
    public void GetMemoryHeadersAndIdempotentDispose()
    {
        using UgoClient client = Manual();
        UgoResponse response = Get(client, new UgoRequest(bench.Url + "/ok"));
        Assert.True(response.IsSuccess);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(UgoHttpVersion.Http11, response.HttpVersion);
        Assert.Equal("UgoHTTP fault bench\n", response.GetBodyAsString());
        Assert.True(response.TryGetHeader(Encoding.ASCII.GetBytes("x-BODY-sha256"), out ReadOnlySpan<byte> sha));
        Assert.Equal(64, sha.Length);
        Assert.Equal("20", response.GetHeader("content-length"));
        Assert.Null(response.GetHeader("X-Missing"));
        UgoResponse copy = response;
        response.Dispose();
        response.Dispose();
        copy.Dispose();
        Assert.Throws<ObjectDisposedException>(() => copy.Body.Length);
        Assert.Equal(200, copy.StatusCode);
        Assert.Throws<ArgumentException>(() => client.TryGetResponse(response.Id, out _));
    }

    [Fact]
    public async Task BackgroundAwaitCompletesWithNothingPumping()
    {
        using UgoClient client = Background();
        string body = await Task.Run(async () =>
        {
            Assert.Null(SynchronizationContext.Current);
            using UgoResponse response = await client.SendAsync(new UgoRequest(bench.Url + "/ok"));
            Assert.True(response.IsSuccess);
            return response.GetBodyAsString();
        }).WaitAsync(Limit);
        Assert.Equal("UgoHTTP fault bench\n", body);
        Assert.Throws<InvalidOperationException>(() => client.TryWaitEvents(new UgoEvent[1], 0, out _));
        Assert.Throws<InvalidOperationException>(() => client.Submit(new UgoRequest(bench.Url + "/ok"), out _));
    }

    [Fact]
    public async Task BackgroundCallbackRunsOnTheDrainThreadWithState()
    {
        using UgoClient client = Background();
        var done = new TaskCompletionSource<(int status, string thread, object state)>(TaskCreationOptions.RunContinuationsAsynchronously);
        object token = new object();
        var request = new UgoRequest(bench.Url + "/status/404");
        Assert.Equal(UgoSubmitStatus.Accepted, client.Send(in request, (response, state) =>
        {
            using (response) done.SetResult((response.StatusCode, Thread.CurrentThread.Name, state));
        }, token));
        var result = await done.Task.WaitAsync(Limit);
        Assert.Equal(404, result.status);
        Assert.Equal("ugo-drain", result.thread);
        Assert.Same(token, result.state);
        var plain = new TaskCompletionSource<UgoErrorKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(UgoSubmitStatus.Accepted, client.Send(in request, response => { using (response) plain.SetResult(response.Error.Kind); }));
        Assert.Equal(UgoErrorKind.HttpStatus, await plain.Task.WaitAsync(Limit));
    }

    [Fact]
    public async Task ManualModeCompletesAwaitInsideTryWaitEvents()
    {
        using UgoClient client = Manual();
        ValueTask<UgoResponse> pending = client.SendAsync(new UgoRequest(bench.Url + "/ok"));
        await Task.Delay(200);
        Assert.False(pending.IsCompleted);
        DrainUntil(client, () => pending.IsCompleted);
        Assert.True(pending.IsCompleted);
        using UgoResponse response = await pending;
        Assert.True(response.IsSuccess);
    }

    static void DrainUntil(UgoClient client, Func<bool> done)
    {
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (!done() && UgoClient.MonotonicMicroseconds < end)
        {
            Assert.True(client.TryWaitEvents(events, 100, out int count));
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public async Task AbortCompletesWithCallerResponse()
    {
        using UgoClient client = Background();
        using var cancel = new CancellationTokenSource();
        int headers = 0;
        UgoEventHandler events = (in UgoEvent e, object state) =>
        {
            if (e.Kind != UgoEventKind.Headers) return;
            Interlocked.Increment(ref headers);
            ((CancellationTokenSource)state).Cancel();
        };
        using UgoResponse response = await client.SendAsync(new UgoRequest(bench.Url + "/stall-after-headers"), events, cancel, cancel.Token)
            .AsTask().WaitAsync(Limit);
        Assert.Equal(1, headers);
        Assert.Equal(UgoErrorKind.Aborted, response.Error.Kind);
        Assert.Equal(UgoErrorReason.Caller, response.Error.Reason);
        Assert.Equal(200, response.StatusCode);
        using var before = new CancellationTokenSource();
        before.Cancel();
        var request = new UgoRequest(bench.Url + "/stall-after-headers") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(new byte[32]) };
        using UgoResponse early = await client.SendAsync(in request, cancellationToken: before.Token).AsTask().WaitAsync(Limit);
        Assert.Equal(UgoErrorReason.Caller, early.Error.Reason);
    }

    [Fact]
    public void RequestHeadersAreValidatedAndSentInOrder()
    {
        using UgoClient client = Manual();
        var request = new UgoRequest(bench.Url + "/ok?headers") { Headers = new[] { new UgoHeader("X-Bad", "a\r\nInjected: 1") } };
        Assert.Equal("Headers", Assert.Throws<ArgumentException>(() => client.Submit(in request, out _)).ParamName);
        request.Headers = new[] { new UgoHeader("X Bad", "a") };
        Assert.Equal("Headers", Assert.Throws<ArgumentException>(() => client.Submit(in request, out _)).ParamName);
        request.Headers = new[] { new UgoHeader("X-Dup", "1"), new UgoHeader("X-Empty", ""), new UgoHeader("X-Dup", "2"), new UgoHeader("User-Agent", "ugo-test") };
        using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
        var sent = new List<string>();
        foreach (JsonElement line in bench.LastRequest("/ok?headers").GetProperty("headers").EnumerateArray()) sent.Add(line.GetString());
        Assert.Equal(new[] { "X-Dup: 1", "X-Dup: 2" }, sent.FindAll(h => h.StartsWith("X-Dup")));
        Assert.Contains("X-Empty:", sent);
        Assert.Contains("User-Agent: ugo-test", sent);
    }

    [Fact]
    public void InvalidRequestsThrowWithTheFieldName()
    {
        using UgoClient client = Manual();
        Assert.Equal("Url", Assert.Throws<ArgumentException>(() => client.Submit(default, out _)).ParamName);
        var request = new UgoRequest(bench.Url + "/ok") { ConnectTimeout = TimeSpan.Zero };
        Assert.Equal("ConnectTimeout", Assert.Throws<ArgumentOutOfRangeException>(() => client.Submit(in request, out _)).ParamName);
        request = new UgoRequest(bench.Url + "/ok") { TotalTimeout = TimeSpan.FromMilliseconds(-2) };
        Assert.Equal("TotalTimeout", Assert.Throws<ArgumentOutOfRangeException>(() => client.Submit(in request, out _)).ParamName);
        request = new UgoRequest(bench.Url + "/ok") { Method = HttpMethod.Head, Body = UgoRequestBody.FromBytes(new byte[1]) };
        Assert.Equal("Body", Assert.Throws<ArgumentException>(() => client.Submit(in request, out _)).ParamName);
        foreach (HttpMethod method in new[] { null, new HttpMethod("CONNECT"), new HttpMethod(new string('X', 33)) })
        {
            request = new UgoRequest(bench.Url + "/ok") { Method = method };
            Assert.Equal("Method", Assert.Throws<ArgumentException>(() => client.Submit(in request, out _)).ParamName);
        }
        request = new UgoRequest(bench.Url + "/ok") { MinimumSpeedBytesPerSecond = 100 };
        Assert.Equal("MinimumSpeedBytesPerSecond", Assert.Throws<ArgumentException>(() => client.Submit(in request, out _)).ParamName);
        request.StallTimeout = Timeout.InfiniteTimeSpan;
        Assert.Equal("MinimumSpeedBytesPerSecond", Assert.Throws<ArgumentException>(() => client.Submit(in request, out _)).ParamName);
        request = new UgoRequest(bench.Url + "/ok") { MinimumSpeedBytesPerSecond = -1, StallTimeout = TimeSpan.FromSeconds(1) };
        Assert.Equal("MinimumSpeedBytesPerSecond", Assert.Throws<ArgumentOutOfRangeException>(() => client.Submit(in request, out _)).ParamName);
        foreach (TimeSpan? stall in new TimeSpan?[] { Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(65535) })
        {
            request = new UgoRequest(bench.Url + "/ok") { StallTimeout = stall };
            using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
        }
        foreach (TimeSpan stall in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-2), TimeSpan.FromSeconds(65535) + TimeSpan.FromTicks(1), TimeSpan.FromSeconds(65536) })
        {
            request.StallTimeout = stall;
            Assert.Equal("StallTimeout", Assert.Throws<ArgumentOutOfRangeException>(() => client.Submit(in request, out _)).ParamName);
        }
        request = new UgoRequest(bench.Url + "/ok") { MaxSendBytesPerSecond = -1 };
        Assert.Equal("MaxSendBytesPerSecond", Assert.Throws<ArgumentOutOfRangeException>(() => client.Submit(in request, out _)).ParamName);
        request = new UgoRequest("ftp://example.com/");
        Assert.Equal("Url", Assert.Throws<ArgumentException>(() => client.Submit(in request, out _)).ParamName);
        Assert.Equal("OperationSlots", Assert.Throws<ArgumentOutOfRangeException>(() => new UgoClient(new UgoClientOptions(-1))).ParamName);
        using var defaults = new UgoClient();
        Assert.Equal(UgoClientOptions.DefaultOperationSlots, defaults.OperationSlots);
    }

    [Fact]
    public void RedirectsAreFollowedByDefault()
    {
        using UgoClient client = Manual();
        using UgoResponse response = Get(client, new UgoRequest(bench.Url + "/redirect/302?to=/ok"));
        Assert.True(response.IsSuccess);
        Assert.Equal(1, response.RedirectCount);
        var none = new UgoRequest(bench.Url + "/redirect/302?to=/ok") { MaxRedirects = 0 };
        using UgoResponse stopped = Get(client, in none);
        Assert.Equal(302, stopped.StatusCode);
        Assert.Equal(UgoErrorKind.HttpStatus, stopped.Error.Kind);
    }

    [Fact]
    public void SeeOtherSendsGetWithoutTheBody()
    {
        using UgoClient client = Manual();
        byte[] bytes = Encoding.UTF8.GetBytes("{\"a\":1}");
        var request = new UgoRequest(bench.Url + "/redirect/303?to=%2Fok%3Fsee-other") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(bytes, "application/json") };
        request.Headers = new[] { new UgoHeader("X-Keep", "1") };
        using (UgoResponse response = Get(client, in request))
        {
            Assert.True(response.IsSuccess);
            Assert.Equal(1, response.RedirectCount);
        }
        AssertRecordedPost("/redirect/303?to=%2Fok%3Fsee-other", bytes);
        JsonElement record = bench.LastRequest("/ok?see-other");
        Assert.Equal("GET", record.GetProperty("method").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(), record.GetProperty("body_sha256").GetString());
        List<string> headers = Headers("/ok?see-other");
        Assert.Contains("X-Keep: 1", headers);
        Assert.DoesNotContain(headers, line => line.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetFileAndEmptyFile()
    {
        using UgoClient client = Manual();
        string path = Path.Combine(bench.Run, "dotnet-response.part");
        var request = new UgoRequest(bench.Url + "/big/4096") { ResponseFilePath = path };
        using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(4096, bytes.Length);
        for (int i = 0; i < bytes.Length; i++) Assert.Equal((byte)i, bytes[i]);
        request.Url = bench.Url + "/status/204";
        using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void PostMemoryFileAndForm()
    {
        using UgoClient client = Manual();
        byte[] bytes = Encoding.UTF8.GetBytes("body borrowed until completion");
        var request = new UgoRequest(bench.Url + "/ok?memory") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(bytes, "application/json") };
        Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId id));
        GC.Collect(); GC.WaitForPendingFinalizers();
        using (UgoResponse response = Complete(client, id)) Assert.Equal(bytes.Length, response.BytesSent);
        AssertRecordedPost("/ok?memory", bytes);
        Assert.Contains("Content-Type: application/json", Headers("/ok?memory"));
        string path = Path.Combine(bench.Run, "dotnet-upload");
        File.WriteAllBytes(path, bytes);
        request.Url = bench.Url + "/ok?file";
        request.Body = UgoRequestBody.FromFile(path);
        using (UgoResponse response = Get(client, in request)) Assert.Equal(bytes.Length, response.BytesSent);
        AssertRecordedPost("/ok?file", bytes);
        request.Url = bench.Url + "/ok?form";
        request.Body = UgoRequestBody.FromForm(new[] { KeyValuePair.Create("k", "v w") });
        using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
        AssertRecordedPost("/ok?form", Encoding.ASCII.GetBytes("k=v+w"));
        Assert.Contains("Content-Type: application/x-www-form-urlencoded", Headers("/ok?form"));
        request.Headers = new[] { new UgoHeader("content-type", "text/plain") };
        Assert.Equal("Headers", Assert.Throws<ArgumentException>(() => client.Submit(in request, out _)).ParamName);
    }

    List<string> Headers(string target)
    {
        var lines = new List<string>();
        foreach (JsonElement line in bench.LastRequest(target).GetProperty("headers").EnumerateArray()) lines.Add(line.GetString());
        return lines;
    }

    void AssertRecordedPost(string target, byte[] bytes)
    {
        JsonElement record = bench.LastRequest(target);
        Assert.Equal("POST", record.GetProperty("method").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), record.GetProperty("body_sha256").GetString());
    }

    [Fact]
    public void MultipartMemoryAndFile()
    {
        using UgoClient client = Manual();
        byte[] bytes = Encoding.UTF8.GetBytes("multipart bytes");
        string path = Path.Combine(bench.Run, "dotnet-multipart");
        File.WriteAllBytes(path, bytes);
        byte[] backing = Encoding.UTF8.GetBytes("--slice bytes--");
        var request = new UgoRequest(bench.Url + "/ok?record-body")
        {
            Method = HttpMethod.Post,
            Body = UgoRequestBody.FromParts(new[]
            {
                UgoFormPart.FromBytes("memory", new ReadOnlyMemory<byte>(backing, 2, 11), contentType: "application/octet-stream"),
                UgoFormPart.FromText("text", "value"),
                UgoFormPart.FromFile("file", path, "upload.txt", "text/plain")
            })
        };
        Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId id));
        GC.Collect(); GC.WaitForPendingFinalizers();
        using UgoResponse response = Complete(client, id);
        Assert.True(response.IsSuccess);
        string sent = bench.LastRequest("/ok?record-body").GetProperty("body").GetString();
        string contentType = Headers("/ok?record-body").Find(h => h.StartsWith("Content-Type: multipart/form-data; boundary="));
        Assert.NotNull(contentType);
        string boundary = "--" + contentType.Substring(contentType.IndexOf('=') + 1);
        string[] parts = sent.Split(boundary);
        Assert.Equal(5, parts.Length);
        Assert.Equal("--\r\n", parts[4]);
        Assert.Equal("\r\nContent-Disposition: form-data; name=\"memory\"\r\nContent-Type: application/octet-stream\r\n\r\nslice bytes\r\n", parts[1]);
        Assert.Equal("\r\nContent-Disposition: form-data; name=\"text\"\r\n\r\nvalue\r\n", parts[2]);
        Assert.Equal("\r\nContent-Disposition: form-data; name=\"file\"; filename=\"upload.txt\"\r\nContent-Type: text/plain\r\n\r\nmultipart bytes\r\n", parts[3]);
    }

    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void MethodsAreSentAsGivenWithAnyBody()
    {
        using UgoClient client = Manual();
        var head = new UgoRequest(bench.Url + "/ok?head") { Method = HttpMethod.Head };
        using (UgoResponse response = Get(client, in head))
        {
            Assert.True(response.IsSuccess);
            Assert.Equal("20", response.GetHeader("Content-Length"));
            Assert.Equal(0, response.Body.Length);
            Assert.Equal(0, response.BytesReceived);
        }
        Assert.Equal("HEAD", bench.LastRequest("/ok?head").GetProperty("method").GetString());

        byte[] bytes = Encoding.UTF8.GetBytes("body for any method");
        foreach (HttpMethod method in new[] { HttpMethod.Put, new HttpMethod("PATCH"), HttpMethod.Delete, HttpMethod.Get, new HttpMethod("X-PURGE") })
        {
            string target = "/ok?with-body-" + method.Method;
            var request = new UgoRequest(bench.Url + target) { Method = method, Body = UgoRequestBody.FromBytes(bytes) };
            using (UgoResponse response = Get(client, in request)) Assert.Equal(bytes.Length, response.BytesSent);
            JsonElement record = bench.LastRequest(target);
            Assert.Equal(method.Method, record.GetProperty("method").GetString());
            Assert.Equal(Sha(bytes), record.GetProperty("body_sha256").GetString());
        }

        var bodiless = new (HttpMethod Method, bool ContentLength)[]
        {
            (HttpMethod.Post, true), (HttpMethod.Put, true), (new HttpMethod("X-PURGE"), true),
            (HttpMethod.Get, false), (HttpMethod.Delete, false), (HttpMethod.Options, false), (HttpMethod.Trace, true)
        };
        foreach ((HttpMethod method, bool contentLength) in bodiless)
        {
            string target = "/ok?no-body-" + method.Method;
            var request = new UgoRequest(bench.Url + target) { Method = method };
            using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
            Assert.Equal(method.Method, bench.LastRequest(target).GetProperty("method").GetString());
            List<string> sent = Headers(target);
            Assert.Equal(contentLength, sent.Contains("Content-Length: 0"));
            Assert.DoesNotContain(sent, h => h.StartsWith("Accept:") || h.StartsWith("Content-Type:") || h.StartsWith("Expect:"));
        }
    }

    [Fact]
    public void OnlyCallerAndBodyHeadersGoOnTheWire()
    {
        using UgoClient client = Manual();
        byte[] big = new byte[2 << 20];
        var request = new UgoRequest(bench.Url + "/ok?wire-big") { Method = HttpMethod.Put, Body = UgoRequestBody.FromBytes(big) };
        using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
        List<string> sent = Headers("/ok?wire-big");
        sent.Sort(StringComparer.Ordinal);
        Assert.Equal(3, sent.Count);
        Assert.Equal("Accept-Encoding: gzip", sent[0]);
        Assert.Equal("Content-Length: " + big.Length, sent[1]);
        Assert.StartsWith("Host: ", sent[2]);
        request = new UgoRequest(bench.Url + "/ok?wire-form")
        {
            Method = HttpMethod.Post,
            Body = UgoRequestBody.FromForm(new[] { KeyValuePair.Create("k", "v") }),
            Headers = new[] { new UgoHeader("Accept", "application/json") }
        };
        using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
        sent = Headers("/ok?wire-form");
        sent.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "Accept-Encoding: gzip", "Accept: application/json", "Content-Length: 3", "Content-Type: application/x-www-form-urlencoded" }, sent.GetRange(0, 4));
        Assert.Equal(5, sent.Count);
    }

    static void AssertCounting(ReadOnlySpan<byte> body, int length)
    {
        Assert.Equal(length, body.Length);
        for (int i = 0; i < body.Length; i++)
            if (body[i] != (byte)i) Assert.Fail("byte " + i);
    }

    [Fact]
    public void GzipIsDecodedUnlessTheCallerSetsAcceptEncoding()
    {
        using UgoClient client = Manual();
        using (UgoResponse response = Get(client, new UgoRequest(bench.Url + "/encoded/gzip?bytes=200000&dotnet")))
        {
            Assert.True(response.IsSuccess);
            AssertCounting(response.Body, 200000);
            Assert.Equal("gzip", response.GetHeader("content-encoding"));
            Assert.Equal(long.Parse(response.GetHeader("content-length")), response.BytesReceived);
            Assert.True(response.BytesReceived < 20000);
        }
        Assert.Contains("Accept-Encoding: gzip", Headers("/encoded/gzip?bytes=200000&dotnet"));

        var request = new UgoRequest(bench.Url + "/encoded/gzip?bytes=200000&dotnet-own") { Headers = new[] { new UgoHeader("Accept-Encoding", "gzip") } };
        using (UgoResponse response = Get(client, in request))
        {
            Assert.True(response.IsSuccess);
            Assert.Equal(response.GetHeader("x-encoded-sha256"), Convert.ToHexString(SHA256.HashData(response.Body)).ToLowerInvariant());
        }

        request = new UgoRequest(bench.Url + "/encoded/gzip?bytes=1000000") { MaxResponseBodyBytes = 65536 };
        using (UgoResponse response = Get(client, in request))
        {
            Assert.Equal(UgoErrorKind.Transfer, response.Error.Kind);
            Assert.Equal(UgoErrorReason.ResponseBodyLimit, response.Error.Reason);
            AssertCounting(response.Body, 65536);
        }

        string path = Path.Combine(bench.Run, "dotnet-gzip.part");
        request = new UgoRequest(bench.Url + "/encoded/gzip?bytes=200000") { ResponseFilePath = path };
        using (UgoResponse response = Get(client, in request)) Assert.True(response.IsSuccess);
        AssertCounting(File.ReadAllBytes(path), 200000);
        File.Delete(path);
        request.Url = bench.Url + "/encoded/gzip?bytes=200000&status=503";
        using (UgoResponse response = Get(client, in request))
        {
            Assert.Equal(UgoErrorKind.HttpStatus, response.Error.Kind);
            Assert.Equal(UgoErrorReason.ResponseBodyLimit, response.Error.Reason);
            AssertCounting(response.Body, 64 * 1024);
        }
        Assert.False(File.Exists(path));
        request.Url = bench.Url + "/encoded/gzip?bytes=200000&cut=500";
        using (UgoResponse response = Get(client, in request))
        {
            Assert.Equal(UgoErrorKind.Transfer, response.Error.Kind);
            Assert.Equal(61, response.Error.NativeCode);
        }
        Assert.False(File.Exists(path));
        request.Url = bench.Url + "/encoded/corrupt?bytes=200000";
        using (UgoResponse response = Get(client, in request))
        {
            Assert.Equal(UgoErrorKind.Transfer, response.Error.Kind);
            Assert.Equal(UgoErrorReason.None, response.Error.Reason);
            Assert.Equal(61, response.Error.NativeCode);
        }
        Assert.False(File.Exists(path));
    }

    UgoClient ResumeClient(bool https) =>
        new UgoClient(new UgoClientOptions(4) { CaBundlePem = https ? bench.CaPem : default });

    string ResumeUrl(bool https, string path) => (https ? bench.HttpsUrl : bench.Url) + path;

    [Theory]
    [InlineData(false, UgoHttpVersion.Http11)]
    [InlineData(true, UgoHttpVersion.Http2)]
    public async Task InterruptedDownloadResumesToIdenticalBytes(bool https, UgoHttpVersion version)
    {
        int size = https ? 700_001 : 700_000;
        string source = Path.Combine(bench.Run, "resume-source-" + size);
        string part = Path.Combine(bench.Run, "resume-" + size + ".part");
        using UgoClient client = ResumeClient(https);
        var plain = new UgoRequest(ResumeUrl(https, "/range/" + size)) { ResponseFilePath = source };
        using (UgoResponse response = await client.SendAsync(in plain).AsTask().WaitAsync(Limit)) Assert.True(response.IsSuccess);

        var cut = new UgoRequest(ResumeUrl(https, "/range/" + size + "?cut=250000")) { ResponseFilePath = part, Resume = true };
        using (UgoResponse response = await client.SendAsync(in cut).AsTask().WaitAsync(Limit))
        {
            Assert.Equal(UgoErrorKind.Transfer, response.Error.Kind);
            Assert.Equal(version, response.HttpVersion);
        }
        Assert.Equal(250_000, new FileInfo(part).Length);

        long offset = -1, received = 0, total = 0;
        UgoEventHandler events = (in UgoEvent e, object state) =>
        {
            if (e.Kind == UgoEventKind.Headers) offset = e.Headers.ResumeOffset;
            if (e.Kind != UgoEventKind.Progress) return;
            received = e.Progress.BytesReceived;
            total = e.Progress.TotalBytesToReceive;
        };
        var resume = new UgoRequest(ResumeUrl(https, "/range/" + size)) { ResponseFilePath = part, Resume = true };
        using (UgoResponse response = await client.SendAsync(in resume, events).AsTask().WaitAsync(Limit))
        {
            Assert.True(response.IsSuccess);
            Assert.Equal(206, response.StatusCode);
            Assert.Equal(size - 250_000, response.BytesReceived);
        }
        Assert.Equal(250_000, offset);
        Assert.Equal(size, received);
        Assert.Equal(size, total);
        Assert.True(File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(part)));
    }

    [Fact]
    public async Task ResumeRestartsCompletesOrDeletes()
    {
        using UgoClient client = ResumeClient(false);
        string part = Path.Combine(bench.Run, "resume-cases.part");

        async Task<UgoResponse> Resume(string path, int have, int version, string ifRange = null)
        {
            File.WriteAllBytes(part, Enumerable.Range(0, have).Select(i => (byte)((i + version * 7) % 256)).ToArray());
            var request = new UgoRequest(bench.Url + path) { ResponseFilePath = part, Resume = true };
            if (ifRange != null) request.Headers = new[] { new UgoHeader("If-Range", ifRange) };
            return await client.SendAsync(in request).AsTask().WaitAsync(Limit);
        }
        bool FileIs(int size, int version) =>
            File.ReadAllBytes(part).SequenceEqual(Enumerable.Range(0, size).Select(i => (byte)((i + version * 7) % 256)));

        using (UgoResponse response = await Resume("/range/9000?version=1", 3000, 0, "\"r9000v0\""))
        {
            Assert.True(response.IsSuccess);
            Assert.Equal(200, response.StatusCode);
        }
        Assert.True(FileIs(9000, 1));

        using (UgoResponse response = await Resume("/range/9001", 9001, 0))
        {
            Assert.True(response.IsSuccess);
            Assert.Equal(416, response.StatusCode);
        }
        Assert.True(FileIs(9001, 0));

        using (UgoResponse response = await Resume("/range/9002", 9500, 0))
        {
            Assert.Equal(UgoErrorKind.Transfer, response.Error.Kind);
            Assert.Equal(UgoErrorReason.ResumeMismatch, response.Error.Reason);
        }
        Assert.False(File.Exists(part));

        File.WriteAllBytes(part, Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray());
        var keep = new UgoRequest(bench.Url + "/range/9003?mode=wrong-start") { ResponseFilePath = part, Resume = true, KeepPartialFile = true };
        using (UgoResponse response = await client.SendAsync(in keep).AsTask().WaitAsync(Limit))
            Assert.Equal(UgoErrorReason.ResumeMismatch, response.Error.Reason);
        Assert.True(FileIs(3000, 0));

        Assert.Throws<ArgumentException>(() => client.Send(new UgoRequest(bench.Url + "/ok") { Resume = true }, _ => { }));
        Assert.Throws<ArgumentException>(() => client.Send(new UgoRequest(bench.Url + "/ok") { Resume = true, ResponseFilePath = part, Method = HttpMethod.Head }, _ => { }));
        Assert.Throws<ArgumentException>(() => client.Send(new UgoRequest(bench.Url + "/ok")
        {
            Resume = true, ResponseFilePath = part, Headers = new[] { new UgoHeader("range", "bytes=0-") }
        }, _ => { }));
    }

    [Fact]
    public void FileRequestKeepsANon2xxBodyInMemory()
    {
        using UgoClient client = Manual();
        string path = Path.Combine(bench.Run, "dotnet-error.part");
        File.Delete(path);
        var request = new UgoRequest(bench.Url + "/status/500?bytes=1000") { ResponseFilePath = path };
        using (UgoResponse response = Get(client, in request))
        {
            Assert.Equal(UgoErrorKind.HttpStatus, response.Error.Kind);
            Assert.Equal(UgoErrorReason.None, response.Error.Reason);
            Assert.Equal(500, response.StatusCode);
            Assert.Equal(1000, response.Body.Length);
            for (int i = 0; i < 1000; i++) Assert.Equal((byte)i, response.Body[i]);
        }
        Assert.False(File.Exists(path));
        request.Url = bench.Url + "/status/500?bytes=65536";
        using (UgoResponse response = Get(client, in request))
        {
            Assert.Equal(UgoErrorKind.HttpStatus, response.Error.Kind);
            Assert.Equal(UgoErrorReason.None, response.Error.Reason);
            Assert.Equal(0, response.Error.NativeCode);
            Assert.Equal(64 * 1024, response.Body.Length);
            Assert.Equal(64 * 1024, response.BytesReceived);
        }
        Assert.False(File.Exists(path));
        request.Url = bench.Url + "/status/502?bytes=200000";
        using (UgoResponse response = Get(client, in request))
        {
            Assert.Equal(UgoErrorKind.HttpStatus, response.Error.Kind);
            Assert.Equal(UgoErrorReason.ResponseBodyLimit, response.Error.Reason);
            Assert.Equal(502, response.StatusCode);
            Assert.Equal(64 * 1024, response.Body.Length);
            Assert.Equal("HTTP status 502", response.GetErrorMessage());
            Assert.Equal(23, response.Error.NativeCode);
            Assert.True(response.BytesReceived < 200000);
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SendSpeedCapAndMinimumSpeed()
    {
        using UgoClient client = Manual();
        var upload = new UgoRequest(bench.Url + "/ok?send-cap") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(new byte[60000]), MaxSendBytesPerSecond = 40000 };
        var watch = Stopwatch.StartNew();
        using (UgoResponse response = Get(client, in upload))
        {
            Assert.True(response.IsSuccess);
            Assert.Equal(60000, response.BytesSent);
        }
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.9), "upload took " + watch.Elapsed);
        var slow = new UgoRequest(bench.Url + "/slow-body/100000?bps=500")
        {
            StallTimeout = TimeSpan.FromMilliseconds(500),
            MinimumSpeedBytesPerSecond = 4000
        };
        using (UgoResponse response = Get(client, in slow)) Assert.Equal(UgoErrorKind.Timeout, response.Error.Kind);
        var trickle = new UgoRequest(bench.Url + "/slow-body/1500?bps=500") { StallTimeout = TimeSpan.FromSeconds(1) };
        using (UgoResponse response = Get(client, in trickle))
        {
            Assert.True(response.IsSuccess, response.GetErrorMessage());
            Assert.Equal(1500, response.Body.Length);
            Assert.True(response.Timings.TotalMicroseconds >= 2000000, "body took " + response.Timings.TotalMicroseconds);
        }
        var stalled = new UgoRequest(bench.Url + "/stall-after-headers") { StallTimeout = TimeSpan.FromSeconds(1) };
        watch.Restart();
        using (UgoResponse response = Get(client, in stalled)) Assert.Equal(UgoErrorKind.Timeout, response.Error.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "stall took " + watch.Elapsed);
    }

    sealed class ContinuationFault : Exception { }
    static int continuationFaults;
    static readonly bool faultHandler = InstallFaultHandler();
    static bool InstallFaultHandler()
    {
        System.Runtime.ExceptionServices.ExceptionHandling.SetUnhandledExceptionHandler(error =>
        {
            if (error is not ContinuationFault) return false;
            Interlocked.Increment(ref continuationFaults);
            return true;
        });
        return true;
    }

    [Fact]
    public void ThrowingInlineContinuationDoesNotStopTheBatch()
    {
        Assert.True(faultHandler);
        int faultsBefore = Volatile.Read(ref continuationFaults);
        using UgoClient client = Manual();
        ValueTask<UgoResponse> first = client.SendAsync(new UgoRequest(bench.Url + "/ok?fault-a"));
        ValueTask<UgoResponse> second = client.SendAsync(new UgoRequest(bench.Url + "/ok?fault-b"));
        int resumed = 0;
        first.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => { Interlocked.Increment(ref resumed); throw new ContinuationFault(); });
        second.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => Interlocked.Increment(ref resumed));
        Span<UgoEvent> events = stackalloc UgoEvent[16];
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (Volatile.Read(ref resumed) < 2 && UgoClient.MonotonicMicroseconds < end)
            Assert.True(client.TryWaitEvents(events, 100, out _));
        Assert.Equal(2, resumed);
        first.GetAwaiter().GetResult().Dispose();
        second.GetAwaiter().GetResult().Dispose();
        while (Volatile.Read(ref continuationFaults) == faultsBefore && UgoClient.MonotonicMicroseconds < end) Thread.Sleep(10);
        Assert.Equal(faultsBefore + 1, Volatile.Read(ref continuationFaults));
    }

    public enum Registration { InlineBeforeCompletion, InlineFlowingContext, PostedBeforeCompletion, AfterCompletion }

    sealed class QueueContext : SynchronizationContext
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object)> Posted = new();
        public override void Post(SendOrPostCallback callback, object state) => Posted.Enqueue((callback, state));
    }

    static readonly AsyncLocal<string> flowed = new AsyncLocal<string>();

    // The 500 ms bound stands in for a callback that waits for IsCompleted: it must not wait on the SetResult that invoked it.
    static void RunRegistered<T>(Registration registration, ValueTask<T> pending, Action<Func<bool>> drainUntil, Action<T> check)
    {
        int ran = 0;
        bool completedInside = false;
        string flowedInside = null;
        if (registration == Registration.PostedBeforeCompletion)
        {
            var context = new QueueContext();
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                ValueTaskAwaiter<T> awaiter = pending.GetAwaiter();
                awaiter.OnCompleted(() =>
                {
                    completedInside = SpinWait.SpinUntil(() => awaiter.IsCompleted, 500);
                    check(awaiter.GetResult());
                    ran++;
                });
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            drainUntil(() => !context.Posted.IsEmpty);
            Assert.Equal(0, ran);
            Assert.True(context.Posted.TryDequeue(out (SendOrPostCallback callback, object state) posted));
            posted.callback(posted.state);
        }
        else
        {
            ConfiguredValueTaskAwaitable<T>.ConfiguredValueTaskAwaiter awaiter = pending.ConfigureAwait(false).GetAwaiter();
            Action continuation = () =>
            {
                completedInside = SpinWait.SpinUntil(() => awaiter.IsCompleted, 500);
                flowedInside = flowed.Value;
                check(awaiter.GetResult());
                Interlocked.Increment(ref ran);
            };
            if (registration == Registration.InlineBeforeCompletion)
            {
                awaiter.UnsafeOnCompleted(continuation);
                drainUntil(() => Volatile.Read(ref ran) != 0);
            }
            else if (registration == Registration.InlineFlowingContext)
            {
                flowed.Value = "flowed";
                awaiter.OnCompleted(continuation);
                flowed.Value = null;
                drainUntil(() => Volatile.Read(ref ran) != 0);
                Assert.Equal("flowed", flowedInside);
            }
            else
            {
                drainUntil(() => pending.IsCompleted);
                awaiter.UnsafeOnCompleted(continuation);
                SpinWait.SpinUntil(() => Volatile.Read(ref ran) != 0, 10000);
            }
        }
        Assert.Equal(1, Volatile.Read(ref ran));
        Assert.True(completedInside, "IsCompleted was false inside the continuation SetResult ran");
    }

    [Theory]
    [InlineData(Registration.InlineBeforeCompletion)]
    [InlineData(Registration.InlineFlowingContext)]
    [InlineData(Registration.PostedBeforeCompletion)]
    [InlineData(Registration.AfterCompletion)]
    public void ContinuationSeesItsOperationCompleted(Registration registration)
    {
        using UgoClient client = Manual(1);
        for (int round = 0; round < 2; round++)
        {
            ValueTask<UgoResponse> pending = client.SendAsync(new UgoRequest(bench.Url + "/ok"));
            RunRegistered(registration, pending, done => DrainUntil(client, done), response =>
            {
                using (response) Assert.Equal(200, response.StatusCode);
            });
        }
    }

    [Fact]
    public void ResponseLimitAndFileErrorAreClassified()
    {
        using UgoClient client = Manual();
        var request = new UgoRequest(bench.Url + "/big/1024") { MaxResponseBodyBytes = 16 };
        using (UgoResponse response = Get(client, in request))
        {
            Assert.Equal(UgoErrorKind.Transfer, response.Error.Kind);
            Assert.Equal(UgoErrorReason.ResponseBodyLimit, response.Error.Reason);
            Assert.NotEmpty(response.GetErrorMessage());
        }
        request.ResponseFilePath = Path.Combine(bench.Run, "missing", "file.part");
        using (UgoResponse response = Get(client, in request)) Assert.Equal(UgoErrorKind.LocalIO, response.Error.Kind);
    }

    [Fact]
    public async Task ShutdownWakesBlockedDrainer()
    {
        using UgoClient client = Manual();
        using var entered = new ManualResetEventSlim();
        var task = Task.Run(() =>
        {
            Span<UgoEvent> events = stackalloc UgoEvent[1];
            entered.Set();
            return client.TryWaitEvents(events, 30000, out _);
        });
        Assert.True(entered.Wait(5000));
        client.Shutdown();
        Assert.False(await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AbortCapacityAndShutdownInFlight()
    {
        using UgoClient client = Manual(1);
        var request = new UgoRequest(bench.Url + "/stall-after-headers");
        Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId id));
        Assert.Equal(UgoSubmitStatus.NoCapacity, client.Submit(in request, out _));
        Assert.Equal(UgoSubmitStatus.NoCapacity, Assert.Throws<UgoSubmitException>(() => client.SendAsync(in request)).Status);
        Assert.False(client.TryGetResponse(id, out _));
        client.Abort(id);
        using (UgoResponse response = Complete(client, id)) Assert.Equal(UgoErrorReason.Caller, response.Error.Reason);
        request.Method = HttpMethod.Post;
        request.Body = UgoRequestBody.FromBytes(new byte[32]);
        Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId next));
        Assert.NotEqual(id, next);
        client.Shutdown();
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        Assert.True(client.TryWaitEvents(events, 0, out int count));
        int completions = 0;
        for (int i = 0; i < count; i++)
            if (events[i].Kind == UgoEventKind.Completed) completions++;
        Assert.Equal(1, completions);
        Assert.True(client.TryGetResponse(next, out UgoResponse aborted));
        Assert.Equal(UgoErrorKind.Aborted, aborted.Error.Kind);
        Assert.Equal(UgoErrorReason.Shutdown, aborted.Error.Reason);
        aborted.Dispose();
        Assert.False(client.TryWaitEvents(events, 0, out _));
        Assert.Equal(UgoSubmitStatus.Shutdown, client.Submit(in request, out _));
        Assert.Equal(UgoSubmitStatus.Shutdown, client.EnqueueDetached(new UgoRequest(bench.Url + "/ok")));
    }

    [Fact]
    public async Task ShutdownCompletesPendingAwaitsInBothModes()
    {
        using UgoClient background = Background();
        ValueTask<UgoResponse> pending = background.SendAsync(new UgoRequest(bench.Url + "/stall-after-headers"));
        await Task.Delay(100);
        background.Shutdown();
        Assert.True(pending.IsCompleted);
        using (UgoResponse response = await pending) Assert.Equal(UgoErrorReason.Shutdown, response.Error.Reason);
        UgoClient manual = Manual();
        Task<UgoResponse> undrained = manual.SendAsync(new UgoRequest(bench.Url + "/stall-after-headers")).AsTask();
        manual.Dispose();
        UgoResponse lost = await undrained.WaitAsync(Limit);
        Assert.Equal(UgoErrorReason.Shutdown, lost.Error.Reason);
        Assert.Throws<ObjectDisposedException>(() => lost.Body.Length);
        lost.Dispose();
    }

    [Fact]
    public void DetachedIsAcceptedWithoutEvents()
    {
        using UgoClient client = Manual(1);
        byte[] body = Encoding.ASCII.GetBytes("detached");
        var request = new UgoRequest(bench.Url + "/ok?detached") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(body) };
        Assert.Equal(UgoSubmitStatus.Accepted, client.EnqueueDetached(in request));
        Array.Clear(body);
        long end = UgoClient.MonotonicMicroseconds + 10_000_000;
        Span<UgoEvent> events = stackalloc UgoEvent[4];
        UgoSubmitStatus status;
        while ((status = client.Submit(new UgoRequest(bench.Url + "/ok"), out UgoId id)) == UgoSubmitStatus.NoCapacity && UgoClient.MonotonicMicroseconds < end)
        {
            Assert.True(client.TryWaitEvents(events, 10, out int count));
            Assert.Equal(0, count);
        }
        Assert.Equal(UgoSubmitStatus.Accepted, status);
        AssertRecordedPost("/ok?detached", Encoding.ASCII.GetBytes("detached"));
        request.ResponseFilePath = Path.Combine(bench.Run, "never");
        Assert.Equal("ResponseFilePath", Assert.Throws<ArgumentException>(() => client.EnqueueDetached(in request)).ParamName);
    }

    [Fact]
    public void AbortStaleAndUnknownIdsIsANoOp()
    {
        using UgoClient client = Manual();
        UgoResponse response = Get(client, new UgoRequest(bench.Url + "/ok"));
        client.Abort(response.Id);
        response.Dispose();
        client.Abort(response.Id);
        client.Abort(default);
        client.Abort(new UgoId(ulong.MaxValue));
    }

    [Fact]
    public async Task DisposeWakesDrainerAndAbortsInFlightTransfer()
    {
        UgoClient client = Manual();
        var request = new UgoRequest(bench.Url + "/stall-after-headers") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(new byte[32]) };
        Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId id));
        Task<UgoResponse> pending = client.SendAsync(new UgoRequest(bench.Url + "/stall-after-headers")).AsTask();
        var drainer = Task.Run(() =>
        {
            Span<UgoEvent> events = stackalloc UgoEvent[8];
            long deadline = UgoClient.MonotonicMicroseconds + 40_000_000;
            while (UgoClient.MonotonicMicroseconds < deadline)
            {
                bool drained;
                int count;
                // WaitForBlockedDrainer's probe is itself a drain and can hold the client when this one starts.
                try { drained = client.TryWaitEvents(events, 30000, out count); }
                catch (InvalidOperationException e) when (e.Message == "Another drain of this client is running.") { Thread.Yield(); continue; }
                if (!drained) throw new InvalidOperationException("Shutdown lost the completed event.");
                for (int i = 0; i < count; i++)
                    if (events[i].Kind == UgoEventKind.Completed) return true;
            }
            throw new TimeoutException("No completed event.");
        });
        WaitForBlockedDrainer(client);
        await Task.Run(client.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await drainer.WaitAsync(TimeSpan.FromSeconds(5)));
        UgoResponse aborted = await pending.WaitAsync(Limit);
        Assert.Equal(UgoErrorKind.Aborted, aborted.Error.Kind);
        Assert.Equal(UgoErrorReason.Shutdown, aborted.Error.Reason);
        Assert.Throws<ObjectDisposedException>(() => aborted.Body.Length);
        aborted.Dispose();
        client.Shutdown();
        client.Dispose();
    }

    static void WaitForBlockedDrainer(UgoClient client)
    {
        var events = new UgoEvent[8];
        long deadline = UgoClient.MonotonicMicroseconds + 5_000_000;
        var spin = new SpinWait();
        while (UgoClient.MonotonicMicroseconds < deadline)
        {
            try { client.TryWaitEvents(events, 0, out _); }
            catch (InvalidOperationException) { return; }
            spin.SpinOnce();
        }
        throw new TimeoutException("Drainer did not enter the native wait.");
    }

    [Fact]
    public void IsDisposedTracksLifetimeWithoutAllocating()
    {
        using UgoClient client = Manual();
        for (int phase = 0; phase < 3; phase++)
        {
            Assert.Equal(phase == 2, client.IsDisposed);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int disposed = 0;
            for (int i = 0; i < 10000; i++)
                if (client.IsDisposed) disposed++;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(phase == 2 ? 10000 : 0, disposed);
            if (phase == 0) client.Shutdown();
            else client.Dispose();
        }
    }

    [Fact]
    public void IsDisposedAfterPlayModeReclaim()
    {
        using UgoClient client = Manual();
        Assert.False(client.IsDisposed);
        client.Reclaim();
        Assert.True(client.IsDisposed);
    }

    [Fact]
    public async Task IsDisposedIsReadableWhileDisposeWaitsForCallback()
    {
        using UgoClient client = Background();
        using var inCallback = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        Assert.Equal(UgoSubmitStatus.Accepted, client.Send(new UgoRequest(bench.Url + "/ok"), response =>
        {
            inCallback.Set();
            releaseCallback.Wait();
            response.Dispose();
        }));
        Task disposing = null;
        try
        {
            Assert.True(inCallback.Wait(Limit));
            disposing = Task.Run(client.Dispose);
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => client.IsDisposed, Limit)).WaitAsync(Limit));
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
            if (disposing != null) await disposing.WaitAsync(Limit);
        }
        Assert.True(client.IsDisposed);
    }

    [Fact]
    public async Task ConcurrentShutdownAndDisposeTolerateClosedHandle()
    {
        UgoClient client = Background();
        Task<UgoResponse> pending = client.SendAsync(new UgoRequest(bench.Url + "/stall-after-headers")).AsTask();
        Task[] tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 32; j++)
                {
                    client.Shutdown();
                    client.Dispose();
                }
            });
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UgoErrorKind.Aborted, (await pending.WaitAsync(Limit)).Error.Kind);
        client.Shutdown();
        client.Dispose();
    }

    [Fact]
    public async Task DomainShutdownToleratesConcurrentDisposeAndRejectsCreate()
    {
        var context = new AssemblyLoadContext("domain-shutdown-test", true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(UgoClient).Assembly.Location);
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "ugo"
                ? NativeLibrary.Load(Paths.Library) : IntPtr.Zero);
            Type clientType = assembly.GetType("UgoHttp.UgoClient", true);
            Type optionsType = assembly.GetType("UgoHttp.UgoClientOptions", true);
            var create = clientType.GetConstructor(new[] { optionsType.MakeByRefType() });
            var dispose = clientType.GetMethod("Dispose");
            var isDisposed = clientType.GetProperty("IsDisposed");
            var shutdownDomain = clientType.GetMethod("ShutdownForDomainUnload", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.Null(clientType.GetMethod("ShutdownForDomainUnload", BindingFlags.Public | BindingFlags.Static));
            object[] clients = new object[32];
            object options = Activator.CreateInstance(optionsType, new object[] { 1 });
            object domainOnly = create.Invoke(new[] { options });
            Assert.False((bool)isDisposed.GetValue(domainOnly));
            for (int i = 0; i < clients.Length; i++) clients[i] = create.Invoke(new[] { options });
            using var start = new ManualResetEventSlim();
            Task disposing = Task.Run(() =>
            {
                start.Wait();
                foreach (object client in clients) dispose.Invoke(client, null);
            });
            Task closing = Task.Run(() => { start.Wait(); shutdownDomain.Invoke(null, null); });
            start.Set();
            await Task.WhenAll(disposing, closing).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((bool)isDisposed.GetValue(domainOnly));
            shutdownDomain.Invoke(null, null);
            var rejected = Assert.Throws<TargetInvocationException>(() => create.Invoke(new[] { options }));
            Assert.IsType<InvalidOperationException>(rejected.InnerException);
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void ManualDrainPathsAllocateZero()
    {
        Assert.False(UgoTracking.Enabled);
        using UgoClient client = Manual();
        var request = new UgoRequest(bench.Url + "/big/8192") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(new byte[128]) };
        Span<UgoEvent> events = stackalloc UgoEvent[8];
        Span<byte> header = stackalloc byte[] { (byte)'x', (byte)'-', (byte)'b', (byte)'o', (byte)'d', (byte)'y' };
        long allocated = 0;
        int seen = 0, delivered = 0;
        int callbackStatus = 0;
        Action<UgoResponse, object> callback = (response, state) =>
        {
            callbackStatus = response.StatusCode;
            _ = response.Body.Length;
            response.Dispose();
        };
        UgoEventHandler handler = (in UgoEvent e, object state) =>
        {
            if (e.Kind == UgoEventKind.Progress) _ = e.Progress.BytesReceived;
        };
        for (int iteration = 0; iteration < 32; iteration++)
        {
            bool raw = iteration % 2 == 0;
            UgoId id = default;
            if (raw) Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out id));
            else
            {
                callbackStatus = 0;
                Assert.Equal(UgoSubmitStatus.Accepted, client.Send(in request, callback, null, handler));
            }
            bool done = false;
            long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (!done && UgoClient.MonotonicMicroseconds < deadline)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Assert.True(client.TryWaitEvents(events, 1000, out int count));
                for (int i = 0; i < count; i++)
                {
                    switch (events[i].Kind)
                    {
                        case UgoEventKind.Headers: _ = events[i].Headers.StatusCode; break;
                        case UgoEventKind.Progress: _ = events[i].Progress.BytesReceived; break;
                        case UgoEventKind.Completed:
                            Assert.True(client.TryGetResponse(id, out UgoResponse response));
                            _ = response.Timings.TotalMicroseconds;
                            _ = response.TryGetHeader(header, out _);
                            response.Dispose();
                            done = true;
                            break;
                    }
                }
                if (!raw && callbackStatus != 0) { done = true; delivered++; }
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (iteration > 1) { allocated += delta; seen += count; }
            }
            Assert.True(done);
        }
        output.WriteLine("Manual drain: {0} managed bytes across {1} raw events and {2} callbacks.", allocated, seen, delivered);
        Assert.True(seen >= 30);
        Assert.Equal(16, delivered);
        Assert.Equal(0, allocated);
    }
    [Theory]
    [InlineData(UgoDrainMode.Background)]
    [InlineData(UgoDrainMode.Manual)]
    public async Task AwaitableSlotIsReusedAfterEachResponse(UgoDrainMode mode)
    {
        using var client = new UgoClient(new UgoClientOptions(1) { DrainMode = mode });
        for (int i = 0; i < 8; i++)
        {
            ValueTask<UgoResponse> pending = client.SendAsync(new UgoRequest(bench.Url + "/ok"));
            if (mode == UgoDrainMode.Manual) DrainUntil(client, () => pending.IsCompleted);
            using UgoResponse response = await pending.AsTask().WaitAsync(Limit);
            Assert.Equal(0, response.Id.Slot);
            Assert.Equal("UgoHTTP fault bench\n", response.GetBodyAsString());
        }
    }

    [Fact]
    public void RawEventsAreCompactedPastOwnedOnes()
    {
        bool mixed = false;
        for (int attempt = 0; attempt < 20 && !mixed; attempt++)
        {
            using UgoClient client = Manual();
            var request = new UgoRequest(bench.Url + "/stall-after-headers");
            bool delivered = false;
            Assert.Equal(UgoSubmitStatus.Accepted, client.Send(in request, (response, state) => { delivered = true; response.Dispose(); }, null));
            Assert.Equal(UgoSubmitStatus.Accepted, client.Submit(in request, out UgoId raw));
            client.Shutdown();
            var events = new UgoEvent[64];
            bool rawDone = false;
            while (true)
            {
                Array.Clear(events);
                if (!client.TryWaitEvents(events, 0, out int count)) break;
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal(raw, events[i].Id);
                    if (events[i].Kind == UgoEventKind.Completed) rawDone = true;
                }
                for (int i = count; i < events.Length; i++)
                    if (events[i].Id == raw) mixed = true;
            }
            Assert.True(rawDone && delivered);
            Assert.True(client.TryGetResponse(raw, out UgoResponse rawResponse));
            using (rawResponse) Assert.Equal(UgoErrorReason.Shutdown, rawResponse.Error.Reason);
        }
        Assert.True(mixed, "No batch had an owned event before a raw one.");
    }

    [Fact]
    public async Task SecondDrainDuringDispatchIsRejected()
    {
        using UgoClient client = Manual();
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new List<string>();
        Exception reentrant = null;
        Task<UgoResponse> pending = client.SendAsync(new UgoRequest(bench.Url + "/stall-after-headers"), (in UgoEvent e, object state) =>
        {
            if (e.Kind != UgoEventKind.Headers) return;
            lock (order) order.Add("headers");
            try { client.TryWaitEvents(new UgoEvent[4], 0, out _); }
            catch (Exception error) { reentrant = error; }
            inHandler.Set();
            release.Wait();
            lock (order) order.Add("headers-done");
        }).AsTask();
        _ = pending.ContinueWith(_ => { lock (order) order.Add("completed"); }, TaskContinuationOptions.ExecuteSynchronously);
        Task<bool> first = Task.Run(() => client.TryWaitEvents(new UgoEvent[1], 10_000, out _));
        Assert.True(inHandler.Wait(Limit));
        Assert.IsType<InvalidOperationException>(reentrant);
        client.Shutdown();
        Assert.Throws<InvalidOperationException>(() => client.TryWaitEvents(new UgoEvent[4], 0, out _));
        Assert.False(pending.IsCompleted);
        release.Set();
        Assert.True(await first.WaitAsync(Limit));
        DrainUntil(client, () => pending.IsCompleted);
        using UgoResponse response = await pending.WaitAsync(Limit);
        Assert.Equal(UgoErrorReason.Shutdown, response.Error.Reason);
        lock (order) Assert.Equal(new[] { "headers", "headers-done", "completed" }, order);
    }

    [Fact]
    public async Task DisposeInsideABatchDeliversTheRealResult()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            UgoClient client = Manual();
            bool finishedInBatch = false;
            bool disposed = false;
            int afterDispose = 0;
            Task<UgoResponse> pending = client.SendAsync(new UgoRequest(bench.Url + "/ok"), (in UgoEvent e, object state) =>
            {
                if (disposed) afterDispose++;
                if (e.Kind != UgoEventKind.Headers) return;
                try { client.GetResult(e.Id.Value, nameof(UgoResponse)); finishedInBatch = true; }
                catch (InvalidOperationException) { return; }
                client.Dispose();
                disposed = true;
            }).AsTask();
            Thread.Sleep(20);
            DrainUntil(client, () => pending.IsCompleted);
            UgoResponse response = await pending.WaitAsync(Limit);
            client.Dispose();
            if (!finishedInBatch) continue;
            Assert.True(response.IsSuccess);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal(20, response.BytesReceived);
            Assert.Equal(0, afterDispose);
            Assert.Throws<ObjectDisposedException>(() => response.Body.Length);
            Assert.False(client.TryWaitEvents(new UgoEvent[1], 0, out _));
            return;
        }
        throw new InvalidOperationException("Headers and completion never arrived in one batch.");
    }

    [Fact]
    public void ReentrantDisposeDoesNotReturnOwnedEventsAsRaw()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            using UgoClient client = Manual(1);
            bool disposedInHeaders = false, success = false;
            int called = 0, status = 0;
            long received = 0;
            Assert.Equal(UgoSubmitStatus.Accepted, client.Send(new UgoRequest(bench.Url + "/ok"), (response, state) =>
            {
                called++;
                success = response.IsSuccess;
                status = response.StatusCode;
                received = response.BytesReceived;
                response.Dispose();
            }, null, (in UgoEvent e, object state) =>
            {
                if (e.Kind != UgoEventKind.Headers) return;
                try { client.GetResult(e.Id.Value, nameof(UgoResponse)); }
                catch (InvalidOperationException) { return; }
                client.Dispose();
                disposedInHeaders = true;
            }));
            Thread.Sleep(20);
            var events = new UgoEvent[8];
            long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
            while (called == 0)
            {
                Assert.True(client.TryWaitEvents(events, 100, out int count));
                Assert.Equal(0, count);
                if (UgoClient.MonotonicMicroseconds > deadline) throw new TimeoutException("Completion did not arrive.");
            }
            if (!disposedInHeaders) continue;
            Assert.Equal(1, called);
            Assert.True(success);
            Assert.Equal(200, status);
            Assert.Equal(20, received);
            return;
        }
        throw new InvalidOperationException("Headers and completion never arrived in one batch.");
    }

    [Fact]
    public async Task ManualDrainLoopReturnsFalseDuringConcurrentDispose()
    {
        using UgoClient client = Manual();
        using var batchReturned = new ManualResetEventSlim();
        using var resumeDrain = new ManualResetEventSlim();
        using var inCompletion = new ManualResetEventSlim();
        using var releaseCompletion = new ManualResetEventSlim();
        int called = 0;
        Exception reentrant = null;
        Action<UgoResponse, object> completed = (response, state) =>
        {
            Interlocked.Increment(ref called);
            try { client.TryWaitEvents(new UgoEvent[1], 0, out _); }
            catch (Exception error) { reentrant = error; }
            inCompletion.Set();
            releaseCompletion.Wait();
            response.Dispose();
        };
        var request = new UgoRequest(bench.Url + "/stall-after-headers");
        Assert.Equal(UgoSubmitStatus.Accepted, client.Send(in request, completed, null));
        Assert.Equal(UgoSubmitStatus.Accepted, client.Send(in request, completed, null));
        Task worker = Task.Run(() =>
        {
            var events = new UgoEvent[1];
            while (client.TryWaitEvents(events, 1000, out _))
            {
                batchReturned.Set();
                resumeDrain.Wait();
            }
        });
        Task disposing = null;
        try
        {
            Assert.True(batchReturned.Wait(Limit));
            disposing = Task.Run(client.Dispose);
            Assert.True(inCompletion.Wait(Limit));
            resumeDrain.Set();
            await worker.WaitAsync(Limit);
        }
        finally
        {
            resumeDrain.Set();
            releaseCompletion.Set();
            if (disposing != null) await disposing.WaitAsync(Limit);
            await worker.WaitAsync(Limit);
        }
        Assert.Equal(2, called);
        Assert.IsType<InvalidOperationException>(reentrant);
    }

    [Theory]
    [InlineData(UgoDrainMode.Background)]
    [InlineData(UgoDrainMode.Manual)]
    public async Task DisposeFromCallbackAndContinuation(UgoDrainMode mode)
    {
        var client = new UgoClient(new UgoClientOptions(4) { DrainMode = mode });
        Task<UgoResponse> stalled = client.SendAsync(new UgoRequest(bench.Url + "/stall-after-headers")).AsTask();
        var called = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(UgoSubmitStatus.Accepted, client.Send(new UgoRequest(bench.Url + "/ok"), response =>
        {
            int status = response.StatusCode;
            client.Dispose();
            response.Dispose();
            called.SetResult(status);
        }));
        Task<int> drained = mode == UgoDrainMode.Manual ? Task.Run(() => DrainToEnd(client)) : Task.FromResult(0);
        Assert.Equal(200, await called.Task.WaitAsync(Limit));
        UgoResponse aborted = await stalled.WaitAsync(Limit);
        Assert.Equal(UgoErrorReason.Shutdown, aborted.Error.Reason);
        await drained.WaitAsync(Limit);
        Assert.Throws<ObjectDisposedException>(() => client.Send(new UgoRequest(bench.Url + "/ok"), _ => { }));

        client = new UgoClient(new UgoClientOptions(4) { DrainMode = mode });
        UgoClient captured = client;
        Task continued = Task.Run(async () =>
        {
            using (UgoResponse response = await captured.SendAsync(new UgoRequest(bench.Url + "/ok"))) Assert.True(response.IsSuccess);
            captured.Dispose();
        });
        drained = mode == UgoDrainMode.Manual ? Task.Run(() => DrainToEnd(captured)) : Task.FromResult(0);
        await continued.WaitAsync(Limit);
        await drained.WaitAsync(Limit);
        captured.Dispose();
    }

    static int DrainToEnd(UgoClient client)
    {
        var events = new UgoEvent[8];
        long deadline = UgoClient.MonotonicMicroseconds + 10_000_000;
        while (client.TryWaitEvents(events, 100, out _))
            if (UgoClient.MonotonicMicroseconds > deadline) throw new TimeoutException("Drain did not end.");
        return 0;
    }

    [Fact]
    public async Task ConsumedAwaitReleasesItsContinuation()
    {
        using UgoClient client = Background(1);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        WeakReference payload = AwaitWithPayload(client, done);
        await done.Task.WaitAsync(Limit);
        long deadline = UgoClient.MonotonicMicroseconds + 2_000_000;
        while (payload.IsAlive && UgoClient.MonotonicMicroseconds < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(10);
        }
        Assert.False(payload.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    WeakReference AwaitWithPayload(UgoClient client, TaskCompletionSource<bool> done)
    {
        var payload = new byte[1024];
        ValueTaskAwaiter<UgoResponse> awaiter = client.SendAsync(new UgoRequest(bench.Url + "/ok")).GetAwaiter();
        awaiter.OnCompleted(Consume(payload, awaiter, done));
        return new WeakReference(payload);
    }

    static Action Consume(byte[] payload, ValueTaskAwaiter<UgoResponse> awaiter, TaskCompletionSource<bool> done) => () =>
    {
        GC.KeepAlive(payload);
        awaiter.GetResult().Dispose();
        done.SetResult(true);
    };

    [Fact]
    public async Task CallbacksRunInTheDrainThreadContextNotTheSenders()
    {
        using UgoClient client = Background();
        var flowing = new AsyncLocal<string>();
        var seen = new List<string>();
        var both = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<UgoResponse> completed = response =>
        {
            response.Dispose();
            lock (seen)
            {
                seen.Add(flowing.Value ?? "none");
                if (seen.Count == 2) both.SetResult(true);
            }
        };
        await Task.Run(() =>
        {
            flowing.Value = "first-sender";
            Assert.Equal(UgoSubmitStatus.Accepted, client.Send(new UgoRequest(bench.Url + "/ok"), completed));
        });
        await Task.Run(() =>
        {
            flowing.Value = "second-sender";
            Assert.Equal(UgoSubmitStatus.Accepted, client.Send(new UgoRequest(bench.Url + "/ok"), completed));
        });
        await both.Task.WaitAsync(Limit);
        Assert.Equal(new[] { "none", "none" }, seen);
    }

    [Fact]
    public async Task EventHandlerGetsHeadersProgressAndState()
    {
        using UgoClient client = Background();
        object token = new object();
        int progress = 0, headers = 0;
        object seenState = null;
        string length = null;
        int fields = 0;
        UgoEventHandler handler = (in UgoEvent e, object state) =>
        {
            seenState = state;
            if (e.Kind == UgoEventKind.Progress && e.Progress.BytesReceived > 0) progress++;
            if (e.Kind != UgoEventKind.Headers) return;
            headers++;
            UgoHeadersEvent h = e.Headers;
            if (h.TryGetHeader(Encoding.ASCII.GetBytes("content-length"), out ReadOnlySpan<byte> value)) length = Encoding.ASCII.GetString(value);
            foreach (UgoHeaderField field in h.EnumerateHeaders()) fields++;
        };
        var request = new UgoRequest(bench.Url + "/big/65536") { TotalTimeout = Timeout.InfiniteTimeSpan };
        using (UgoResponse response = await client.SendAsync(in request, handler, token).AsTask().WaitAsync(Limit)) Assert.True(response.IsSuccess);
        Assert.Equal(1, headers);
        Assert.True(progress >= 1);
        Assert.Same(token, seenState);
        Assert.Equal("65536", length);
        Assert.True(fields > 0);
    }

    [Theory]
    [InlineData(UgoDrainMode.Background)]
    [InlineData(UgoDrainMode.Manual)]
    public void SendAndSendAsyncAllocateZeroWithoutAToken(UgoDrainMode mode)
    {
        Assert.False(UgoTracking.Enabled);
        using var client = new UgoClient(new UgoClientOptions(2) { DrainMode = mode });
        var request = new UgoRequest(bench.Url + "/ok") { Method = HttpMethod.Post, Body = UgoRequestBody.FromBytes(new byte[64]),
            Headers = new[] { new UgoHeader("X-A", "1") } };
        var done = new SemaphoreSlim(0);
        Action<UgoResponse, object> callback = (response, state) => { response.Dispose(); ((SemaphoreSlim)state).Release(); };
        long allocated = 0;
        for (int i = 0; i < 12; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            UgoSubmitStatus status = client.Send(in request, callback, done);
            ValueTask<UgoResponse> pending = client.SendAsync(in request);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(UgoSubmitStatus.Accepted, status);
            if (mode == UgoDrainMode.Manual) DrainUntil(client, () => pending.IsCompleted && done.CurrentCount > 0);
            Assert.True(done.Wait(Limit));
            Assert.True(SpinWait.SpinUntil(() => pending.IsCompleted, Limit));
            using (UgoResponse response = pending.Result) Assert.True(response.IsSuccess);
            if (i >= 4) allocated += delta;
        }
        output.WriteLine("{0}: {1} managed bytes across 8 Send + 8 SendAsync submits.", mode, allocated);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task DomainUnloadWithCallbackCreatingAClientDoesNotDeadlock()
    {
        var context = new IsolatedContext();
        try
        {
            Assembly tests = context.LoadFromAssemblyName(typeof(IsolatedScenarios).Assembly.GetName());
            MethodInfo run = tests.GetType(nameof(IsolatedScenarios), true).GetMethod(nameof(IsolatedScenarios.UnloadWhileCallbackCreatesClient));
            Task<string> scenario = Task.Run(() => (string)run.Invoke(null, new object[] { bench.Url }));
            Assert.Equal("rejected", await scenario.WaitAsync(Limit));
        }
        finally { context.Unload(); }
    }
}

sealed class IsolatedContext : AssemblyLoadContext
{
    public IsolatedContext() : base("ugo-isolated", true) { }
    protected override Assembly Load(AssemblyName name) =>
        name.Name == "UgoHttp" || name.Name == "UgoHttp.Tests" ? LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll")) : null;
}

public static class IsolatedScenarios
{
    public static string UnloadWhileCallbackCreatesClient(string url)
    {
        NativeLibrary.SetDllImportResolver(typeof(UgoClient).Assembly, (name, _, _) => name == "ugo"
            ? NativeLibrary.Load(Paths.Library) : IntPtr.Zero);
        var client = new UgoClient(new UgoClientOptions(2));
        using var inCallback = new ManualResetEventSlim();
        string outcome = null;
        UgoEventHandler events = (in UgoEvent e, object state) => { if (e.Kind == UgoEventKind.Headers) inCallback.Set(); };
        client.Send(new UgoRequest(url + "/stall-after-headers"), (response, state) =>
        {
            response.Dispose();
            try { new UgoClient(new UgoClientOptions(1)).Dispose(); outcome = "created"; }
            catch (InvalidOperationException) { outcome = "rejected"; }
        }, null, events);
        if (!inCallback.Wait(10_000)) return "no headers";
        UgoClient.ShutdownForDomainUnload();
        return outcome;
    }
}
