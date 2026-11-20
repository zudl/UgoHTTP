import asyncio
import json
from pathlib import Path
import subprocess
import runpy
import sys
import tempfile

import trustme

ROOT = Path('/work')
sys.path.insert(0, str(ROOT / 'tests/bench'))
from server import Bench


def run(*args):
    subprocess.run([str(arg) for arg in args], check=True)


async def main():
    native_tests = runpy.run_path(str(ROOT / 'native/tests/run'))
    output = native_tests['container_deps']('tsan')
    flags = native_tests['FLAGS']['tsan']
    smoke = output / 'smoke'
    smoke.mkdir(exist_ok=True)
    (smoke / 'ca_bundle.h').write_text('static const char ugo_ca_bundle[] = "";\n')
    run('cmake', '-S', ROOT / 'native/smoke', '-B', smoke, '-G', 'Ninja',
        '-DUGO_OUTPUT=' + str(output), '-DCMAKE_C_COMPILER=clang',
        '-DCMAKE_C_FLAGS=' + flags, '-DCMAKE_EXE_LINKER_FLAGS=' + flags)
    run('cmake', '--build', smoke, '--parallel', '6')
    with tempfile.TemporaryDirectory(prefix='bench-', dir=output) as directory:
        directory = Path(directory)
        bench = Bench(directory)
        ca = trustme.CA()
        ca.cert_pem.write_to_path(directory / 'ca.pem')
        import ssl
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ca.issue_cert('127.0.0.1').configure_cert(context)
        context.set_alpn_protocols(['h2', 'http/1.1'])
        server = await asyncio.start_server(bench.accept, '127.0.0.1', 0, ssl=context)
        url = f'https://127.0.0.1:{server.sockets[0].getsockname()[1]}/ok'
        print(json.dumps({'ready': True, 'url': url}), flush=True)
        try:
            process = await asyncio.create_subprocess_exec(str(smoke / 'ugo-concurrent-tls'), url, str(directory / 'ca.pem'))
            result = await asyncio.wait_for(process.wait(), 120)
            if result:
                raise RuntimeError(f'TSan client exited {result}')
        finally:
            if process.returncode is None:
                process.kill()
                await process.wait()
            server.close()
            for task in tuple(bench.tasks):
                task.cancel()
            await asyncio.gather(*bench.tasks, return_exceptions=True)
            await server.wait_closed()
            bench.requests.close()


if __name__ == '__main__':
    asyncio.run(main())
