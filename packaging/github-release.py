"""Publish prepared artifacts using the user's existing Git Credential Manager login.

Credentials stay in memory, never in source, URLs, command arguments or logs.
Repository/release creation is deliberately separate from build and verification.
"""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import subprocess
import urllib.error
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('action', choices=['check', 'create-repository', 'release'])
parser.add_argument('--owner', required=True)
parser.add_argument('--repository', default='PCWatt')
parser.add_argument('--version', default='0.1.0')
parser.add_argument('--directory', type=Path)
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

def api(method, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request('https://api.github.com' + path, data=data, method=method,
        headers=dict(headers, **({'Content-Type': 'application/json'} if data is not None else {})))
    try:
        with urllib.request.urlopen(request, timeout=90) as response:
            raw = response.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        # Never print request headers, credentials or arbitrary response payloads.
        raise RuntimeError(f'GitHub {method} {path}: HTTP {error.code}') from None

user = api('GET', '/user')
if user['login'].lower() != args.owner.lower():
    raise SystemExit('Git credential account does not match the authorized owner.')
repository = f'/repos/{args.owner}/{args.repository}'
if args.action == 'check':
    print(json.dumps({'authenticated_as': user['login'], 'credential_source': 'Git Credential Manager'}))
elif args.action == 'create-repository':
    result = api('POST', '/user/repos', {'name': args.repository, 'private': False, 'auto_init': False,
        'description': '看清电脑用了多少电，花了多少钱。Windows 本地功耗、电费、温度与占用率工具。',
        'has_issues': True, 'has_wiki': False, 'has_projects': False})
    print(json.dumps({'url': result['html_url'], 'default_branch': result['default_branch'], 'private': result['private']}))
else:
    directory = args.directory.resolve()
    expected = [f'PCWatt-{args.version}-Setup-win-x64.exe', f'PCWatt-{args.version}-win-x64.zip', 'SHA256SUMS.txt', '使用说明.txt']
    if not all((directory / name).is_file() for name in expected):
        raise SystemExit('Release artifacts are incomplete.')
    for row in (directory / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
        digest, name = row.split('  ', 1)
        if hashlib.sha256((directory / name).read_bytes()).hexdigest() != digest:
            raise SystemExit('Release hash mismatch; nothing was uploaded.')
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=directory / 'source', text=True).strip()
    notes = (directory / 'source/docs/releases/0.1.0.md').read_text(encoding='utf-8')
    release = api('POST', repository + '/releases', {'tag_name': 'v' + args.version,
        'target_commitish': commit, 'name': 'PC Watt ' + args.version + ' · 首个公开预览版',
        'body': notes, 'draft': True, 'prerelease': True, 'generate_release_notes': False})
    upload = release['upload_url'].split('{', 1)[0]
    parsed = urllib.parse.urlsplit(upload)
    if parsed.scheme != 'https' or parsed.hostname != 'uploads.github.com':
        raise SystemExit('Unexpected GitHub upload host; draft left unpublished.')
    assets = []
    for name in expected:
        path = directory / name
        connection = http.client.HTTPSConnection(parsed.hostname, timeout=300)
        connection.putrequest('POST', parsed.path + '?' + urllib.parse.urlencode({'name': name}))
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
        expected_digest = 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest()
        if asset['size'] != path.stat().st_size or asset.get('digest') != expected_digest or asset['state'] != 'uploaded':
            raise SystemExit('GitHub asset verification failed; draft remains unpublished.')
        assets.append({'name': name, 'bytes': asset['size'], 'sha256': expected_digest, 'url': asset['browser_download_url']})
        print(json.dumps({'uploaded': name, 'bytes': asset['size']}), flush=True)
        connection.close()
    published = api('PATCH', repository + '/releases/' + str(release['id']), {'draft': False})
    verification = api('GET', repository + '/releases/tags/v' + args.version)
    if verification['draft'] or len(verification['assets']) != len(expected):
        raise SystemExit('Published release metadata did not match expected assets.')
    record = {'url': published['html_url'], 'commit': commit, 'prerelease': published['prerelease'], 'assets': assets}
    (directory / 'github-release.json').write_text(json.dumps(record, indent=2, ensure_ascii=False), encoding='utf-8')
    print(json.dumps(record, ensure_ascii=False))
