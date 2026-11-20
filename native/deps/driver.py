#!/usr/bin/env python3
from concurrent.futures import ThreadPoolExecutor, as_completed
import contextlib
import functools
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import selectors
import shlex
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
from urllib.parse import unquote
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PINS = dict(shlex.split(line)[0].split('=', 1) for line in (ROOT / 'scripts/versions.env').read_text().splitlines()
            if line and not line.startswith('#'))
# target: (platform, architecture as the toolchain names it)
SLICES = {
    'macos-arm64': ('macos', 'arm64'),
    'macos-x86_64': ('macos', 'x86_64'),
    'ios-arm64': ('ios', 'arm64'),
    'ios-simulator-arm64': ('ios-simulator', 'arm64'),
    'ios-simulator-x86_64': ('ios-simulator', 'x86_64'),
    'linux-arm64': ('linux', 'aarch64'),
    'linux-x86_64': ('linux', 'x86_64'),
    'android-arm64': ('android', 'arm64-v8a'),
    'android-armv7': ('android', 'armeabi-v7a'),
    'android-x86_64': ('android', 'x86_64'),
    'windows-x64': ('windows', 'x86_64'),
    'windows-arm64': ('windows', 'aarch64'),
}
FAT = {'macos-universal': ('macos-arm64', 'macos-x86_64'),
       'ios-xcframework': ('ios-arm64', 'ios-simulator-arm64', 'ios-simulator-x86_64')}
TARGETS = (*SLICES, *FAT)
EXPORTS = sorted(['ugo_monotonic_us', 'ugo_client_create', 'ugo_client_shutdown', 'ugo_client_destroy',
                  'ugo_shutdown_all', 'ugo_submit', 'ugo_abort', 'ugo_release', 'ugo_get_result',
                  'ugo_wait_events', 'ugo_ws_open', 'ugo_ws_send', 'ugo_ws_close',
                  'ugo_ws_release'])
SHIM = {'macos': 'libugo.dylib', 'ios': 'libugo.a', 'ios-simulator': 'libugo.a', 'linux': 'libugo.so',
        'android': 'libugo.so', 'windows': 'ugo.dll'}
PLUGIN = {'macos-universal': 'Plugins/macOS/ugo.bundle', 'ios-xcframework': 'Plugins/iOS/libugo.xcframework',
          'linux-x86_64': 'Plugins/Linux/x86_64/libugo.so',
          'linux-arm64': 'Plugins/Linux/arm64/libugo.so',
          'android-arm64': 'Plugins/Android/arm64-v8a/libugo.so',
          'android-armv7': 'Plugins/Android/armeabi-v7a/libugo.so',
          'android-x86_64': 'Plugins/Android/x86_64/libugo.so',
          'windows-x64': 'Plugins/Windows/x86_64/ugo.dll',
          'windows-arm64': 'Plugins/Windows/arm64/ugo.dll'}
WINDOWS_HOST = os.name == 'nt'
# The Windows host builds and checks every slice except the Apple ones; packaging and releases stay on macOS.
HOST_TARGETS = tuple(t for t in TARGETS if not t.startswith(('macos', 'ios'))) if WINDOWS_HOST else TARGETS
CACHE = Path.home() / '.cache/ugohttp/src'
TOOLS_CACHE = Path.home() / '.cache/ugohttp/tools'
WINDOWS_HOST_TOOLS = ('PYTHON', 'UV', 'CMAKE', 'NINJA', 'ZIG', 'CURL', 'NDK')
AVD_SDK = Path(os.environ.get('UGOHTTP_AVD_SDK', PINS['ANDROID_AVD_SDK_ROOT']))
AVD_PACKAGES = {'emulator': 'emulator/emulator',
                PINS['ANDROID_AVD_IMAGE']: PINS['ANDROID_AVD_IMAGE'].replace(';', '/') + '/system.img'}
if WINDOWS_HOST:
    UNITY = Path('C:/Program Files/Unity/Hub/Editor') / PINS['UNITY_VERSION']
    EDITOR = UNITY / 'Editor/Unity.exe'
else:
    UNITY = Path('/Applications/Unity/Hub/Editor') / PINS['UNITY_VERSION']
    EDITOR = UNITY / 'Unity.app/Contents/MacOS/Unity'
OUT = ROOT / 'out'
ENV = os.environ.copy()


def tool_home(name):
    """The extracted archive of Windows host tool `name`, entered if it holds a single directory."""
    home = TOOLS_CACHE / PINS[f'WINDOWS_HOST_{name}_SHA256']
    entries = list(home.iterdir()) if home.is_dir() else []
    return entries[0] if len(entries) == 1 and entries[0].is_dir() else home


CALLER_PATH = ENV.get('PATH', '')


def use_windows_tools():
    """Puts the cached Windows host tools first on PATH; runs again once bootstrap has extracted them."""
    global BENCH_CURL
    homes = [tool_home(name) for name in WINDOWS_HOST_TOOLS if name != 'NDK']
    ENV['PATH'] = os.pathsep.join([str(home / 'bin' if (home / 'bin').is_dir() else home) for home in homes]
                                  + [CALLER_PATH])
    BENCH_CURL = tool_home('CURL') / 'bin/curl.exe'
    ENV['UGOHTTP_CURL'] = str(BENCH_CURL)
    # CreateProcess resolves a bare program name on this process's PATH, not on the child's env.
    os.environ.update({key: ENV[key] for key in ('PATH', 'UGOHTTP_CURL')})


# Pinned tools come from the Brewfile or the tool cache; other installs earlier on the caller's PATH
# (e.g. ~/.local/bin/uv) must not win.
if WINDOWS_HOST:
    SYSTEM_CURL = Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/curl.exe'
    use_windows_tools()
else:
    if BREW := shutil.which('brew'):
        BREW_PREFIX = Path(BREW).parents[1]
        ENV['JAVA_HOME'] = str(BREW_PREFIX / 'opt/openjdk@21/libexec/openjdk.jdk/Contents/Home')
        ENV['PATH'] = os.pathsep.join([ENV['JAVA_HOME'] + '/bin', str(BREW_PREFIX / 'bin'), CALLER_PATH])
    SYSTEM_CURL = BENCH_CURL = Path('/usr/bin/curl')
    ENV['UGOHTTP_CURL'] = str(BENCH_CURL)
ENV['ZIG_GLOBAL_CACHE_DIR'] = str(OUT / 'zig-cache')
ENV['ZIG_LOCAL_CACHE_DIR'] = str(OUT / 'zig-local-cache')
# Would pass -j to ninja, and an explicit -j makes ninja ignore the jobserver.
ENV.pop('CMAKE_BUILD_PARALLEL_LEVEL', None)


def run(args, **kwargs):
    return subprocess.run([str(x) for x in args], cwd=ROOT, env=ENV, check=True, **kwargs)


def capture(args):
    return run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True).stdout.strip()


def emit(value):
    print(json.dumps(value, separators=(',', ':')), flush=True)


def script(path):
    """The command running repository script `path`: Windows cannot execute a shebang file."""
    if not WINDOWS_HOST:
        return [path]
    with open(path, 'rb') as file:
        shebang = file.readline()
    if b'python' in shebang:
        return [sys.executable, path]
    sh = shutil.which('sh', path=ENV['PATH'])
    if not sh:
        raise RuntimeError('sh missing: run from Git Bash')
    return [sh, path]


def ndk_path():
    if WINDOWS_HOST:
        return tool_home('NDK')
    sdk = Path(ENV.get('ANDROID_SDK_ROOT') or ENV.get('ANDROID_HOME') or
               str(Path.home() / 'Library/Android/sdk'))
    return sdk / 'ndk' / PINS['NDK_VERSION']


@functools.cache
def download(name):
    url, sha = PINS[name + '_URL'], PINS[name + '_SHA256']
    return fetch(url, sha, CACHE, sha + '-' + url.rsplit('/', 1)[1], name)


def fetch(url, sha, cache, filename, name=None):
    """`url` in the immutable content-addressed `cache`, checked against `sha`."""
    name = name or url
    cache.mkdir(parents=True, exist_ok=True)
    dest = cache / filename
    if not dest.exists():
        fd, temp = tempfile.mkstemp(prefix='.download-', dir=cache)
        os.close(fd)
        try:
            run([SYSTEM_CURL, '--fail', '--location', '--silent', '--show-error',
                 '--retry', '3', '--output', temp, url], stdout=sys.stderr)
            if file_sha256(temp) != sha:
                raise RuntimeError(f'{name}: downloaded SHA256 mismatch')
            if WINDOWS_HOST:
                # Windows cannot unlink a read-only link, and rename never replaces an existing file.
                with contextlib.suppress(FileExistsError):
                    os.rename(temp, dest)
                    os.chmod(dest, 0o444)
            else:
                os.chmod(temp, 0o444)
                with contextlib.suppress(FileExistsError):
                    os.link(temp, dest)
        finally:
            Path(temp).unlink(missing_ok=True)
    if file_sha256(dest) != sha:
        raise RuntimeError(f'{name}: cached SHA256 mismatch: {dest}')
    return dest


def file_sha256(path):
    with open(path, 'rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def install_tool(name):
    """Extracts Windows host tool `name` into the immutable tool cache, published once by rename."""
    home = TOOLS_CACHE / PINS[f'WINDOWS_HOST_{name}_SHA256']
    if home.is_dir():
        return
    archive = download(f'WINDOWS_HOST_{name}')
    work = Path(tempfile.mkdtemp(prefix='.extract-', dir=TOOLS_CACHE))
    try:
        if archive.name.endswith('.zip'):
            with zipfile.ZipFile(archive) as file:
                file.extractall(work)
        else:
            with tarfile.open(archive) as file:
                file.extractall(work, filter='data')
        with contextlib.suppress(FileExistsError):
            os.rename(work, home)
    finally:
        shutil.rmtree(work, ignore_errors=True)


TOOLS = {
    'cmake': (['cmake', '--version'], r'cmake version ([\d.]+)', 'CMAKE'),
    'ninja': (['ninja', '--version'], r'([\d.]+)', 'NINJA'),
    'zig': (['zig', 'version'], r'([\d.]+)', 'ZIG'),
    'uv': (['uv', '--version'], r'uv ([\d.]+)', 'UV'),
    'python': (['python3', '--version'], r'Python ([\d.]+)', 'PYTHON_MIN'),
    'java': (['java', '--version'], r'openjdk ([\d.]+)', 'JAVA'),
    'xcode': (['xcodebuild', '-version'], r'Xcode ([\d.]+)', 'XCODE'),
    'sdkmanager': (['sdkmanager', '--version'], r'(?m)^([\d.]+)$', 'SDKMANAGER'),
    'docker': (['docker', '--version'], r'Docker version ([\d.]+)', 'DOCKER'),
    'dotnet': (['dotnet', '--version'], r'([\d.]+)', 'DOTNET'),
}
if WINDOWS_HOST:
    # Docker Desktop is a system install with its own pin; Java, Android command-line tools and Xcode serve
    # only macOS-host steps.
    for name in ('java', 'xcode', 'sdkmanager'):
        del TOOLS[name]
    TOOLS.update(python=([sys.executable, '--version'], r'Python ([\d.]+)', 'PYTHON_MIN'),
                 docker=(['docker', '--version'], r'Docker version ([\d.]+)', 'WINDOWS_HOST_DOCKER'))
SOURCES = ('CURL', 'MBEDTLS', 'NGHTTP2', 'ZLIB', 'CA')
WINE = ROOT / 'tests/wine'
# Windows architecture: (base image pin, docker platform, package lock). Debian's Wine runs PE of its own
# architecture only.
WINE_RUNNERS = {'x86_64': ('WINE_BASE_IMAGE', 'linux/amd64', 'packages.lock'),
                'aarch64': ('WINE_ARM64_BASE_IMAGE', 'linux/arm64', 'packages-arm64.lock')}
if WINDOWS_HOST:
    # x64 PE runs natively. Wine arm64 under Docker Desktop's QEMU user emulation crashes ugo-smoke.exe in its
    # CRT startup, so arm64 PE gets static checks only on this host.
    WINE_RUNNERS.clear()
# Per-platform manifest digests for x86_64: the classic docker store keeps one platform per reference.
SMOKE_IMAGES = (('LINUX_SMOKE_IMAGE', 'linux/arm64'), ('LINUX_X86_64_SMOKE_IMAGE', 'linux/amd64'),
                *((base, platform) for base, platform, _ in WINE_RUNNERS.values()))
PE_MACHINE = {'x86_64': 'IMAGE_FILE_MACHINE_AMD64', 'aarch64': 'IMAGE_FILE_MACHINE_ARM64'}
# DLLs Windows 10+ always has. The C runtime is UCRT through its API sets; mingw's own DLLs
# (libwinpthread-1, libgcc_s_seh-1, libstdc++-6) must never appear.
WINDOWS_IMPORTS = {'kernel32.dll', 'ws2_32.dll', 'bcrypt.dll', 'iphlpapi.dll', 'api-ms-win-core-realtime-l1-1-1.dll'}
WINDOWS_CRT_PREFIX = 'api-ms-win-crt-'
# Armv8 crypto extensions, optional on Windows on Arm.
ARM64_CRYPTO = {'aese', 'aesd', 'aesmc', 'aesimc', 'pmull', 'pmull2'}
UGO_CLASS_TLS = 3


def cached(name):
    url, sha = PINS[name + '_URL'], PINS[name + '_SHA256']
    return CACHE / (sha + '-' + url.rsplit('/', 1)[1])


def wine_key(arch):
    base, _, lock = WINE_RUNNERS[arch]
    digest = hashlib.sha256()
    for name in ('Dockerfile', lock):
        digest.update((WINE / name).read_bytes())
    digest.update(PINS[base].encode())
    return digest.hexdigest()


def wine_image(arch):
    """The image id of the Wine runner for `arch`, or None if ./scripts/bootstrap has not built it."""
    marker = OUT / 'wine' / (wine_key(arch) + '.iid')
    if not marker.is_file():
        return None
    image = marker.read_text().strip()
    if not image_present(image, WINE_RUNNERS[arch][1]):
        return None
    return image


def build_wine_image(arch):
    base, platform, lock = WINE_RUNNERS[arch]
    key = wine_key(arch)
    OUT.mkdir(exist_ok=True)
    context = Path(tempfile.mkdtemp(prefix='build.', dir=OUT))
    try:
        (context / 'packages').mkdir()
        for line in (WINE / lock).read_text().splitlines():
            sha, url = line.split()
            deb = fetch(url, sha, CACHE.parent / 'deb', sha + '.deb')
            shutil.copyfile(deb, context / 'packages' / unquote(url.rsplit('/', 1)[1]))
        shutil.copyfile(WINE / 'Dockerfile', context / 'Dockerfile')
        iid = context / 'image.iid'
        run(['docker', 'build', '--platform', platform, '--build-arg', 'BASE_IMAGE=' + PINS[base],
             '--tag', 'ugohttp-wine:' + key, '--iidfile', iid, context], stdout=sys.stderr)
        (OUT / 'wine').mkdir(parents=True, exist_ok=True)
        shutil.move(iid, OUT / 'wine' / (key + '.iid'))
    finally:
        shutil.rmtree(context, ignore_errors=True)


def image_present(image, platform):
    """True if `image` is local for `platform`; a wrong-platform copy under the same reference is an error."""
    # An unreachable or paused daemon must not read as a missing image.
    daemon = subprocess.run(['docker', 'info', '--format', '{{.DriverStatus}}'], env=ENV, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True, timeout=120)
    if daemon.returncode:
        raise RuntimeError('docker daemon unavailable: ' + daemon.stderr.strip())
    # The containerd image store keeps several platforms per reference and, unless asked for one, reports
    # the host's even when only another platform's content is local.
    containerd = 'io.containerd.snapshotter' in daemon.stdout
    result = subprocess.run(['docker', 'image', 'inspect', *(['--platform', platform] if containerd else []),
                             '--format', '{{.Os}}/{{.Architecture}}', image], env=ENV,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if result.returncode == 0:
        if result.stdout.strip() != platform:
            raise RuntimeError(f'docker image {image} is {result.stdout.strip()}, expected {platform}; '
                               'remove that local copy and run ./scripts/bootstrap')
        return True
    if 'No such image' in result.stderr or 'does not provide the specified platform' in result.stderr:
        return False
    raise RuntimeError('docker image inspect failed: ' + result.stderr.strip())


def version_key(version):
    return tuple(int(part) for part in version.split('.'))


def check(extra_problems=()):
    """Reads versions and caches only: installs, downloads and pulls nothing."""
    tools, problems = {}, list(extra_problems)
    for name, (command, pattern, pin) in TOOLS.items():
        expected = PINS.get(pin + '_VERSION')
        try:
            match = re.search(pattern, capture(command))
            actual = match.group(1) if match else 'unknown'
            tools[name] = actual
            if expected is None:
                problems.append(f'{name}: {pin}_VERSION missing in scripts/versions.env')
            elif pin.endswith('_MIN') and actual != 'unknown':
                if version_key(actual) < version_key(expected):
                    problems.append(f'{name}: expected {expected} or newer, got {actual}')
            elif actual != expected:
                problems.append(f'{name}: expected {expected}, got {actual}')
        except (OSError, subprocess.CalledProcessError) as error:
            tools[name] = None
            problems.append(f'{name}: {error}')
    try:
        if not EDITOR.is_file():
            raise FileNotFoundError(EDITOR)
        # Unity Hub installs every Windows editor into a directory named by its version.
        tools['unity'] = (UNITY.name if WINDOWS_HOST else
                          plistlib.loads((EDITOR.parents[1] / 'Info.plist').read_bytes())['CFBundleVersion'])
        if tools['unity'] != PINS['UNITY_VERSION']:
            problems.append(f'unity: expected {PINS["UNITY_VERSION"]}, got {tools["unity"]}')
    except (OSError, ValueError, KeyError) as error:
        tools['unity'] = None
        problems.append(f'unity: install Unity {PINS["UNITY_VERSION"]}: {error}')
    projects = {}
    for project in ('tests/unity', 'tests/player'):
        try:
            text = (ROOT / project / 'ProjectSettings/ProjectVersion.txt').read_text()
            match = re.search(r'^m_EditorVersion: (.+)$', text, re.MULTILINE)
            projects[project] = match.group(1) if match else 'unknown'
            if projects[project] != PINS['UNITY_VERSION']:
                problems.append(f'{project}: expected Unity {PINS["UNITY_VERSION"]}, got {projects[project]}')
        except OSError as error:
            projects[project] = None
            problems.append(f'{project}: {error}')
    props = ndk_path() / 'source.properties'
    if props.is_file():
        match = re.search(r'Pkg.Revision\s*=\s*(.+)', props.read_text())
        tools['ndk'] = match.group(1).strip() if match else 'unknown'
        if tools['ndk'] != PINS['NDK_VERSION']:
            problems.append(f'ndk: expected {PINS["NDK_VERSION"]}, got {tools["ndk"]}')
    else:
        tools['ndk'] = None
        problems.append(f'ndk: {PINS["NDK_VERSION"]} not installed at {props.parent}')
    for package, path in ({} if WINDOWS_HOST else AVD_PACKAGES).items():
        if not (AVD_SDK / path).is_file():
            problems.append(f'{package}: not installed in {AVD_SDK}')
    for name in SOURCES:
        path = cached(name)
        if not path.is_file():
            problems.append(f'{name}: source not cached: {path}')
        elif file_sha256(path) != PINS[name + '_SHA256']:
            problems.append(f'{name}: cached SHA256 mismatch: {path}')
    if tools.get('docker'):
        try:
            for image, platform in SMOKE_IMAGES:
                if not image_present(PINS[image], platform):
                    problems.append('docker image missing: ' + PINS[image])
            for arch, (_, _, lock) in WINE_RUNNERS.items():
                if not wine_image(arch):
                    problems.append(f'Wine image missing for tests/wine/{lock} (key {wine_key(arch)})')
        except (RuntimeError, subprocess.TimeoutExpired) as error:
            problems.append(str(error))
    warnings = []
    xcode = tuple(map(int, re.findall(r'\d+', tools.get('xcode') or '0')))
    sanitizers = {'ubsan': 'available', 'asan': 'available', 'tsan': 'available'}
    if WINDOWS_HOST:
        sanitizers.update(asan='unavailable', tsan='unavailable')
        warnings.append('host ASan/TSan unavailable on Windows (zig windows-gnu); the container steps run them')
    elif xcode < (26, 4):
        sanitizers.update(asan='unavailable', tsan='unavailable')
        warnings.append('ASan/TSan unavailable on macOS 26 with Xcode < 26.4')
    emit({'status': 'error' if problems else 'ok', 'tools': tools,
          'unity_projects': projects, 'problems': problems, 'warnings': warnings, 'sanitizers': sanitizers})
    return 1 if problems else 0


def bootstrap():
    problems = []
    try:
        ndk = ndk_path()
        if WINDOWS_HOST:
            TOOLS_CACHE.mkdir(parents=True, exist_ok=True)
            for name in WINDOWS_HOST_TOOLS:
                install_tool(name)
            use_windows_tools()
        elif not (ndk / 'source.properties').is_file():
            run(['sdkmanager', '--sdk_root=' + str(ndk.parent.parent),
                 'ndk;' + PINS['NDK_VERSION']], input='y\n' * 100, text=True, stdout=sys.stderr)
        missing = [package for package, path in AVD_PACKAGES.items() if not (AVD_SDK / path).is_file()]
        if missing and not WINDOWS_HOST:
            run(['sdkmanager', '--sdk_root=' + str(AVD_SDK), *missing], input='y\n' * 100, text=True,
                stdout=sys.stderr)
        for name in SOURCES:
            download(name)
        for image, platform in SMOKE_IMAGES:
            if not image_present(PINS[image], platform):
                run(['docker', 'pull', '--platform', platform, PINS[image]], stdout=sys.stderr)
            # A digest-only pull has no tag, so `docker image prune` (run by other projects' build scripts)
            # counts it as dangling and deletes it.
            run(['docker', 'tag', PINS[image], 'ugohttp-pin:' + image.lower()], stdout=sys.stderr)
        for arch in WINE_RUNNERS:
            if not wine_image(arch):
                build_wine_image(arch)
        for relative in ('tests/bench/bootstrap', 'tests/container/bootstrap', 'tests/dotnet/bootstrap'):
            hook = ROOT / relative
            if hook.is_file():
                run(script(hook), stdout=sys.stderr)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        problems.append(str(error))
    return check(problems)


def write_changed(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists() or path.read_text() != content:
        path.write_text(content)


def toolchain(target):
    platform, arch = SLICES[target]
    directory = OUT / target / 'toolchain'
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / 'target.cmake'
    if platform == 'macos':
        # Cache entries: dependencies with CMP0126 OLD drop normal variables of these names.
        content = (f'set(CMAKE_SYSTEM_NAME Darwin)\nset(CMAKE_OSX_ARCHITECTURES {arch} CACHE STRING "")\n'
                   f'set(CMAKE_OSX_DEPLOYMENT_TARGET {PINS["MACOS_DEPLOYMENT_TARGET"]} CACHE STRING "")\n'
                   'set(CMAKE_C_COMPILER /usr/bin/clang)\n'
                   'set(CMAKE_CXX_COMPILER /usr/bin/clang++)\n')
    elif platform.startswith('ios'):
        sysroot = 'iphoneos' if platform == 'ios' else 'iphonesimulator'
        content = (f'set(CMAKE_SYSTEM_NAME iOS)\nset(CMAKE_OSX_SYSROOT {sysroot} CACHE STRING "")\n'
                   f'set(CMAKE_OSX_ARCHITECTURES {arch} CACHE STRING "")\n'
                   f'set(CMAKE_OSX_DEPLOYMENT_TARGET {PINS["IOS_DEPLOYMENT_TARGET"]} CACHE STRING "")\n'
                   'set(CMAKE_C_COMPILER /usr/bin/clang)\n'
                   'set(CMAKE_CXX_COMPILER /usr/bin/clang++)\n')
    elif platform in ('linux', 'windows'):
        zig = shutil.which('zig', path=ENV['PATH'])
        if not zig:
            raise RuntimeError('zig missing; run scripts/bootstrap')
        triple = f'{arch}-linux-gnu.{PINS["LINUX_GLIBC"]}' if platform == 'linux' else f'{arch}-windows-gnu'
        system = 'Linux' if platform == 'linux' else 'Windows'
        content = f'set(CMAKE_SYSTEM_NAME {system})\nset(CMAKE_SYSTEM_PROCESSOR {arch})\n'
        if WINDOWS_HOST:
            # No shebang wrappers on Windows: CMake takes the compiler as a list with its mandatory arguments,
            # and CMAKE_AR must be a single executable, so the archiver is the NDK's llvm-ar.
            zig = Path(zig).as_posix()
            content += (f'set(CMAKE_C_COMPILER "{zig}" cc -target {triple})\n'
                        f'set(CMAKE_CXX_COMPILER "{zig}" c++ -target {triple})\n'
                        f'set(CMAKE_AR "{llvm("llvm-ar").as_posix()}")\n'
                        f'set(CMAKE_RANLIB "{llvm("llvm-ranlib").as_posix()}")\n')
        else:
            for name, command in [('cc', ['cc', '-target', triple]), ('cxx', ['c++', '-target', triple]),
                                  ('ar', ['ar']), ('ranlib', ['ranlib'])]:
                wrapper = directory / name
                write_changed(wrapper, '#!/bin/sh\nexec ' + shlex.join([zig] + command) + ' "$@"\n')
                wrapper.chmod(0o755)
            content += (f'set(CMAKE_C_COMPILER "{directory / "cc"}")\n'
                        f'set(CMAKE_CXX_COMPILER "{directory / "cxx"}")\n'
                        f'set(CMAKE_AR "{directory / "ar"}")\n'
                        f'set(CMAKE_RANLIB "{directory / "ranlib"}")\n')
    else:
        ndk = ndk_path()
        content = (f'set(ANDROID_ABI {arch})\nset(ANDROID_PLATFORM android-{PINS["ANDROID_API"]})\n'
                   f'include("{(ndk / "build/cmake/android.toolchain.cmake").as_posix()}")\n')
    if platform in ('macos', 'ios', 'ios-simulator'):
        content += 'set(HAVE_BUILTIN_AVAILABLE OFF CACHE BOOL "")\n'
    write_changed(path, content)
    return path


def dependency_signature(target, tc):
    pin_names = ['CURL', 'MBEDTLS', 'NGHTTP2', 'ZLIB', 'CMAKE', 'NINJA']
    pin_names += {'macos': ['XCODE', 'MACOS_DEPLOYMENT_TARGET'], 'ios': ['XCODE', 'IOS_DEPLOYMENT_TARGET'],
                  'ios-simulator': ['XCODE', 'IOS_DEPLOYMENT_TARGET'], 'linux': ['ZIG', 'LINUX_GLIBC'],
                  'android': ['NDK', 'ANDROID_API'], 'windows': ['ZIG']}[SLICES[target][0]]
    if WINDOWS_HOST and SLICES[target][0] in ('linux', 'windows'):
        pin_names.append('NDK')
    inputs = {key: value for key, value in PINS.items()
              if any(key == name or key.startswith(name + '_') for name in pin_names)}
    for path in sorted(tc.parent.iterdir()):
        inputs[path.name] = (path.read_text().replace(ndk_path().as_posix() + '/', '<ndk>/')
                             .replace(ROOT.as_posix() + '/', '<checkout>/'))
    for name in ('CMakeLists.txt', 'client_config.h', 'client_config.cmake', 'threading_alt.h'):
        inputs[name] = (ROOT / 'native/deps' / name).read_text()
    for patch in sorted((ROOT / 'native/deps/patches').glob('*.patch')):
        inputs['patches/' + patch.name] = patch.read_text()
    return hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()


def invalidate_dependencies(target, tc):
    output = OUT / target
    signature = dependency_signature(target, tc)
    marker = output / 'dependency-inputs.sha256'
    if not marker.exists() or marker.read_text().strip() != signature:
        print(f'{target}: dependency inputs changed; rebuilding dependencies', file=sys.stderr)
        for name in ('deps', 'include', 'lib', 'share', 'smoke', 'shim'):
            if (output / name).exists():
                shutil.rmtree(output / name)
        marker.unlink(missing_ok=True)
    return marker, signature


def ca_header(directory, pem):
    write_changed(directory / 'ca_bundle.h', 'static const char ugo_ca_bundle[] =\n' +
                  ''.join(json.dumps(line + '\n') + '\n' for line in pem.splitlines()) + ';\n')


def build_shim(target, tc, log):
    output = OUT / target
    shim = output / 'shim'
    shim.mkdir(exist_ok=True)
    ca_header(shim, download('CA').read_text())
    run(['cmake', '-S', ROOT / 'native', '-B', shim, '-G', 'Ninja',
         '-DCMAKE_BUILD_TYPE=MinSizeRel', '-DCMAKE_TOOLCHAIN_FILE=' + tc.as_posix(),
         '-DUGO_SHARED=ON', '-DUGO_TESTS=OFF', '-DUGO_DEV_TLS=OFF',
         '-DCMAKE_C_FLAGS=', '-DCMAKE_CXX_FLAGS=', '-DCMAKE_OBJC_FLAGS=',
         '-DCMAKE_C_FLAGS_MINSIZEREL=-Os -DNDEBUG', '-DCMAKE_CXX_FLAGS_MINSIZEREL=-Os -DNDEBUG',
         '-DCMAKE_OBJC_FLAGS_MINSIZEREL=-Os -DNDEBUG',
         '-DCMAKE_EXE_LINKER_FLAGS=', '-DCMAKE_SHARED_LINKER_FLAGS=', '-DCMAKE_MODULE_LINKER_FLAGS=',
         '-DCMAKE_EXE_LINKER_FLAGS_MINSIZEREL=', '-DCMAKE_SHARED_LINKER_FLAGS_MINSIZEREL=',
         '-DCMAKE_MODULE_LINKER_FLAGS_MINSIZEREL=',
         '-DUGO_DEPS=' + output.as_posix(), '-DUGO_CA_HEADER_DIR=' + shim.as_posix(),
         '-DCMAKE_LIBRARY_OUTPUT_DIRECTORY=' + (output / 'lib').as_posix()], stdout=log, stderr=subprocess.STDOUT)
    run(['cmake', '--build', shim], stdout=log, stderr=subprocess.STDOUT)


def libs(target):
    return {str(p.relative_to(ROOT)): p.stat().st_size
            for p in sorted((OUT / target / 'lib').glob('*.a')) if p.name != 'libugo.a'}


def copy_changed(source, destination):
    if source.is_dir():
        if destination.is_file() or destination.is_symlink():
            destination.unlink()
        wanted = {path.relative_to(source) for path in source.rglob('*')}
        if destination.is_dir():
            for path in sorted(destination.rglob('*'), reverse=True):
                if path.relative_to(destination) not in wanted:
                    shutil.rmtree(path) if path.is_dir() and not path.is_symlink() else path.unlink()
        for path in sorted(source.rglob('*')):
            if path.is_file():
                copy_changed(path, destination / path.relative_to(source))
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists() or destination.read_bytes() != source.read_bytes():
        shutil.copy2(source, destination)


def artifact(target):
    """The file or directory that ships for a target (what gets staged and verified)."""
    if target == 'macos-universal':
        return OUT / target / 'ugo.bundle'
    if target == 'ios-xcframework':
        return OUT / target / 'libugo.xcframework'
    return OUT / target / 'lib' / SHIM[SLICES[target][0]]


class Unavailable(RuntimeError):
    pass


def tree_sha256(path):
    if not path.is_dir():
        return hashlib.sha256(path.read_bytes()).hexdigest()
    digest = hashlib.sha256()
    for file in sorted(p for p in path.rglob('*') if p.is_file()):
        digest.update(str(file.relative_to(path)).encode() + b'\0' + file.read_bytes())
    return digest.hexdigest()


def build_signature(target):
    paths = [Path(__file__), ROOT / 'native/CMakeLists.txt']
    for directory in ('native/src', 'native/include'):
        paths.extend(p for p in (ROOT / directory).rglob('*') if p.is_file())
    if target in PLUGIN:
        plugin = Path(PLUGIN[target])
        paths.extend(ROOT / 'native/unity' / parent.with_name(parent.name + '.meta')
                     for parent in [*plugin.parents[:-1], plugin])
    inputs = {str(path.relative_to(ROOT)): tree_sha256(path) for path in sorted(paths)}
    if target in FAT:
        inputs['slices'] = {name: check_provenance(name) for name in FAT[target]}
        if target == 'macos-universal':
            inputs['bundle_plist'] = bundle_plist(json.loads((ROOT / 'package/package.json').read_text())['version'])
    else:
        inputs['dependencies'] = dependency_signature(target, OUT / target / 'toolchain/target.cmake')
        inputs['ca'] = tree_sha256(cached('CA'))
        inputs['ca_pins'] = {key: value for key, value in PINS.items() if key.startswith('CA_')}
    return hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()


def artifact_hashes(target):
    paths = [artifact(target)]
    if target in SLICES and SLICES[target][0] == 'macos':
        paths.append(OUT / target / 'lib/ugo')
    return {str(path.relative_to(OUT / target)): tree_sha256(path) for path in paths}


def check_provenance(target):
    try:
        stamp = json.loads((OUT / target / 'build-stamp.json').read_text())
        if stamp != {'inputs_sha256': build_signature(target), 'artifacts_sha256': artifact_hashes(target)}:
            raise ValueError('stamp mismatch')
        return stamp
    except (OSError, ValueError, KeyError) as error:
        raise Unavailable(f'{target}: build provenance stamp missing or mismatched; run ./scripts/build {target}') from error


def write_provenance(target, signature):
    if build_signature(target) != signature:
        raise RuntimeError(f'{target}: inputs changed during build; run ./scripts/build {target}')
    stamp = {'inputs_sha256': signature, 'artifacts_sha256': artifact_hashes(target)}
    fd, temporary = tempfile.mkstemp(prefix='.build-stamp-', dir=OUT / target)
    try:
        with os.fdopen(fd, 'w') as file:
            file.write(json.dumps(stamp, sort_keys=True) + '\n')
        Path(temporary).replace(OUT / target / 'build-stamp.json')
    finally:
        Path(temporary).unlink(missing_ok=True)


def smoke_binary(target):
    return OUT / target / 'smoke' / ('ugo-smoke.exe' if SLICES[target][0] == 'windows' else 'ugo-smoke')


def stage(target, package=ROOT / 'package'):
    if target not in PLUGIN:
        return None
    templates = ROOT / 'native/unity'
    plugin = Path(PLUGIN[target])
    for parent in [*reversed(plugin.parents[:-1]), plugin]:
        meta = parent.with_name(parent.name + '.meta')
        copy_changed(templates / meta, package / meta)
    staged = package / plugin
    copy_changed(artifact(target), staged)
    return {str(staged.relative_to(ROOT) if staged.is_relative_to(ROOT) else plugin): tree_bytes(staged)}


def tree_bytes(path):
    return sum(p.stat().st_size for p in path.rglob('*') if p.is_file()) if path.is_dir() else path.stat().st_size


def build_slice(target):
    platform = SLICES[target][0]
    output = OUT / target
    output.mkdir(parents=True, exist_ok=True)
    log_path = output / 'build.log'
    tc = toolchain(target)
    marker, signature = invalidate_dependencies(target, tc)
    download('CA')
    native_signature = build_signature(target)
    pins = ''
    for name in ('CURL', 'MBEDTLS', 'NGHTTP2', 'ZLIB'):
        pins += f'set({name}_ARCHIVE "{download(name).as_posix()}")\n'
        pins += f'set({name}_SHA256 "{PINS[name + "_SHA256"]}")\n'
    write_changed(output / 'pins.cmake', pins)
    with log_path.open('w') as log:
        try:
            run(['cmake', '-S', ROOT / 'native/deps', '-B', output / 'deps', '-G', 'Ninja',
                 '-DUGO_PINS=' + (output / 'pins.cmake').as_posix(), '-DUGO_OUTPUT=' + output.as_posix(),
                 '-DUGO_TOOLCHAIN=' + tc.as_posix()], stdout=log, stderr=subprocess.STDOUT)
            run(['cmake', '--build', output / 'deps'], stdout=log, stderr=subprocess.STDOUT)
            # The smoke probe runs on the host or in a container; iOS has neither.
            if not platform.startswith('ios'):
                smoke = output / 'smoke'
                smoke.mkdir(exist_ok=True)
                ca_header(smoke, download('CA').read_text())
                run(['cmake', '-S', ROOT / 'native/smoke', '-B', smoke, '-G', 'Ninja',
                     '-DCMAKE_BUILD_TYPE=MinSizeRel', '-DCMAKE_TOOLCHAIN_FILE=' + tc.as_posix(),
                     '-DUGO_OUTPUT=' + output.as_posix()], stdout=log, stderr=subprocess.STDOUT)
                run(['cmake', '--build', smoke], stdout=log, stderr=subprocess.STDOUT)
            write_changed(marker, signature + '\n')
            build_shim(target, tc, log)
        except subprocess.CalledProcessError as error:
            raise BuildError(str(error), log_path) from error
    write_provenance(target, native_signature)
    return {'artifacts': libs(target), 'shim': {str(artifact(target).relative_to(ROOT)): artifact(target).stat().st_size}}


class BuildError(RuntimeError):
    def __init__(self, message, log_path):
        super().__init__(message)
        self.log_path = log_path


def bundle_plist(version):
    return ('<?xml version="1.0" encoding="UTF-8"?>\n'
            '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
            '<plist version="1.0">\n<dict>\n'
            '\t<key>CFBundleDevelopmentRegion</key>\n\t<string>en</string>\n'
            '\t<key>CFBundleExecutable</key>\n\t<string>ugo</string>\n'
            '\t<key>CFBundleIdentifier</key>\n\t<string>com.ugohttp.ugo</string>\n'
            '\t<key>CFBundleInfoDictionaryVersion</key>\n\t<string>6.0</string>\n'
            '\t<key>CFBundleName</key>\n\t<string>ugo</string>\n'
            '\t<key>CFBundlePackageType</key>\n\t<string>BNDL</string>\n'
            f'\t<key>CFBundleShortVersionString</key>\n\t<string>{version}</string>\n'
            f'\t<key>CFBundleVersion</key>\n\t<string>{version}</string>\n'
            f'\t<key>LSMinimumSystemVersion</key>\n\t<string>{PINS["MACOS_DEPLOYMENT_TARGET"]}</string>\n'
            '</dict>\n</plist>\n')


def build_fat(target):
    signature = build_signature(target)
    slices = FAT[target]
    output = OUT / target
    output.mkdir(parents=True, exist_ok=True)
    if target == 'macos-universal':
        bundle = output / 'ugo.bundle'
        binary = output / 'ugo'
        run(['/usr/bin/lipo', '-create', '-output', binary, *[OUT / name / 'lib/ugo' for name in slices]])
        version = json.loads((ROOT / 'package/package.json').read_text())['version']
        write_changed(output / 'Info.plist', bundle_plist(version))
        if bundle.exists():
            shutil.rmtree(bundle)
        (bundle / 'Contents/MacOS').mkdir(parents=True)
        shutil.copy2(output / 'Info.plist', bundle / 'Contents/Info.plist')
        shutil.copy2(binary, bundle / 'Contents/MacOS/ugo')
        # Ad-hoc signature: arm64 macOS refuses to load unsigned code; a shipping game re-signs with its identity.
        run(['/usr/bin/codesign', '--force', '--sign', '-', '--timestamp=none', bundle], stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL)
    else:
        device, simulator_arm, simulator_x64 = slices
        simulator = output / 'simulator/libugo.a'
        simulator.parent.mkdir(parents=True, exist_ok=True)
        run(['/usr/bin/lipo', '-create', '-output', simulator, OUT / simulator_arm / 'lib/libugo.a',
             OUT / simulator_x64 / 'lib/libugo.a'])
        headers = output / 'headers'
        headers.mkdir(exist_ok=True)
        copy_changed(ROOT / 'native/include/ugo.h', headers / 'ugo.h')
        framework = output / 'libugo.xcframework'
        if framework.exists():
            shutil.rmtree(framework)
        run(['xcodebuild', '-create-xcframework', '-library', OUT / device / 'lib/libugo.a', '-headers', headers,
             '-library', simulator, '-headers', headers, '-output', framework], stdout=subprocess.DEVNULL)
    write_provenance(target, signature)
    path = artifact(target)
    return {'slices': list(slices), 'shim': {str(path.relative_to(ROOT)): tree_bytes(path)}}


@contextlib.contextmanager
def jobserver():
    """One GNU make jobserver for every ninja the build starts, nested ExternalProject builds included."""
    jobs = os.cpu_count() or 1
    if WINDOWS_HOST:
        # GNU make 4.4 on Windows: the pool is a named semaphore.
        import ctypes
        kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel32.CreateSemaphoreW.restype = ctypes.c_void_p
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        name = f'ugohttp-jobserver-{os.getpid()}'
        handle = kernel32.CreateSemaphoreW(None, jobs - 1, max(jobs - 1, 1), name)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            ENV['MAKEFLAGS'] = f'-j{jobs} --jobserver-auth={name}'
            yield
        finally:
            ENV.pop('MAKEFLAGS', None)
            kernel32.CloseHandle(handle)
        return
    OUT.mkdir(exist_ok=True)
    fifo = OUT / f'jobserver-{os.getpid()}'
    fifo.unlink(missing_ok=True)
    os.mkfifo(fifo)
    fd = os.open(fifo, os.O_RDWR)
    try:
        # Each client owns one implicit slot, so the pool holds one token fewer than the budget.
        os.write(fd, b'+' * (jobs - 1))
        ENV['MAKEFLAGS'] = f'-j{jobs} --jobserver-auth=fifo:{fifo}'
        yield
    finally:
        ENV.pop('MAKEFLAGS', None)
        os.close(fd)
        fifo.unlink(missing_ok=True)


def timed(function, target):
    start = time.monotonic()
    try:
        return function(target), None, time.monotonic() - start
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        return None, error, time.monotonic() - start


def report(target, result, error, seconds):
    if error is None:
        emit({'status': 'ok', 'target': target, **result, 'staged': stage(target),
              'build_seconds': round(seconds, 3)})
        return 0
    log_path = getattr(error, 'log_path', None)
    print(log_path.read_text()[-10000:] if log_path and log_path.exists() else str(error), file=sys.stderr)
    emit({'status': 'error', 'target': target, 'problems': [str(error)],
          'log': str(log_path.relative_to(ROOT)) if log_path else None,
          'build_seconds': round(seconds, 3)})
    return 1


def build(targets):
    """Every needed slice at once under one job budget; a fat target as soon as its slices are done."""
    slices = [name for name in SLICES if name in targets or any(name in FAT[t] for t in targets if t in FAT)]
    fats = [target for target in targets if target in FAT]
    done, failed = {}, 0
    with jobserver(), ThreadPoolExecutor(len(slices)) as pool:
        pending = {pool.submit(timed, build_slice, name): name for name in slices}
        for future in as_completed(pending):
            name = pending[future]
            done[name] = future.result()
            if name in targets:
                failed |= report(name, *done[name])
            for fat in [fat for fat in fats if all(name in done for name in FAT[fat])]:
                fats.remove(fat)
                broken = [name for name in FAT[fat] if done[name][1] is not None]
                if broken:
                    cause = done[broken[0]][1]
                    error = BuildError(f'slice {broken[0]} failed: {cause}', getattr(cause, 'log_path', None))
                    failed |= report(fat, None, error, 0)
                else:
                    failed |= report(fat, *timed(build_fat, fat))
    return failed


def llvm(tool):
    if WINDOWS_HOST:
        return ndk_path() / 'toolchains/llvm/prebuilt/windows-x86_64/bin' / (tool + '.exe')
    return ndk_path() / 'toolchains/llvm/prebuilt/darwin-x86_64/bin' / tool


def nm_names(*args):
    """Symbol names from `nm`, without archive member headers."""
    lines = capture(['/usr/bin/nm', *args]).splitlines()
    return [line.split()[-1] for line in lines if line.strip() and not line.endswith(':')]


def require_exports(names, where):
    names = [name.removeprefix('_') for name in names]
    if sorted(set(names)) != EXPORTS or len(names) != len(EXPORTS):
        raise RuntimeError(f'{where}: exports mismatch: expected {EXPORTS}, got {sorted(names)}')
    return sorted(names)


def elf_exports(path):
    names = []
    for line in capture([llvm('llvm-readelf'), '--dyn-syms', '--wide', path]).splitlines():
        fields = line.split()
        if len(fields) >= 8 and fields[4] in ('GLOBAL', 'WEAK') and fields[6] != 'UND':
            names.append(fields[7])
    return require_exports(names, path.name)


def pe_dll(path, arch):
    """A DLL for `arch` exporting exactly ugo_* and importing only DLLs every Windows 10 has."""
    readobj = llvm('llvm-readobj')
    headers = capture([readobj, '--file-headers', path])
    if not re.search(r'Machine: ' + PE_MACHINE[arch] + r' \(', headers) or 'IMAGE_FILE_DLL ' not in headers:
        raise RuntimeError(f'{path.name} must be a {PE_MACHINE[arch]} DLL')
    names = re.findall(r'^\s*Name: (\S+)$', capture([readobj, '--coff-exports', path]), re.M)
    imports = sorted({name.lower() for name in
                      re.findall(r'^\s*Name: (\S+)$', capture([readobj, '--coff-imports', path]), re.M)})
    unexpected = [name for name in imports if name not in WINDOWS_IMPORTS and not name.startswith(WINDOWS_CRT_PREFIX)]
    if unexpected:
        raise RuntimeError(f'{path.name} imports unexpected DLLs: {unexpected}')
    if arch == 'aarch64':
        listing = capture([llvm('llvm-objdump'), '-d', '--no-show-raw-insn', path]).splitlines()
        crypto = sorted({fields[1] for fields in map(str.split, listing)
                         if len(fields) > 1 and fields[1] in ARM64_CRYPTO})
        if crypto:
            raise RuntimeError(f'{path.name} uses optional crypto instructions: {crypto}')
    return {'exports': require_exports(names, path.name), 'imports': imports}


def dll_probe(target, urls=(), network=False):
    """Runs ugo-dll-probe.exe (natively if the host can, else under Wine): ugo.dll loaded by path, requests
    through its exports only."""
    probe, dll = OUT / target / 'smoke/ugo-dll-probe.exe', artifact(target)
    arch = SLICES[target][1]
    command = ([probe, dll] if arch not in WINE_RUNNERS else
               wine_command(arch, network) + [wine_path(probe), wine_path(dll)])
    for url, error_class in urls:
        command += [url, str(error_class)]
    try:
        proc = subprocess.run([str(arg) for arg in command], cwd=ROOT, env=ENV, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, text=True, timeout=300)
    except subprocess.TimeoutExpired as error:
        raise RuntimeError('ugo-dll-probe did not finish in 300 s') from error
    if proc.returncode:
        raise RuntimeError(f'ugo-dll-probe exited {proc.returncode}: {proc.stdout.strip()} {proc.stderr.strip()}')
    return json.loads(proc.stdout)


def archive_versions(target):
    versions = {}
    for name, lib in [('curl', 'libcurl.a'), ('mbedtls', 'libmbedtls.a'), ('nghttp2', 'libnghttp2.a'), ('zlib', 'libz.a')]:
        version = PINS[name.upper() + '_VERSION']
        strings = capture([llvm('llvm-strings'), OUT / target / 'lib' / lib])
        if not re.search(r'(?<![\d.])' + re.escape(version) + r'(?![\d.])', strings):
            raise RuntimeError(f'{target}/{lib}: embedded version {version} missing')
        versions[name] = version
    return versions


def check_library_set(target):
    expected = {'libcurl.a', 'libmbedtls.a', 'libmbedx509.a', 'libtfpsacrypto.a', 'libmbedcrypto.a', 'libnghttp2.a', 'libz.a'}
    actual = {Path(p).name for p in libs(target)}
    if actual != expected:
        raise RuntimeError(f'{target}: library set mismatch: expected {sorted(expected)}, got {sorted(actual)}')


def probe_problems(probe):
    problems = []
    for key in ('curl', 'mbedtls', 'nghttp2', 'zlib'):
        if probe[key] != PINS[key.upper() + '_VERSION']:
            problems.append(f'{key}: expected {PINS[key.upper() + "_VERSION"]}, got {probe[key]}')
    if probe['protocols'] != ['http', 'https', 'ws', 'wss']:
        problems.append('protocol mismatch: ' + str(probe['protocols']))
    if not {'SSL', 'HTTP2', 'AsynchDNS', 'libz'} <= set(probe['features']):
        problems.append('missing SSL/HTTP2/threaded resolver/libz feature')
    forbidden = {'brotli', 'zstd', 'IDN', 'PSL', 'HTTP3', 'NTLM', 'GSS-API', 'SPNEGO', 'UnixSockets'}
    if forbidden & set(probe['features']):
        problems.append('unexpected features: ' + str(forbidden & set(probe['features'])))
    if not probe['ssl'].startswith('mbedTLS/' + PINS['MBEDTLS_VERSION']):
        problems.append('unexpected TLS backend: ' + probe['ssl'])
    return problems


def wine_command(arch, network=False):
    image = wine_image(arch)
    if not image:
        raise RuntimeError('Wine image missing; run ./scripts/bootstrap')
    return ['docker', 'run', '--rm', '--platform', WINE_RUNNERS[arch][1], *([] if network else ['--network', 'none']),
            '-v', str(ROOT) + ':/work:ro', image]


def wine_path(path):
    return 'Z:/work/' + path.relative_to(ROOT).as_posix()


def runs_here(target):
    """True if this host can run the target's smoke and DLL probes, natively, in a container or under Wine."""
    platform, arch = SLICES[target]
    if platform in ('android', 'ios', 'ios-simulator'):
        return False
    if platform == 'windows':
        return arch in WINE_RUNNERS or (WINDOWS_HOST and arch == 'x86_64')
    return True


def smoke_command(target, network=False):
    binary = smoke_binary(target)
    platform, arch = SLICES[target]
    if platform == 'macos':
        return (['arch', '-x86_64'] if arch == 'x86_64' else []) + [binary]
    if platform == 'windows':
        return [binary] if arch not in WINE_RUNNERS else wine_command(arch, network) + [wine_path(binary)]
    image, docker_platform = SMOKE_IMAGES[0 if arch == 'aarch64' else 1]
    image = PINS[image]
    return ['docker', 'run', '--rm', '--platform', docker_platform, *([] if network else ['--network', 'none']),
            '-v', str(ROOT) + ':/work:ro', image, '/work/' + binary.relative_to(ROOT).as_posix()]


ELF_MACHINE = {'aarch64': 'AArch64', 'x86_64': 'X86-64', 'arm64-v8a': 'AArch64', 'armeabi-v7a': 'ARM'}
PRIVATE_PREFIXES = ('curl_', 'Curl_', 'mbedtls_', 'psa_', 'tf_psa_', 'nghttp2_', 'inflate', 'zlib', 'crc32', 'adler32', 'zcalloc', 'zcfree', 'z_errmsg', 'get_crc_table')


def ios_private(target):
    """libugo.a exports only ugo_*, keeps no named local symbol and needs nothing from curl, mbedTLS,
    nghttp2 or zlib at app link time. Assembler-private labels (l*, L*) stay: relocations still point at them."""
    archive = OUT / target / 'lib/libugo.a'
    exports = require_exports(nm_names('-gU', archive), f'{target}/libugo.a')
    lines = [line.split() for line in capture(['/usr/bin/nm', '-U', archive]).splitlines()]
    named = [f[-1] for f in lines if len(f) == 3 and f[1].islower() and not f[2].startswith(('l', 'L'))]
    if named:
        raise RuntimeError(f'{target}: libugo.a keeps named local symbols: {named[:20]}')
    unresolved = [name for name in nm_names('-u', archive) if name.removeprefix('_').startswith(PRIVATE_PREFIXES)]
    if unresolved:
        raise RuntimeError(f'{target}: libugo.a leaves dependency symbols unresolved: {unresolved[:20]}')
    return {'exports': exports, 'local_labels': sum(1 for f in lines if len(f) == 3 and f[1].islower())}


def ios_linked_bytes(target):
    """What libugo adds to an app: a dead-stripped executable calling every export minus an empty one."""
    platform, arch = SLICES[target]
    work = OUT / target / 'linked'
    work.mkdir(exist_ok=True)
    sdk = 'iphoneos' if platform == 'ios' else 'iphonesimulator'
    sysroot = capture(['xcrun', '--sdk', sdk, '--show-sdk-path'])
    target_flag = (f'-mios-version-min={PINS["IOS_DEPLOYMENT_TARGET"]}' if platform == 'ios' else
                   f'-mios-simulator-version-min={PINS["IOS_DEPLOYMENT_TARGET"]}')
    calls = ''.join(f'    p = (void *)&{name}; sink ^= (unsigned long)p;\n' for name in EXPORTS)
    write_changed(work / 'empty.c', 'int main(void) { return 0; }\n')
    probe = ('#include "ugo.h"\n{decl}int main(void)\n{{\n    void *p;\n    volatile unsigned long sink = 0;\n'
             + calls.replace('{', '{{').replace('}', '}}') + '{use}    return (int)sink;\n}}\n')
    write_changed(work / 'probe.c', probe.format(decl='', use=''))
    write_changed(work / 'clash.c', probe.format(decl='const char *curl_version(void);\n',
                                                 use='    sink ^= (unsigned long)curl_version()[0];\n'))
    sizes = {}
    frameworks = ['-framework', 'CoreFoundation', '-framework', 'UIKit']
    # A second, unrelated copy of curl/mbedTLS/nghttp2/zlib in the same app link must not collide with libugo.
    other_sdk = ['-Wl,-force_load,' + str(OUT / target / 'lib' / lib)
                 for lib in ('libcurl.a', 'libnghttp2.a', 'libmbedtls.a', 'libmbedx509.a', 'libtfpsacrypto.a', 'libz.a')]
    for name, extra in (('empty', []), ('probe', [OUT / target / 'lib/libugo.a', *frameworks]),
                        ('clash', [OUT / target / 'lib/libugo.a', *other_sdk, *frameworks,
                                   '-framework', 'SystemConfiguration'])):
        binary = work / name
        run(['/usr/bin/clang', '-arch', arch, '-isysroot', sysroot, target_flag, '-Os', '-dead_strip',
             '-Wl,-x', '-I', ROOT / 'native/include', '-o', binary, work / (name + '.c'),
             *extra], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        run(['/usr/bin/strip', '-x', binary])
        sizes[name] = binary.stat().st_size
    return sizes['probe'] - sizes['empty']


def verify_slice(target, result, problems, online, online_host):
    platform, arch = SLICES[target]
    output = OUT / target
    check_library_set(target)
    path = artifact(target)
    if not path.is_file():
        raise RuntimeError(f'shim missing: {path.relative_to(ROOT)}')
    if platform in ('macos', 'ios', 'ios-simulator'):
        libraries = [path, output / 'lib/ugo'] if platform == 'macos' else [path]
        for library in libraries:
            weak = sorted({line.split()[-1] for line in capture(['/usr/bin/nm', '-m', library]).splitlines()
                           if '(undefined) weak external' in line})
            if weak:
                raise RuntimeError(f'{library.relative_to(ROOT)}: weak undefined symbols: {weak}')
    budget = int(PINS['LIBUGO_MAX_BYTES'])
    if platform in ('android', 'linux'):
        headers = capture([llvm('llvm-readelf'), '--file-header', '--program-headers', '--wide', path])
        if ELF_MACHINE[arch] not in headers or 'DYN' not in headers:
            raise RuntimeError(f'{path.name} must be an {ELF_MACHINE[arch]} ELF shared object')
        if 'ELF32' in headers:
            imports = {line.split()[-1].split('@')[0] for line in
                       capture([llvm('llvm-nm'), '-D', '--undefined-only', path]).splitlines() if line.split()}
            non_lfs = imports & {'lseek', 'ftruncate', 'truncate', 'pread', 'pwrite', 'mmap', 'fseeko', 'ftello',
                                 'preadv', 'pwritev', 'sendfile', 'fallocate', 'posix_fallocate', 'posix_fadvise',
                                 'fgetpos', 'fsetpos', 'funopen', 'lockf', '__pread_chk', '__pwrite_chk'}
            if non_lfs:
                raise RuntimeError(f'non-LFS file-offset imports in 32-bit ELF: {sorted(non_lfs)}')
        if platform == 'android':
            alignments = [int(line.split()[-1], 16) for line in headers.splitlines()
                          if line.strip().startswith('LOAD ')]
            if not alignments or min(alignments) < 0x4000:
                raise RuntimeError(f'invalid Android LOAD alignments: {alignments}')
            result['load_alignment'] = hex(min(alignments))
        result['exports'] = elf_exports(path)
        result['bytes'] = path.stat().st_size
    elif platform == 'macos':
        names = nm_names('-gU', path)
        result['exports'] = require_exports(names, path.name)
        result['bytes'] = path.stat().st_size
        bundle = output / 'lib/ugo'
        require_exports(nm_names('-gU', bundle), f'{target}/lib/ugo')
        result['bundle_bytes'] = bundle.stat().st_size
    elif platform == 'windows':
        result.update(pe_dll(path, arch))
        result['bytes'] = path.stat().st_size
    else:
        result.update(ios_private(target))
        result['archive_bytes'] = path.stat().st_size
        result['bytes'] = ios_linked_bytes(target)
        result['bytes_note'] = 'linked into a dead-stripped app; the archive itself is not shipped as is'
    if result['bytes'] > budget:
        problems.append(f'{target}: {result["bytes"]} bytes over budget {budget}')
    runs = runs_here(target)
    if not runs:
        result['versions'] = archive_versions(target)
        result['version_check'] = ('archive strings; no runtime probe on this platform'
                                   if platform != 'windows' else 'archive strings; no runtime probe on this host')
    else:
        probe = json.loads(capture(smoke_command(target)))
        result['versions'] = probe
        problems.extend(probe_problems(probe))
    if platform == 'windows' and runs:
        result['dll_probe'] = dll_probe(target)
    if online and target == 'macos-arm64':
        result['online_local_tls'] = local_tls(output / 'smoke/ugo-smoke')
    if (online and platform in ('linux', 'windows') and runs) or (online_host and target == 'macos-arm64'):
        command = smoke_command(target, network=True)
        result['online_public_tls'] = [https_probe(command, url, code) for url, code in
                                      [('https://example.com', 0), ('https://wrong.host.badssl.com', 60)]]
    if online and platform == 'windows' and runs:
        result['online_dll_probe'] = dll_probe(target, [('https://example.com', 0),
                                                        ('https://wrong.host.badssl.com', UGO_CLASS_TLS)], network=True)


def verify_fat(target, result, problems):
    budget = int(PINS['LIBUGO_MAX_BYTES'])
    path = artifact(target)
    if not path.is_dir():
        raise RuntimeError(f'missing: {path.relative_to(ROOT)}; run ./scripts/build {target}')
    if target == 'macos-universal':
        binary = path / 'Contents/MacOS/ugo'
        if sorted(capture(['/usr/bin/lipo', '-archs', binary]).split()) != ['arm64', 'x86_64']:
            raise RuntimeError(f'{binary.name}: expected arm64 + x86_64')
        if 'BUNDLE' not in capture(['/usr/bin/otool', '-hv', binary]):
            raise RuntimeError(f'{binary.name}: not an MH_BUNDLE')
        run(['/usr/bin/codesign', '--verify', '--strict', path], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        slices = {}
        for name in FAT[target]:
            arch = SLICES[name][1]
            require_exports(nm_names('-gU', '-arch', arch, binary), f'ugo.bundle ({arch})')
            thin = OUT / target / f'ugo-{arch}'
            run(['/usr/bin/lipo', binary, '-thin', arch, '-output', thin])
            slices[arch] = thin.stat().st_size
            thin.unlink()
            if slices[arch] > budget:
                problems.append(f'ugo.bundle ({arch}): {slices[arch]} bytes over budget {budget}')
        result.update(slice_bytes=slices, bytes=binary.stat().st_size)
    else:
        info = plistlib.loads((path / 'Info.plist').read_bytes())
        found = {}
        for library in info['AvailableLibraries']:
            key = library['SupportedPlatform'] + library.get('SupportedPlatformVariant', '')
            found[key] = sorted(library['SupportedArchitectures'])
            archive = path / library['LibraryIdentifier'] / library['LibraryPath']
            for arch in library['SupportedArchitectures']:
                require_exports(nm_names('-gU', '-arch', arch, archive), f'{key}/{arch}')
        if found != {'ios': ['arm64'], 'iossimulator': ['arm64', 'x86_64']}:
            raise RuntimeError(f'libugo.xcframework slices: {found}')
        result.update(slices=found, bytes=tree_bytes(path))


def embedded_build_paths(path):
    needles = {os.fsencode(str(ROOT)), ROOT.as_posix().encode(), os.fsencode(str(Path.home()))}
    files = [path] if path.is_file() else sorted(p for p in path.rglob('*') if p.is_file() and not p.is_symlink())
    return [str(p.relative_to(ROOT)) for p in files if any(needle in p.read_bytes() for needle in needles)]


def verify(target, online=False, online_host=False):
    problems = []
    result = {'target': target}
    try:
        if target in FAT:
            verify_fat(target, result, problems)
        else:
            result['artifacts'] = libs(target)
            verify_slice(target, result, problems, online, online_host)
        leaked = embedded_build_paths(artifact(target))
        if leaked:
            problems.append(f'build host paths embedded in {leaked}')
    except (OSError, RuntimeError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        detail = getattr(error, 'stderr', None)
        problems.append(str(error) + (': ' + detail.strip() if isinstance(detail, str) and detail.strip() else ''))
    result.update(status='error' if problems else 'ok', problems=problems)
    emit(result)
    return bool(problems)


def https_probe(command, url, expected_code, ca=None):
    proc = subprocess.run([str(arg) for arg in command] + [url] + ([str(ca)] if ca else []),
                          cwd=ROOT, env=ENV, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, text=True)
    check = json.loads(proc.stdout)
    if proc.returncode != expected_code or check['curl_code'] != expected_code:
        raise RuntimeError(f'{url}: expected CURLcode {expected_code}, got exit {proc.returncode}: '
                           + proc.stderr.strip())
    if expected_code == 0 and (check['http_status'] != 200 or check['verify_result'] != 0):
        raise RuntimeError(f'{url}: expected verified HTTPS 200')
    return {'url': url, 'exit_code': proc.returncode, 'diagnostic': proc.stderr.strip(), **check}


def local_tls(binary):
    with (OUT / 'macos-arm64/bench.stderr').open('w') as log:
        bench = subprocess.Popen([str(ROOT / 'scripts/bench')], cwd=ROOT, env=ENV,
                                 stdout=subprocess.PIPE, stderr=log, text=True)
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(bench.stdout, selectors.EVENT_READ)
                if not selector.select(timeout=30):
                    raise RuntimeError('TLS bench did not publish readiness in 30 seconds')
                ready = json.loads(bench.stdout.readline())
            if not ready['ready']:
                raise RuntimeError('TLS bench readiness failed')
            checks = []
            for endpoint in ('https', 'expired', 'wrong_hostname', 'self_signed', 'incomplete_chain'):
                checks.append(https_probe([binary], ready['endpoints'][endpoint] + '/ok',
                                          0 if endpoint == 'https' else 60, ready['ca_pem']))
            return {'bench_pid': bench.pid, 'checks': checks}
        finally:
            if bench.poll() is None:
                bench.terminate()
                try:
                    bench.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    bench.kill()
                    bench.wait()
            bench.stdout.close()


def package(destination):
    """The upm branch tree: tracked package/ files under package/ plus every shipped plugin and its metas."""
    destination = destination.resolve()
    if destination.is_relative_to(ROOT / 'package') or (destination.exists() and any(destination.iterdir())):
        raise RuntimeError(f'{destination} must be a new or empty directory outside package/')
    for target in PLUGIN:
        check_provenance(target)
    tracked = capture(['git', 'ls-files', '-z', 'package']).split('\0')
    root = destination / 'package'
    root.mkdir(parents=True, exist_ok=True)
    for name in sorted(filter(None, tracked)):
        relative = Path(name).relative_to('package')
        if relative.parts[0] == 'Plugins':
            continue
        target = root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / name, target)
    shutil.copyfile(ROOT / 'LICENSE.md', root / 'LICENSE.md')
    shutil.copyfile(ROOT / 'native/unity/LICENSE.md.meta', root / 'LICENSE.md.meta')
    staged = {}
    for target in sorted(PLUGIN):
        staged.update(stage(target, root))
    emit({'status': 'ok', 'destination': str(destination), 'plugins': staged, 'tree_sha256': tree_sha256(destination)})
    return 0


def main():
    action, *arguments = sys.argv[1:] or ['']
    if action == 'package':
        if len(arguments) != 1:
            raise ValueError('usage: scripts/package <directory>')
        return package(Path(arguments[0]))
    if action not in ('bootstrap', 'check', 'build', 'verify'):
        print('usage: driver.py bootstrap|check|build <target>|verify [target] [--online] [--online-host]'
              '|package <directory>', file=sys.stderr)
        emit({'status': 'error', 'problems': [f'unknown action: {action!r}']})
        return 2
    if action in ('bootstrap', 'check'):
        if arguments:
            raise ValueError(f'usage: driver.py {action}')
        return bootstrap() if action == 'bootstrap' else check()
    online = '--online' in arguments
    online_host = '--online-host' in arguments
    arguments = [arg for arg in arguments if arg not in ('--online', '--online-host')]
    if arguments and arguments[0] in TARGETS and arguments[0] not in HOST_TARGETS:
        raise ValueError(f'{arguments[0]} needs a macOS host')
    if len(arguments) > 1 or (arguments and arguments[0] not in (*HOST_TARGETS, 'all')):
        raise ValueError(f'usage: scripts/{action} [{"|".join(HOST_TARGETS)}|all]' +
                         (' [--online] [--online-host]' if action == 'verify' else ''))
    if action == 'build' and (not arguments or online or online_host):
        raise ValueError(f'usage: scripts/build {"|".join(HOST_TARGETS)}|all')
    targets = HOST_TARGETS if not arguments or arguments == ['all'] else arguments
    if action == 'build':
        return build(targets)
    failed = 0
    for target in targets:
        failed |= verify(target, online, online_host)
    return failed


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        emit({'status': 'error', 'problems': [str(error)]})
        sys.exit(1)
