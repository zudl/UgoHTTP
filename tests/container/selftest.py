import asyncio
import errno
import json
import os
from pathlib import Path
import platform
import subprocess

SLICE = "linux-arm64" if platform.machine() == "aarch64" else "linux-x86_64"


async def command(*args):
    process = await asyncio.create_subprocess_exec(*args, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    stdout, stderr = await asyncio.wait_for(process.communicate(), 15)
    return process.returncode, stdout.decode(), stderr.decode()


async def main():
    results = []

    async def fault(*args):
        code, out, err = await command("fault", *args)
        assert code == 0, (args, code, out, err)
        return out

    async def handle(reader, writer):
        try:
            await reader.readuntil(b"\r\n\r\n")
            writer.write(b"HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nok\n")
            await writer.drain()
        finally:
            writer.close()
            await writer.wait_closed()

    server = await asyncio.start_server(handle, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    url = f"http://127.0.0.1:{port}/"

    async def curl(target=url, timeout="0.3"):
        return await command("curl", "--noproxy", "*", "-sS", "--connect-timeout", timeout,
            "--max-time", "3", "-w", "\n%{time_connect}", target)

    try:
        code, out, err = await curl()
        assert code == 0 and out.startswith("ok\n"), (code, out, err)
        native_code, native_out, native_err = await command(f"/work/out/{SLICE}/smoke/ugo-smoke", url)
        native = json.loads(native_out)
        assert native_code == 0 and native["curl_code"] == 0 and native["http_status"] == 200, (native_code, native, native_err)
        results.append({"scenario": f"native-{SLICE}", "pass": True, "probe": native})
        results.append({"scenario": "baseline", "pass": True, "curl_exit": code})
        await fault("blackhole", str(port))
        code, out, err = await curl()
        assert code == 28 and float(out.strip()) == 0, (code, out, err)
        results.append({"scenario": "blackhole", "pass": True, "curl_exit": code, "time_connect": 0})
        await fault("online")
        await fault("slow-connect", "250")
        code, out, err = await curl(timeout="2")
        delay = float(out.splitlines()[-1])
        assert code == 0 and 0.225 <= delay < 1.5, (code, delay, out, err)
        results.append({"scenario": "slow-connect", "pass": True, "curl_exit": code, "time_connect": delay})
        await fault("online")
        for mode, expected in (("nxdomain", 3), ("servfail", 2), ("ok", 0)):
            name = "bench.invalid"
            await fault("dns", mode, name)
            rcode = json.loads(await fault("rcode", name))["rcode"]
            code, out, err = await curl(f"http://{name}:{port}/", timeout="2")
            assert rcode == expected and code == (0 if mode == "ok" else 6), (mode, rcode, code, out, err)
            results.append({"scenario": "dns-" + mode, "pass": True, "rcode": rcode, "curl_exit": code})
        await fault("offline")
        code, out, err = await curl()
        assert code == 28, (code, out, err)
        await fault("online")
        restored, out, err = await curl()
        assert restored == 0 and out.startswith("ok\n"), (restored, out, err)
        results.append({"scenario": "offline-online", "pass": True, "offline_exit": code, "online_exit": restored})
        target = Path("/fault-disk/full")
        caught = None
        try:
            with target.open("wb", buffering=0) as file:
                while True:
                    file.write(b"x" * 65536)
        except OSError as error:
            caught = error.errno
        finally:
            target.unlink(missing_ok=True)
        assert caught == errno.ENOSPC, caught
        results.append({"scenario": "tmpfs-enospc", "pass": True, "errno": caught})
        _, version, _ = await command("curl", "--version")
        _, packages, _ = await command("dpkg-query", "-W")
        print(json.dumps({"pass": True, "platform": os.uname().machine, "curl_version": version.splitlines()[0],
            "packages": packages.splitlines(), "scenarios": results}))
    finally:
        await fault("online")
        server.close()
        await server.wait_closed()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as error:
        print(json.dumps({"pass": False, "error": repr(error)}))
        raise
