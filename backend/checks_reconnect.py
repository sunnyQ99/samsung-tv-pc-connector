"""Offline lifecycle checks: extract classes without importing server startup or touching TV."""
import ast
import asyncio
import base64
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler
import json
from pathlib import Path
import ssl
import tempfile
import threading
import types
import unittest
import urllib.parse

SOURCE = Path(__file__).with_name('server.py')
TREE = ast.parse(SOURCE.read_text(encoding='utf-8-sig'))


def load_class(name, namespace):
    node = next(node for node in TREE.body if isinstance(node, ast.ClassDef) and node.name == name)
    exec(compile(ast.Module(body=[node], type_ignores=[]), str(SOURCE), 'exec'), namespace)
    return namespace[name]


class Clock:
    now = 1000.0

    def monotonic(self):
        return self.now


class FakeRemote:
    def __init__(self):
        self.ws = None
        self.ime = False
        self.connects = 0
        self.closes = 0

    def connect(self):
        self.connects += 1
        self.ws = object()

    def close(self):
        self.closes += 1
        self.ws = None


class LifecycleChecks(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='tvcitation-offline-')
        self.clock = Clock()
        self.available = False
        self.availability_checks = 0
        self.sessions = 0
        self.started = asyncio.Event()
        self.drop = asyncio.Event()
        self.stubborn = False
        self.ns = dict(asyncio=asyncio, time=self.clock, threading=threading, SamsungRemote=FakeRemote, json=json,
                       ROOT=Path(self.temp.name), DIAG=Path(self.temp.name), TV='mock-tv',
                       PRIVATE=Path(self.temp.name),
                       SDB=Path(self.temp.name) / 'sdb.exe',
                       SdbHealth=lambda *args: types.SimpleNamespace(status=lambda: {}, ensure_ready=lambda *args: None),
                       NativeVideoController=lambda *args: types.SimpleNamespace(status=lambda: {'videoConnected': False}, stop=lambda: asyncio.sleep(0)),
                       NativeInputController=lambda *args: types.SimpleNamespace(status=lambda: {'pointerAvailable': False}, stop=lambda: asyncio.sleep(0)),
                       CastAudioCoordinator=lambda *args: types.SimpleNamespace(status=lambda: {'castActive': False}),
                       CastMonitor=lambda *args: types.SimpleNamespace(status=lambda: {'streamConnected': False, 'applicationRunning': False, 'streamEvidence': 'notConfirmed'}),
                       route_callback_host=lambda _: 'mock-pc', cast_state=lambda: {'active': False})

        async def fake_probe(*args, session_callback, **kwargs):
            self.sessions += 1
            await session_callback(object())
            return {'offline': True}

        self.ns['probe'] = fake_probe
        controller_type = load_class('Controller', self.ns)
        self.controller = controller_type()
        self.controller._tv_available = self.check_available

        async def fake_session(connection):
            controller = self.controller
            controller.connection = connection
            controller.state = 'connected'
            controller.retry_failures = 0
            controller.next_retry = 0
            self.started.set()
            if self.stubborn:
                await asyncio.Event().wait()
            stopping = asyncio.create_task(controller.stop_event.wait())
            dropping = asyncio.create_task(self.drop.wait())
            try:
                completed, _ = await asyncio.wait([stopping, dropping], return_when=asyncio.FIRST_COMPLETED)
                if dropping in completed:
                    raise ConnectionError('offline simulated network drop')
            finally:
                for task in (stopping, dropping):
                    task.cancel()
                await asyncio.gather(stopping, dropping, return_exceptions=True)

        self.controller._session = fake_session

    def check_available(self):
        self.availability_checks += 1
        return self.available

    async def asyncTearDown(self):
        if self.controller.task and not self.controller.task.done():
            self.controller.task.cancel()
            await asyncio.gather(self.controller.task, return_exceptions=True)
        self.temp.cleanup()

    async def start_online(self):
        self.available = True
        self.controller.last_client = self.clock.now
        await self.controller.connect()
        await self.controller._maintain_connection()
        await asyncio.wait_for(self.started.wait(), 1)

    async def test_connect_is_immediate_and_off_to_on_creates_one_task(self):
        results = await asyncio.gather(*(self.controller.connect() for _ in range(30)))
        self.assertTrue(all(item['ok'] for item in results))
        self.assertEqual(self.availability_checks, 0)
        self.assertIsNone(self.controller.task)
        await self.controller._maintain_connection()
        self.assertEqual(self.availability_checks, 1)
        self.assertEqual(self.controller.state, 'disconnected')
        self.assertEqual(self.controller.next_retry - self.clock.now, 1)
        self.assertEqual(self.controller.retry_failures, 0)
        self.available = True
        await self.controller._maintain_connection()
        self.assertEqual(self.availability_checks, 1)
        self.clock.now = self.controller.next_retry
        await self.controller._maintain_connection()
        await asyncio.wait_for(self.started.wait(), 1)
        task = self.controller.task
        for _ in range(10):
            await self.controller.connect()
            await self.controller._maintain_connection()
        self.assertIs(self.controller.task, task)
        self.assertEqual(self.sessions, 1)

    async def test_disconnect_during_availability_does_not_start_late_session(self):
        entered, release = threading.Event(), threading.Event()

        def blocked_check():
            entered.set()
            release.wait(2)
            return True

        self.controller._tv_available = blocked_check
        await self.controller.connect()
        checking = asyncio.create_task(self.controller._maintain_connection())
        try:
            await asyncio.to_thread(entered.wait, 1)
            await self.controller.disconnect()
        finally:
            release.set()
        await checking
        self.assertIsNone(self.controller.task)
        self.assertFalse(self.controller.auto_connect)

    async def test_sdb_health_failure_uses_existing_backoff_before_root_probe(self):
        def unavailable(*args):
            raise RuntimeError('offline simulated SDB unavailable')
        self.controller.sdb_health.ensure_ready = unavailable
        self.available = True
        await self.controller.connect()
        await self.controller._maintain_connection()
        await asyncio.wait_for(self.controller.task, 1)
        self.assertEqual(self.sessions, 0)
        self.assertEqual(self.controller.state, 'error')
        self.assertIn('SDB unavailable', self.controller.message)
        self.assertEqual(self.controller.retry_failures, 1)
        self.assertEqual(self.controller.next_retry - self.clock.now, 2)
        self.assertIsNone(self.controller.remote.ws)

    async def test_cancelled_health_worker_receives_per_bootstrap_cancel(self):
        entered, finished = threading.Event(), threading.Event()
        observed = []
        def checking(cancelled):
            observed.append(cancelled)
            entered.set()
            cancelled.wait(2)
            finished.set()
        self.controller.sdb_health.ensure_ready = checking
        self.available = True
        await self.controller.connect()
        await self.controller._maintain_connection()
        self.assertTrue(await asyncio.to_thread(entered.wait, 1))
        self.controller.task.cancel()
        await asyncio.gather(self.controller.task, return_exceptions=True)
        self.assertTrue(await asyncio.to_thread(finished.wait, 1))
        self.assertTrue(observed[0].is_set())
        self.assertEqual(self.sessions, 0)

    async def test_network_drop_cleans_and_retries_after_backoff(self):
        await self.start_online()
        self.drop.set()
        await asyncio.wait_for(self.controller.task, 1)
        self.assertIsNone(self.controller.connection)
        self.assertIsNone(self.controller.remote.ws)
        self.assertFalse(self.controller.video_connected)
        self.assertEqual(self.controller.state, 'error')
        self.started.clear()
        self.drop = asyncio.Event()
        await self.controller._maintain_connection()
        self.assertEqual(self.sessions, 1)
        self.clock.now = self.controller.next_retry
        await self.controller._maintain_connection()
        await asyncio.wait_for(self.started.wait(), 1)
        self.assertEqual(self.sessions, 2)

    async def test_idle_ui_releases_session_and_fresh_heartbeat_resumes(self):
        await self.start_online()
        self.clock.now += self.controller.CLIENT_IDLE_TIMEOUT + 1
        await self.controller._maintain_connection()
        await asyncio.wait_for(self.controller.task, 1)
        await self.controller._maintain_connection()
        self.assertTrue(self.controller.auto_connect)
        self.assertEqual(self.sessions, 1)
        self.assertIsNone(self.controller.connection)
        self.started.clear()
        self.controller.last_client = self.clock.now
        await self.controller._maintain_connection()
        await asyncio.wait_for(self.started.wait(), 1)
        self.assertEqual(self.sessions, 2)

    async def test_explicit_disconnect_stays_disconnected_with_heartbeats(self):
        await self.start_online()
        await self.controller.disconnect()
        await asyncio.wait_for(self.controller.task, 1)
        self.clock.now += 100
        self.controller.last_client = self.clock.now
        await self.controller._maintain_connection()
        self.assertEqual(self.sessions, 1)
        self.assertFalse(self.controller.auto_connect)

    async def test_stuck_session_cancelled_before_replacement(self):
        self.stubborn = True
        await self.start_online()
        previous = self.controller.task
        await self.controller.disconnect()
        await self.controller.connect()
        await self.controller._maintain_connection()
        self.assertIs(self.controller.task, previous)
        self.clock.now += self.controller.STOP_GRACE + 0.1
        await self.controller._maintain_connection()
        await asyncio.gather(previous, return_exceptions=True)
        self.assertTrue(previous.cancelled())
        self.assertIsNone(self.controller.connection)
        self.assertIsNone(self.controller.remote.ws)

    async def test_retry_cap_and_authorized_heartbeat(self):
        delays = []
        for _ in range(6):
            self.controller._schedule_retry()
            delays.append(self.controller.next_retry - self.clock.now)
        self.assertEqual(delays, [2, 4, 8, 15, 15, 15])
        self.ns.update(BaseHTTPRequestHandler=BaseHTTPRequestHandler, PORT=18765, hmac=hmac,
                       API_TOKEN='offline-test-token', controller=self.controller)
        handler_type = load_class('Handler', self.ns)
        handler = handler_type.__new__(handler_type)
        handler.headers = {'Host': 'localhost:18765', 'X-TVCitation-Token': 'offline-test-token'}
        replies = []
        handler.reply = lambda *args: replies.append(args)
        self.clock.now += 25
        self.assertTrue(handler.authorized())
        self.assertEqual(self.controller.last_client, self.clock.now)
        self.clock.now += 25
        handler.headers['X-TVCitation-Token'] = 'invalid'
        self.assertFalse(handler.authorized())
        self.assertNotEqual(self.controller.last_client, self.clock.now)
        self.assertEqual(replies[-1][0], 401)


class RemoteGenerationCheck(unittest.TestCase):
    def test_closed_connect_cannot_publish_stale_socket(self):
        entered, release = threading.Event(), threading.Event()
        certificate = b'offline-certificate'

        class FakeSocket:
            sock = types.SimpleNamespace(getpeercert=lambda **_: certificate)
            closed = False

            def recv(self):
                entered.set()
                release.wait(2)
                return '{"event":"ms.channel.connect"}'

            def settimeout(self, _):
                pass

            def close(self):
                self.closed = True

        socket = FakeSocket()
        with tempfile.TemporaryDirectory(prefix='tvcitation-remote-check-') as temporary:
            root = Path(temporary)
            (root / 'diagnostics').mkdir()
            (root / 'diagnostics/tv-remote-result.json').write_text(json.dumps({'checks': {'tls': {
                'certificate_sha256': hashlib.sha256(certificate).hexdigest()}}}))
            namespace = dict(threading=threading, urllib=urllib, base64=base64, ssl=ssl, json=json,
                             hashlib=hashlib, ROOT=root, TV='offline', remote_token=lambda: 'fake',
                             websocket=types.SimpleNamespace(create_connection=lambda *a, **k: socket))
            remote = load_class('SamsungRemote', namespace)()
            connecting = threading.Thread(target=remote.connect)
            connecting.start()
            self.assertTrue(entered.wait(1))
            closing = threading.Thread(target=remote.close)
            closing.start()
            release.set()
            connecting.join(2)
            closing.join(2)
            self.assertFalse(connecting.is_alive())
            self.assertFalse(closing.is_alive())
            self.assertIsNone(remote.ws)
            self.assertTrue(socket.closed)


if __name__ == '__main__':
    unittest.main(verbosity=2)
