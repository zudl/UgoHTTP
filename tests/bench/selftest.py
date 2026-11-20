import argparse
import asyncio
import gzip
import hashlib
import json
import os
from pathlib import Path
import signal
import ssl
import sys
import tempfile
from urllib.parse import urlencode, urlsplit

import h2.config
import h2.connection
import h2.events
import h2.settings

from wsproto import ConnectionType, WSConnection
from wsproto.events import AcceptConnection, BytesMessage, CloseConnection, Ping, Request, TextMessage
from wsproto.utilities import RemoteProtocolError

from server import Bench, OK, OK_SHA256, ROOT, stop_event_name, ws_text

CURL = os.environ.get("UGOHTTP_CURL", "/usr/bin/curl")
WINDOWS = sys.platform == "win32"


def stop_bench(pid):
    import ctypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenEventW.restype = ctypes.c_void_p
    kernel32.SetEvent.argtypes = kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    event = kernel32.OpenEventW(0x0002, False, stop_event_name(pid))  # EVENT_MODIFY_STATE
    if not event:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        if not kernel32.SetEvent(event):
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        kernel32.CloseHandle(event)


async def command(*args):
    proc = await asyncio.create_subprocess_exec(*args, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    stdout, stderr = await asyncio.wait_for(proc.communicate(), 20)
    return proc.returncode, stdout, stderr


async def check_bench(ready):
    run = Path(ready["ca_pem"]).parent
    results = []

    async def curl(name, target, *args, code=0, body=None, endpoint="http", version=None, status=None):
        with tempfile.TemporaryDirectory(prefix="selftest-", dir=run) as directory:
            out = Path(directory) / "body"
            headers = Path(directory) / "headers"
            rc, stdout, stderr = await command(CURL, "--noproxy", "*", "-sS", "--max-time", "3",
                "--cacert", ready["ca_pem"], "-o", str(out), "-D", str(headers),
                "-w", "%{http_code} %{http_version} %{time_total}", *args, ready["endpoints"][endpoint] + target)
            data = out.read_bytes() if out.exists() else b""
            info = stdout.decode().split()
            expected = (code,) if isinstance(code, int) else code
            assert rc in expected, (name, rc, stderr.decode())
            if body is not None:
                assert data == body, (name, len(data), len(body))
            if version:
                assert info[1] == version, (name, info)
            if status:
                assert int(info[0]) == status, (name, info)
            results.append({"scenario": name, "pass": True, "curl_exit": rc})
            return data, headers.read_text(), float(info[2])

    endpoint = urlsplit(ready["endpoints"]["http"])
    reader, writer = await asyncio.open_connection(endpoint.hostname, endpoint.port)
    writer.write(b"POST /ok HTTP/1.1\r\nHost: bench\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n")
    await writer.drain()
    writer.write_eof()
    try:
        assert await asyncio.wait_for(reader.read(), 1) == b""
    finally:
        writer.close()
        await writer.wait_closed()
    results.append({"scenario": "chunked-trailer-eof", "pass": True})
    assert hashlib.sha256(OK).hexdigest() == OK_SHA256
    await curl("ok", "/ok", body=OK)
    await curl("tls-http1", "/ok", "--http1.1", endpoint="https", body=OK, version="1.1")
    await curl("stall-after-headers", "/stall-after-headers", "--max-time", "0.25", code=28)
    data, _, duration = await curl("slow-body", "/slow-body/256?bps=512", body=bytes(range(256)))
    assert duration >= 0.45, duration
    await curl("cut-mid-body", "/cut-mid-body", code=18, body=OK[:len(OK) // 2])
    await curl("wrong-content-length", "/wrong-content-length", code=18, body=OK)
    await curl("chunked", "/chunked", body=OK)
    full = (bytes(range(256)) * 40)[:10000]
    await curl("range-full", "/range/10000", body=full, status=200)
    _, headers, _ = await curl("range-206", "/range/10000", "-H", "Range: bytes=4000-", body=full[4000:], status=206)
    assert "content-range: bytes 4000-9999/10000" in headers.lower() and 'etag: "r10000v0"' in headers.lower()
    await curl("range-206-h2", "/range/10000", "-H", "Range: bytes=4000-", endpoint="https", version="2", body=full[4000:], status=206)
    await curl("range-ignored", "/range/10000?mode=ignore", "-H", "Range: bytes=4000-", body=full, status=200)
    await curl("if-range-match", "/range/10000", "-H", "Range: bytes=4000-", "-H", 'If-Range: "r10000v0"', body=full[4000:], status=206)
    v1 = (bytes(range(7, 256)) + bytes(range(7))) * 40
    await curl("if-range-mismatch", "/range/10000?version=1", "-H", "Range: bytes=4000-", "-H", 'If-Range: "r10000v0"', body=v1[:10000], status=200)
    _, headers, _ = await curl("range-416", "/range/10000", "--fail-with-body", "-H", "Range: bytes=10000-", code=22, status=416)
    assert "content-range: bytes */10000" in headers.lower()
    _, headers, _ = await curl("range-wrong-start", "/range/10000?mode=wrong-start", "-H", "Range: bytes=4000-", status=206)
    assert "content-range: bytes 4001-9999/10000" in headers.lower()
    _, headers, _ = await curl("range-wrong-total", "/range/10000?mode=wrong-total", "-H", "Range: bytes=4000-", status=206)
    assert "content-range: bytes 4000-9999/10001" in headers.lower()
    _, headers, _ = await curl("range-bare-416", "/range/10000?mode=bare-416", "--fail-with-body", "-H", "Range: bytes=10000-", code=22, status=416)
    assert "content-range" not in headers.lower()
    await curl("range-cut", "/range/10000?cut=3000", code=18, body=full[:3000])
    rng = ("-H", "Range: bytes=4000-")
    _, headers, _ = await curl("range-unknown-total", "/range/10000?mode=unknown-total", *rng, body=full[4000:], status=206)
    assert "content-range: bytes 4000-9999/*" in headers.lower()
    _, headers, _ = await curl("range-short", "/range/10000?mode=short-range&chunk=100", *rng, body=full[4000:4100], status=206)
    assert "content-range: bytes 4000-4099/10000" in headers.lower()
    _, headers, _ = await curl("range-duplicate", "/range/10000?mode=dup-range-rev", *rng, body=full[4000:], status=206)
    assert headers.lower().count("content-range:") == 2
    data, headers, _ = await curl("range-multipart", "/range/10000?mode=multipart", *rng, status=206)
    assert "content-type: multipart/byteranges" in headers.lower() and data.startswith(b"--x\r\n") and full[4000:] in data
    await curl("range-length-short", "/range/10000?mode=length-short", *rng, body=full[4000:9998], status=206)
    await curl("range-length-long", "/range/10000?mode=length-long", *rng, body=full[4000:] + b"xyz", status=206)
    _, headers, _ = await curl("range-chunked-short", "/range/10000?mode=chunked-short", *rng, body=full[4000:9998], status=206)
    assert "transfer-encoding: chunked" in headers.lower()
    _, headers, _ = await curl("range-no-length", "/range/10000?mode=no-length-short", *rng, body=full[4000:9998], status=206)
    assert "content-length" not in headers.lower()
    await curl("range-huge-length", "/range/10000?mode=huge-length", *rng, code=18, body=full[4000:4001])
    _, headers, _ = await curl("range-huge-length-chunked", "/range/10000?mode=huge-length-chunked", *rng, body=full[4000:], status=206)
    assert "transfer-encoding: chunked" in headers.lower() and "content-length: 9223372036854775807" in headers.lower()
    for order in ("multipart-then-plain", "plain-then-multipart"):
        _, headers, _ = await curl("range-" + order, "/range/10000?mode=" + order, *rng, status=206)
        assert headers.lower().count("content-type:") == 2
    await curl("range-early-416", "/range/10000?mode=early-416", "--fail-with-body", *rng, code=22, status=416)
    data, _, _ = await curl("range-big-416", "/range/10000?mode=big-416", "--fail-with-body", "-H", "Range: bytes=10000-", code=22, status=416)
    assert len(data) > 65536
    data, headers, _ = await curl("range-gzip", "/range/10000?gzip=1", "-H", "Accept-Encoding: gzip", status=200)
    assert "content-encoding: gzip" in headers.lower() and gzip.decompress(data) == full
    await curl("range-identity", "/range/10000?gzip=1", "-H", "Accept-Encoding: identity", body=full, status=200)
    for status in (204, 408, 429, 500, 503):
        _, headers, _ = await curl(f"status-{status}", f"/status/{status}?retry_after=7", status=status,
            body=b"" if status == 204 else OK)
        assert "retry-after: 7" in headers.lower()
    body = '{"signed":"wire bytesé"}'.encode("utf-8")
    digest = hashlib.sha256(body).hexdigest()
    # A file, not an argument: Windows hands a non-ASCII argument to curl in the ANSI code page.
    body_file = Path(tempfile.mkdtemp()) / "body.json"
    body_file.write_bytes(body)
    for code in (301, 302, 307, 308):
        tag = f"redirect-{code}-{os.getpid()}"
        final = "/ok?tag=" + tag
        second = f"/redirect/{code}?" + urlencode({"to": final, "tag": tag})
        target = f"/redirect/{code}?" + urlencode({"to": second, "tag": tag})
        await curl(f"redirect-{code}", target, "-L", "--post301", "--post302", "--post303", "--data-binary", f"@{body_file}", body=OK)
        rows = [json.loads(line) for line in (run / "requests.jsonl").read_text().splitlines()]
        hops = [row for row in rows if "target" in row and f"tag={tag}" in row["target"]]
        assert len(hops) == 3 and all(row["method"] == "POST" and row["body_sha256"] == digest for row in hops), hops
    body_file.unlink()
    body_file.parent.rmdir()
    tag = f"multipart-{os.getpid()}"
    await curl("redirect-multipart", "/redirect/307?" + urlencode({"to": "/ok?tag=" + tag, "tag": tag}),
        "-L", "-F", "field=value", body=OK)
    rows = [json.loads(line) for line in (run / "requests.jsonl").read_text().splitlines()]
    hops = [row for row in rows if "target" in row and f"tag={tag}" in row["target"]]
    assert len(hops) == 2 and hops[0]["body_sha256"] == hops[1]["body_sha256"] and all(row["method"] == "POST" for row in hops)
    size = 131123
    big = (bytes(range(256)) * ((size + 255) // 256))[:size]
    await curl("big", f"/big/{size}", body=big)
    plain = (bytes(range(256)) * 400)[:100000]
    for kind in ("gzip", "deflate"):
        await curl("encoded-" + kind, "/encoded/" + kind + "?bytes=100000", "--compressed", body=plain)
    await curl("h2-encoded-gzip", "/encoded/gzip?bytes=100000", "--compressed", "--http2", endpoint="https",
        body=plain, version="2")
    _, headers, _ = await curl("encoded-raw", "/encoded/gzip?bytes=100000", body=gzip.compress(plain, mtime=0))
    assert "content-encoding: gzip" in headers.lower()
    await curl("encoded-cut", "/encoded/gzip?bytes=100000&cut=500", body=gzip.compress(plain, mtime=0)[:500])
    await curl("encoded-chunked", "/encoded/gzip?bytes=100000&chunked=1", "--compressed", body=plain)
    await curl("encoded-corrupt", "/encoded/corrupt?bytes=100000", "--compressed", code=61)
    await curl("encoded-error-status", "/encoded/gzip?bytes=1000&status=500", "--compressed", status=500,
        body=plain[:1000])
    for mode in ("expired", "wrong_hostname", "self_signed", "incomplete_chain"):
        await curl("tls-" + mode, "/ok", code=60, endpoint=mode)
    await curl("tls12", "/ok", endpoint="tls12", body=OK)
    endpoint = urlsplit(ready["endpoints"]["tls12"])
    _, writer = await asyncio.open_connection(endpoint.hostname, endpoint.port,
        ssl=ssl.create_default_context(cafile=ready["ca_pem"]))
    version = writer.get_extra_info("ssl_object").version()
    writer.close()
    await writer.wait_closed()
    assert version == "TLSv1.2", version
    results.append({"scenario": "tls12-negotiated", "pass": True, "version": version})
    await curl("h2-ok", "/ok", "--http2", endpoint="https", body=OK, version="2")
    await curl("h2-big", f"/big/{size}", "--http2", endpoint="https", body=big, version="2")
    await curl("h2-cut-rst-stream", "/cut-mid-body", "--http2", endpoint="https", code=(16, 92), version="2")
    upgrade = ("-H", "Connection: Upgrade", "-H", "Upgrade: websocket", "-H", "Sec-WebSocket-Version: 13",
        "-H", "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==")
    await curl("ws-bearer-required", "/ws/echo", *upgrade, status=401, body=b"")
    _, headers, _ = await curl("ws-handshake-redirect", "/ws/redirect?to=/ws/echo", *upgrade, "-H", "Authorization: Bearer bench-token", status=302, body=b"")
    assert "location: /ws/echo" in headers.lower()

    async def websocket(mode, secure=False):
        endpoint = urlsplit(ready["endpoints"]["https" if secure else "http"])
        ctx = ssl.create_default_context(cafile=ready["ca_pem"]) if secure else None
        reader, writer = await asyncio.open_connection(endpoint.hostname, endpoint.port, ssl=ctx)
        ws = WSConnection(ConnectionType.CLIENT)
        writer.write(ws.send(Request(host=endpoint.netloc, target="/ws/" + mode,
            extra_headers=[(b"authorization", b"Bearer bench-token")])))
        await writer.drain()
        accepted = False
        received = ""
        received_bytes = b""
        ping = False
        closing = False
        try:
            async with asyncio.timeout(3):
                while data := await reader.read(65536):
                    try:
                        ws.receive_data(data)
                        events = list(ws.events())
                    except RemoteProtocolError:
                        assert mode == "bad-accept"
                        return
                    for event in events:
                        if isinstance(event, AcceptConnection):
                            accepted = True
                            if mode == "echo":
                                writer.write(ws.send(TextMessage(data="echo text")))
                            elif mode == "binary-echo":
                                writer.write(ws.send(BytesMessage(data=b"\x00\xff", message_finished=False)))
                                writer.write(ws.send(BytesMessage(data=b"echo", message_finished=True)))
                        elif isinstance(event, Ping):
                            ping = True
                            writer.write(ws.send(event.response()))
                            if mode in ("ping", "split-ping"):
                                writer.write(ws.send(TextMessage(data="after pong")))
                        elif isinstance(event, TextMessage):
                            received += event.data
                            if event.message_finished:
                                expected = {"echo": "echo text", "ping": "after pong", "split-ping": "after pong", "fragmented": "frag-mented",
                                    "big": ws_text(0, 70000)}[mode]
                                assert accepted and received == expected
                                assert mode in ("echo", "big") or ping
                                writer.write(ws.send(CloseConnection(code=1000)))
                                await writer.drain()
                                closing = True
                        elif isinstance(event, BytesMessage):
                            received_bytes += event.data
                            if event.message_finished:
                                expected = {"binary": b"\x00\x01binary\xff", "fragmented-binary": b"\x00frag\xffmented",
                                    "binary-echo": b"\x00\xffecho"}[mode]
                                assert accepted and received_bytes == expected
                                assert mode != "fragmented-binary" or ping
                                writer.write(ws.send(CloseConnection(code=1000)))
                                await writer.drain()
                                closing = True
                        elif isinstance(event, CloseConnection):
                            assert accepted and event.code == 1000
                            if mode == "close":
                                assert event.reason == "bench done"
                                writer.write(ws.send(event.response()))
                                await writer.drain()
                            else:
                                assert closing
                            return
                    await writer.drain()
                raise AssertionError((mode, "unexpected EOF"))
        finally:
            writer.close()
            await writer.wait_closed()

    for mode in ("echo", "ping", "split-ping", "fragmented", "big", "close", "bad-accept", "binary", "fragmented-binary"):
        await websocket(mode)
        results.append({"scenario": "ws-" + mode, "pass": True})
    await websocket("echo", secure=True)
    results.append({"scenario": "wss-echo", "pass": True})
    # An unknown mode is the plain echo.
    await websocket("binary-echo")
    results.append({"scenario": "ws-binary-echo", "pass": True})

    async def raw_handshake(target, line, count):
        endpoint = urlsplit(ready["endpoints"]["http"])
        reader, writer = await asyncio.open_connection(endpoint.hostname, endpoint.port)
        ws = WSConnection(ConnectionType.CLIENT)
        writer.write(ws.send(Request(host=endpoint.netloc, target=target,
            extra_headers=[(b"authorization", b"Bearer bench-token")])))
        try:
            async with asyncio.timeout(3):
                head = await reader.readuntil(b"\r\n\r\n")
            lines = head.lower().split(b"\r\n")
            assert lines[0].startswith(b"http/1.1 101"), head
            assert sum(1 for item in lines if item.startswith(line)) == count, head
        finally:
            writer.close()
            await writer.wait_closed()

    for target, line, count in (("/ws/extension?value=~", b"sec-websocket-extensions:", 1),
                                ("/ws/duplicate-accept", b"sec-websocket-accept:", 2),
                                ("/ws/missing-accept", b"sec-websocket-accept:", 0),
                                ("/ws/echo?protocol=chat", b"sec-websocket-protocol: chat", 1),
                                ("/ws/echo?protocol=~&protocol=chat", b"sec-websocket-protocol:", 2)):
        await raw_handshake(target, line, count)
        results.append({"scenario": "ws-handshake-" + target.rsplit("/", 1)[1], "pass": True})

    async def raw_close(target, close_first, payload):
        endpoint = urlsplit(ready["endpoints"]["http"])
        reader, writer = await asyncio.open_connection(endpoint.hostname, endpoint.port)
        ws = WSConnection(ConnectionType.CLIENT)
        writer.write(ws.send(Request(host=endpoint.netloc, target=target,
            extra_headers=[(b"authorization", b"Bearer bench-token")])))
        try:
            async with asyncio.timeout(3):
                head = await reader.readuntil(b"\r\n\r\n")
                assert head.startswith(b"HTTP/1.1 101"), head
                if close_first:
                    writer.write(bytes([0x88, 0x82, 0, 0, 0, 0, 0x03, 0xe8]))
                frame = await reader.readexactly(2 + len(payload))
                assert frame == bytes([0x88, len(payload)]) + payload, frame
        finally:
            writer.close()
            await writer.wait_closed()

    for payload in ("58", "03ed", "03e8ff"):
        await raw_close(f"/ws/bad-close-reply?payload={payload}", True, bytes.fromhex(payload))
        results.append({"scenario": "ws-bad-close-reply-" + payload, "pass": True})
    await raw_close("/ws/close?payload=03e8610062", False, bytes.fromhex("03e8610062"))
    results.append({"scenario": "ws-close-raw-payload", "pass": True})
    return {"pid": ready["pid"], "endpoints": ready["endpoints"], "scenarios": results, "pass": True}


async def h2_regressions():
    with tempfile.TemporaryDirectory(prefix="h2-reset-", dir=ROOT / "tmp/bench") as directory:
        bench = Bench(Path(directory))
        owner = asyncio.get_running_loop().create_future()

        async def accept(reader, writer):
            task = asyncio.current_task()
            coroutine = bench.http2(reader, writer)
            owner.set_result((task, coroutine))
            try:
                await coroutine
            finally:
                writer.close()
                await writer.wait_closed()

        server = await asyncio.start_server(accept, "127.0.0.1", 0)
        reader, writer = await asyncio.open_connection("127.0.0.1", server.sockets[0].getsockname()[1])
        task, coroutine = await owner
        conn = h2.connection.H2Connection(config=h2.config.H2Configuration(client_side=True))
        conn.initiate_connection()
        conn.update_settings({h2.settings.SettingCodes.INITIAL_WINDOW_SIZE: 0})

        async def barrier(tag):
            conn.ping(tag)
            writer.write(conn.data_to_send())
            await writer.drain()
            async with asyncio.timeout(3):
                while True:
                    data = await reader.read(65536)
                    assert data, "unexpected h2 EOF"
                    events = conn.receive_data(data)
                    writer.write(conn.data_to_send())
                    await writer.drain()
                    if any(isinstance(event, h2.events.PingAckReceived) and event.ping_data == tag for event in events):
                        return

        try:
            for sid in range(1, 21, 2):
                conn.send_headers(sid, [(":method", "POST"), (":scheme", "http"),
                    (":authority", "bench"), (":path", "/ok")])
                conn.send_data(sid, b"x" * 1000)
                conn.reset_stream(sid)
            await barrier(b"uploads!")
            state = coroutine.cr_frame.f_locals
            assert not state["streams"], state["streams"]
            conn.send_headers(21, [(":method", "GET"), (":scheme", "http"),
                (":authority", "bench"), (":path", "/big/1000000")], end_stream=True)
            await barrier(b"blocked!")
            response = state["senders"][21]
            assert not response.done()
            conn.reset_stream(21)
            await barrier(b"reset!!!")
            await asyncio.gather(response, return_exceptions=True)
            assert response.cancelled() and not state["senders"], state["senders"]
            conn.send_headers(23, [(":method", "POST"), (":scheme", "http"),
                (":authority", "bench"), (":path", "/ok")])
            conn.send_data(23, b"still alive")
            await barrier(b"alive!!!")
            assert bytes(state["streams"][23][1]) == b"still alive"
            conn.reset_stream(23)
            await barrier(b"clean!!!")
            assert not state["streams"]
            conn.send_headers(25, [(":method", "GET"), (":scheme", "http"),
                (":authority", "bench"), (":path", "/ok")], end_stream=True)
            await barrier(b"zero!!!!")
            assert not state["senders"][25].done()
            assert state["conn"].local_flow_control_window(25) == 0
            conn.update_settings({h2.settings.SettingCodes.INITIAL_WINDOW_SIZE: 65535})
            writer.write(conn.data_to_send())
            await writer.drain()
            body = bytearray()
            ended = False
            async with asyncio.timeout(3):
                while not ended:
                    data = await reader.read(65536)
                    assert data, "unexpected h2 EOF after SETTINGS increase"
                    for event in conn.receive_data(data):
                        if isinstance(event, h2.events.DataReceived) and event.stream_id == 25:
                            body.extend(event.data)
                        elif isinstance(event, h2.events.StreamEnded) and event.stream_id == 25:
                            ended = True
            assert bytes(body) == OK, body
        finally:
            writer.close()
            await writer.wait_closed()
            await asyncio.wait_for(task, 3)
            server.close()
            await server.wait_closed()
            bench.requests.close()
    return [
        {"scenario": "h2-reset-releases-upload-and-cancels-response", "pass": True},
        {"scenario": "h2-settings-increase-resumes-without-window-update", "pass": True},
    ]


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ready", type=Path, help="Use an existing bench ready.json without owning its process")
    parser.add_argument("--parallel", action="store_true", help="Start and verify two independent benches concurrently")
    args = parser.parse_args()
    processes = []
    try:
        if args.ready:
            ready = [json.loads(args.ready.read_text())]
        else:
            ready = []
            for _ in range(2 if args.parallel else 1):
                if WINDOWS:
                    process = await asyncio.create_subprocess_exec(
                        sys.executable, str(ROOT / "tests/bench/server.py"), stdout=asyncio.subprocess.PIPE)
                else:
                    process = await asyncio.create_subprocess_exec(str(ROOT / "scripts/bench"), stdout=asyncio.subprocess.PIPE)
                processes.append(process)
                line = await asyncio.wait_for(process.stdout.readline(), 10)
                ready.append(json.loads(line))
            ports = [urlsplit(url).port for item in ready for url in item["endpoints"].values()]
            assert len(ports) == len(set(ports))
        rc, version, _ = await command(CURL, "--version")
        assert rc == 0 and b"HTTP2" in version
        regressions = await h2_regressions()
        summaries = await asyncio.gather(*(check_bench(item) for item in ready))
        held = []
        if processes:
            for item in ready:
                endpoint = urlsplit(item["endpoints"]["https"])
                context = ssl.create_default_context(cafile=item["ca_pem"])
                reader, writer = await asyncio.open_connection(endpoint.hostname, endpoint.port, ssl=context)
                writer.write(b"GET /stall-after-headers HTTP/1.1\r\nHost: bench\r\n\r\n")
                await writer.drain()
                assert b"200" in await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 3)
                held.append(writer)
                _, partial = await asyncio.open_connection(endpoint.hostname, endpoint.port)
                partial.write(b"\x16")
                await partial.drain()
                held.append(partial)
    finally:
        for process, item in zip(processes, ready):
            if process.returncode is None:
                if WINDOWS:
                    # The venv's python.exe is a launcher: the server is its child, named by the ready line's pid.
                    stop_bench(item["pid"])
                else:
                    process.send_signal(signal.SIGTERM)
        for process in processes:
            try:
                await asyncio.wait_for(process.wait(), 5)
            except TimeoutError:
                process.kill()
                await process.wait()
                raise
            assert process.returncode == 0, process.returncode
            assert not await process.stdout.read(), "bench wrote more than one stdout line"
            if not WINDOWS:
                rc, pids, _ = await command("pgrep", "-f", str(ROOT / "tests/bench/server.py"))
                assert rc in (0, 1) and str(process.pid) not in pids.decode().split(), "bench still running"
    for writer in held:
        writer.close()
    for summary in summaries:
        if processes:
            summary["scenarios"].append({"scenario": "sigterm-active-and-incomplete-tls", "pass": True})
    print(json.dumps({"pass": True, "curl": CURL, "curl_version": version.decode().splitlines()[0], "instances": summaries, "regressions": regressions}))


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as error:
        print(json.dumps({"pass": False, "error": repr(error)}))
        raise
