"""One owned QN90C native pointer session; never imports or operates the TV at import time."""
from __future__ import annotations

import asyncio
import base64
from collections import deque
from dataclasses import dataclass, field
import hashlib
import hmac
import json
from pathlib import Path
import re
import secrets
import socket
import time

PC = '192.168.1.75'
TV = '192.168.1.5'
CAPABILITIES = ['move', 'left-down', 'left-up']
POLICY_CLEANUP_SECONDS = 11
POLICY_FILES = ('policy-session.json', 'policy-owner.json', 'policy-heartbeat.json',
                'policy-ready.json', 'policy-release.json', 'policy-result.json')


@dataclass
class _Input:
    action: str
    x: int
    y: int
    waiters: list[asyncio.Future] = field(default_factory=list)


class NativeInputController:
    def __init__(self, root: Path):
        self.root = Path(root)
        self._ensure_lock = asyncio.Lock()
        self._rpc_lock = asyncio.Lock()
        self._queue = deque()
        self._queue_changed = asyncio.Event()
        self._server = None
        self._writer = None
        self._connection = None
        self._heartbeat_task = None
        self._worker_task = None
        self._handlers = set()
        self._pending = {}
        self._ready_future = None
        self._closed_future = None
        self._launch_known = None
        self._generation = 0
        self._next_id = 0
        self._ready = False
        self._stopping = False
        self._buttons = 0
        self._pid = 0
        self._pid_start = None
        self._retired = []
        self._policy_required = set()
        self._policy_evidence = {}
        self._folder = None
        self._token = None
        self._failure_count = 0
        self._next_retry = 0.0
        self._message = 'Mouse input not connected'
        self._state = 'disconnected'
        self._last_cleanup = None
        self._ack_samples = deque(maxlen=64)
        self._last_ack_ms = None
        self._max_ack_ms = None
        self._retired_ids = deque(maxlen=16)

    def status(self):
        return {'pointerAvailable': self._ready, 'inputState': self._state,
                'inputMessage': self._message, 'inputButtons': self._buttons,
                'pointerCapabilities': CAPABILITIES.copy(), 'pointerWidth': 1920,
                'pointerHeight': 1080, 'inputCleanupComplete': self._last_cleanup,
                'lastAckMilliseconds': self._last_ack_ms, 'maxAckMilliseconds': self._max_ack_ms,
                'meanAckMilliseconds': round(sum(self._ack_samples) / len(self._ack_samples), 2) if self._ack_samples else None,
                'ackSampleCount': len(self._ack_samples), 'ackMeaning': 'PC send to native acceptance; not app visual latency'}

    async def ensure_connected(self, connection):
        if self._ready and self._connection is connection:
            return True
        if self._ensure_lock.locked() or time.monotonic() < self._next_retry:
            return False
        async with self._ensure_lock:
            if self._ready and self._connection is connection:
                return True
            # A replacement root session must verify cleanup through the new live RPC.
            await self._stop_locked(cleanup_connection=connection)
            self._stopping = False
            self._connection = connection
            self._generation += 1
            generation = self._generation
            self._state, self._message = 'connecting', 'Connecting TV mouse input'
            self._ready_future = asyncio.get_running_loop().create_future()
            self._closed_future = asyncio.get_running_loop().create_future()
            self._launch_known = asyncio.Event()
            self._token = secrets.token_hex(32)
            try:
                if self._retired:
                    raise RuntimeError('Previous native input cleanup is not yet confirmed')
                parent = int(connection.identity.pid)
                if parent <= 1:
                    raise ValueError('Invalid root-session PID')
                self._folder = f'/run/samsung-tv-root/agent-{parent}/native-input-{secrets.token_hex(6)}'
                package = self.root / 'tools/tv-native-input/out'
                manifest = json.loads((package / 'manifest.json').read_text('utf8'))
                files = {}
                for name in ('NativeInput.dll', 'NativeInput.deps.json', 'NativeInput.runtimeconfig.json'):
                    data = (package / name).read_bytes()
                    if hashlib.sha256(data).hexdigest() != manifest.get(name):
                        raise RuntimeError('Native input package checksum mismatch: ' + name)
                    files[name] = data
                port = await self._listen(generation)
                result = await connection.execute(f'mkdir -m 700 {self._folder}', 5)
                self._check_exec(result, 'Create native input directory')
                for name, data in files.items():
                    await connection.write_file(self._folder + '/' + name, data, 10)
                config = {'host': PC, 'port': port, 'token': self._token,
                          'parentPid': parent, 'width': 1920, 'height': 1080}
                await connection.write_file(self._folder + '/session.json', json.dumps(config).encode(), 5)
                result = await connection.execute(
                    f'chmod 600 {self._folder}/session.json; '
                    f'LD_LIBRARY_PATH= LD_PRELOAD= /usr/bin/dotnet {self._folder}/NativeInput.dll '
                    f'--session {self._folder}/session.json > {self._folder}/output.jsonl 2>&1 < /dev/null & echo $!', 5)
                self._check_exec(result, 'Start native input helper')
                output = result.stdout.strip()
                if not re.fullmatch(r'[1-9][0-9]*', output):
                    raise RuntimeError('Native input launch returned an invalid PID')
                self._pid = int(output)
                self._launch_known.set()
                await asyncio.wait_for(asyncio.shield(self._ready_future), 12)
                self._failure_count = 0
                self._next_retry = 0
                return True
            except asyncio.CancelledError:
                await self._stop_locked()
                raise
            except Exception as exc:
                self._message = 'TV mouse input unavailable: ' + str(exc)[:240]
                await self._stop_locked()
                self._state = 'retrying'
                self._failure_count += 1
                self._next_retry = time.monotonic() + min(15, 2 ** min(self._failure_count, 4))
                return False

    async def _listen(self, generation):
        for _ in range(12):
            port = 49152 + secrets.randbelow(16384)
            try:
                self._server = await asyncio.start_server(
                    lambda r, w: self._accepted(r, w, generation), PC, port, limit=8192)
                return port
            except OSError:
                continue
        raise RuntimeError('Cannot open the fixed-PC input callback listener')

    async def _accepted(self, reader, writer, generation):
        task = asyncio.current_task()
        self._handlers.add(task)
        authenticated = False
        try:
            peer = writer.get_extra_info('peername')
            if not peer or peer[0] != TV or generation != self._generation:
                return
            hello = await self._read_json(reader, 3)
            token = hello.get('token')
            if (hello.get('type') != 'hello' or hello.get('protocol') != 1 or
                    not isinstance(token, str) or not self._token or
                    not hmac.compare_digest(token, self._token) or self._writer):
                return
            await asyncio.wait_for(self._launch_known.wait(), 6)
            if (self._writer or not self._token or not hmac.compare_digest(token, self._token) or
                    hello.get('pid') != self._pid or hello.get('width') != 1920 or hello.get('height') != 1080 or
                    not re.fullmatch(r'[0-9]+', str(hello.get('startTime', '')))):
                return
            # The token is one-use and must never be included in diagnostics.
            self._token = None
            self._writer = writer
            self._pid_start = str(hello['startTime'])
            authenticated = True
            if self._folder:
                # No policy companion may start until this authenticated reply.
                self._policy_required.add(self._folder)
            sock = writer.get_extra_info('socket')
            if sock is not None:
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            writer.write(b'{"type":"hello","ok":true}\n')
            await writer.drain()
            ready = await self._read_json(reader, 8)
            if (ready.get('type') != 'ready' or ready.get('ok') is not True or
                    ready.get('width') != 1920 or ready.get('height') != 1080 or
                    ready.get('pointerAvailable') is not True or ready.get('buttons') != 0):
                raise RuntimeError(ready.get('error', 'Native input did not become ready'))
            if generation != self._generation or self._stopping:
                return
            if self._folder in self._policy_required:
                self._remember_policy_ready(self._folder, ready.get('policy'))
            self._ready = True
            self._buttons = 0
            self._state, self._message = 'ready', 'Mouse input connected (left button)'
            self._last_cleanup = None
            if not self._ready_future.done():
                self._ready_future.set_result(True)
            self._worker_task = asyncio.create_task(self._worker())
            self._heartbeat_task = asyncio.create_task(self._heartbeat())
            if self._server:
                self._server.close()  # Only one authenticated TV may use this listener.
            while generation == self._generation:
                response = await self._read_json(reader, 12)
                if response.get('type') == 'closed':
                    # A native close receipt precedes independent process/file
                    # verification. Do not advertise complete while a lease may live.
                    self._last_cleanup = (False if self._folder in self._policy_required
                                          else response.get('cleanupComplete') is True)
                    if self._closed_future and not self._closed_future.done():
                        self._closed_future.set_result(response)
                    break
                if response.get('type') == 'error':
                    raise RuntimeError(response.get('error', 'Native input helper failed'))
                identifier = response.get('id')
                future = self._pending.get(identifier)
                if future is None:
                    if self._stopping and identifier in self._retired_ids:
                        continue
                    raise RuntimeError('Unexpected native input response ID')
                if not future.done():
                    future.set_result(response)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            if authenticated and generation == self._generation:
                self._fault(str(exc))
            elif generation == self._generation and self._ready_future and not self._ready_future.done() and self._token is None:
                self._ready_future.set_exception(RuntimeError(str(exc)))
        finally:
            if authenticated and generation == self._generation:
                if self._closed_future and not self._closed_future.done():
                    self._closed_future.set_result({'type': 'closed', 'cleanupComplete': False,
                                                   'transportClosed': True})
                self._fault('TV mouse connection closed', preserve_message=self._stopping)
            writer.close()
            try:
                await writer.wait_closed()
            except Exception:
                pass
            self._handlers.discard(task)

    @staticmethod
    async def _read_json(reader, timeout):
        line = await asyncio.wait_for(reader.readline(), timeout)
        if not line:
            raise ConnectionError('Native input EOF')
        if len(line) > 4096:
            raise ValueError('Native input response too large')
        value = json.loads(line)
        if not isinstance(value, dict):
            raise ValueError('Native input response must be an object')
        return value

    async def _rpc(self, action, x=None, y=None, *, allow_stopping=False):
        async with self._rpc_lock:
            if not self._writer or (not self._ready and not allow_stopping):
                raise ConnectionError('TV mouse not connected')
            self._next_id += 1
            identifier = self._next_id
            command = {'id': identifier, 'action': action}
            if x is not None:
                command.update(x=x, y=y, buttons=1 if action == 'down' else 0 if action == 'up' else self._buttons)
            future = asyncio.get_running_loop().create_future()
            self._pending[identifier] = future
            started = time.perf_counter()
            try:
                self._writer.write((json.dumps(command, separators=(',', ':')) + '\n').encode())
                await asyncio.wait_for(self._writer.drain(), 2)
                response = await asyncio.wait_for(asyncio.shield(future), 3)
                if response.get('ok') is not True or response.get('result') != 0:
                    raise RuntimeError(response.get('error', 'The TV rejected the mouse event'))
                buttons = response.get('buttons')
                if type(buttons) is not int or buttons not in (0, 1):
                    raise RuntimeError('Invalid native button state')
                self._buttons = buttons
                if action in ('move', 'down', 'up'):
                    elapsed = round((time.perf_counter() - started) * 1000, 2)
                    self._last_ack_ms = elapsed
                    self._max_ack_ms = max(elapsed, self._max_ack_ms or 0)
                    self._ack_samples.append(elapsed)
                return response
            finally:
                self._pending.pop(identifier, None)
                if not future.done():
                    self._retired_ids.append(identifier)
                    future.cancel()
                elif not future.cancelled():
                    future.exception()  # Drain a fault racing cancellation of the awaiting worker.

    async def pointer(self, action, x, y, button='left', delta=0):
        if button != 'left':
            raise ValueError('Only the left mouse button is verified; right and middle buttons are not supported')
        if action == 'wheel':
            raise ValueError('TV wheel input is not verified and not supported')
        if action not in ('move', 'down', 'up', 'click'):
            raise ValueError('Unsupported mouse action')
        if any(isinstance(v, bool) or not isinstance(v, int) for v in (x, y)) or not 0 <= x < 1920 or not 0 <= y < 1080:
            raise ValueError('Mouse coordinates must be inside 1920×1080')
        if not self._ready or self._stopping:
            raise ConnectionError('TV mouse not connected; use the arrow keys')
        future = asyncio.get_running_loop().create_future()
        # Only the last unsent move may be replaced; down/up/click are barriers.
        if action == 'move' and self._queue and self._queue[-1].action == 'move':
            item = self._queue[-1]
            if len(item.waiters) >= 256:
                raise ValueError('Too many mouse move requests; wait for the previous batch')
            item.x, item.y = x, y
            item.waiters.append(future)
        else:
            if len(self._queue) >= 128:
                self._fault('Mouse input queue overloaded; disconnected to release buttons')
                raise ConnectionError('Mouse input too fast; the connection was released safely')
            self._queue.append(_Input(action, x, y, [future]))
        self._queue_changed.set()
        # HTTP cancellation must not cancel a queued release or remove its ordering barrier.
        try:
            return await asyncio.shield(future)
        except asyncio.CancelledError:
            # Keep the native command (especially up) but consume an eventual error
            # after the HTTP caller has gone away.
            future.add_done_callback(lambda f: None if f.cancelled() else f.exception())
            raise

    async def _worker(self):
        current = None
        try:
            while self._ready and not self._stopping:
                if not self._queue:
                    self._queue_changed.clear()
                    await self._queue_changed.wait()
                    continue
                current = self._queue.popleft()
                actions = ('down', 'up') if current.action == 'click' else (current.action,)
                for action in actions:
                    response = await self._rpc(action, current.x, current.y)
                result = {'ok': True, 'nativeResult': response['result'], 'buttons': self._buttons,
                          'x': current.x, 'y': current.y, 'coalesced': len(current.waiters) > 1}
                for future in current.waiters:
                    if not future.done():
                        future.set_result(result)
                current = None
        except asyncio.CancelledError:
            if current:
                self._fail_waiters(current.waiters, 'Mouse connection is closing')
            raise
        except Exception as exc:
            if current:
                self._fail_waiters(current.waiters, str(exc))
            self._fault(str(exc))

    async def _heartbeat(self):
        try:
            while self._ready and not self._stopping:
                await asyncio.sleep(2)
                if self._ready and not self._stopping:
                    await self._rpc('ping')
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            self._fault(str(exc))

    def _fault(self, message, preserve_message=False):
        self._ready = False
        if not preserve_message:
            self._state, self._message = 'disconnected', 'TV mouse input disconnected: ' + message[:200]
        error = ConnectionError(message)
        if self._ready_future and not self._ready_future.done():
            self._ready_future.set_exception(error)
        for future in tuple(self._pending.values()):
            if not future.done():
                future.set_exception(error)
        while self._queue:
            self._fail_waiters(self._queue.popleft().waiters, message)
        self._queue_changed.set()
        if self._writer:
            self._writer.close()  # EOF makes the TV release any pressed left button.

    @staticmethod
    def _fail_waiters(waiters, message):
        for future in waiters:
            if not future.done():
                future.set_exception(ConnectionError(message))

    async def stop(self):
        async with self._ensure_lock:
            await self._stop_locked()
            self._state, self._message = 'disconnected', 'Mouse input disconnected'
            self._next_retry = 0

    async def _stop_locked(self, cleanup_connection=None):
        self._stopping = True
        self._ready = False
        for task in (self._heartbeat_task, self._worker_task):
            if task and task is not asyncio.current_task():
                task.cancel()
                try:
                    await task
                except (asyncio.CancelledError, Exception):
                    pass
        self._heartbeat_task = self._worker_task = None
        while self._queue:
            self._fail_waiters(self._queue.popleft().waiters, 'Mouse connection is closing')
        if self._writer and not self._writer.is_closing():
            try:
                await self._rpc('stop', allow_stopping=True)
                if self._closed_future:
                    await asyncio.wait_for(asyncio.shield(self._closed_future), POLICY_CLEANUP_SECONDS)
            except Exception:
                pass
        if self._writer:
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except Exception:
                pass
        self._writer = None
        if self._server:
            self._server.close()
            await self._server.wait_closed()
        self._server = None
        self._generation += 1
        for task in tuple(self._handlers):
            if task is not asyncio.current_task():
                task.cancel()
        if self._handlers:
            await asyncio.gather(*(t for t in self._handlers if t is not asyncio.current_task()), return_exceptions=True)
        self._handlers.clear()
        await self._cleanup_remote(cleanup_connection or self._connection)
        if self._ready_future and self._ready_future.done() and not self._ready_future.cancelled():
            self._ready_future.exception()
        self._connection = None
        self._folder, self._token, self._pid = None, None, 0
        self._pid_start = None
        self._buttons = 0

    async def _cleanup_remote(self, connection):
        if self._folder:
            record = (self._pid, self._pid_start, self._folder)
            if record not in self._retired:
                self._retired.append(record)
        had_records = bool(self._retired)
        if had_records:
            self._last_cleanup = False
        if not connection:
            return
        for record in self._retired.copy():
            try:
                if await self._cleanup_record(connection, *record):
                    self._retired.remove(record)
                    self._policy_required.discard(record[2])
                    self._policy_evidence.pop(record[2], None)
            except Exception:
                # Retain identity; the next live root session can verify it.
                pass
        if had_records:
            self._last_cleanup = not self._retired

    async def _cleanup_record(self, connection, pid, start, folder):
        if not re.fullmatch(r'/run/samsung-tv-root/agent-[1-9][0-9]*/native-input-[a-f0-9]{12}', folder):
            raise RuntimeError('Invalid owned cleanup path')
        expected_argv = ['/usr/bin/dotnet', folder + '/NativeInput.dll', '--session', folder + '/session.json']
        expected_cmdline = b'\0'.join(arg.encode('utf8') for arg in expected_argv) + b'\0'
        async def identity():
            nonlocal start
            if pid <= 1:
                return 'gone'
            result = await asyncio.wait_for(connection.execute(
                f'if test -d /proc/{pid}; then cat /proc/{pid}/stat; '
                f'base64 < /proc/{pid}/cmdline; else echo gone; fi', 3), 4)
            self._check_exec(result, 'Read owned input identity')
            if result.stdout.strip() == 'gone':
                return 'gone'
            lines = result.stdout.splitlines()
            fields = lines[0][lines[0].rfind(')') + 2:].split() if lines else []
            if len(fields) < 20:
                raise RuntimeError('Cannot verify retired input process')
            # A reused PID is not ours, and must never receive a signal.
            if start and fields[19] != start:
                return 'different'
            # RootAgent's older Execute transport doubles backslashes. Keep shell
            # RPC free of escapes and decode the NUL-separated bytes on the PC.
            commandline = base64.b64decode(''.join(lines[1:]), validate=True)
            if commandline != expected_cmdline:
                return 'different'
            start = fields[19]
            return 'owned'
        state = await identity()
        if state == 'owned':
            # Exact command line is the fallback if setup failed before the authenticated hello.
            if not re.fullmatch(r'[0-9]+', start or ''):
                raise RuntimeError('Invalid retired process start time')
            command = (f'if test "$(sed \'s/^.*) //\' /proc/{pid}/stat | cut -d \' \' -f 20)" = "{start}" '
                       f'&& test "$(sha256sum /proc/{pid}/cmdline | cut -d \' \' -f 1)" = "{hashlib.sha256(expected_cmdline).hexdigest()}"; '
                       f'then kill -TERM {pid}; fi')
            result = await asyncio.wait_for(connection.execute(command, 3), 4)
            if result.exit_code != 0:
                state = await identity()
            else:
                for _ in range(4):
                    await asyncio.sleep(0.15)
                    state = await identity()
                    if state != 'owned':
                        break
        if state == 'owned':
            return False
        if folder in self._policy_required:
            if not await self._cleanup_policy(connection, pid, start, folder):
                self._last_cleanup = False
                return False
        try:
            result = await asyncio.wait_for(connection.execute(f'tail -c 4096 {folder}/output.jsonl 2>/dev/null; :', 3), 4)
            # The helper never writes its authentication token to stdout.
            diag = self.root / 'diagnostics/remote-view/native-input-last-cleanup.json'
            diag.parent.mkdir(parents=True, exist_ok=True)
            cleanup_log = {'cleanupComplete': False, 'tail': result.stdout[-4096:]}
            diag.write_text(json.dumps(cleanup_log, ensure_ascii=False, indent=2), 'utf8')
            names = ['NativeInput.dll', 'NativeInput.deps.json', 'NativeInput.runtimeconfig.json', 'session.json', 'output.jsonl']
            if folder in self._policy_required:
                names += list(POLICY_FILES) + [name + '.tmp' for name in POLICY_FILES]
            result = await asyncio.wait_for(connection.execute(
                'rm -f ' + ' '.join(folder + '/' + name for name in names) +
                f'; if test -d {folder}; then rmdir {folder}; fi', 3), 4)
            self._check_exec(result, 'Remove verified retired input files')
            cleanup_log['cleanupComplete'] = True
            diag.write_text(json.dumps(cleanup_log, ensure_ascii=False, indent=2), 'utf8')
        except Exception:
            return False
        return True

    def _remember_policy_ready(self, folder, policy):
        if (not isinstance(policy, dict) or type(policy.get('pid')) is not int or policy['pid'] <= 1 or
                not re.fullmatch(r'[0-9]+', str(policy.get('startTime', ''))) or
                not isinstance(policy.get('instance'), str) or not re.fullmatch(r'[a-f0-9]{32}', policy['instance']) or
                policy.get('policyReady') is not True or
                policy.get('mode') not in ('owned-false-to-true', 'preexisting-true')):
            raise RuntimeError('Native input policy ready identity is invalid')
        self._policy_evidence.setdefault(folder, {})['ready'] = {
            key: policy[key] for key in ('pid', 'startTime', 'instance', 'mode')}

    async def _read_policy_snapshot(self, connection, folder):
        # Marked base64 keeps RootAgent's legacy backslash escaping out of JSON/argv.
        # Only fixed, credential-free companion files are read; never session.json.
        command = (f'if test -d {folder}; then echo directory-present; else echo directory-missing; fi; '
                   'for name in policy-owner.json policy-result.json; do echo "$name"; '
                   f'if test -f {folder}/"$name"; then base64 < {folder}/"$name"; fi; echo end-file; done; '
                   f'if test -f {folder}/policy-heartbeat.json; then echo heartbeat-present; else echo heartbeat-missing; fi')
        result = await asyncio.wait_for(connection.execute(command, 3), 4)
        self._check_exec(result, 'Read owned policy evidence')
        if len(result.stdout) > 65536:
            raise RuntimeError('Owned policy evidence exceeds bounded size')
        lines = result.stdout.splitlines()
        if not lines or lines[0] not in ('directory-present', 'directory-missing'):
            raise RuntimeError('Invalid policy evidence envelope')
        value = {'directoryPresent': lines[0] == 'directory-present'}
        index = 1
        for name in ('policy-owner.json', 'policy-result.json'):
            if index >= len(lines) or lines[index] != name:
                raise RuntimeError('Invalid policy evidence file boundary')
            index += 1
            encoded = []
            while index < len(lines) and lines[index] != 'end-file':
                encoded.append(lines[index]); index += 1
            if index >= len(lines):
                raise RuntimeError('Incomplete policy evidence file')
            index += 1
            data = base64.b64decode(''.join(encoded), validate=True) if encoded else b''
            if len(data) > 16384:
                raise RuntimeError('Owned policy JSON exceeds bounded size')
            value[name] = json.loads(data) if data else None
        if index != len(lines) - 1 or lines[index] not in ('heartbeat-present', 'heartbeat-missing'):
            raise RuntimeError('Invalid policy heartbeat evidence')
        value['heartbeatPresent'] = lines[index] == 'heartbeat-present'
        return value

    @staticmethod
    def _validate_policy_identity(value, folder, input_pid, input_start):
        expected = ['/usr/bin/dotnet', folder + '/NativeInput.dll', '--policy-session', folder + '/policy-session.json']
        root_pid = int(re.fullmatch(r'/run/samsung-tv-root/agent-([1-9][0-9]*)/native-input-[a-f0-9]{12}', folder)[1])
        if (not isinstance(value, dict) or type(value.get('pid')) is not int or value['pid'] <= 1 or
                not re.fullmatch(r'[0-9]+', str(value.get('startTime', ''))) or
                value.get('inputPid') != input_pid or str(value.get('inputStartTime')) != str(input_start) or
                value.get('rootPid') != root_pid or not re.fullmatch(r'[0-9]+', str(value.get('rootStartTime', ''))) or
                not isinstance(value.get('instance'), str) or not re.fullmatch(r'[a-f0-9]{32}', value['instance']) or
                value.get('argv') != expected):
            raise RuntimeError('Companion identity does not match the retired input session')
        return {key: value[key] for key in ('pid', 'startTime', 'inputPid', 'inputStartTime',
                'rootPid', 'rootStartTime', 'instance', 'argv')}

    @staticmethod
    def _policy_result_complete(result):
        if result.get('cleanupComplete') is not True:
            return False
        if (result.get('mode') == 'pending' and result.get('neverEnabled') is True and
                result.get('restoreRequired') is False and result.get('restored') is None and
                (result.get('finalValue') is None or type(result.get('finalValue')) is bool)):
            return True
        if result.get('mode') == 'owned-false-to-true':
            return (result.get('initialValue') is False and result.get('neverEnabled') is False and
                    result.get('restoreRequired') is True and
                    result.get('restored') is True and result.get('finalValue') is False)
        if result.get('mode') == 'preexisting-true':
            # An external owner may independently change True to False. We must not
            # write either value back on its behalf or label that as our restoration.
            return (result.get('initialValue') is True and result.get('neverEnabled') is True and
                    result.get('restoreRequired') is False and
                    result.get('restored') is None and type(result.get('finalValue')) is bool)
        return False

    async def _policy_process_state(self, connection, identity):
        pid = identity['pid']
        result = await asyncio.wait_for(connection.execute(
            f'if test -d /proc/{pid}; then cat /proc/{pid}/stat; base64 < /proc/{pid}/cmdline; else echo gone; fi', 3), 4)
        self._check_exec(result, 'Read companion process identity')
        if result.stdout.strip() == 'gone':
            return 'gone'
        lines = result.stdout.splitlines()
        fields = lines[0][lines[0].rfind(')') + 2:].split() if lines else []
        if len(fields) < 20:
            raise RuntimeError('Invalid companion process stat')
        if fields[19] != str(identity['startTime']):
            return 'different-start'
        if fields[0] in ('Z', 'X'):
            return 'exited'
        actual = base64.b64decode(''.join(lines[1:]), validate=True)
        expected = b'\0'.join(arg.encode('utf8') for arg in identity['argv']) + b'\0'
        return 'owned' if actual == expected else 'different-argv'

    def _save_policy_cleanup(self, folder, evidence):
        diagnostic = self.root / 'diagnostics/remote-view' / ('native-input-policy-cleanup-' + folder.rsplit('/', 1)[-1] + '.json')
        diagnostic.parent.mkdir(parents=True, exist_ok=True)
        # Only validated identities and whitelisted lease fields, never credentials.
        diagnostic.write_text(json.dumps({'folder': folder, **evidence}, ensure_ascii=False, indent=2), 'utf8')

    async def _cleanup_policy(self, connection, input_pid, input_start, folder):
        evidence = self._policy_evidence.setdefault(folder, {})
        async def wait_for_restoration():
            while True:
                snapshot = await self._read_policy_snapshot(connection, folder)
                owner = snapshot['policy-owner.json']
                result = snapshot['policy-result.json']
                if owner is not None:
                    identity = self._validate_policy_identity(owner, folder, input_pid, input_start)
                    previous = evidence.get('owner')
                    if previous and previous != identity:
                        raise RuntimeError('Companion owner identity changed')
                    ready = evidence.get('ready')
                    if ready and any(str(ready[key]) != str(identity[key]) for key in ('pid', 'startTime', 'instance')):
                        raise RuntimeError('Companion owner does not match authenticated ready')
                    evidence['owner'] = identity
                identity = evidence.get('owner')
                if result is not None:
                    result_identity = self._validate_policy_identity(result, folder, input_pid, input_start)
                    if identity is None or identity != result_identity:
                        raise RuntimeError('Policy result identity does not match its owner')
                    keys = ('mode', 'initialValue', 'finalValue', 'restoreRequired', 'restored',
                            'neverEnabled', 'cleanupComplete', 'exitCode', 'reason')
                    evidence['result'] = {**result_identity, **{key: result.get(key) for key in keys}}
                if identity:
                    process_state = await self._policy_process_state(connection, identity)
                    evidence['processState'] = process_state
                    if process_state in ('gone', 'different-start', 'exited'):
                        complete = bool(evidence.get('result') and self._policy_result_complete(evidence['result']))
                        evidence['policyCleanupComplete'] = complete
                        self._save_policy_cleanup(folder, evidence)
                        return complete
                elif (snapshot['directoryPresent'] and not snapshot['heartbeatPresent'] and result is None and not evidence.get('ready')):
                    # Companion writes owner before any enable and waits for a seed
                    # heartbeat. Prove there is no remaining process before treating
                    # an interrupted pre-owner launch as never enabled.
                    result = await asyncio.wait_for(connection.execute(
                        # Bracket literals prevent this search command from matching
                        # its own shell/grep argv, without transport-unsafe escapes.
                        f"grep -l -E '[/]{folder[1:]}/NativeInput[.]dll' /proc/[0-9]*/cmdline 2>/dev/null; :", 3), 4)
                    self._check_exec(result, 'Find only the retired input companion')
                    paths = result.stdout.splitlines()
                    if any(not re.fullmatch(r'/proc/[1-9][0-9]*/cmdline', item) for item in paths):
                        raise RuntimeError('Invalid owned process search result')
                    if not paths:
                        evidence.update(policyCleanupComplete=True, neverEnabled=True,
                                        processState='no-owned-process', heartbeatPresent=False,
                                        basis='Input exited; no owner, no seed heartbeat, no matching companion')
                        self._save_policy_cleanup(folder, evidence)
                        return True
                await asyncio.sleep(.15)
        try:
            return await asyncio.wait_for(wait_for_restoration(), POLICY_CLEANUP_SECONDS)
        except Exception as exc:
            evidence['policyCleanupComplete'] = False
            evidence['error'] = str(exc)[:300] or 'Companion cleanup confirmation timed out'
            self._save_policy_cleanup(folder, evidence)
            return False

    @staticmethod
    def _check_exec(result, operation):
        if result.exit_code != 0 or result.timed_out:
            raise RuntimeError(operation + ' failed: ' + (result.stderr or result.stdout)[-300:])
