"""Read the existing WeChat Codex profile; never create a model turn or copy auth."""
import argparse
import json
import os
import selectors
import shlex
import signal
import subprocess
import time
from pathlib import Path
from .common import atomic_json, snapshot


class Rpc:
    def __init__(self, command, cwd):
        self.process = subprocess.Popen([command, 'app-server', '--stdio'], cwd=cwd,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL)
        self.selector = selectors.DefaultSelector()
        self.selector.register(self.process.stdout, selectors.EVENT_READ)
        self.buffer = b''
        self.counter = 0

    def request(self, method, params):
        self.counter += 1
        ident = self.counter
        body = json.dumps({'id': ident, 'method': method, 'params': params}).encode() + b'\n'
        self.process.stdin.write(body)
        self.process.stdin.flush()
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            while b'\n' in self.buffer:
                line, self.buffer = self.buffer.split(b'\n', 1)
                response = json.loads(line)
                if response.get('id') == ident:
                    if 'error' in response:
                        raise ValueError('codex_rpc_error')
                    return response['result']
            if not self.selector.select(max(0, deadline - time.monotonic())):
                break
            chunk = os.read(self.process.stdout.fileno(), 65536)
            if not chunk:
                break
            self.buffer += chunk
            if len(self.buffer) > 4_000_000:
                raise ValueError('codex_response_too_large')
        raise TimeoutError('codex_read_timeout')

    def close(self):
        self.process.terminate()
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.selector.close()
        self.process.stdin.close()
        self.process.stdout.close()


def collect(args):
    actual_model = args.actual_model
    if args.source_service:
        raw = subprocess.check_output(['systemctl', 'show', args.source_service, '-p', 'Environment', '--value'],
                                      timeout=5, text=True)
        relevant = {k: v for item in shlex.split(raw) for k, _, v in [item.partition('=')]
                    if k in ('CODEX_MODEL', 'CODEX_HOME')}
        if relevant.get('CODEX_HOME') != os.environ.get('CODEX_HOME'):
            raise ValueError('source_profile_changed')
        actual_model = relevant.get('CODEX_MODEL', '')
    rpc = Rpc(args.command, args.workspace)
    try:
        rpc.request('initialize', {'clientInfo': {'name': 'codex-usage-sentinel', 'version': '2.0.0'},
                                   'capabilities': {'experimentalApi': True}})
        rpc.process.stdin.write(b'{"method":"initialized"}\n')
        rpc.process.stdin.flush()
        models, cursor = [], None
        for _ in range(5):
            page = rpc.request('model/list', {'includeHidden': True, 'limit': 100, 'cursor': cursor})
            models += [m.get('model') or m.get('id') for m in page.get('data', [])]
            cursor = page.get('nextCursor')
            if not cursor:
                break
        limits = rpc.request('account/rateLimits/read', None)
        return snapshot(limits, models, time.time(), actual_model=actual_model)
    finally:
        rpc.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--command', required=True)
    parser.add_argument('--workspace', required=True)
    parser.add_argument('--actual-model', default='')
    parser.add_argument('--source-service', default='')
    parser.add_argument('--output', required=True)
    parser.add_argument('--request', required=True)
    parser.add_argument('--once', action='store_true')
    args = parser.parse_args()
    running = True

    def stop(*_):
        nonlocal running
        running = False

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    due, last_request, last_read = 0, 0, 0
    while running:
        try:
            requested = Path(args.request).stat().st_mtime_ns
        except OSError:
            requested = 0
        if time.monotonic() >= due or (requested != last_request and time.monotonic() - last_read >= 10):
            last_request = requested
            try:
                data = collect(args)
            except (OSError, ValueError, KeyError, TypeError, TimeoutError):
                data = {'ok': False, 'checked_at': time.time(), 'error': 'cli_read_failed'}
            atomic_json(args.output, data)
            last_read = time.monotonic()
            due = last_read + 60
            if args.once:
                print(json.dumps(data, ensure_ascii=True))
                return 0 if data['ok'] else 1
        time.sleep(1)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
