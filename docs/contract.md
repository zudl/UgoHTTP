# Contract

What UgoHTTP must do. A gap between this document and the code is listed in `docs/debt.md` until it is closed.

Every requirement is met within the rails in `AGENTS.md`: light, direct, minimal allocations, nothing on the Unity main thread but enqueue and drain.

Scope: what the base specifications require of a client (RFC 9110/9112/9113 for HTTP, RFC 6455 for WebSocket) is in, implemented or listed as a gap. Optional features and protocol extensions are in only where this document names them; everything else is out (*Out of scope*). Policy (retries, host failover, reconnects, deadlines, caching, queueing beyond the operation bound) belongs to the caller.

## Platforms

Unity 6000.3+, IL2CPP and Mono scripting backends. IL2CPP is the stricter target (AOT, stripping) and the usual one for players; the Editor always runs Mono.

| Platform | Artifact | Constraint |
|---|---|---|
| Android | `.so` arm64-v8a, armeabi-v7a, x86_64 | API 25+; 16 KB ELF page alignment (Google Play) |
| iOS | XCFramework: device arm64, simulator arm64 + x86_64 | iOS 15.0+; static, `__Internal`; no symbol clashes with other SDKs |
| macOS | universal `.bundle` (arm64 + x86_64) | macOS 11.0+; Editor + standalone; signing/notarization for standalone |
| Windows | DLL x64, ARM64 | Windows 10+; Editor + standalone, both architectures |
| Linux | `.so` x64, ARM64 | glibc 2.17+; x64: Editor + standalone/server; ARM64: standalone only (Unity 6000.3 has neither a Linux ARM64 Editor nor player yet: the importer setting exists, for headless and future players) |
| Nintendo Switch | static `libugo.a` in the separate `com.ugohttp.switch` package | built outside this repository against the NintendoSDK (NDA); see *Future platforms* |
| WebGL | not by the native core | no sockets; see *Future platforms* |

## HTTP

| Area | Requirement |
|---|---|
| Methods | Any RFC 9110 method token, standard and custom; HEAD sends and reads no body; CONNECT is rejected as invalid input (no tunnel; see *Errors*). The body (none, bytes, file, multipart) is independent of the method |
| Request body | Sent exactly as given. From memory, from a file, or multipart. Must be replayable on redirect (seek/rewind) |
| Headers | Arbitrary request headers; a header holding CR, LF or NUL is invalid input. All headers of the final response and its status are delivered, for HTTP error statuses too. Interim 1xx responses are consumed, never delivered; trailers are not delivered |
| Response to memory | One native buffer, caller-set size limit; exceeding it fails the request, never grows silently. Exposed in place (no copy) until the caller releases it. Delivered for non-2xx statuses too |
| Response to file | Body written natively to a caller-given path, never through managed memory. A non-2xx body is kept in memory up to a fixed 64 KiB cap instead; exceeding it aborts the transfer with `ResponseBodyLimit` and delivers the first 64 KiB. HTTP/1.1 loses the connection; HTTP/2 resets only the stream. See *File sink* |
| Compression | Responses: gzip, transparent. Without a caller `Accept-Encoding` the library sends `Accept-Encoding: gzip` and delivers the decoded body (memory cap, error-body cap and file sink count decoded bytes); a caller `Accept-Encoding` or a resumed download gets the body exactly as received. A body that fails decoding is a `Transfer` error. Request bodies are never compressed |
| Concurrency | Many transfers at once, HTTP/1.1 and HTTP/2, connection reuse. Two caller-set bounds: operations a client holds at once (running, plus finished and not yet released) and open connections. A submit when the operation bound is reached fails immediately, never blocks or queues. The connection bound counts TCP connections, not HTTP/2 streams; a transfer waiting for a free connection is queued natively, and the wait counts towards its connect and total timeouts |
| Timeouts | Connect timeout; optional total timeout (0 = none); optional low-speed abort (combined send and receive speed below N bytes/s for T seconds after connect, including a stalled wait for the first response byte or inactivity during the body). No separate first-byte timer |
| Events | Per transfer: headers (final response, after redirects), progress (only when bytes changed; intermediate progress updates are published at most once every 10 ms per transfer; a count held by that limit is published before completed, regardless of the interval, so the last progress carries the final byte counts), completed (with phase timings and connection reuse). Each carries the monotonic time it was published, so the caller can exclude app pause from its deadlines |
| Pause | The library never stops IO on app pause |
| Fire-and-forget | Per-request `detached` flag: no events are delivered, the library frees the transfer itself when it finishes; `submit` only reports accepted or rejected. For posts whose caller keeps its own durable copy (telemetry, logs) |
| Finish in background (iOS) | Per-request flag: the transfer holds an iOS background task (`beginBackgroundTask`) from submit to finish, so it can complete after the app leaves the foreground; when the OS expires the task the transfer is aborted. Not a background `URLSession`: it does not survive suspension. No-op on other platforms |
| Resume | Opt-in per request, off by default: a file download continues from the bytes already in its file (`Range`), a server that ignores the range restarts it, a complete file succeeds, a response that cannot continue the file fails with a classified error. Validation (`If-Range`, hashes) stays the caller's. See *File sink* |
| Redirects | Followed, limit configurable; `Authorization` and `Cookie` go only to the original origin. 303 switches to GET without a body (HEAD stays HEAD) and drops the body's headers; 301, 302, 307 and 308 keep the method and replay the body |
| Retries | None by the library's choice. curl itself re-sends a request, body rewound, on a fresh connection when a reused connection died before any response byte or an HTTP/2 server refused the stream (`REFUSED_STREAM`, GOAWAY), at most 5 times |
| DNS | Cache TTL configurable down to 0. Resolve failures are a distinct error class |
| Speed limit | Per-request receive and send limits |
| Abort | Asynchronous. Every transfer gets exactly one `completed`: `Aborted` if `abort` came before it finished, otherwise its real result. Abort of a finished or unknown transfer does nothing. Until `completed` the native side may still touch the transfer's buffers and file |

### File sink

- Writes go to the path the caller gave (a unique `.part`). The library never renames into place: commit and validation are the caller's.
- The file is flushed and closed **before** `completed` is delivered. `completed(success)` means HTTP success, full body, file closed.
- A write error (disk full, permission) aborts the transfer with class `LocalIO`, never a network class: callers must not blame the host for a full disk.
- On abort or failure the library deletes the partial file before `completed`, unless the request asked to keep it or to resume. A non-2xx response writes no file; its body is delivered from memory up to 64 KiB; exceeding the cap aborts the transfer with `ResponseBodyLimit`.
- Resume: the state is the file itself, nothing else is stored. A request that resumes asks for the rest of a non-empty file and an unencoded representation; it appends a `206` that continues the file, held to exactly its stated range; rewrites the file on a `2xx` other than `206`; treats a `416` that reports the file's length as success; fails a `206` that stops before the resource's end as `Transfer` with the file kept, so the next attempt continues; fails any other answer as `Transfer` (reason `ResumeMismatch`). The file is deleted only when the server contradicts it and the request did not ask to keep it. A non-2xx response leaves the file as it was.

## WebSocket

| Area | Requirement |
|---|---|
| Handshake | Custom headers (e.g. `Authorization: Bearer ...`) |
| Redirects | Followed by the library like HTTP redirects (any 3xx with `Location`, at most 10), credentials kept to the original origin |
| Failed handshake | Status and headers of the last response delivered |
| Messages | Text and binary, reassembled from fragments; each message keeps its type. Text is valid UTF-8 both ways: received invalid text closes with 1007, sending invalid text is invalid input |
| Ping/pong | RFC PONG answered by the library without the caller; application heartbeat is the caller's |
| Handshake check | Before the socket counts as open: exactly one `Sec-WebSocket-Accept`, matching the key; no `Sec-WebSocket-Extensions`; a `Sec-WebSocket-Protocol` only if the request offered it, and one of the offered values. Otherwise the handshake fails |
| Protocol errors | On a protocol error from the server the socket stops reading, sends CLOSE with the matching code (1002, 1007, 1009) within the close timeout and ends |
| Limits | Caller-set message size and receive backlog. A caller that stops draining pauses receiving (TCP back-pressure): nothing is dropped, nothing grows |
| Send | Text or binary, copied into a bounded per-socket buffer; a full buffer or a socket that is not open fails the call at once |
| Extensions | None: permessage-deflate and other extensions are never offered; a caller-set `Sec-WebSocket-Extensions` header is invalid input |

## Errors

Every failure of an accepted operation has a stable class plus the raw `CURLcode` and message:

`Dns, Connect, Tls, Timeout, Transfer, HttpStatus, Aborted, LocalIO, Internal`

`HttpStatus` still delivers status, headers and (for memory responses) the body.

Invalid input (for example a malformed URL, an invalid method token or CONNECT) is not a failure class: the call rejects it synchronously (`E_INVALID_ARG` natively, `ArgumentException` in C#) and no operation or `completed` exists.

## Non-functional

- **Size.** Minimal curl: HTTP, HTTPS, WS, WSS, nghttp2, zlib (gzip decoding). No HTTP/3, brotli, zstd, proxies, FTP, SSH, LDAP, NTLM, SMTP or other protocols. Size budget per ABI: `LIBUGO_MAX_BYTES` in `scripts/versions.env`. No RSS parity with UnityWebRequest is promised: resident memory grows with the number of open connections, and `MaxConnections` is the caller's bound on it (decisions.md *Size and performance against UnityWebRequest*).
- **Allocations.** No managed allocation per body chunk, per progress event, or per WebSocket message sent or received as bytes. Conversions to and from `string` allocate.
- **Threading.** All network IO, TLS and file writes run on native threads, never on the Unity main thread. Public calls are safe from any thread. Events are drained per client either by a library-owned background thread (default) or by the caller on the thread it chooses, by polling or by a blocking wait. Transfers never depend on the Unity loop or main thread; the core uses no Unity API and runs in plain .NET.
- **TLS.** TLS 1.2 with AEAD suites only, and TLS 1.3. Peer and hostname verification always on. Trust is the bundled Mozilla root set, or a PEM bundle the caller passes instead of it. The OS trust store is not consulted: roots installed on the device are trusted only if the caller puts them in its bundle, and Android Network Security Config does not apply (architecture.md *TLS and trust*). Disabling verification needs a native library built for development; the released library rejects it at client creation.
- **IL2CPP/AOT.** Fixed C ABI, no variadic functions. Editor domain reload and app quit leave nothing running or leaked.
- **Reliability.** No double completion, no use-after-free on abort/shutdown, no request that never completes.
- **Updates.** curl, TLS, nghttp2 and zlib versions pinned and built from source; a dependency security fix ships as a patch release.
- **License.** Own code MIT. Dependencies compatible (curl license, nghttp2 MIT, mbedTLS Apache-2.0, zlib license).

## Editor

The full API works in the Editor outside Play Mode, for editor tools. Nothing has to be pumped: no scheduler, no `EditorApplication.update` hook, no Play Mode. A request completes on its own: the background drain thread delivers it, or the caller drains events from any thread, including the Editor main thread or a blocking wait on a worker thread.

- Domain reload aborts in-flight transfers of that domain; each gets its one `completed` before the reload proceeds.
- Native state outlives a domain reload (the plugin stays loaded): a client left by a previous domain is shut down on the next init, never leaked or left running.
- Entering Play Mode with domain reload disposes every client (domain reload rule above). Leaving Play Mode disposes every client created since Play Mode was entered; clients that existed before survive (only observable with domain reload disabled). In-flight HTTP transfers and WebSockets complete as `Aborted` / `Shutdown` unless already finished. The boundary is the client: a request sent in Play Mode through a pre-existing edit-time client survives. Clients registered after the exit snapshot, including from leftover application threads or shutdown callbacks, are edit-scoped and remain alive.

## Future platforms

Consoles (PlayStation, Xbox) and WebGL are not targets of v1, but no v1 decision may rule them out. Two routes must stay open:

- **Port the native core**: the shim builds with a console toolchain once its platform layer is reimplemented (see `docs/architecture.md`, *Portability*).
- **Fallback**: the same public C# types implemented over UnityWebRequest for that platform, with a documented reduced feature set.

Nintendo Switch took the first route, in a port outside this repository (`docs/decisions.md`, *Ports outside the repository*).

## Out of scope for v1

HTTP/3, proxies (explicit or system, proxy auth), cookies, client certificates, OS trust store, RFC HTTP cache, WebSocket extensions (permessage-deflate included), transfers that survive OS suspension (background `URLSession`, WorkManager), WebGL, consoles other than Nintendo Switch, asset-bundle caching.
