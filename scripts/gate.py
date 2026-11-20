#!/usr/bin/env python3
import difflib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from xml.sax.saxutils import escape
from xml.etree import ElementTree


ROOT = Path(__file__).resolve().parents[1]
PINS = dict(line.split('=', 1) for line in (ROOT / 'scripts/versions.env').read_text().splitlines()
            if line and not line.startswith('#'))
sys.path.insert(0, str(ROOT / 'native/deps'))
import driver  # noqa: E402

LIBRARIES = ('libcurl.a', 'libmbedtls.a', 'libmbedx509.a', 'libtfpsacrypto.a',
             'libmbedcrypto.a', 'libnghttp2.a', 'libz.a')
ENV = os.environ.copy()
ENV.update(DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
           DOTNET_NOLOGO='1', NUGET_XMLDOC_MODE='skip')
ENV.update({key: driver.ENV[key] for key in ('ZIG_GLOBAL_CACHE_DIR', 'ZIG_LOCAL_CACHE_DIR')})


Unavailable = driver.Unavailable
WINDOWS_HOST = driver.WINDOWS_HOST
# The Linux slice the fault container runs: the host's own architecture, never an emulated one.
CONTAINER_TARGET = 'linux-x86_64' if WINDOWS_HOST else 'linux-arm64'
HOST_LIBRARY = 'windows-x64' if WINDOWS_HOST else 'macos-arm64'


def emit(value, file=sys.stdout):
    print(json.dumps(value, separators=(',', ':')), file=file, flush=True)


def run(command, log, *, required=False, stdout=None, env=ENV, timeout=None):
    command = [str(arg) for arg in command]
    print('$ ' + shlex.join(command), file=log, flush=True)
    result = subprocess.run(command, cwd=ROOT, env=env, stdout=stdout or log,
                            stderr=log if stdout else subprocess.STDOUT, timeout=timeout)
    log.flush()
    if required and result.returncode:
        raise Unavailable(f'{shlex.join(command)} exited {result.returncode}; run ./scripts/bootstrap')
    return result.returncode


def command_status(command, log):
    return 'pass' if run(command, log) == 0 else 'fail'


def tool(name):
    if not shutil.which(name):
        raise Unavailable(f'{name} is missing; run ./scripts/bootstrap')


def files(paths, remedy):
    missing = [str(path.relative_to(ROOT)) if path.is_relative_to(ROOT) else str(path)
               for path in paths if not path.is_file()]
    if missing:
        raise Unavailable('missing ' + ', '.join(missing) + '; ' + remedy)


def artifacts(target):
    driver.check_provenance(target)
    remedy = f'run ./scripts/build {target}'
    shipped = driver.artifact(target)
    if target in driver.FAT:
        if not shipped.is_dir():
            raise Unavailable(f'missing {shipped.relative_to(ROOT)}; {remedy}')
        return
    base = ROOT / 'out' / target
    platform = driver.SLICES[target][0]
    smoke = [] if platform.startswith('ios') else [driver.smoke_binary(target)]
    if platform == 'windows':
        smoke.append(base / 'smoke/ugo-dll-probe.exe')
    files([base / 'lib' / name for name in LIBRARIES] + [shipped] + smoke, remedy)


def tree(path):
    return {p.relative_to(path): p for p in path.rglob('*') if p.is_file()} if path.is_dir() else {Path(): path}


def staged(target):
    driver.check_provenance(target)
    plugin = ROOT / 'package' / driver.PLUGIN[target]
    relative = plugin.relative_to(ROOT / 'package')
    metas = [path.with_name(path.name + '.meta') for path in [relative, *relative.parents[:-1]]]
    pairs = [(ROOT / 'package' / meta, ROOT / 'native/unity' / meta) for meta in metas]
    built = tree(driver.artifact(target))
    copies = tree(plugin) if plugin.exists() else {}
    if copies.keys() != built.keys():
        raise Unavailable(f'{relative} is missing or stale; run ./scripts/build {target}')
    pairs += [(copies[key], built[key]) for key in built]
    files([copy for copy, _ in pairs], f'run ./scripts/build {target}')
    for copy, source in pairs:
        if copy.read_bytes() != source.read_bytes():
            raise Unavailable(f'{copy.relative_to(ROOT)} is stale; run ./scripts/build {target}')


def docker(log, *, image=None):
    tool('docker')
    run(['docker', 'info', '--format', '{{.ServerVersion}}'], log, required=True)
    if image:
        run(['docker', 'image', 'inspect', image], log, required=True)


def container_ready(log):
    tool('docker')
    key = subprocess.check_output(driver.script(ROOT / 'tests/container/image-key'), cwd=ROOT, env=ENV,
                                  stderr=log, text=True).strip()
    marker = ROOT / 'out/container' / (key + '.iid')
    files([marker], 'run ./scripts/bootstrap')
    docker(log, image=marker.read_text().strip())


def versions(work, log):
    return 'pass' if run([sys.executable, ROOT / 'native/deps/driver.py', 'check'], log) == 0 else 'error'


def header(work, log):
    source = work / 'header.c'
    source.write_text('#include "ugo.h"\nint main(void) { return 0; }\n')
    for compiler, standard, language in (('cc', 'c11', 'c'), ('c++', 'c++17', 'c++')):
        compiler = host_compiler(compiler)
        for shared in (False, True):
            # zig cc on Windows fails -fsyntax-only (it renames an object file it never wrote).
            check = ['-c', '-o', work / 'header.o'] if WINDOWS_HOST else ['-fsyntax-only']
            command = [*compiler, '-x', language, '-std=' + standard, '-pedantic', '-Wall',
                       '-Wextra', '-Werror', *check, '-I', ROOT / 'native/include']
            if shared:
                command.append('-DUGO_BUILD_SHARED')
            if run(command + [source], log):
                return 'fail'
    return 'pass'


def host_compiler(name):
    """`cc` or `c++` for the host; Windows has none but the pinned zig."""
    if WINDOWS_HOST:
        tool('zig')
        return [shutil.which('zig'), name]
    tool(name)
    return [name]


def dotnet_ready(log):
    tool('dotnet')
    sdks = subprocess.check_output(['dotnet', '--list-sdks'], cwd=ROOT, env=ENV,
                                   stderr=log, text=True)
    print(sdks, file=log, flush=True)
    if not any(line.startswith('10.0.') for line in sdks.splitlines()):
        raise Unavailable('.NET 10 SDK is missing; run ./scripts/bootstrap')


def abi_layout(work, log):
    compiler = host_compiler('cc')
    dotnet_ready(log)
    mirror = ROOT / 'package/Runtime/UgoHTTP/UgoAbi.cs'
    files([mirror], 'restore package/Runtime/UgoHTTP/UgoAbi.cs')
    native = work / 'native.dump'
    with native.open('w') as dump:
        if run(driver.script(ROOT / 'native/abi/dump'), log, stdout=dump,
               env={**ENV, 'CC': shlex.join(Path(arg).as_posix() for arg in compiler)}):
            return 'fail'
    project = work / 'managed'
    project.mkdir()
    (project / 'Layout.csproj').write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><AllowUnsafeBlocks>true</AllowUnsafeBlocks>'
        '</PropertyGroup><ItemGroup><Compile Include="' + escape(str(mirror), {'"': '&quot;'}) +
        '" /></ItemGroup></Project>\n')
    (project / 'Program.cs').write_text(
        'foreach (var line in UgoHttp.Native.UgoAbi.Dump()) System.Console.WriteLine(line);\n')
    (project / 'NuGet.Config').write_text(
        '<configuration><packageSources><clear /></packageSources></configuration>\n')
    if run(['dotnet', 'build', project / 'Layout.csproj', '--nologo', '--verbosity', 'quiet',
            '-p:NuGetAudit=false'], log):
        return 'fail'
    managed = work / 'managed.dump'
    with managed.open('w') as dump:
        if run(['dotnet', project / 'bin/Debug/net10.0/Layout.dll'], log, stdout=dump):
            return 'fail'
    diff = ''.join(difflib.unified_diff(native.read_text().splitlines(keepends=True),
                                     managed.read_text().splitlines(keepends=True),
                                     fromfile='native.dump', tofile='managed.dump'))
    if diff:
        print(diff, file=log, flush=True)
        return 'fail'
    return 'pass'


def verify(work, log):
    for target in driver.HOST_TARGETS:
        artifacts(target)
    docker(log, image=PINS['LINUX_SMOKE_IMAGE'])
    docker(log, image=PINS['LINUX_X86_64_SMOKE_IMAGE'])
    try:
        missing = [arch for arch in driver.WINE_RUNNERS if not driver.wine_image(arch)]
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        raise Unavailable(str(error)) from error
    if missing:
        raise Unavailable(f'Wine image for tests/wine missing ({", ".join(missing)}); run ./scripts/bootstrap')
    files([driver.llvm(name) for name in ('llvm-readelf', 'llvm-readobj', 'llvm-objdump', 'llvm-strings')]
          + ([] if WINDOWS_HOST else [Path('/usr/bin/nm')]), 'run ./scripts/bootstrap')
    return command_status(driver.script(ROOT / 'scripts/verify'), log)


def package(work, log):
    for target in driver.PLUGIN:
        artifacts(target)
    trees = []
    for name in ('first', 'second'):
        report = work / (name + '.json')
        with report.open('w') as out:
            code = run(driver.script(ROOT / 'scripts/package') + [work / name], log, stdout=out)
        try:
            result = json.loads(report.read_text().splitlines()[-1])
        except (IndexError, ValueError):
            result = {}
        if code or result.get('status') != 'ok':
            emit({'status': 'fail', 'problems': [f'scripts/package exited {code}'], 'output': result}, file=log)
            return 'fail'
        for filename in ('LICENSE.md', 'Third Party Notices.md'):
            if not (work / name / 'package' / filename).is_file():
                emit({'status': 'fail', 'problems': [f'package/{filename} is missing']}, file=log)
                return 'fail'
        trees.append(result['tree_sha256'])
    status = 'pass' if trees[0] == trees[1] else 'fail'
    emit({'status': status, 'tree_sha256': trees}, file=log)
    return status


def bench_ready(log):
    python = ROOT / ('tests/bench/.venv/Scripts/python.exe' if WINDOWS_HOST else 'tests/bench/.venv/bin/python')
    files([python], 'run ./scripts/bootstrap')
    run([python, '-c', 'import h2, wsproto, trustme'], log, required=True)
    curl = driver.BENCH_CURL
    files([curl], 'run ./scripts/bootstrap' if WINDOWS_HOST else 'install the system curl with HTTP/2 support')
    version = subprocess.check_output([curl, '--version'], cwd=ROOT, env=ENV, stderr=log, text=True)
    print(version, file=log, flush=True)
    if 'HTTP2' not in version:
        raise Unavailable(f'{curl} lacks HTTP/2 support')


def bench(work, log):
    bench_ready(log)
    return command_status(driver.script(ROOT / 'tests/bench/selftest'), log)


def container(work, log):
    artifacts(CONTAINER_TARGET)
    container_ready(log)
    code = run(driver.script(ROOT / 'tests/container/selftest'), log)
    return 'error' if code == 125 else 'pass' if code == 0 else 'fail'


def container_sources(log):
    container_ready(log)
    cache = Path.home() / '.cache/ugohttp/src'
    files([cache / (PINS[name + '_SHA256'] + '-' + PINS[name + '_URL'].rsplit('/', 1)[1])
           for name in ('CURL', 'MBEDTLS', 'NGHTTP2', 'ZLIB')], 'run ./scripts/bootstrap')


def container_tsan(work, log):
    container_sources(log)
    code = run(driver.script(ROOT / 'tests/container/tsan'), log)
    return 'error' if code == 125 else 'pass' if code == 0 else 'fail'


def native_tests(log, sanitizer, in_container=False):
    artifacts(CONTAINER_TARGET if in_container else 'macos-arm64')
    if in_container:
        container_sources(log)
        command = driver.script(ROOT / 'tests/container/native')
    else:
        for name in ('cmake', 'ninja', 'clang'):
            tool(name)
        bench_ready(log)
        command = [ROOT / 'native/tests/run']
    code = run(command + ['--sanitizer', sanitizer], log)
    return 'error' if in_container and code == 125 else 'pass' if code == 0 else 'fail'


def dotnet_tests(work, log):
    dotnet_ready(log)
    if not WINDOWS_HOST:
        tool('clang')
    artifacts(HOST_LIBRARY)
    bench_ready(log)
    assets = ROOT / 'tests/dotnet/obj/UgoHTTP.Tests/project.assets.json'
    files([assets], 'run dotnet restore tests/dotnet/UgoHTTP.Tests.csproj')
    packages = json.loads(assets.read_text())
    for name, package in packages['libraries'].items():
        if package['type'] == 'package' and not any(
                (Path(folder) / package['path']).is_dir() for folder in packages['packageFolders']):
            raise Unavailable(f'NuGet package {name} is missing; run dotnet restore tests/dotnet/UgoHTTP.Tests.csproj')
    code = run(['dotnet', 'test', 'tests/dotnet/UgoHTTP.Tests.csproj', '--nologo', '--no-restore'], log)
    match = re.search(r'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)',
                      Path(log.name).read_text())
    counts = dict(zip(('failed', 'passed', 'skipped', 'total'), map(int, match.groups()))) if match else {}
    status = 'pass' if code == 0 and counts.get('total', 0) > 0 and counts['failed'] == 0 else 'fail'
    emit({**counts, 'status': status}, file=log)
    return status


UNITY = driver.UNITY
EDITOR = driver.EDITOR
UNITY_EDITMODE_TIMEOUT = 900


def unity_editmode(work, log):
    files([EDITOR], f'install Unity {PINS["UNITY_VERSION"]}')
    plugin = 'windows-x64' if WINDOWS_HOST else 'macos-universal'
    artifacts(plugin)
    staged(plugin)
    compiler = host_compiler('cc')
    bench_ready(log)
    result_path = work.parent / 'unity-editmode.xml'
    editor_log = work / 'unity-editmode.log'
    try:
        code = run([EDITOR, '-batchmode', '-projectPath', ROOT / 'tests/unity',
                    '-runTests', '-testPlatform', 'EditMode', '-testResults', result_path,
                    '-logFile', editor_log], log, timeout=UNITY_EDITMODE_TIMEOUT,
                   env={**ENV, 'CC': shlex.join(Path(arg).as_posix() for arg in compiler)})
    except subprocess.TimeoutExpired:
        code = None
    output = editor_log.read_text(errors='replace') if editor_log.is_file() else ''
    print(output, file=log, flush=True)
    if code is None:
        emit({'status': 'fail', 'problems': [f'Unity EditMode exceeded {UNITY_EDITMODE_TIMEOUT}s'],
              'log_tail': output.splitlines()[-20:]}, file=log)
        return 'fail'
    try:
        result = ElementTree.parse(result_path).getroot()
        total = int(result.get('total', '0'))
    except (OSError, ValueError, ElementTree.ParseError) as error:
        emit({'status': 'fail', 'problems': [f'invalid NUnit result: {error}']}, file=log)
        return 'fail'
    layout = [case.get('result') for case in result.iter('test-case')
              if case.get('name') == 'MonoLayoutMatchesNativeDump']
    status = 'pass' if (code == 0 and result.get('result') == 'Passed' and total > 0
                        and layout == ['Passed'] and 'has no meta file' not in output) else 'fail'
    emit({'status': status, 'result': result.get('result'), 'total': total, 'mono_layout': layout,
          'exit_code': code, 'missing_meta': 'has no meta file' in output}, file=log)
    return status


def android_player(work, log):
    android = UNITY / 'PlaybackEngines/AndroidPlayer'
    files([EDITOR], f'install Unity {PINS["UNITY_VERSION"]}')
    files([android / 'SDK/platform-tools/adb', android / 'NDK/source.properties',
           android / 'OpenJDK/bin/java'], f'install the Android Build Support module (SDK, NDK, OpenJDK) for Unity {PINS["UNITY_VERSION"]}')
    artifacts('android-arm64')
    staged('android-arm64')
    tool('cc')
    bench_ready(log)
    apk = work / 'ugohttp-player.apk'
    build_log = work / 'unity-build.log'
    started = time.monotonic()
    code = run([EDITOR, '-batchmode', '-nographics', '-projectPath', ROOT / 'tests/player', '-buildTarget', 'Android',
                '-executeMethod', 'AndroidPlayerBuild.Build', '-ugoApk', apk, '-logFile', build_log], log)
    build_seconds = round(time.monotonic() - started, 1)
    if code or not apk.is_file():
        output = build_log.read_text(errors='replace') if build_log.is_file() else ''
        print(output[-20000:], file=log, flush=True)
        emit({'status': 'fail', 'problems': [f'APK build exited {code}'], 'build_seconds': build_seconds}, file=log)
        return 'fail'
    report = work / 'runner.json'
    with report.open('w') as out:
        code = run(driver.script(ROOT / 'tests/android/run') + ['--apk', apk, '--work', work / 'runner'], log, stdout=out)
    lines = report.read_text().splitlines()
    try:
        summary = json.loads(lines[-1])
    except (IndexError, ValueError):
        emit({'status': 'fail', 'problems': ['runner printed no JSON summary'], 'exit_code': code}, file=log)
        return 'fail'
    return runner_status(code, summary, log, build_seconds)


def runner_status(code, summary, log, build_seconds):
    if code == 2:
        status = 'error'
    else:
        checks = summary.get('checks') or {}
        status = 'pass' if (code == 0 and summary.get('status') == 'pass' and checks
                            and all(value == 'pass' for value in checks.values())
                            and summary.get('layout_diff_lines') == 0) else 'fail'
    emit({**summary, 'status': status, 'runner_status': summary.get('status'), 'build_seconds': build_seconds},
         file=log)
    return status


def ios_player(work, log):
    files([EDITOR], f'install Unity {PINS["UNITY_VERSION"]}')
    files([UNITY / 'PlaybackEngines/iOSSupport/UnityEditor.iOS.Extensions.dll'],
          f'install the iOS Build Support module for Unity {PINS["UNITY_VERSION"]}')
    tool('xcodebuild')
    artifacts('ios-xcframework')
    staged('ios-xcframework')
    tool('cc')
    bench_ready(log)
    xcode = work / 'xcode'
    build_log = work / 'unity-build.log'
    started = time.monotonic()
    code = run([EDITOR, '-batchmode', '-nographics', '-projectPath', ROOT / 'tests/player', '-buildTarget', 'iOS',
                '-executeMethod', 'IosPlayerBuild.Build', '-ugoXcode', xcode, '-logFile', build_log], log)
    derived = ROOT / 'out/ios-player-derived'
    if code == 0:
        code = run(['xcodebuild', '-project', xcode / 'Unity-iPhone.xcodeproj', '-scheme', 'Unity-iPhone',
                    '-configuration', 'Release', '-sdk', 'iphonesimulator', '-arch', 'arm64',
                    '-derivedDataPath', derived, 'CODE_SIGN_IDENTITY=-', 'CODE_SIGNING_REQUIRED=NO', 'build'], log)
    build_seconds = round(time.monotonic() - started, 1)
    apps = sorted((derived / 'Build/Products/Release-iphonesimulator').glob('*.app'))
    if code or len(apps) != 1:
        output = build_log.read_text(errors='replace') if build_log.is_file() else ''
        print(output[-20000:], file=log, flush=True)
        emit({'status': 'fail', 'problems': [f'simulator app build exited {code}'], 'build_seconds': build_seconds},
             file=log)
        return 'fail'
    report = work / 'runner.json'
    with report.open('w') as out:
        code = run(driver.script(ROOT / 'tests/ios/run') + ['--app', apps[0], '--work', work / 'runner'], log, stdout=out)
    try:
        summary = json.loads(report.read_text().splitlines()[-1])
    except (IndexError, ValueError):
        emit({'status': 'fail', 'problems': ['runner printed no JSON summary'], 'exit_code': code}, file=log)
        return 'fail'
    return runner_status(code, summary, log, build_seconds)


STEPS = [
    ('versions', versions),
    ('header', header),
    ('abi-layout', abi_layout),
    ('verify', verify),
    ('package', package),
    ('bench', bench),
    ('container', container),
    ('container-tsan', container_tsan),
    ('native-asan', lambda work, log: native_tests(log, 'asan')),
    ('native-tsan', lambda work, log: native_tests(log, 'tsan')),
    ('container-native-asan', lambda work, log: native_tests(log, 'asan', in_container=True)),
    ('container-native-tsan', lambda work, log: native_tests(log, 'tsan', in_container=True)),
    ('dotnet', dotnet_tests),
    ('unity-editmode', unity_editmode),
    ('android-player', android_player),
    ('ios-player', ios_player),
]
if WINDOWS_HOST:
    STEPS = [(name, action) for name, action in STEPS
             if name not in ('package', 'native-asan', 'native-tsan', 'android-player', 'ios-player')]


def prune_runs(base):
    finished = []
    for path in base.iterdir():
        if path.is_dir() and not path.is_symlink():
            try:
                finished.append(((path / 'summary.json').stat().st_mtime_ns, path))
            except FileNotFoundError:
                pass
    for _, path in sorted(finished, reverse=True)[10:]:
        try:
            shutil.rmtree(path)
        except FileNotFoundError:
            pass


def main():
    started = time.monotonic()
    names = dict(STEPS)
    arguments = sys.argv[1:]
    if arguments == ['--list']:
        print('\n'.join(names))
        return 0
    if any(name not in names for name in arguments):
        print('usage: scripts/gate [step ...] | --list', file=sys.stderr)
        emit({'status': 'error', 'steps': {}, 'seconds': round(time.monotonic() - started, 3)})
        return 2
    selected = list(dict.fromkeys(arguments)) if arguments else list(names)
    try:
        commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
        dirty = bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT))
        base = ROOT / 'out/gate'
        base.mkdir(parents=True, exist_ok=True)
        work = Path(tempfile.mkdtemp(prefix=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ-'), dir=base))
    except (OSError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        emit({'status': 'error', 'steps': {}, 'seconds': round(time.monotonic() - started, 3)})
        return 2
    statuses = {}
    package_tree_sha256 = None
    for name in selected:
        step_started = time.monotonic()
        step_work = work / name
        log_path = work / (name + '.log')
        with log_path.open('w') as log:
            try:
                step_work.mkdir()
                action = names[name]
                status = action(step_work, log) if callable(action) else command_status(action, log)
            except (OSError, Unavailable, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
                status = 'error'
                emit({'status': 'error', 'problems': [str(error)]}, file=log)
        details = []
        for line in log_path.read_text(errors='replace').splitlines():
            try:
                value = json.loads(line)
                if isinstance(value, dict) and ('status' in value or 'pass' in value):
                    details.append(value)
            except ValueError:
                pass
        if name == 'package' and status == 'pass':
            package_tree_sha256 = details[-1]['tree_sha256'][0]
        statuses[name] = status
        emit({'step': name, 'status': status, 'seconds': round(time.monotonic() - step_started, 3),
              'log': str(log_path.relative_to(ROOT)),
              'detail': details[0] if len(details) == 1 else details or None})
    status = 'fail' if 'fail' in statuses.values() else 'error' if 'error' in statuses.values() else 'pass'
    dirty = (dirty or bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT))
             or commit != subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip())
    summary = {'status': status, 'steps': statuses, 'seconds': round(time.monotonic() - started, 3),
               'commit': commit, 'dirty': dirty, 'tree_sha256': package_tree_sha256}
    (work / 'summary.tmp').write_text(json.dumps(summary) + '\n')
    (work / 'summary.tmp').replace(work / 'summary.json')
    try:
        prune_runs(base)
    except OSError as error:
        print(f'gate retention failed: {error}', file=sys.stderr)
        summary['status'] = status = 'error'
    emit(summary)
    return {'pass': 0, 'fail': 1, 'error': 2}[status]


if __name__ == '__main__':
    sys.exit(main())
