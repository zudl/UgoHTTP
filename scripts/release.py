#!/usr/bin/env python3
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

from gate import ROOT, STEPS, driver


class Precondition(RuntimeError):
    def __init__(self, classification, message):
        super().__init__(message)
        self.classification = classification


def git(*arguments, cwd=ROOT):
    return subprocess.check_output(['git', *arguments], cwd=cwd, text=True,
                                   stderr=subprocess.PIPE).strip()


def require(condition, classification, message):
    if not condition:
        raise Precondition(classification, message)


def release(version):
    require(re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version),
            'version', 'expected X.Y.Z')
    require(git('branch', '--show-current') == 'master', 'branch', 'release must run on master')
    require(not git('status', '--porcelain'), 'dirty', 'working tree must be clean')
    commit = git('rev-parse', 'HEAD')
    require(json.loads((ROOT / 'package/package.json').read_text())['version'] == version,
            'package_version', 'package/package.json version must match ' + version)
    require(re.search(r'^## \[' + re.escape(version) + r'\](?:\s|$)',
                      (ROOT / 'package/CHANGELOG.md').read_text(), re.MULTILINE),
            'changelog', 'package/CHANGELOG.md must contain ## [' + version + ']')
    require(re.search(r'^"com\.ugohttp\.core": ".*#v' + re.escape(version) + r'"$',
                      (ROOT / 'README.md').read_text(), re.MULTILINE),
            'readme', 'README install line must end with #v' + version + '"')
    tag = 'v' + version
    require(not git('tag', '--list', tag), 'local_tag', tag + ' already exists locally')
    require(not git('ls-remote', '--tags', 'origin', 'refs/tags/' + tag),
            'remote_tag', tag + ' already exists on origin')
    runs = list((ROOT / 'out/gate').glob('*/'))
    require(runs, 'gate', 'no gate run; run ./scripts/gate')
    latest = max(runs, key=lambda path: path.stat().st_mtime_ns)
    try:
        summary = json.loads((latest / 'summary.json').read_text())
    except (OSError, ValueError) as error:
        raise Precondition('gate', 'latest gate run has no readable summary; run ./scripts/gate') from error
    require(isinstance(summary, dict) and summary.get('status') == 'pass'
            and summary.get('steps') == dict.fromkeys((name for name, _ in STEPS), 'pass')
            and summary.get('commit') == commit and summary.get('dirty') is False,
            'gate', 'latest gate run must pass all steps on this HEAD with a clean tree; run ./scripts/gate')
    try:
        git('fetch', 'origin', 'upm:refs/remotes/origin/upm')
        remote_upm = True
    except subprocess.CalledProcessError:
        if git('ls-remote', '--heads', 'origin', 'refs/heads/upm'):
            raise
        remote_upm = False
    exists = bool(git('branch', '--list', 'upm'))
    if remote_upm:
        if exists:
            require(git('rev-list', '--count', 'upm..origin/upm') == '0',
                    'upm_behind', 'local upm is behind origin/upm; update it before releasing')
        else:
            git('branch', 'upm', 'origin/upm')
            exists = True
    with tempfile.TemporaryDirectory(prefix='ugo-release-') as temporary:
        temporary = Path(temporary)
        tree = temporary / 'tree'
        result = subprocess.run([ROOT / 'scripts/package', tree], cwd=ROOT,
                                text=True, capture_output=True)
        require(result.returncode == 0, 'package', result.stdout.strip() or result.stderr.strip()
                or 'scripts/package failed')
        require(driver.tree_sha256(tree) == summary.get('tree_sha256'),
                'package', 'package tree differs from the passing gate; run ./scripts/gate')
        require(git('rev-parse', 'HEAD') == commit and not git('status', '--porcelain'),
                'dirty', 'master changed while assembling the package')
        work = temporary / 'upm'
        git('worktree', 'add', str(work), *(['upm'] if exists else ['--detach', commit]))
        try:
            if not exists:
                git('switch', '--orphan', 'upm', cwd=work)
            if (work / 'package').exists():
                shutil.rmtree(work / 'package')
            shutil.copytree(tree / 'package', work / 'package')
            git('add', '-f', '--all', 'package', cwd=work)
            git('commit', '-m', 'UgoHTTP ' + version, '-m',
                f'Built from master {commit} by ./scripts/package; '
                f'./scripts/gate passed all {len(STEPS)} steps on that commit.', cwd=work)
            release_commit = git('rev-parse', 'HEAD', cwd=work)
            git('tag', '-a', tag, '-m', 'UgoHTTP ' + version, release_commit)
        finally:
            git('worktree', 'remove', '--force', str(work))
    return {'status': 'ok', 'version': version, 'master': commit, 'commit': release_commit,
            'tag': tag, 'push': ['git push origin upm', 'git push origin ' + tag]}


def main():
    try:
        if len(sys.argv) != 2:
            print(json.dumps({'status': 'error', 'class': 'usage', 'problems': ['usage: scripts/release X.Y.Z']}))
            return 2
        result = release(sys.argv[1])
    except Precondition as error:
        print(json.dumps({'status': 'fail', 'class': error.classification, 'problems': [str(error)]}))
        return 1
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        message = (error.stderr.strip() or str(error)) if isinstance(error, subprocess.CalledProcessError) else str(error)
        print(json.dumps({'status': 'error', 'class': 'tooling', 'problems': [message]}))
        return 2
    print(json.dumps(result))
    return 0


if __name__ == '__main__':
    sys.exit(main())
