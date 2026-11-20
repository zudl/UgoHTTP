import hashlib
import json
import os
from pathlib import Path
import selectors
import shlex
import signal
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
PINS = dict(shlex.split(line)[0].split('=', 1) for line in (ROOT / 'scripts/versions.env').read_text().splitlines()
            if line and not line.startswith('#'))


class Unavailable(Exception):
    pass


class Failed(Exception):
    pass


def log(message):
    print(message, file=sys.stderr, flush=True)


def run(args, *, check=True, timeout=120):
    args = [str(arg) for arg in args]
    log('$ ' + shlex.join(args))
    result = subprocess.run(args, cwd=ROOT, timeout=timeout, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT)
    if result.stdout.strip():
        log(result.stdout.rstrip())
    if check and result.returncode:
        raise Failed(f'{shlex.join(args)} exited {result.returncode}')
    return result.stdout


def stop_process(process, *, group=False, timeout=30):
    def send(sig):
        try:
            if group:
                os.killpg(process.pid, sig)
            else:
                process.send_signal(sig)
        except ProcessLookupError:
            pass

    send(signal.SIGTERM)
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        send(signal.SIGKILL)
        process.wait()


class Lines:
    def __init__(self, stream):
        self.fd, self.pending, self.closed = stream.fileno(), b'', False
        self.selector = selectors.DefaultSelector()
        self.selector.register(stream, selectors.EVENT_READ)

    def read(self, timeout):
        if not self.closed and self.selector.select(timeout=timeout):
            chunk = os.read(self.fd, 65536)
            self.closed = not chunk
            self.pending += chunk
        *complete, self.pending = self.pending.split(b'\n')
        return [line.decode(errors='replace').rstrip('\r') for line in complete]


def start_bench(*args):
    bench = subprocess.Popen([str(ROOT / 'scripts/bench'), *args], cwd=ROOT, text=True,
                             stdout=subprocess.PIPE, stderr=sys.stderr)
    try:
        deadline = time.monotonic() + 60
        lines = Lines(bench.stdout)
        try:
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise Failed('bench did not publish readiness in 60 s')
                complete = lines.read(remaining)
                if complete:
                    ready = json.loads(complete[0])
                    break
                if lines.closed:
                    raise Failed('bench ended before readiness')
        finally:
            lines.selector.close()
        if not ready.get('ready'):
            raise Failed('bench did not publish readiness')
    except BaseException:
        stop_process(bench, timeout=10)
        raise
    return bench, ready


def check_posts(checks, ready, background_interval=None):
    requests = [json.loads(line) for line in
                (Path(ready['ca_pem']).parent / 'requests.jsonl').read_text().splitlines()]
    detached = 'UgoHTTP detached POST: \u03c0 \u2603\n'.encode('utf-8')
    posts = [
        ('post_body_on_server', '/ok?post=memory', checks.pop('post_memory_sha256', None), None),
        ('detached_post_on_server', '/ok?post=detached', hashlib.sha256(detached).hexdigest(), 'foreground'),
    ]
    if background_interval is not None:
        posts.append(('background_detached_post_on_server', '/slow-body/2000?bps=200&post=detached-background',
                      hashlib.sha256(b'x' * 65536 + detached).hexdigest(), 'background'))
    for name, target, sha, header in posts:
        matches = [r for r in requests if r.get('method') == 'POST' and r['target'] == target
                   and r['body_sha256'] == sha and (header is None or any(
                       key.lower() == 'x-ugo-detached' and value.strip() == header
                       for key, value in (line.split(':', 1) for line in r['headers'])))]
        checks[name] = 'pass' if matches else f'fail: no POST {target} with body sha256 {sha} and header {header} on the bench'
        if header == 'background':
            start, end = background_interval
            checks['background_detached_post_while_backgrounded'] = (
                'pass' if start is not None and any(start <= r['time'] <= end for r in matches)
                else 'fail: no full detached POST body received while in the background')
