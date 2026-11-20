# Changelog

All notable changes to this package are documented here, following [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.3.1] - 2026-11-20

### Fixed

- The Android libraries no longer embed absolute paths of the build machine: nghttp2 keeps its assertions in release builds, and their file names carried the build directory.

## [0.3.0] - 2026-11-09

### Changed

- **Breaking (C# API):** `UgoClientOptions.MaxOperations` and `UgoClient.MaxOperations` are now `OperationSlots`: the size of the client's operation table, which counts finished operations not yet disposed and was never a concurrency limit. Behaviour is unchanged: with every slot held a submit still fails at once with `NoCapacity`.
- **Breaking (C# API):** `UgoRequest.MinimumSpeedWindow` is now `StallTimeout` (`TimeSpan?`, null = none) and works alone: the request fails as `Timeout` when it moves less than one byte per second for that long, measured once connected. `MinimumSpeedBytesPerSecond` is an optional higher threshold for it and is rejected without a `StallTimeout`.
- `OperationSlots` is optional: 0 means `UgoClientOptions.DefaultOperationSlots` (64). `new UgoClient()` creates a client with default options.

## [0.2.3] - 2026-11-07

### Added

- Nintendo Switch players call the native library through `__Internal`; the library itself ships in a separate package.

### Fixed

- On Nintendo Switch, an abort, submit or shutdown that followed another one reached the network thread about 100 ms late: curl wakes its event loop there over a loopback TCP pair, which now has Nagle disabled.

## [0.2.2] - 2026-11-06

### Changed

- TLS 1.2 is negotiated with AEAD suites only (AES-GCM, AES-CCM, ChaCha20-Poly1305); a server that offers only CBC suites over TLS 1.2 now fails with `Tls`. TLS 1.3 is unchanged.
- The native library is about 37 KB smaller on android-arm64 (1,208,248 → 1,170,912 bytes) and 17–38 KB smaller on the other platforms.
- AES-GCM on 64-bit Arm is about four times faster per core for full-size TLS records, measured on two Android phones; encryption takes the same path as decryption.

## [0.2.1] - 2026-11-06

### Changed

- Intermediate progress updates are published at most once every 10 ms per transfer; a count held by that limit is published before completion, regardless of the interval, so the last progress carries the final byte counts. A 100 MiB HTTPS download used about a third less process CPU.

## [0.2.0] - 2026-11-06

### Added

- WebSocket binary messages, sent and received; each message keeps its type (`UgoWebSocketMessage.MessageType`).

### Changed

- **Breaking (native ABI v2):** `ugo_ws_send` takes a message type (`UGO_WS_TEXT`, `UGO_WS_BINARY`); `ugo_ev_ws_message` carries the type (24 → 32 bytes). Plugin and C# must come from the same release.
- **Breaking (C# API):** `UgoWebSocketMessage.Utf8` is now `Data`; `UgoWebSocket.Send(ReadOnlySpan<byte>)` is now `Send(ReadOnlySpan<byte>, UgoWebSocketMessageType)`. `Send(string)` still sends text. `GetText()` on a binary message throws `InvalidOperationException`.
- A 303 redirect is followed with a GET (a HEAD stays HEAD) without the request body and without its `Content-Type`, `Content-Length`, `Content-Encoding` and `Transfer-Encoding` headers, and to another origin without `Authorization`, `Cookie` and a caller-set `Host`; 301, 302, 307 and 308 still send the method and body again. On HTTP/1.1 the GET after a 303 cannot use the connection curl had picked for it, which is closed.
- Sending invalid UTF-8 as WebSocket text throws `ArgumentException` (`UGO_E_INVALID_ARG` natively) instead of going out.
- A `Sec-WebSocket-Extensions` request header throws `ArgumentException` (`UGO_E_INVALID_ARG`): no extension is supported.
- Native callers: a request header line holding NUL or a CR other than before its LF is `UGO_E_INVALID_ARG`, for HTTP and WebSocket.

### Fixed

- A WebSocket handshake redirect to another origin no longer sends the caller's `Host` header there (it already dropped `Authorization` and `Cookie`).
- The WebSocket handshake accepted a repeated `Sec-WebSocket-Accept`, an empty `Sec-WebSocket-Extensions` and a `Sec-WebSocket-Protocol` the client did not offer.
- After a WebSocket protocol error (invalid UTF-8, an oversized message, a bad CLOSE) the library kept reading frames and waited for the server's CLOSE; it now stops reading, sends its CLOSE and ends the socket.

## [0.1.7] - 2026-11-06

### Fixed

- A WebSocket receiving a flood of frames, or a fast stream of empty outgoing messages, could delay close timeouts, aborts, shutdown and other transfers of the same client.

### Changed

- TLS no longer allocates and copies on the native heap for every record and every outgoing WebSocket frame.

## [0.1.6] - 2026-11-05

### Fixed

- iOS link failure (`_pipe2` undefined) with Xcode older than 27.

## [0.1.5] - 2026-11-05

### Added

- Tracker **Pause** toggle: freezes the table for inspection; sorting, grouping, search, selection and copy keep working.
- Tracker **Bytes** column: HTTP bytes sent and received, live while a request runs (a resumed download counts only the bytes of this transfer); WebSocket rows show none.
- Tracker host totals: a host row counts finished, failed and aborted operations, new connections and HTTP traffic since **Clear Host Totals**; **Keep Finished Hosts** keeps a host row after its rows leave.

### Changed

- Tracker settings (**Group by Host**, **Keep Finished Hosts**, **Capture Stacks**, **Clear Host Totals**) moved into a **View** menu; the toolbar keeps **Pause** and search.
- **Group by Host** groups every host, not only those with several rows; hosts start collapsed and their open state is kept in memory only. A collapsed host row shows the method and result of its latest request.

## [0.1.4] - 2026-11-05

### Added

- `UgoClient.IsDisposed` to check whether disposal has started and recreate a shared client when needed.
- Tracker row context menu with Copy URL, Copy Row and Copy Stack Trace; Cmd/Ctrl+C copies the selected rows' URLs.

### Changed

- Released operations stay greyed in the tracker for 10 s instead of 30 s.
- Tracker columns can be narrowed below their content; clipped values end in an ellipsis and show in full as a tooltip.
- The tracker groups the rows of a host that has several under a host row with live and closed counts (**Group by Host**, on by default); collapsed hosts survive reloads.
- A tracker view scrolled to the edge where new rows appear stays pinned there; elsewhere the rows in view keep their position.
- The tracker refreshes without rebuilding or reformatting unchanged rows.

### Fixed

- WebSocket message and handshake access throws `ObjectDisposedException` after client disposal instead of exposing freed native memory.

## [0.1.3] - 2026-11-05

### Changed

- The Editor tracker always records method, URL and start time in the Editor; the **Enable Tracking** toggle is gone and **Capture Stacks** is the only option. The window is a sortable, searchable table with Result, Started and Elapsed columns, full values with horizontal scrolling, a draggable splitter and clickable stack frames.
- Released operations stay in the tracker greyed for 30 s; operations left to client disposal and clients reclaimed at Play Mode exit stay marked as leaks.

### Fixed

- The tracker's held WebSocket message count no longer reads zero for messages received while tracking was off.

## [0.1.2] - 2026-11-05

### Fixed

- An open Editor tracker window survives script reloads and Play Mode transitions.

## [0.1.1] - 2026-11-05

### Changed

- WebSocket handshakes follow redirects: any 3xx with `Location`, at most 10, `http`/`https` targets mapped to `ws`/`wss`; `Authorization` and `Cookie` are not sent to another origin. `UgoWebSocketResult.StatusCode`, `Handshake` and `Timings` describe the last response; too many redirects fail as `Transfer` with native code 47.

### Added

- `UgoWebSocketResult.RedirectCount`.
- Editor leak tracker (Window → UgoHTTP → Tracker): live clients, responses, WebSockets and messages not disposed, with optional URL, age and creation stack.

## [0.1.0] - 2026-11-05

### Added

- HTTP/1.1 and HTTP/2 transfers, streaming downloads with resume, gzip, and text WebSocket support over a fixed native ABI.
- Thread-safe C# awaitable and callback APIs independent of the Unity loop, with Editor reload cleanup and iOS finish-in-background.
- Native plugins for macOS, iOS, Android, Linux, and Windows, built from pinned curl, mbedTLS, nghttp2, and zlib sources.
- Fault, sanitizer, .NET, Unity EditMode, and Android/iOS IL2CPP player checks; Windows runtime checks currently use Wine.
