# UgoHTTP

Lightweight HTTP and WebSocket client for Unity, built on libcurl. Designed mobile-first: Android and iOS set the size, memory and CPU budget; macOS, Windows and Linux run the same core.

Written because no existing Unity client combined few allocations, a small binary and native speed, and because Unity will be Unity.[^dns]

[Install](#install) · [HTTP](#http) · [Downloads](#downloads) · [Uploads](#uploads) · [WebSocket](#websocket) · [Benchmarks](#benchmarks) · [Documentation](#documentation)

- **Independent of the PlayerLoop.** Network IO, TLS and file writes run on native threads.
- **Low GC pressure.** No managed allocations per body chunk or progress event.
- **Direct file downloads.** Write to disk without passing the body through managed memory, with opt-in resume.
- **Async/await or callbacks.** Works in players, Editor tools outside Play Mode and plain .NET.

**Vibe-coded. Not vibe-designed.**

## Benchmarks

Xiaomi 24129PN74G · Android 16 · Unity IL2CPP release · local HTTPS through USB.

<p>
<img src="docs/images/benchmark-download.svg" width="420" alt="Download throughput: UgoHTTP 508, UnityWebRequest 417, YetAnotherHttpHandler 386, HttpClient (Mono) 63.8, Best HTTP 49.0 MiB/s. 1.2× higher throughput than the next best, UnityWebRequest.">
<img src="docs/images/benchmark-api.svg" width="420" alt="Total time for 100 requests: UgoHTTP 104, YetAnotherHttpHandler 273, UnityWebRequest 837, Best HTTP 859, HttpClient (Mono) 9,327 ms. 2.6× faster completion than the next best, YetAnotherHttpHandler.">
</p>
<p>
<img src="docs/images/benchmark-cpu.svg" width="420" alt="Process CPU time during the download run: UgoHTTP 180, UnityWebRequest 236, YetAnotherHttpHandler 524, Best HTTP 2,747, HttpClient (Mono) 3,608 ms. 1.3× less CPU time than the next best, UnityWebRequest.">
<img src="docs/images/benchmark-allocations.svg" width="420" alt="Managed objects allocated during the download run: UgoHTTP 46, UnityWebRequest 752, YetAnotherHttpHandler 42,100, Best HTTP 518k, HttpClient (Mono) 644k. 16.3× fewer than the next best, UnityWebRequest. Logarithmic scale.">
</p>

Medians of three measured runs after warm-up; whiskers show min–max. On this phone UgoHTTP leads all four tests: 1.2× higher download throughput, 2.6× faster API completion, 1.3× less CPU time and 16.3× fewer managed objects than the next best client in each test. Client configurations differ; see the conditions below.

<details>
<summary>Test conditions and limitations</summary>

- Measured November 6, 2026, with Unity 6000.3.25f1, IL2CPP arm64. Clients: UgoHTTP 0.2.2, UnityWebRequest from Unity 6000.3.25f1, Best HTTP 3.1.0, YetAnotherHttpHandler 1.11.7 and `System.Net.Http.HttpClient` with Unity's default handler. All five in one release APK; a fresh process for each run, with client order reversed every other round.
- Downloads: five parallel 20 MiB files. API burst: 100 GETs of 1–4 KiB, eight in flight. CPU: process CPU time for the whole download run. Allocations: managed object count for the download run, measured with the IL2CPP allocation counter; the axis is logarithmic. Each client is created before the measured run and its creation measured separately: UgoHTTP 32 objects and 2.6 ms CPU (median for the download tests); HttpClient 51 objects; YetAnotherHttpHandler 7. UnityWebRequest and Best HTTP have no client object and initialise on the first request, inside the run.
- HTTPS to a local bench through `adb reverse` over USB. This measures the clients and the tunnel, not internet download speed; the same app read plain HTTP through the tunnel at 609.4 MiB/s (best of three). Protocols and TCP connection counts differ: UgoHTTP opened five connections for downloads and eight for the API burst; UnityWebRequest multiplexed the downloads over one connection but opened 7–13 for the API burst; YetAnotherHttpHandler used one for both; Best HTTP opened one or five for downloads and one or six for the API burst; HttpClient speaks only HTTP/1.1 and opened five and eight.
- UgoHTTP and YetAnotherHttpHandler verified the test CA; UnityWebRequest and HttpClient accepted the test certificate through a custom callback; Best HTTP ran without its TLS verification add-on. UgoHTTP used the low-level `Submit` API, reading events by hand on a background thread; UnityWebRequest and Best HTTP completed on the main thread; both `HttpClient` setups ran `async` workers on the thread pool with `ConfigureAwait(false)`, copying the response stream to a file in 64 KiB chunks. HttpClient's per-host connection limit was raised from 2 to 64. Best HTTP caching was disabled and file downloads used `BlockingDownloadContentStream` copied to a file on a background task.
- Results depend on the phone: on a Pixel 7a (Android 17), with this build and the same method (medians of three, client creation outside the run), UgoHTTP downloaded at 206 MiB/s against UnityWebRequest's 125 MiB/s and used 480 against 815 ms of CPU; the plain-HTTP tunnel ceiling there was 236.4 MiB/s (best of three).
- Memory, measured without a warm-up run: creating the first UgoHTTP client grew RSS by about 1 MiB. About 930 KiB of it is a once-per-process cost: IL2CPP loads `libugo.so` on the first call, and its pages, almost all clean and reclaimable, count as resident; each further client adds 50–130 KiB depending on `OperationSlots`. UnityWebRequest has no client object and its code is already resident in the engine. After eight parallel 2 MiB downloads and 3 s idle, median RSS growth was 352 KiB for UgoHTTP and 820 KiB for UnityWebRequest. Native library size on android-arm64: UgoHTTP 1.17 MB, YetAnotherHttpHandler 3.26 MB; UnityWebRequest and HttpClient ship inside the engine.

</details>

## Install

Unity 6000.3 or newer, IL2CPP or Mono. Android, iOS, macOS, Windows and Linux; Nintendo Switch through a separate package (below). WebGL and other consoles are not supported. See [platforms and minimum OS versions](docs/contract.md#platforms).

Add to `dependencies` in `Packages/manifest.json`, pinned to a release tag of the `upm` branch:

```json
"com.ugohttp.core": "https://github.com/zudl/UgoHTTP.git?path=package#v0.3.1"
```

<details>
<summary>Android platform note</summary>

Android Network Security Config does not block UgoHTTP's `http://` requests; see [TLS and trust](docs/architecture.md#tls-and-trust).

</details>

<details>
<summary>Nintendo Switch</summary>

The native library for Switch ships in a separate package, `com.ugohttp.switch`, built against the NintendoSDK. It is not public because of the Nintendo NDA. The C# API is the same; install it next to `com.ugohttp.core` of the version it names.

</details>

## HTTP

```csharp
using UgoHttp;
using UnityEngine;

var client = new UgoClient();

using UgoResponse response = await client.SendAsync(new UgoRequest("https://example.com/config.json"));

if (response.IsSuccess)
{
    Debug.Log(response.GetBodyAsString());
}
else
{
    Debug.LogWarning($"{response.Error.Kind} {response.StatusCode}: {response.GetErrorMessage()}");
}
```

Failures are responses, not exceptions: an HTTP error status, a transport failure or an abort completes with `IsSuccess` false and `Error.Kind` set. To abort, cancel the `CancellationToken` passed to `SendAsync` or call `Abort`; the request still completes, as `UgoErrorKind.Aborted` unless it already finished. Only a request rejected before submission throws, for example when every operation slot of the client is held; see the XML docs.

A client has a fixed table of operation slots (`UgoClientOptions.OperationSlots`, 64 by default). A request holds a slot from submit until its response is disposed, a WebSocket until it is disposed. With every slot held, a submit throws `UgoSubmitException` (`NoCapacity`) at once; nothing is queued. The table bounds memory, not network concurrency: limit that with `MaxConnections` or your own gate. `NoCapacity` under steady load usually means a response or socket that was never disposed; **Window → UgoHTTP → Tracker** lists what holds each slot.

By default the body is kept in native memory, up to `MaxResponseBodyBytes` (16 MiB); `response.Body` is a `ReadOnlySpan<byte>` over it, no copy, valid until the response is disposed.

One client per app or subsystem; dispose it on shutdown: in the default mode a started client keeps a thread until `Shutdown` or `Dispose`. Every response must be disposed: its body and headers live in native memory until then. In Unity, clients are disposed for you on domain reload, app quit and, for a client created in Play Mode, on leaving it: recreate a shared client when `client == null || client.IsDisposed`.

By default completions and events are delivered from the client's own background thread; with `UgoDrainMode.Manual`, from `TryWaitEvents` on the thread that calls it. Awaited on the Unity main thread, the continuation returns there; awaited without a synchronization context, it may run on the delivering thread. Callbacks always run there; marshal Unity object updates to the main thread.

### POST

```csharp
using System.Net.Http;
using System.Text;

var request = new UgoRequest("https://example.com/api/scores")
{
    Method = HttpMethod.Post,
    Headers = new[] { new UgoHeader("Authorization", $"Bearer {token}") },
    Body = UgoRequestBody.FromBytes(Encoding.UTF8.GetBytes(json), "application/json"),
};

using UgoResponse response = await client.SendAsync(request);
```

### Timeouts

A request has a 10 s connect timeout and nothing else by default: once connected, it waits for as long as the server stays silent. Set a bound that fits the call:

```csharp
var request = new UgoRequest("https://example.com/api/profile")
{
    ConnectTimeout = TimeSpan.FromSeconds(5),
    TotalTimeout = TimeSpan.FromSeconds(15),
};
```

`TotalTimeout` covers the whole request, so it does not suit downloads of unknown size. For those, set `StallTimeout`: the request fails when the transfer stays stalled for that long, a server that never answers included.

```csharp
var request = new UgoRequest("https://example.com/content.bin")
{
    ResponseFilePath = partPath,
    StallTimeout = TimeSpan.FromSeconds(30),
};
```

<details>
<summary>Advanced: minimum speed and speed caps</summary>

"Stalled" means less than one byte per second, request and response body together, measured once connected. The speed is an average over about the last 5 seconds, so silence after an active transfer is noticed about 5 seconds later than `StallTimeout`, and a byte every few seconds does not count as progress.

To give up on a host that answers but too slowly, raise that bar with `MinimumSpeedBytesPerSecond`: the request fails when the speed stays below it for `StallTimeout`.

```csharp
var request = new UgoRequest("https://example.com/content.bin")
{
    ResponseFilePath = partPath,
    StallTimeout = TimeSpan.FromSeconds(30),
    MinimumSpeedBytesPerSecond = 16 * 1024,
    MaxReceiveBytesPerSecond = 2 * 1024 * 1024,
};
```

`MaxReceiveBytesPerSecond` and `MaxSendBytesPerSecond` cap the transfer speed, for example to keep a background download from starving gameplay traffic. A capped transfer is measured at its capped speed: keep the minimum speed below any cap you set.

</details>

Every timeout ends the request as `UgoErrorKind.Timeout`.

### Errors

`response.Error.Kind` says what failed; the library never retries, so the decision is yours:

| `Error.Kind` | Meaning | Retry |
|---|---|---|
| `Dns`, `Connect`, `Timeout` | The network or the host did not answer; `response.Timings` shows how far it got | Yes, with backoff |
| `Transfer` | The connection broke mid-transfer; the server may have processed the request | Yes if the request is idempotent |
| `HttpStatus` | The server answered with a non-2xx status; `StatusCode`, headers and body are there | By status: 429 and 5xx usually, other 4xx not |
| `Tls` | Handshake or certificate verification failed | No |
| `LocalIO` | A local file could not be read or written, for example a full disk | No |
| `Aborted` | You aborted it, or the client shut down; `Error.Reason` says which | No |

<details>
<summary>Callback form</summary>

A status other than `Accepted` means the request was not sent and the callback never runs: A status other than `Accepted` means the request was not sent and the callback never runs:

```csharp
UgoSubmitStatus status = client.Send(new UgoRequest("https://example.com/config.json"), response =>
{
    using (response)
    {
        Debug.Log(response.StatusCode);
    }
});

if (status != UgoSubmitStatus.Accepted)
{
    Debug.LogWarning(status);
}
```

</details>

<details>
<summary>HTTP behavior</summary>

HTTP/1.1 and HTTP/2, custom headers, byte/file/multipart request bodies and transparent gzip response decoding. A caller-provided `Accept-Encoding` header or a resumed download gets the body as received.

Retries, host failover, application deadlines and caching are left to the caller. Headers and progress events reach the optional `events` handler of `Send` and `SendAsync`, before completion, on the delivering thread. See the [HTTP contract](docs/contract.md#http) for the full behavior.

</details>

## Downloads

Download straight to a file; `Resume` continues from the bytes already there and keeps partial data on failure, unless the server's reply contradicts the file (`UgoErrorReason.ResumeMismatch`).

```csharp
var request = new UgoRequest("https://example.com/content.bin")
{
    ResponseFilePath = partPath,
    Resume = true,
};

using UgoResponse download = await client.SendAsync(request);

if (!download.IsSuccess)
{
    Debug.LogWarning(download.GetErrorMessage());
}
```

Progress arrives through the optional `events` handler, at most once every 10 ms per request and always with the final counts before completion. It runs on the delivering thread, not the Unity main thread: store the value and read it from your UI code.

```csharp
using UgoResponse download = await client.SendAsync(request, (in UgoEvent e, object state) =>
{
    if (e.Kind == UgoEventKind.Progress && e.Progress.TotalBytesToReceive > 0)
    {
        Volatile.Write(ref progress, (float)e.Progress.BytesReceived / e.Progress.TotalBytesToReceive);
    }
});
```

Use a unique partial path; validate and move the file into place after success. For content that can change at the same URL, use an `If-Range` validator; see the [file and resume contract](docs/contract.md#file-sink).

On iOS, set `FinishInBackground = true` to give an in-flight request time to finish after the app leaves the foreground. iOS may expire that time and abort the request, and the request does not survive app suspension.

## Uploads

A file body is read natively when sent, without passing through managed memory:

```csharp
var request = new UgoRequest("https://example.com/replays")
{
    Method = HttpMethod.Put,
    Body = UgoRequestBody.FromFile(replayPath, "application/octet-stream"),
};

using UgoResponse response = await client.SendAsync(request);
```

`multipart/form-data`, with text fields and files in one request:

```csharp
var request = new UgoRequest("https://example.com/support/tickets")
{
    Method = HttpMethod.Post,
    Body = UgoRequestBody.FromParts(new[]
    {
        UgoFormPart.FromText("subject", subject),
        UgoFormPart.FromFile("log", logPath, "player.log", "text/plain"),
        UgoFormPart.FromBytes("screenshot", png, "screen.png", "image/png"),
    }),
};
```

Bytes passed to `FromBytes` are borrowed: keep them alive and unchanged until the request completes. `UgoRequestBody.FromForm` sends `application/x-www-form-urlencoded` fields. Upload progress reaches the same `events` handler as `BytesSent` and `TotalBytesToSend`.

## WebSocket

Text and binary messages. Without a listener the socket is awaitable. Dispose each message: undisposed messages count against the receive backlog, and once it is full (`MaxBacklogMessages`, 64 by default, or `MaxBacklogBytes`, 4 MiB) the library stops reading and the server waits; nothing is dropped.

```csharp
using UgoWebSocket socket = client.OpenWebSocket(new UgoWebSocketRequest("wss://example.com/chat"));

if (!await socket.WaitOpenAsync())
{
    return;
}

socket.Send("hello");
socket.Send(new byte[] { 1, 2, 3 }, UgoWebSocketMessageType.Binary);

while (true)
{
    using UgoWebSocketMessage message = await socket.ReceiveAsync();
    if (message.IsEnd)
    {
        break;
    }

    if (message.MessageType == UgoWebSocketMessageType.Text)
    {
        Debug.Log(message.GetText());
    }
    else
    {
        Handle(message.Data);
    }
}
```

`Send` copies the message into the send buffer and returns at once; `BufferFull` or `NotOpen` means it was not queued, and the socket can already be closing right after `WaitOpenAsync`. `IsEnd` marks the end: the socket closed and every message was delivered. `Close()` starts the close handshake; `Dispose` aborts a socket still open and releases its messages.

Callback form: pass a `UgoWebSocketListener` subclass overriding `OnOpen`, `OnMessage` and `OnClosed` as the second argument of `OpenWebSocket`. A message is valid only inside `OnMessage`: copy what you keep.

## Leak tracker

Shows what your code still holds and what it forgot to dispose: clients, requests in flight, responses and WebSocket messages not yet disposed. Open **Window → UgoHTTP → Tracker**. **Group by Host** adds per-host totals, including new connections, which shows whether connections are reused.

- Rows in normal colour are alive, still held by your code. One that stays there after its work should have finished is a missing `Dispose`.
- **Closed**, **Aborted**, **Failed**, **Closed with client** — finished and released; the row stays greyed for 10 s. **Closed with client** means disposing the client released it, which is fine.
- **Reclaimed at Play Mode exit** — a leak: your code never disposed this client or operation, and the plugin freed it when Play Mode stopped. These rows stay until newer finished rows push them out.

To find where a leak comes from, turn on **View → Capture Stacks**: everything created after that records its creation stack, shown for the selected row with clickable source links. It costs a stack walk per operation and stays on between Editor sessions. **Pause** freezes the table; right-click a row or press Cmd/Ctrl+C to copy URLs, rows or stacks.

Tracking runs only in the Editor, in and outside Play Mode. Players never record anything: the cost there is a few flag checks per operation and no per-operation tracking allocations.

## Documentation

- [Supported platforms and behavior](docs/contract.md)
- [Threading, memory and TLS](docs/architecture.md)
- [Testing](docs/testing.md)
- [Changelog](package/CHANGELOG.md)

The public C# API is documented in XML comments. Contributors: [C ABI](docs/abi.md) · [Agent instructions](AGENTS.md).

## License

[MIT](LICENSE.md). Bundled dependencies and CA bundle: [Third Party Notices](package/Third%20Party%20Notices.md).

[^dns]: Starting with 8.16, curl caches failed DNS lookups too, for half the cache lifetime: 30 seconds by default. Until [8.22](https://github.com/curl/curl/pull/22302) it cached every failure, not only "no such host", so a request made while the phone is offline leaves the failure in the cache, and the host stays unresolvable for half a minute after the network comes back. On Android and desktop, UnityWebRequest runs on the libcurl that ships inside the engine. Unity moved it to 8.20 in [6000.3.21f1](https://unity.com/releases/editor/whats-new/6000.3.21f1), it is still 8.20 in 6000.3.28, and UnityWebRequest offers no way to change the cache. UgoHTTP ships a newer curl, and `UgoClientOptions.DnsCacheDuration` sets the cache lifetime per client; zero turns it off.
