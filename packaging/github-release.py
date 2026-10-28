"""Publish prepared artifacts using the user's existing Git Credential Manager login.

Credentials stay in memory, never in source, URLs, command arguments or logs.
Repository/release creation is deliberately separate from build and verification.
"""
import argparse
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import http.client
import json
import os
import re
from pathlib import Path
import subprocess
import urllib.error
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('action', choices=['check', 'create-repository', 'push-source', 'release'])
parser.add_argument('--owner', required=True)
parser.add_argument('--repository', default='PCWatt')
parser.add_argument('--version', default='0.1.0')
parser.add_argument('--directory', type=Path)
parser.add_argument('--source-directory', type=Path, default=Path(__file__).resolve().parent.parent)
parser.add_argument('--title')
args = parser.parse_args()
environment = dict(os.environ, GIT_TERMINAL_PROMPT='0', GCM_INTERACTIVE='Never')
credential = subprocess.run(['git', '-c', 'credential.interactive=never', 'credential', 'fill'],
    input=f'protocol=https\nhost=github.com\nusername={args.owner}\n\n',
    text=True, capture_output=True, env=environment, check=False)
if credential.returncode:
    raise SystemExit('Existing Git credential unavailable; sign in with Git Credential Manager.')
fields = dict(line.split('=', 1) for line in credential.stdout.splitlines() if '=' in line)
token = fields.get('password')
if not token:
    raise SystemExit('No usable Git credential; nothing was published.')
headers = {'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
    'X-GitHub-Api-Version': '2026-03-10', 'User-Agent': 'PCWatt-release'}

def api(method, path, payload=None, allow_missing=False):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request('https://api.github.com' + path, data=data, method=method,
        headers=dict(headers, **({'Content-Type': 'application/json'} if data is not None else {})))
    try:
        with urllib.request.urlopen(request, timeout=90) as response:
            raw = response.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        if allow_missing and error.code == 404:
            return None
        # Never print request headers, credentials or arbitrary response payloads.
        raise RuntimeError(f'GitHub {method} {path}: HTTP {error.code}') from None

user = api('GET', '/user')
if user['login'].lower() != args.owner.lower():
    raise SystemExit('Git credential account does not match the authorized owner.')
repository = f'/repos/{args.owner}/{args.repository}'
source_root = args.source_directory.resolve()

def git(*arguments):
    return subprocess.check_output(['git', *arguments], cwd=source_root)

def clean_commit():
    if git('status', '--porcelain', '--untracked-files=normal').strip():
        raise SystemExit('Commit the prepared source before publishing.')
    return git('rev-parse', 'HEAD').decode().strip()

def push_source():
    """REST fallback for unavailable Git transport; verify identical local tree AND commit SHAs."""
    commit = clean_commit()
    ref = api('GET', repository + '/git/ref/heads/main')
    previous = ref['object']['sha']
    if previous == commit:
        print(json.dumps({'source_commit': commit, 'already_pushed': True})); return
    raw_commit = git('cat-file', 'commit', commit)
    metadata, message = raw_commit.split(b'\n\n', 1)
    lines = metadata.decode().splitlines()
    parents = [line[7:] for line in lines if line.startswith('parent ')]
    if parents != [previous]:
        raise SystemExit('Remote main changed; inspect and reconcile before publishing. No ref was updated.')
    base = api('GET', repository + '/git/commits/' + previous)
    changes = git('diff-tree', '-r', '--no-commit-id', '--raw', '-z', '--no-renames', previous, commit).split(b'\0')
    entries = []
    for i in range(0, len(changes) - 1, 2):
        fields = changes[i].decode().split()
        path = changes[i + 1].decode('utf-8')
        mode, sha, status = fields[1], fields[3], fields[4]
        entry = {'path': path, 'mode': mode if status != 'D' else fields[0][1:], 'type': 'blob'}
        if status == 'D':
            entry['sha'] = None
        else:
            content = git('cat-file', 'blob', sha)
            try:
                decoded = content.decode('utf-8')
                if '\0' in decoded: raise UnicodeError()
                entry['content'] = decoded
            except UnicodeError:
                result = api('POST', repository + '/git/blobs', {'encoding': 'base64', 'content': base64.b64encode(content).decode('ascii')})
                if result['sha'] != sha: raise SystemExit('Uploaded blob hash mismatch.')
                entry['sha'] = sha
        entries.append(entry)
    tree = api('POST', repository + '/git/trees', {'base_tree': base['tree']['sha'], 'tree': entries})
    local_tree = git('rev-parse', 'HEAD^{tree}').decode().strip()
    if tree['sha'] != local_tree: raise SystemExit('Remote tree differs from verified local source. No ref was updated.')
    def identity(prefix):
        line = next(line[len(prefix) + 1:] for line in lines if line.startswith(prefix + ' '))
        matched = re.fullmatch(r'(.*) <([^>]*)> (\d+) ([+-])(\d{2})(\d{2})', line)
        if not matched: raise SystemExit('Unsupported commit identity.')
        name, email, seconds, sign, hours, minutes = matched.groups()
        offset = timedelta(hours=int(hours), minutes=int(minutes)) * (1 if sign == '+' else -1)
        return {'name': name, 'email': email, 'date': datetime.fromtimestamp(int(seconds), timezone(offset)).isoformat()}
    remote_commit = api('POST', repository + '/git/commits', {'tree': local_tree, 'parents': parents,
        'message': message.decode('utf-8'), 'author': identity('author'), 'committer': identity('committer')})
    if remote_commit['sha'] != commit: raise SystemExit('Commit identity mismatch. No branch ref was updated.')
    if api('GET', repository + '/git/ref/heads/main')['object']['sha'] != previous:
        raise SystemExit('Remote main changed during preparation. No branch ref was updated.')
    updated = api('PATCH', repository + '/git/refs/heads/main', {'sha': commit, 'force': False})
    if updated['object']['sha'] != commit: raise SystemExit('Source ref verification failed.')
    subprocess.run(['git', 'update-ref', 'refs/remotes/origin/main', commit], cwd=source_root, check=True)
    print(json.dumps({'source_commit': commit, 'tree': local_tree, 'files': len(entries), 'transport': 'GitHub REST; exact Git object hashes verified'}))

if args.action == 'check':
    print(json.dumps({'authenticated_as': user['login'], 'credential_source': 'Git Credential Manager'}))
elif args.action == 'create-repository':
    result = api('POST', '/user/repos', {'name': args.repository, 'private': False, 'auto_init': False,
        'description': '看清电脑用了多少电，花了多少钱。Windows 本地功耗、电费、温度与占用率工具。',
        'has_issues': True, 'has_wiki': False, 'has_projects': False})
    print(json.dumps({'url': result['html_url'], 'default_branch': result['default_branch'], 'private': result['private']}))
elif args.action == 'push-source':
    push_source()
else:
    directory = args.directory.resolve()
    expected = [f'PCWatt-{args.version}-Setup-win-x64.exe', f'PCWatt-{args.version}-win-x64.zip', 'SHA256SUMS.txt', '使用说明.txt']
    if not all((directory / name).is_file() for name in expected):
        raise SystemExit('Release artifacts are incomplete.')
    for row in (directory / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
        digest, name = row.split('  ', 1)
        if hashlib.sha256((directory / name).read_bytes()).hexdigest() != digest:
            raise SystemExit('Release hash mismatch; nothing was uploaded.')
    commit = clean_commit()
    if api('GET', repository + '/git/ref/heads/main')['object']['sha'] != commit:
        raise SystemExit('Push and verify the source commit before uploading assets.')
    notes = (source_root / f'docs/releases/{args.version}.md').read_text(encoding='utf-8-sig')
    release = api('GET', repository + '/releases/tags/v' + args.version, allow_missing=True)
    if release is not None and (not release['draft'] or release['target_commitish'] != commit):
        raise SystemExit('Existing release is preserved; inspect it before retrying.')
    if release is None:
        release = api('POST', repository + '/releases', {'tag_name': 'v' + args.version,
            'target_commitish': commit, 'name': args.title or 'PC Watt ' + args.version + ' · 公开预览版',
            'body': notes, 'draft': True, 'prerelease': True, 'generate_release_notes': False})
    upload = release['upload_url'].split('{', 1)[0]
    parsed = urllib.parse.urlsplit(upload)
    if parsed.scheme != 'https' or parsed.hostname != 'uploads.github.com':
        raise SystemExit('Unexpected GitHub upload host; draft left unpublished.')
    assets = []
    for name in expected:
        path = directory / name
        remote_name = f'PCWatt-{args.version}-Readme-zh-CN.txt' if name == '使用说明.txt' else name
        expected_digest = 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest()
        existing = next((a for a in release['assets'] if a['name'] == remote_name), None)
        if existing is not None:
            if existing.get('digest') == expected_digest and existing['size'] == path.stat().st_size and existing['state'] == 'uploaded':
                print(json.dumps({'verified_existing': remote_name}), flush=True); continue
            raise SystemExit('Conflicting draft asset preserved; inspect it before retrying.')
        connection = http.client.HTTPSConnection(parsed.hostname, timeout=300)
        connection.putrequest('POST', parsed.path + '?' + urllib.parse.urlencode({'name': remote_name}))
        for key, value in dict(headers, **{'Content-Type': 'application/octet-stream', 'Content-Length': str(path.stat().st_size)}).items():
            connection.putheader(key, value)
        connection.endheaders()
        with path.open('rb') as stream:
            while block := stream.read(1024 * 1024):
                connection.send(block)
        response = connection.getresponse()
        payload = response.read()
        if response.status != 201:
            raise SystemExit(f'Asset upload failed: HTTP {response.status}; draft remains unpublished.')
        asset = json.loads(payload)
        if asset['size'] != path.stat().st_size or asset.get('digest') != expected_digest or asset['state'] != 'uploaded':
            raise SystemExit('GitHub asset verification failed; draft remains unpublished.')
        assets.append({'name': name, 'bytes': asset['size'], 'sha256': expected_digest, 'url': asset['browser_download_url']})
        print(json.dumps({'uploaded': name, 'bytes': asset['size']}), flush=True)
        connection.close()
    uploaded = api('GET', repository + '/releases/' + str(release['id']))
    remote_names = {f'PCWatt-{args.version}-Readme-zh-CN.txt' if n == '使用说明.txt' else n for n in expected}
    if {a['name'] for a in uploaded['assets']} != remote_names:
        raise SystemExit('Draft asset set differs from expected files; draft remains unpublished.')
    published = api('PATCH', repository + '/releases/' + str(release['id']), {'draft': False})
    verification = api('GET', repository + '/releases/tags/v' + args.version)
    if verification['draft'] or len(verification['assets']) != len(expected):
        raise SystemExit('Published release metadata did not match expected assets.')
    if api('GET', repository + '/git/ref/tags/v' + args.version)['object']['sha'] != commit:
        raise SystemExit('Published tag does not match the verified source commit.')
    # Draft upload URLs contain temporary untagged names; only published URLs are deliverables.
    assets = [{'name': a['name'], 'bytes': a['size'], 'sha256': a.get('digest'), 'url': a['browser_download_url']} for a in verification['assets']]
    record = {'url': published['html_url'], 'commit': commit, 'prerelease': published['prerelease'], 'assets': assets}
    (directory / 'github-release.json').write_text(json.dumps(record, indent=2, ensure_ascii=False), encoding='utf-8')
    print(json.dumps(record, ensure_ascii=False))
