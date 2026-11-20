import argparse
import asyncio
import contextlib
import datetime
import gzip
import hashlib
import json
import logging
import os
from pathlib import Path
import signal
import shutil
import socket
import ssl
import sys
import tempfile
import threading
import zlib
from urllib.parse import parse_qs, urlencode, urlsplit

import h2.config
import h2.connection
import h2.events
import trustme
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from wsproto import ConnectionType, WSConnection
from wsproto.events import AcceptConnection, BytesMessage, CloseConnection, Ping, Pong, TextMessage

ROOT = Path(__file__).resolve().parents[2]
OK = b"UgoHTTP fault bench\n"
OK_SHA256 = "24903a9184a70149a1cf1bb97a29591d669e6be27cfeb99efddcee1986ec427a"
PATTERN = bytes(range(256)) * 64
KEEP_ALIVE = ("ok", "big", "status", "slow-body", "redirect")
WS_PATTERN = "abcdefghijklmnopqrstuvwxyz0123456789"


def generated(offset, count):
    start = offset % len(PATTERN)
    return (PATTERN + PATTERN)[start:start + count]


def range_response(method, headers, parts, query):
    """/range/<size>: `size` pattern bytes with a strong ETag, honouring Range and If-Range.

    version=N shifts the content and the ETag; gzip=1 serves a gzip-coded representation to a request that
    accepts gzip; cut=N stops the body after N bytes (connection or stream); bps=N paces the body. mode= makes
    a ranged answer misbehave: ignore (200), wrong-start, wrong-total, unknown-total, short-range (chunk=N bytes
    only), no-content-range, dup-range, dup-range-rev, dup-garbage, multipart, multipart-bare, multipart-then-plain, plain-then-multipart, length-short,
    length-long, chunked-short, chunked-long, no-length-short, huge-length, huge-length-chunked, early-416, bare-416, big-416.
    Returns status, header pairs, the bytes to send, the declared Content-Length (None: none), chunked, cut."""
    size = int(parts[1])
    version = int(query.get("version", ["0"])[0])
    mode = query.get("mode", ["honor"])[0]
    shift = version * 7 % 256
    body = ((bytes(range(shift, 256)) + bytes(range(shift))) * (size // 256 + 1))[:size]
    etag = f'"r{size}v{version}"'
    fields = [("ETag", etag), ("Accept-Ranges", "bytes"), ("Last-Modified", "Mon, 02 Nov 2026 10:00:00 GMT")]
    if "gzip" in query and "gzip" in headers.get("accept-encoding", ""):
        c = zlib.compressobj(6, zlib.DEFLATED, 31)
        body = c.compress(body) + c.flush()
        fields.append(("Content-Encoding", "gzip"))
    status = 200
    declared = None
    chunked = False
    wanted = headers.get("range", "")
    if_range = headers.get("if-range")
    if wanted.startswith("bytes=") and wanted.endswith("-") and mode != "ignore" and (if_range is None or if_range == etag):
        first = int(wanted[6:-1])
        if first >= len(body) or mode == "early-416":
            status = 416
            if mode != "bare-416":
                fields.append(("Content-Range", f"bytes */{len(body)}"))
            body = generated(0, 16384) * 5 if mode == "big-416" else b""
        else:
            status = 206
            last = len(body) - 1
            if mode == "short-range":
                last = min(first + int(query.get("chunk", ["1000"])[0]) - 1, last)
            total = "*" if mode == "unknown-total" else str(len(body) + 1 if mode == "wrong-total" else len(body))
            start = first + 1 if mode == "wrong-start" else first
            ranges = {"no-content-range": [], "multipart-bare": [],
                "dup-range": [f"bytes {start}-{last}/{total}", "garbage"],
                "dup-garbage": ["garbage", f"bytes {start}-{last}/{total}"],
                "dup-range-rev": [f"bytes {start + 1}-{last}/{total}", f"bytes {start}-{last}/{total}"]}
            fields += [("Content-Range", v) for v in ranges.get(mode, [f"bytes {start}-{last}/{total}"])]
            body = body[first:last + 1]
            if mode.startswith("multipart") or mode == "plain-then-multipart":
                types = ["Multipart/ByteRanges; boundary=x"]
                if mode == "multipart-then-plain":
                    types.append("application/octet-stream")
                elif mode == "plain-then-multipart":
                    types.insert(0, "application/octet-stream")
                fields += [("Content-Type", t) for t in types]
                body = (b"--x\r\nContent-Type: application/octet-stream\r\nContent-Range: " +
                        f"bytes {first}-{last}/{total}".encode() + b"\r\n\r\n" + body + b"\r\n--x--\r\n")
            elif mode in ("length-short", "chunked-short", "no-length-short"):
                body = body[:-2]
            elif mode in ("length-long", "chunked-long"):
                body += b"xyz"
            chunked = mode.startswith("chunked") or mode == "huge-length-chunked"
            if mode == "huge-length":
                declared = 2 ** 63 - 1
            elif mode == "huge-length-chunked":
                fields.append(("Content-Length", str(2 ** 63 - 1)))
    if method == "HEAD":
        return status, fields, b"", len(body), False, False
    if declared is None and not chunked and mode != "no-length-short":
        declared = len(body)
    cut = int(query["cut"][0]) if "cut" in query else 1 if mode == "huge-length" else None
    if cut is not None and cut < len(body):
        return status, fields, body[:cut], declared, chunked, True
    return status, fields, body, declared, chunked, False


def encoded(kind, body):
    """`body` as served by /encoded/<kind>: the Content-Encoding header and the bytes on the wire."""
    packed = gzip.compress(body, mtime=0)
    if kind == "gzip":
        return "gzip", packed
    if kind == "deflate":
        return "deflate", zlib.compress(body)
    if kind == "raw-deflate":
        packer = zlib.compressobj(wbits=-zlib.MAX_WBITS)
        return "deflate", packer.compress(body) + packer.flush()
    if kind == "empty":
        return "gzip", b""
    if kind == "empty-stream":
        return "gzip", gzip.compress(b"", mtime=0)
    if kind == "corrupt":
        # 0xff after the gzip header starts a deflate block of the reserved type 3.
        return "gzip", packed[:10] + b"\xff" * (len(packed) - 10)
    if kind == "trailing":
        return "gzip", packed + b"junk"
    if kind == "unsupported":
        return "br", body
    raise ValueError("unknown encoding scenario")


def ws_text(index, size):
    """Message `index` of `size` ASCII bytes: an 8-digit index, then WS_PATTERN from offset 8."""
    body = (WS_PATTERN * (size // len(WS_PATTERN) + 1))[8:size]
    return (f"{index:08d}" + body)[:size]


def soak_text(kind, seq, size):
    """Soak message of `size` ASCII bytes: `kind`, a 10-digit sequence, a space, the 8-digit size, a space, then byte i is
    WS_PATTERN[(seq + i) % len(WS_PATTERN)]."""
    head = f"{kind}{seq:010d} {size:08d} "
    start = (seq + len(head)) % len(WS_PATTERN)
    return head + (WS_PATTERN * ((size + start) // len(WS_PATTERN) + 1))[start:start + size - len(head)]


def ws_closed(ws):
    return ws.state.name != "OPEN"


def close_frame(query):
    payload = bytes.fromhex(query["payload"][0])
    return bytes([0x88, len(payload)]) + payload


def route(target):
    parsed = urlsplit(target)
    return parsed.path.strip("/").split("/"), parse_qs(parsed.query)


class Bench:
    def __init__(self, run):
        self.run = run
        self.tasks = set()
        self.requests = (run / "requests.jsonl").open("a", buffering=1)

    def record(self, method, target, body, protocol, writer, headers=()):
        entry = {"method": method, "target": target, "headers": list(headers),
            "body_sha256": hashlib.sha256(body).hexdigest(), "protocol": protocol,
            "peer_port": writer.get_extra_info("peername")[1], "cipher": writer.get_extra_info("cipher"), "time": round(asyncio.get_running_loop().time(), 6)}
        if "record-body" in target:
            entry["body"] = body.decode("latin-1")
        self.requests.write(json.dumps(entry) + "\n")

    async def accept(self, reader, writer):
        task = asyncio.current_task()
        self.tasks.add(task)
        try:
            tls = writer.get_extra_info("ssl_object")
            if tls and tls.selected_alpn_protocol() == "h2":
                await self.http2(reader, writer)
            else:
                await self.http1(reader, writer)
        except (ConnectionError, asyncio.IncompleteReadError, ssl.SSLError):
            pass
        except Exception:
            logging.exception("connection failed")
        finally:
            writer.close()
            with contextlib.suppress(ConnectionError, ssl.SSLError):
                await writer.wait_closed()
            self.tasks.discard(task)

    async def http1(self, reader, writer):
        while await self.http1_exchange(reader, writer):
            pass

    async def http1_exchange(self, reader, writer):
        head = await reader.readuntil(b"\r\n\r\n")
        first, *lines = head.decode("latin1").split("\r\n")
        method, target, _ = first.split(" ", 2)
        headers = dict(line.split(":", 1) for line in lines if ":" in line)
        headers = {k.lower(): v.strip() for k, v in headers.items()}
        if headers.get("expect", "").lower() == "100-continue":
            writer.write(b"HTTP/1.1 100 Continue\r\n\r\n")
            await writer.drain()
        if headers.get("transfer-encoding", "").lower() == "chunked":
            chunks = []
            while True:
                size = int((await reader.readline()).split(b";", 1)[0], 16)
                if not size:
                    while True:
                        trailer = await reader.readline()
                        if not trailer:
                            raise asyncio.IncompleteReadError(trailer, None)
                        if trailer == b"\r\n":
                            break
                    break
                chunks.append(await reader.readexactly(size))
                await reader.readexactly(2)
            body = b"".join(chunks)
        else:
            body = await reader.readexactly(int(headers.get("content-length", 0)))
        self.record(method, target, body, "http/1.1", writer, [line for line in lines if line])
        parts, query = route(target)
        if parts[0] == "ws":
            await self.websocket(reader, writer, target, head, headers, parts, query)
            return False
        if parts[0] == "range":
            status, fields, payload, declared, chunked, cut = range_response(method, headers, parts, query)
            keep = (not cut and (declared is not None or chunked) and method != "HEAD"
                    and headers.get("connection", "").lower() != "close")
            framing = [("Transfer-Encoding", "chunked")] if chunked else [] if declared is None else [("Content-Length", str(declared))]
            writer.write((f"HTTP/1.1 {status} Bench\r\n" + "".join(f"{k}: {v}\r\n" for k, v in
                [("Connection", "keep-alive" if keep else "close"), *fields, *framing]) + "\r\n").encode("latin1"))
            rate = int(query.get("bps", ["0"])[0])
            step = max(1, rate // 10) if rate else max(1, len(payload))
            for offset in range(0, len(payload), step):
                piece = payload[offset:offset + step]
                writer.write(f"{len(piece):x}\r\n".encode() + piece + b"\r\n" if chunked else piece)
                await writer.drain()
                if rate:
                    await asyncio.sleep(len(piece) / rate)
            if chunked and not cut:
                writer.write(b"0\r\n\r\n")
            await writer.drain()
            return keep
        status, extra, payload, size = self.response(parts, query)
        chunked = parts[0] == "chunked" or "chunked" in query
        keep = ((parts[0] in KEEP_ALIVE or "gzip-body" in query) and method != "HEAD" and first.endswith("HTTP/1.1")
                and headers.get("connection", "").lower() != "close")
        fields = {"Connection": "keep-alive" if keep else "close", **extra}
        if chunked:
            fields["Transfer-Encoding"] = "chunked"
        else:
            fields["Content-Length"] = str(size)
        writer.write((f"HTTP/1.1 {status} Bench\r\n" + "".join(
            f"{k}: {v}\r\n" for k, v in fields.items()) + "\r\n").encode("latin1"))
        await writer.drain()
        if parts[0] == "stall-after-headers":
            await reader.read()
            return False
        if method == "HEAD":
            return False
        if parts[0] == "cut-mid-body":
            writer.write(OK[:len(OK) // 2])
        elif parts[0] == "slow-body":
            rate = max(1, int(query.get("bps", ["1024"])[0]))
            sent = 0
            start = asyncio.get_running_loop().time()
            while sent < size:
                count = min(max(1, rate // 10), size - sent, len(PATTERN))
                writer.write(generated(sent, count))
                await writer.drain()
                sent += count
                await asyncio.sleep(max(0, start + sent / rate - asyncio.get_running_loop().time()))
        elif parts[0] == "big":
            for offset in range(0, size, len(PATTERN)):
                writer.write(generated(offset, min(len(PATTERN), size - offset)))
                await writer.drain()
        elif chunked:
            body = OK if parts[0] == "chunked" else payload
            for piece in (body[:5], body[5:]):
                writer.write(f"{len(piece):x}\r\n".encode() + piece + b"\r\n")
            writer.write(b"0\r\nX-Bench-Trailer: done\r\n\r\n")
        else:
            writer.write(payload)
        await writer.drain()
        return keep

    def response(self, parts, query):
        kind = parts[0]
        if kind == "status":
            status = int(parts[1])
            if status < 200 or status > 599:
                raise ValueError("status must be between 200 and 599")
            extra = {}
            if "retry_after" in query:
                extra["Retry-After"] = query["retry_after"][0]
            body = b"" if status in (204, 304) else OK
            if "bytes" in query:
                count = int(query["bytes"][0])
                body = (PATTERN * (count // len(PATTERN) + 1))[:count]
            return status, extra, body, len(body)
        if kind == "redirect" and parts[1] in ("301", "302", "303", "307", "308"):
            location = query.get("to", ["/ok"])[0]
            if "\r" in location or "\n" in location:
                raise ValueError("invalid redirect location")
            if "gzip-body" in query:
                body = gzip.compress(OK, mtime=0)[:10]
                return int(parts[1]), {"Location": location, "Content-Encoding": "gzip"}, body, len(body)
            return int(parts[1]), {"Location": location}, b"", 0
        if kind in ("big", "slow-body"):
            size = int(parts[1]) if len(parts) > 1 else int(query.get("bytes", ["2048"])[0])
            if size < 0:
                raise ValueError("negative body size")
            return 200, {}, b"", size
        if kind == "encoded":
            count = int(query["bytes"][0]) if "bytes" in query else 0
            body = (PATTERN * (count // len(PATTERN) + 1))[:count] if count else OK
            encoding, payload = encoded(parts[1], body)
            if "cut" in query:
                payload = payload[:int(query["cut"][0])]
            extra = {"Content-Encoding": encoding, "X-Body-SHA256": hashlib.sha256(body).hexdigest(),
                     "X-Encoded-SHA256": hashlib.sha256(payload).hexdigest()}
            return int(query.get("status", ["200"])[0]), extra, payload, len(payload)
        if kind == "wrong-content-length":
            return 200, {}, OK, len(OK) + 7
        if kind in ("ok", "chunked", "cut-mid-body", "stall-after-headers"):
            return 200, {"X-Body-SHA256": hashlib.sha256(OK).hexdigest()}, OK, len(OK)
        return 404, {}, b"unknown scenario\n", 17

    def ws_log(self, target, **fields):
        self.requests.write(json.dumps({"ws_path": target, **fields}) + "\n")

    async def ws_soak(self, reader, writer, ws, target, query):
        """Pushes numbered messages (steady rate, periodic bursts, every big_every-th one big_bytes long), pings, and echoes the
        client's numbered messages after checking them. Any mismatch or a missing pong closes the socket; stats go to requests.jsonl."""
        def number(name, default):
            return float(query.get(name, [default])[0])

        rate, burst, burst_every = number("rate", 10), int(number("burst", 100)), number("burst_every", 30)
        big_every, big_bytes, ping_every = int(number("big_every", 300)), int(number("big_bytes", 70000)), number("ping_every", 10)
        stats = {"pushed": 0, "pushed_bytes": 0, "echoed": 0, "echoed_bytes": 0, "pings": 0, "pongs": 0,
                 "problem": None, "client_close": None, "dropped": False}
        loop = asyncio.get_running_loop()
        started = loop.time()

        def fail(code, reason):
            if stats["problem"] is None:
                stats["problem"] = reason
                if not ws_closed(ws):
                    writer.write(ws.send(CloseConnection(code=code, reason=reason)))

        async def push():
            seq, next_burst = 0, started + burst_every
            while not ws_closed(ws):
                count = 1
                if loop.time() >= next_burst:
                    count, next_burst = burst, next_burst + burst_every
                for _ in range(count):
                    size = big_bytes if seq % big_every == big_every - 1 else 32 + seq * 97 % 4000
                    writer.write(ws.send(TextMessage(data=soak_text("P", seq, size))))
                    stats["pushed"] += 1
                    stats["pushed_bytes"] += size
                    seq += 1
                await writer.drain()
                await asyncio.sleep(1 / rate)

        async def ping():
            while True:
                await asyncio.sleep(ping_every)
                if ws_closed(ws):
                    return
                if stats["pings"] - stats["pongs"] >= 3:
                    fail(1011, "no pong to 3 pings")
                    return
                writer.write(ws.send(Ping(payload=f"soak-{stats['pings']}".encode())))
                stats["pings"] += 1
                await writer.drain()

        tasks = [asyncio.create_task(push()), asyncio.create_task(ping())]
        message, expected = "", 0
        try:
            await writer.drain()
            while data := await reader.read(65536):
                ws.receive_data(data)
                for event in ws.events():
                    if isinstance(event, TextMessage):
                        message += event.data
                        if not event.message_finished:
                            continue
                        if message != soak_text("C", expected, len(message)):
                            fail(1008, f"client message {expected} mismatch")
                        elif not ws_closed(ws):
                            writer.write(ws.send(TextMessage(data=message)))
                            stats["echoed"] += 1
                            stats["echoed_bytes"] += len(message)
                        expected, message = expected + 1, ""
                    elif isinstance(event, Ping):
                        if not ws_closed(ws):
                            writer.write(ws.send(event.response()))
                    elif isinstance(event, Pong):
                        if event.payload != f"soak-{stats['pongs']}".encode():
                            fail(1008, f"pong {stats['pongs']} payload mismatch")
                        stats["pongs"] += 1
                    elif isinstance(event, CloseConnection):
                        stats["client_close"] = event.code
                        if ws.state.name == "REMOTE_CLOSING":
                            writer.write(ws.send(event.response()))
                        await writer.drain()
                        return
                await writer.drain()
            stats["dropped"] = True
        except (ConnectionError, OSError):
            stats["dropped"] = True
        finally:
            for task in tasks:
                task.cancel()
            done = await asyncio.gather(*tasks, return_exceptions=True)
            errors = [repr(e) for e in done if isinstance(e, Exception) and not isinstance(e, asyncio.CancelledError)]
            if errors and stats["problem"] is None:
                stats["problem"] = "; ".join(errors)
            self.ws_log(target, ws_soak={**stats, "seconds": round(loop.time() - started, 1)})

    async def websocket(self, reader, writer, target, head, headers, parts, query):
        if headers.get("authorization") != "Bearer bench-token":
            writer.write(b"HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            await writer.drain()
            return
        mode = parts[1] if len(parts) > 1 else "echo"
        if mode == "redirect":
            location = query.get("to", ["/ws/echo"])[0]
            code = int(query.get("code", ["302"])[0])
            hops = int(query.get("hops", ["1"])[0])
            if "\r" in location or "\n" in location or not 300 <= code <= 399:
                raise ValueError("invalid redirect")
            if hops > 1:
                location = "/ws/redirect?" + urlencode({"to": location, "code": code, "hops": hops - 1})
            writer.write(f"HTTP/1.1 {code} Bench\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".encode())
            await writer.drain()
            return
        if mode == "stall-handshake":
            await reader.read()
            return
        ws = WSConnection(ConnectionType.SERVER)
        ws.receive_data(head)
        list(ws.events())
        handshake = ws.send(AcceptConnection())
        if mode in ("bad-accept", "missing-accept"):
            lines = handshake.split(b"\r\n")
            handshake = b"\r\n".join(b"Sec-WebSocket-Accept: invalid" if line.lower().startswith(
                b"sec-websocket-accept:") else line for line in lines if mode == "bad-accept" or not line.lower().startswith(
                b"sec-websocket-accept:"))
        elif mode == "extension":
            # "~" stands for an empty value: parse_qs drops blank ones.
            value = query.get("value", ["permessage-deflate"])[0].replace("~", "").encode()
            handshake = handshake.replace(b"\r\n\r\n", b"\r\nSec-WebSocket-Extensions: " + value + b"\r\n\r\n", 1)
        elif mode == "duplicate-accept":
            accept = next(line for line in handshake.split(b"\r\n") if line.lower().startswith(b"sec-websocket-accept:"))
            handshake = handshake.replace(b"\r\n\r\n", b"\r\n" + accept + b"\r\n\r\n", 1)
        elif "protocol" in query:
            lines = b"".join(b"Sec-WebSocket-Protocol: " + value.replace("~", "").encode() + b"\r\n" for value in query["protocol"])
            handshake = handshake.replace(b"\r\n\r\n", b"\r\n" + lines + b"\r\n", 1)
        writer.write(handshake)
        # RSV bits without a negotiated extension: a frame curl's own decoder refuses.
        junk = b"\xf1\x00" if "junk" in query else b""
        if mode == "soak":
            await self.ws_soak(reader, writer, ws, target, query)
            return
        if mode == "ping":
            writer.write(ws.send(Ping(payload=b"server-ping")))
        elif mode == "split-ping":
            frame = bytes([0x89, 10]) + b"split-ping"
            writer.write(frame[:5])
            await writer.drain()
            await asyncio.sleep(0.05)
            writer.write(frame[5:])
        elif mode == "fragmented":
            writer.write(ws.send(TextMessage(data="frag-", message_finished=False)))
            writer.write(ws.send(Ping(payload=b"between")))
            writer.write(ws.send(TextMessage(data="mented", message_finished=True)))
        elif mode == "close":
            writer.write((close_frame(query) if "payload" in query else ws.send(CloseConnection(code=1000, reason="bench done"))) + junk)
        elif mode == "big":
            writer.write(ws.send(TextMessage(data=ws_text(0, int(query.get("bytes", ["70000"])[0])))))
        elif mode == "binary":
            writer.write(ws.send(BytesMessage(data=b"\x00\x01binary\xff")))
        elif mode == "fragmented-binary":
            writer.write(ws.send(BytesMessage(data=b"\x00frag", message_finished=False)) + ws.send(Ping(payload=b"between")) +
                         ws.send(BytesMessage(data=b"\xffmented", message_finished=True)) + junk)
        elif mode == "bad-utf8":
            writer.write(b"\x81\x04ok\xff\xfe" + (ws.send(TextMessage(data=query["then"][0])) if "then" in query else b"") + junk)
        elif mode == "slow-read":
            await writer.drain()
            await asyncio.sleep(0.2)
            message = ""
            while not ws_closed(ws) and (data := await reader.read(262144)):
                await asyncio.sleep(0.01)
                ws.receive_data(data)
                for event in ws.events():
                    if isinstance(event, TextMessage):
                        message += event.data
                        if event.message_finished:
                            writer.write(ws.send(TextMessage(data=message)))
                            message = ""
                await writer.drain()
            return
        elif mode == "stall-read":
            await writer.drain()
            await asyncio.sleep(120)
            return
        elif mode == "flood":
            count, size = int(query.get("count", ["64"])[0]), int(query.get("size", ["65536"])[0])
            # A small send buffer and a gap per message keep the client's read loop ahead of the flood, so only its back-pressure can stall it.
            writer.get_extra_info("socket").setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 65536)
            started = asyncio.get_running_loop().time()
            for index in range(count):
                writer.write(ws.send(TextMessage(data=ws_text(index, size))))
                await writer.drain()
                await asyncio.sleep(0.001)
            self.ws_log(target, ws_flood=round(asyncio.get_running_loop().time() - started, 3))
        elif mode == "pong-flood":
            frames = bytes([0x8A, 0]) * 65536
            end = asyncio.get_running_loop().time() + float(query.get("seconds", ["20"])[0])
            while not writer.is_closing() and asyncio.get_running_loop().time() < end:
                writer.write(frames)
                await writer.drain()
                # sleep(0) starves the bench's other connections.
                await asyncio.sleep(0.001)
            return
        await writer.drain()
        message = ""
        binary = b""
        while data := await reader.read(16384):
            ws.receive_data(data)
            for event in ws.events():
                if isinstance(event, TextMessage):
                    message += event.data
                    if event.message_finished:
                        if mode != "discard":
                            writer.write(ws.send(TextMessage(data=message)))
                        message = ""
                elif isinstance(event, BytesMessage):
                    binary += event.data
                    if event.message_finished:
                        if mode != "discard":
                            writer.write(ws.send(BytesMessage(data=binary)))
                        binary = b""
                elif isinstance(event, Ping):
                    writer.write(ws.send(event.response()))
                elif isinstance(event, Pong):
                    self.ws_log(target, ws_pong=event.payload.decode())
                elif isinstance(event, CloseConnection):
                    self.ws_log(target, ws_close=event.code, ws_reason=event.reason)
                    if mode == "no-close-reply" or "silent" in query:
                        after = sum(1 for _ in ws.events())
                        while data := await reader.read(16384):
                            after += len(data)
                        self.ws_log(target, ws_after=after)
                        return
                    if mode == "bad-close-reply":
                        writer.write(close_frame(query))
                    elif mode != "close":
                        writer.write(ws.send(event.response()))
                    await writer.drain()
                    return
            await writer.drain()

    async def http2(self, reader, writer):
        conn = h2.connection.H2Connection(config=h2.config.H2Configuration(client_side=False, header_encoding="utf-8"))
        conn.initiate_connection()
        writer.write(conn.data_to_send())
        streams = {}
        senders = {}
        window = asyncio.Event()

        async def respond(stream_id, headers, body):
            fields = dict(headers)
            method, target = fields[":method"], fields[":path"]
            self.record(method, target, body, "h2", writer, [f"{name}: {value}" for name, value in headers])
            parts, query = route(target)
            cut = False
            rate = 0
            if parts[0] == "range":
                status, fields, payload, declared, _, cut = range_response(method, {k.lower(): v for k, v in headers}, parts, query)
                size = len(payload)
                rate = int(query.get("bps", ["0"])[0])
                fields = [(k.lower(), v) for k, v in fields] + ([] if declared is None else [("content-length", str(declared))])
            else:
                if parts[0] in ("ok", "big", "cut-mid-body", "slow-body", "encoded", "redirect"):
                    status, extra, payload, size = self.response(parts, query)
                else:
                    status, extra, payload, size = 404, {}, b"unsupported h2 scenario\n", 24
                fields = [("content-length", str(size)), *[(k.lower(), v) for k, v in extra.items()]]
                if parts[0] == "slow-body":
                    rate = max(1, int(query.get("bps", ["1024"])[0]))
            conn.send_headers(stream_id, [(":status", str(status)), *fields])
            offset = 0
            limit = size // 2 if parts[0] == "cut-mid-body" else size
            started = asyncio.get_running_loop().time()
            while offset < limit:
                count = min(limit - offset, conn.local_flow_control_window(stream_id), conn.max_outbound_frame_size)
                if rate:
                    count = min(count, max(1, rate // 10))
                if count <= 0:
                    window.clear()
                    await window.wait()
                    continue
                piece = generated(offset, count) if parts[0] in ("big", "slow-body") else payload[offset:offset + count]
                conn.send_data(stream_id, piece)
                offset += len(piece)
                writer.write(conn.data_to_send())
                await writer.drain()
                if rate:
                    await asyncio.sleep(max(0, started + offset / rate - asyncio.get_running_loop().time()))
            if parts[0] == "cut-mid-body" or cut:
                conn.reset_stream(stream_id, error_code=2)
            else:
                conn.end_stream(stream_id)
            writer.write(conn.data_to_send())
            await writer.drain()

        def sender_done(stream_id, task):
            if senders.get(stream_id) is task:
                del senders[stream_id]
            if not task.cancelled() and task.exception() is not None:
                logging.error("h2 response failed: %s", task.exception())

        try:
            while data := await reader.read(65536):
                for event in conn.receive_data(data):
                    if isinstance(event, h2.events.RequestReceived):
                        streams[event.stream_id] = (event.headers, bytearray())
                    elif isinstance(event, h2.events.DataReceived):
                        streams[event.stream_id][1].extend(event.data)
                        conn.acknowledge_received_data(event.flow_controlled_length, event.stream_id)
                    elif isinstance(event, h2.events.StreamEnded):
                        headers, body = streams.pop(event.stream_id)
                        task = asyncio.create_task(respond(event.stream_id, headers, body))
                        senders[event.stream_id] = task
                        task.add_done_callback(lambda done, sid=event.stream_id: sender_done(sid, done))
                    elif isinstance(event, h2.events.StreamReset):
                        streams.pop(event.stream_id, None)
                        task = senders.get(event.stream_id)
                        if task is not None:
                            task.cancel()
                    elif isinstance(event, (h2.events.WindowUpdated, h2.events.RemoteSettingsChanged)):
                        window.set()
                    elif isinstance(event, h2.events.ConnectionTerminated):
                        return
                writer.write(conn.data_to_send())
                await writer.drain()
        finally:
            for task in senders.values():
                task.cancel()
            await asyncio.gather(*senders.values(), return_exceptions=True)


def stop_event_name(pid):
    return f"ugohttp-bench-stop-{pid}"


def watch_stop_event(loop, stop):
    """Windows has no SIGTERM: a stopper sets a named event keyed by the bench's pid, which, unlike a pipe,
    outlives the caller's domain reloads."""
    import ctypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateEventW.restype = ctypes.c_void_p
    kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    event = kernel32.CreateEventW(None, True, False, stop_event_name(os.getpid()))
    if not event:
        raise ctypes.WinError(ctypes.get_last_error())
    threading.Thread(target=lambda: (kernel32.WaitForSingleObject(event, 0xFFFFFFFF), loop.call_soon_threadsafe(stop.set)),
                     daemon=True).start()


async def main(host):
    started = asyncio.get_running_loop().time()
    base = ROOT / "tmp" / "bench"
    base.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="run-", dir=base))
    logging.basicConfig(filename=run / "bench.log", level=logging.INFO)
    asyncio.get_running_loop().set_exception_handler(lambda loop, ctx: logging.error("asyncio: %s", ctx))
    bench = Bench(run)
    ca = trustme.CA()
    ca.cert_pem.write_to_path(run / "ca.pem")
    now = datetime.datetime.now(datetime.timezone.utc)
    self_signed = trustme.CA().issue_cert("127.0.0.1")
    leaf = x509.load_pem_x509_certificate(self_signed.cert_chain_pems[0].bytes())
    key = serialization.load_pem_private_key(self_signed.private_key_pem.bytes(), password=None)
    certificate = (x509.CertificateBuilder().subject_name(leaf.subject).issuer_name(leaf.subject)
        .public_key(key.public_key()).serial_number(leaf.serial_number)
        .not_valid_before(leaf.not_valid_before_utc).not_valid_after(leaf.not_valid_after_utc)
        .add_extension(leaf.extensions.get_extension_for_class(x509.SubjectAlternativeName).value, critical=False)
        .sign(key, hashes.SHA256()))
    (run / "self-signed.pem").write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
    self_signed.private_key_pem.write_to_path(run / "self-signed.key")
    certs = {
        "https": ca.issue_cert(*dict.fromkeys(("127.0.0.1", "localhost", "10.0.2.2", host))),
        "https1": ca.issue_cert("127.0.0.1", "localhost"),
        "tls12": ca.issue_cert("127.0.0.1", "localhost"),
        "expired": ca.issue_cert("127.0.0.1", not_before=now - datetime.timedelta(days=3), not_after=now - datetime.timedelta(days=1)),
        "wrong_hostname": ca.issue_cert("wrong.example.invalid"),
        "self_signed": self_signed,
        "incomplete_chain": ca.create_child_ca().issue_cert("127.0.0.1"),
    }
    servers = []
    endpoints = {}
    for name in ("http", *certs):
        ctx = None
        if name != "http":
            ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            cert = certs[name]
            if name == "self_signed":
                ctx.load_cert_chain(run / "self-signed.pem", run / "self-signed.key")
            elif name == "incomplete_chain":
                cert.cert_chain_pems[0].write_to_path(run / "incomplete.pem")
                cert.private_key_pem.write_to_path(run / "incomplete.key")
                ctx.load_cert_chain(run / "incomplete.pem", run / "incomplete.key")
            else:
                cert.configure_cert(ctx)
            ctx.set_alpn_protocols(["h2", "http/1.1"] if name == "https" else ["http/1.1"])
            if name == "tls12":
                ctx.maximum_version = ssl.TLSVersion.TLSv1_2
        server = await asyncio.start_server(bench.accept, host, 0, ssl=ctx)
        servers.append(server)
        endpoints[name] = f"{'https' if ctx else 'http'}://{host}:{server.sockets[0].getsockname()[1]}"
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    if sys.platform == "win32":
        watch_stop_event(loop, stop)
    else:
        for sig in (signal.SIGTERM, signal.SIGINT):
            loop.add_signal_handler(sig, stop.set)
    ready = {"ready": True, "pid": os.getpid(), "ca_pem": str(run / "ca.pem"), "endpoints": endpoints}
    (run / "ready.json").write_text(json.dumps(ready) + "\n")
    logging.info("ready in %.3fs", asyncio.get_running_loop().time() - started)
    print(json.dumps(ready), flush=True)
    await stop.wait()
    for server in servers:
        server.close()
    for server in servers:
        server.abort_clients()
    for task in tuple(bench.tasks):
        task.cancel()
    await asyncio.gather(*bench.tasks, return_exceptions=True)
    await asyncio.gather(*(server.wait_closed() for server in servers))
    bench.requests.close()
    logging.shutdown()
    shutil.rmtree(run)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1", help="IPv4 address to listen on; also put into the https certificate")
    parser.add_argument("--stderr", type=Path, help="Write stderr to this file (for callers that cannot redirect it)")
    args = parser.parse_args()
    if args.stderr:
        sys.stderr = open(args.stderr, "w", buffering=1)
    asyncio.run(main(args.host))
