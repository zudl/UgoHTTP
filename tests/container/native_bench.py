import asyncio
import json
from pathlib import Path
import signal
import shutil
import ssl
import sys
import tempfile

import trustme

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tests/bench'))
from server import Bench


async def main():
    base = ROOT / 'tmp/bench'
    base.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix='native-', dir=base))
    bench = Bench(run)
    ca = trustme.CA()
    ca.cert_pem.write_to_path(run / 'ca.pem')
    trustme.CA().cert_pem.write_to_path(run / 'self-signed.pem')
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ca.issue_cert('127.0.0.1', 'localhost').configure_cert(context)
    context.set_alpn_protocols(['h2', 'http/1.1'])
    context1 = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ca.issue_cert('127.0.0.1', 'localhost').configure_cert(context1)
    context1.set_alpn_protocols(['http/1.1'])
    context12 = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ca.issue_cert('127.0.0.1', 'localhost').configure_cert(context12)
    context12.maximum_version = ssl.TLSVersion.TLSv1_2
    http = await asyncio.start_server(bench.accept, '127.0.0.1', 0)
    https = await asyncio.start_server(bench.accept, '127.0.0.1', 0, ssl=context)
    https1 = await asyncio.start_server(bench.accept, '127.0.0.1', 0, ssl=context1)
    tls12 = await asyncio.start_server(bench.accept, '127.0.0.1', 0, ssl=context12)
    endpoints = {'http': f'http://127.0.0.1:{http.sockets[0].getsockname()[1]}',
                 'https': f'https://127.0.0.1:{https.sockets[0].getsockname()[1]}',
                 'https1': f'https://127.0.0.1:{https1.sockets[0].getsockname()[1]}',
                 'tls12': f'https://127.0.0.1:{tls12.sockets[0].getsockname()[1]}'}
    print(json.dumps({'ready': True, 'ca_pem': str(run / 'ca.pem'), 'endpoints': endpoints}), flush=True)
    stop = asyncio.Event()
    for sig in (signal.SIGTERM, signal.SIGINT):
        asyncio.get_running_loop().add_signal_handler(sig, stop.set)
    await stop.wait()
    for server in (http, https, https1, tls12):
        server.close()
    for task in tuple(bench.tasks):
        task.cancel()
    await asyncio.gather(*bench.tasks, return_exceptions=True)
    bench.requests.close()
    shutil.rmtree(run)


if __name__ == '__main__':
    asyncio.run(main())
