"""Local TV Control bridge. Only fixed TV operations are exposed over loopback."""
import asyncio
import base64
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import secrets
import socket
import ssl
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT / 'research/samsung-tv-root/src'), str(ROOT / 'tools/python')]
import websocket
from samsung_tv_root.archive_root import probe
from samsung_tv_root.sdb import SdbClient, route_callback_host
from cast_audio import CastAudioCoordinator
from cast_monitor import CastMonitor
from native_video import NativeVideoController
from native_input import NativeInputController
from sdb_health import SdbHealth

TV = '192.168.1.5'
PORT = 18765
# Moonlight deep link target: Sunshine serverinfo <uniqueid> and the numeric id of its "Desktop" app.
MOONLIGHT_SERVER_UID = 'A16CA7DF-0078-5B28-B40F-1DB3A7171AB7'
MOONLIGHT_DESKTOP_APP_ID = 881448767
SDB = ROOT / 'tools/sdb/data/tools/sdb.exe'
os.environ['SDB'] = str(SDB)
os.environ['PATH'] = r'C:\Program Files\Git\usr\bin' + os.pathsep + os.environ['PATH']
PRIVATE = Path(os.environ['LOCALAPPDATA']) / 'TVCITATION/Control'
PRIVATE.mkdir(parents=True, exist_ok=True)
sid = subprocess.check_output(['whoami', '/user', '/fo', 'csv', '/nh'], text=True, creationflags=0x08000000).strip().split(',')[-1].strip('"')
subprocess.run(['icacls', str(PRIVATE), '/inheritance:r', '/grant:r', f'*{sid}:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F'], capture_output=True, check=True, creationflags=0x08000000)
TOKEN_FILE = PRIVATE / 'api-token'
if not TOKEN_FILE.exists():
    TOKEN_FILE.write_text(secrets.token_urlsafe(32), encoding='ascii')
API_TOKEN = TOKEN_FILE.read_text(encoding='ascii').strip()
DIAG = ROOT / 'diagnostics/remote-view'
DIAG.mkdir(parents=True, exist_ok=True)

KEYS = {'KEY_HOME', 'KEY_MENU', 'KEY_RETURN', 'KEY_EXIT', 'KEY_UP', 'KEY_DOWN',
        'KEY_LEFT', 'KEY_RIGHT', 'KEY_ENTER', 'KEY_VOLUP', 'KEY_VOLDOWN',
        'KEY_MUTE', 'KEY_PLAY', 'KEY_PAUSE', 'KEY_PLAYPAUSE', 'KEY_STOP',
        'KEY_REWIND', 'KEY_FF', 'KEY_CHUP', 'KEY_CHDOWN', 'KEY_INFO',
        'KEY_GUIDE', 'KEY_SOURCE', 'KEY_TOOLS', 'KEY_CONTENTS', 'KEY_CLEAR'}
KEYS.update('KEY_' + str(n) for n in range(10))

class Blob(ctypes.Structure):
    _fields_ = [('size', wintypes.DWORD), ('data', ctypes.POINTER(ctypes.c_byte))]

def remote_token():
    encrypted = (Path(os.environ['LOCALAPPDATA']) / 'TVCITATION/samsung-token.dpapi').read_bytes()
    buf = ctypes.create_string_buffer(encrypted)
    source = Blob(len(encrypted), ctypes.cast(buf, ctypes.POINTER(ctypes.c_byte)))
    result = Blob()
    if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(source), None, None, None, None, 0, ctypes.byref(result)):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(result.data, result.size).decode('utf8')
    finally:
        ctypes.windll.kernel32.LocalFree(result.data)

class SamsungRemote:
    def __init__(self):
        self.ws = None
        self.lock = threading.Lock()
        self.ime = False
        self.generation = 0

    def connect(self):
        with self.lock:
            if self.ws is not None:
                return
            generation = self.generation
            query = urllib.parse.urlencode({'name': base64.b64encode(b'TV Control').decode(), 'token': remote_token()})
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
            ws = websocket.create_connection(f'wss://{TV}:8002/api/v2/channels/samsung.remote.control?{query}', timeout=12,
                                             sslopt={'context': context}, http_no_proxy=[TV])
            expected = json.loads((ROOT / 'diagnostics/tv-remote-result.json').read_text())['checks']['tls']['certificate_sha256']
            if hashlib.sha256(ws.sock.getpeercert(binary_form=True)).hexdigest() != expected:
                ws.close()
                raise RuntimeError('TV certificate does not match the paired device')
            if json.loads(ws.recv()).get('event') != 'ms.channel.connect':
                ws.close()
                raise RuntimeError('The TV rejected the saved remote authorization')
            ws.settimeout(1)
            if generation != self.generation:
                ws.close()
                return
            self.ws = ws
            threading.Thread(target=self._receive, args=(ws,), daemon=True).start()

    def _receive(self, ws):
        while self.ws is ws:
            try:
                opcode, data = ws.recv_data(control_frame=True)
                if opcode not in (1, 2):
                    continue
                event = json.loads(data).get('event')
                if event in ('ms.remote.imeStart', 'ms.remote.imeUpdate'):
                    self.ime = True
                elif event == 'ms.remote.imeEnd':
                    self.ime = False
            except websocket.WebSocketTimeoutException:
                continue
            except Exception:
                with self.lock:
                    if self.ws is ws:
                        self.ws = None
                        self.ime = False
                try:
                    ws.close()
                except Exception:
                    pass
                break

    def send(self, params):
        self.connect()
        with self.lock:
            self.ws.send(json.dumps({'method': 'ms.remote.control', 'params': params}))

    def key(self, key):
        if key not in KEYS:
            raise ValueError('Unsupported remote key')
        self.send({'Cmd': 'Click', 'DataOfCmd': key, 'Option': 'false', 'TypeOfRemote': 'SendRemoteKey'})

    def text(self, value):
        if not isinstance(value, str) or not 1 <= len(value) <= 2000:
            raise ValueError('Text must be 1–2000 characters')
        if not self.ime:
            raise ValueError('Select a text input field on the TV first')
        self.send({'Cmd': base64.b64encode(value.encode()).decode(), 'DataOfCmd': 'base64', 'TypeOfRemote': 'SendInputString'})
        self.send({'TypeOfRemote': 'SendInputEnd'})

    def close(self):
        # Invalidates a connect() running in a worker thread before its socket is published.
        self.generation += 1
        with self.lock:
            ws, self.ws = self.ws, None
            self.ime = False
        if ws:
            ws.close()

def powershell(script, *args):
    result = subprocess.run(['pwsh', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(ROOT / script), *args],
                            capture_output=True, text=True, encoding='utf8', timeout=120, creationflags=0x08000000)
    if result.returncode:
        raise RuntimeError((result.stderr or result.stdout).strip()[-1500:])
    return result.stdout.strip()

def cast_state():
    try:
        with urllib.request.urlopen('http://127.0.0.1:47989/serverinfo', timeout=1) as response:
            xml = ET.fromstring(response.read())
        return {'active': xml.findtext('currentgame') != '0', 'appId': int(xml.findtext('currentgame') or '0'),
                'serverUid': xml.findtext('uniqueid')}
    except Exception:
        return {'active': False, 'appId': 0}

class Controller:
    RETRY_BASE = 2.0
    RETRY_MAX = 15.0
    CLIENT_IDLE_TIMEOUT = 90.0
    # Native input independently restores its cursor policy before its owned
    # files can be removed. Allow its bounded 11-second close and root cleanup.
    STOP_GRACE = 15.0
    AUDIO_LOSS_GRACE = 5.0

    def __init__(self):
        self.state = 'disconnected'
        self.message = 'TV not connected; waiting for the TV to power on…'
        self.frame = b''
        self.etag = ''
        self.frame_time = 0
        self.fps = 0
        self.width, self.height = 1920, 1080
        self.task = None
        self.stop_event = asyncio.Event()
        self.connection = None
        self.remote = SamsungRemote()
        self.last_client = time.monotonic()
        self.tv_only_audio = False
        self.cast_lock = asyncio.Lock()
        self.audio = CastAudioCoordinator(ROOT, PRIVATE)
        self.cast_monitor = CastMonitor(ROOT, PRIVATE)
        self.cast_task = None
        self.cast_operation = {'busy': False, 'state': 'idle', 'message': ''}
        self.auto_connect = False
        self.video_connected = False
        self.native_video = NativeVideoController(ROOT)
        self.native_input = NativeInputController(ROOT)
        self.sdb_health = SdbHealth(SDB, PRIVATE, self._sdb_recovery_blocker)
        self.wake_event = asyncio.Event()
        self.retry_failures = 0
        self.next_retry = 0.0
        self.stop_requested_at = None
        self.idle_suspended = False
        self.audio_confirmed_session = None
        self.audio_loss_since = None
        self.audio_recovery_attempted = None
        self.audio_auto_recovery = {'state': 'idle'}

    def status(self):
        audio = self.audio.status()
        cast = self.cast_monitor.status()
        video = self.native_video.status()
        native_input = self.native_input.status()
        return {'connected': self.state == 'connected', 'controlConnected': self.state == 'connected',
                'autoConnect': self.auto_connect,
                'extendedControlReady': self.connection is not None,
                'retryAfterSeconds': round(max(0, self.next_retry - time.monotonic()), 1),
                'state': self.state, 'message': self.message,
                **video, **native_input, **self.sdb_health.status(), 'textAvailable': self.remote.ime,
                **audio, 'castActive': cast['streamConnected'], 'castApplicationRunning': cast['applicationRunning'],
                'castEvidence': cast['streamEvidence'], 'castOperation': dict(self.cast_operation),
                'audioAutoRecovery': dict(self.audio_auto_recovery)}

    async def connect(self):
        # Only the watcher starts sessions, so concurrent POSTs cannot duplicate a probe.
        if not self.auto_connect:
            self.retry_failures = 0
            self.next_retry = 0.0
        self.auto_connect = True
        self.wake_event.set()
        return {'ok': True, 'state': self.state, 'autoConnect': True}

    @staticmethod
    def _tv_available():
        try:
            with urllib.request.urlopen(f'http://{TV}:8001/api/v2/', timeout=0.8) as response:
                device = json.load(response).get('device', {})
            if device.get('PowerState', '').lower() != 'on':
                return False
            with socket.create_connection((TV, 26101), timeout=0.5):
                return True
        except Exception:
            return False

    async def watch(self):
        while True:
            self.wake_event.clear()
            # Audio recovery is independent of the control-window heartbeat. Closing the GUI
            # preserves a playing cast, while a later confirmed stream loss can still recover.
            try:
                await self._maintain_cast_audio()
            except Exception as error:
                self.audio_auto_recovery = {'state': 'checkFailed', 'message': str(error)[:300]}
            try:
                await self._maintain_connection()
            except Exception as error:
                self.state, self.message = 'error', 'TV connection unavailable, retrying: ' + str(error)[:300]
                self._schedule_retry()
            try:
                await asyncio.wait_for(self.wake_event.wait(), 1)
            except TimeoutError:
                pass

    @staticmethod
    def _prepared_tv_audio(session):
        return bool(session and session.get('phase') == 'prepared' and
                    session.get('tvOnlyAudio') is True and session.get('defaultsSaved') and
                    session.get('configSaved'))

    @staticmethod
    def _confirmed_cast_loss(cast):
        return bool(not cast.get('streamConnected') and
                    (not cast.get('hostOwned') or cast.get('lastStreamEvent') == 'CLIENT DISCONNECTED' or
                     (cast.get('serverStateKnown') and not cast.get('applicationRunning'))))

    async def _maintain_cast_audio(self):
        if ((self.cast_task and not self.cast_task.done()) or self.cast_lock.locked() or self.audio._busy):
            self.audio_loss_since = None
            return
        session = await asyncio.to_thread(self.audio._read_session)
        if not self._prepared_tv_audio(session):
            self.audio_confirmed_session = None
            self.audio_loss_since = None
            return
        session_id = session['id']
        cast = await asyncio.to_thread(self.cast_monitor.status)
        # Revalidate after the read awaits: a start/stop may have claimed cast_task meanwhile.
        if ((self.cast_task and not self.cast_task.done()) or self.cast_lock.locked() or self.audio._busy):
            self.audio_loss_since = None
            return
        if cast.get('streamConnected') and cast.get('hostOwned'):
            self.audio_confirmed_session = session_id
            self.audio_loss_since = None
            self.audio_auto_recovery = {'state': 'watching'}
            return
        if session_id != self.audio_confirmed_session or session_id == self.audio_recovery_attempted:
            self.audio_loss_since = None
            return
        if not self._confirmed_cast_loss(cast):
            # An unreachable HTTP probe alone does not establish that a client disconnected.
            self.audio_loss_since = None
            return
        now = time.monotonic()
        if self.audio_loss_since is None:
            self.audio_loss_since = now
        remaining = max(0.0, self.AUDIO_LOSS_GRACE - (now - self.audio_loss_since))
        self.audio_auto_recovery = {'state': 'debouncing', 'retryAfterSeconds': round(remaining, 1)}
        if remaining > 0:
            return
        self.cast_operation = {'busy': True, 'state': 'recovering', 'message': 'TV stream lost, restoring PC audio…'}
        self.cast_task = asyncio.create_task(self._recover_lost_cast_audio(session_id))

    async def _recover_lost_cast_audio(self, session_id):
        async with self.cast_lock:
            try:
                session = await asyncio.to_thread(self.audio._read_session)
                cast = await asyncio.to_thread(self.cast_monitor.status)
                if (not self._prepared_tv_audio(session) or session['id'] != session_id or
                        self.audio._busy or not self._confirmed_cast_loss(cast)):
                    self.audio_loss_since = None
                    self.audio_auto_recovery = {'state': 'cancelled'}
                    self.cast_operation = {'busy': False, 'state': 'completed', 'ok': True,
                                           'message': 'Stream state changed; automatic audio restore cancelled'}
                    return
                host = await asyncio.to_thread(self.audio._host_state)
                if host.get('otherCount') or (host.get('owned') and not host.get('known')):
                    raise RuntimeError('Could not safely confirm the cast host owned by this session; audio settings were not changed')
                # The owned process may have crashed since the last confirmed cast. No other
                # Sunshine instance is allowed, and the persisted session must still match.
                session = await asyncio.to_thread(self.audio._read_session)
                cast = await asyncio.to_thread(self.cast_monitor.status)
                if (not self._prepared_tv_audio(session) or session['id'] != session_id or
                        cast.get('streamConnected') or not self._confirmed_cast_loss(cast)):
                    self.audio_loss_since = None
                    self.audio_auto_recovery = {'state': 'cancelled'}
                    self.cast_operation = {'busy': False, 'state': 'completed', 'ok': True,
                                           'message': 'Stream recovered; keeping the current audio settings'}
                    return
                self.audio_recovery_attempted = session_id
                self.audio_auto_recovery = {'state': 'recovering'}
                outcome = await asyncio.to_thread(self.audio.stop)
            except Exception as error:
                self.audio_recovery_attempted = session_id
                outcome = {'ok': False, 'needsRecovery': True, 'message': str(error)[:600]}
            self.audio_loss_since = None
            self.audio_auto_recovery = {'state': 'completed' if outcome.get('ok') else 'error'}
            self.cast_operation = {**outcome, 'busy': False,
                                   'state': 'completed' if outcome.get('ok') else 'error'}

    def _schedule_retry(self):
        delay = min(self.RETRY_MAX, self.RETRY_BASE * 2 ** min(self.retry_failures, 8))
        self.retry_failures += 1
        self.next_retry = time.monotonic() + delay

    def _request_session_stop(self):
        self.stop_event.set()
        if self.stop_requested_at is None:
            self.stop_requested_at = time.monotonic()
        if (self.task and not self.task.done() and
                time.monotonic() - self.stop_requested_at >= self.STOP_GRACE and not self.task.cancelling()):
            self.task.cancel()

    async def _maintain_connection(self):
        active = self.task is not None and not self.task.done()
        idle = time.monotonic() - self.last_client > self.CLIENT_IDLE_TIMEOUT
        if not self.auto_connect or idle:
            self.idle_suspended = idle and self.auto_connect
            if active:
                self._request_session_stop()
            else:
                self.state = 'disconnected'
                self.message = 'Waiting for the PC control window to connect…' if self.idle_suspended else 'TV control disconnected'
            return
        if self.idle_suspended:
            self.idle_suspended = False
            self.next_retry = 0.0
            self.retry_failures = 0
        if active:
            if self.stop_event.is_set():
                self._request_session_stop()
            elif self.state == 'connected' and self.remote.ws is None:
                self.state, self.message = 'error', 'TV remote connection lost, reconnecting…'
                self._request_session_stop()
            return
        self.stop_requested_at = None
        if time.monotonic() < self.next_retry:
            return
        available = await asyncio.to_thread(self._tv_available)
        # A disconnect or lost UI heartbeat may arrive while the availability check awaits.
        if not self.auto_connect or time.monotonic() - self.last_client > self.CLIENT_IDLE_TIMEOUT:
            return
        if not available:
            self.state, self.message = 'disconnected', 'TV not connected; waiting for the TV to power on and its network services to start…'
            self.next_retry = time.monotonic() + 1.0
            return
        self.stop_event = asyncio.Event()
        self.state, self.message = 'connecting', 'Establishing the TV control connection…'
        self.task = asyncio.create_task(self._run())

    def _sdb_recovery_blocker(self):
        if (self.stop_event.is_set() or not self.auto_connect or
                time.monotonic() - self.last_client > self.CLIENT_IDLE_TIMEOUT):
            return 'Connection cancelled or the control window was closed.'
        if self.connection is not None:
            return 'A TV control session is still running.'
        video, pointer = self.native_video, self.native_input
        if (video.receiver is not None or video.sender_pid or video.connection is not None or video.lock.locked()):
            return 'A TV video session or its cleanup is still in progress.'
        if (pointer._connection is not None or pointer._writer is not None or pointer._pid or
                pointer._retired or pointer._ensure_lock.locked()):
            return 'A TV input session or its cleanup is still in progress.'
        return None

    async def _run(self):
        try:
            await asyncio.to_thread(self.remote.connect)
            if self.stop_event.is_set():
                return
            self.state, self.message = 'connected', 'TV remote connected; preparing the casting control channel'
            health_cancel = threading.Event()
            try:
                await asyncio.to_thread(self.sdb_health.ensure_ready, health_cancel)
            finally:
                # Cancelling to_thread does not stop its worker. Bind cancellation to this
                # bootstrap rather than a stop_event that the next session can replace.
                health_cancel.set()
            if self.stop_event.is_set():
                return
            result = await probe(TV, model='QA50QN90CAGXXP', callback_host=route_callback_host(TV),
                                 payloads=ROOT / 'diagnostics/root-analysis/self-built-net6/ready-net6',
                                 session_callback=self._session, accept_timeout=35)
            (DIAG / 'last-session.json').write_text(json.dumps(result, indent=2), encoding='utf8')
        except Exception as error:
            self.state, self.message = 'error', str(error)[:1200]
            print(json.dumps({'event': 'session-error', 'error': self.message}), flush=True)
        finally:
            await asyncio.gather(self.native_video.stop(), self.native_input.stop(), return_exceptions=True)
            self.connection = None
            self.fps = 0
            self.video_connected = False
            try:
                await asyncio.to_thread(self.remote.close)
            except Exception as error:
                print(json.dumps({'event': 'connection-cleanup-error', 'error': str(error)[:300]}), flush=True)
            finally:
                self.stop_requested_at = None
                if self.auto_connect and time.monotonic() - self.last_client <= self.CLIENT_IDLE_TIMEOUT:
                    self._schedule_retry()
                    if self.state != 'error':
                        self.state, self.message = 'disconnected', 'TV connection lost, reconnecting…'
                else:
                    self.state, self.message = 'disconnected', 'TV control disconnected'
                self.wake_event.set()

    async def _session(self, connection):
        self.connection = connection
        folder = '/run/samsung-tv-root/agent-' + str(connection.identity.pid) + '/tv-control'
        await connection.execute(f'mkdir {folder}', 5)
        try:
            # Save public device tools once for model-specific offline inspection.
            for remote_path in ('/usr/bin/enlightenment_info', '/usr/lib/libdisplay-capture-api.so.0'):
                local = DIAG / Path(remote_path).name
                if not local.exists():
                    copied = await connection.execute(f'cp -L {remote_path} {folder}/inspection.bin', 5)
                    if copied.exit_code == 0:
                        file = await connection.read_file(folder + '/inspection.bin', 10)
                        local.write_bytes(file.data)
                        local.with_suffix(local.suffix + '.sha256').write_text(file.sha256, encoding='ascii')
                        await connection.execute(f'rm {folder}/inspection.bin', 5)
            info = await connection.execute('/usr/bin/enlightenment_info -input_devices; /usr/bin/enlightenment_info -show_plane_state; /usr/bin/enlightenment_info -hwc_wins; :', 10)
            (DIAG / 'device-capture-input.txt').write_text(info.stdout + info.stderr, encoding='utf8')
            self.state, self.message = 'connected', 'TV connected; preparing live picture and mouse'
            self.retry_failures = 0
            self.next_retry = 0.0
            # Native channels have independent retry/error state. Neither may tear down
            # working remote keys, video, or the separate PC-to-TV cast/audio session.
            native_tasks = set()
            async def maintain_native():
                await asyncio.gather(self.native_video.ensure_connected(connection),
                                     self.native_input.ensure_connected(connection), return_exceptions=True)
            while not self.stop_event.is_set():
                if time.monotonic() - self.last_client > self.CLIENT_IDLE_TIMEOUT:
                    self.stop_event.set()
                    break
                await connection.ping(5)
                native_tasks = {task for task in native_tasks if not task.done()}
                if not native_tasks:
                    native_tasks.add(asyncio.create_task(maintain_native()))
                try:
                    await asyncio.wait_for(self.stop_event.wait(), 3)
                except TimeoutError:
                    pass
        finally:
            for task in locals().get('native_tasks', ()):
                task.cancel()
            await asyncio.gather(*locals().get('native_tasks', ()), return_exceptions=True)
            await asyncio.gather(self.native_video.stop(), self.native_input.stop(), return_exceptions=True)
            try:
                await connection.execute(f'rm -f {folder}/screen.png {folder}/inspection.bin; rmdir {folder}', 5)
            except Exception:
                pass

    async def disconnect(self, wait=False):
        self.auto_connect = False
        self._request_session_stop()
        self.state, self.message = 'disconnected', 'TV control disconnected'
        self.wake_event.set()
        if wait and self.task and not self.task.done():
            try:
                await asyncio.wait_for(asyncio.shield(self.task), self.STOP_GRACE)
            except TimeoutError:
                self.task.cancel()
                try:
                    await asyncio.wait_for(asyncio.shield(self.task), 3)
                except (TimeoutError, asyncio.CancelledError):
                    pass
        return {'ok': True, 'state': self.state}

    async def action(self, path, data):
        if path == '/connect':
            return await self.connect()
        if path == '/disconnect':
            return await self.disconnect()
        if path == '/remote':
            await asyncio.to_thread(self.remote.key, data.get('key'))
        elif path == '/text':
            await asyncio.to_thread(self.remote.text, data.get('text'))
        elif path == '/pointer':
            return await self.native_input.pointer(data.get('action'), data.get('x'), data.get('y'),
                                                   data.get('button', 'left'), data.get('delta', 0))
        elif path in ('/cast/start', '/cast/stop'):
            if self.cast_task and not self.cast_task.done():
                raise ValueError('A casting operation is in progress; wait for it to finish')
            start = path == '/cast/start'
            if start and not self.connection:
                raise ValueError('Wait for the TV control connection first')
            tv_only = data.get('tvOnlyAudio', False)
            if not isinstance(tv_only, bool):
                raise ValueError('Invalid audio mode')
            self.cast_operation = {'busy': True, 'state': 'starting' if start else 'stopping',
                                   'message': 'Preparing PC casting…' if start else 'Stopping casting and restoring audio…'}
            self.cast_task = asyncio.create_task(self._cast_operation(start, tv_only))
            return {'ok': True, 'pending': True}
        else:
            raise ValueError('Unknown operation')
        return {'ok': True}

    async def _prepare_tv_cast_app(self):
        # The TV app can retain stale host state after the previous stream stops.
        # Restart only its idle UI through the verified application manager, never
        # its paired service or storage. Refuse ambiguous/active host state.
        cast = await asyncio.to_thread(self.cast_monitor.status)
        if (not cast.get('hostOwned') or not cast.get('serverStateKnown')
                or cast.get('applicationRunning') or cast.get('streamConnected')):
            raise RuntimeError('Could not confirm casting is idle; the TV app was not restarted')
        connection = self.connection
        if connection is None:
            raise RuntimeError('TV control connection lost')
        check = '/usr/bin/app_launcher -u 5001 -r MoonLightS.MoonlightWasm'

        async def running():
            result = await connection.execute(check, 5)
            if result.timed_out:
                raise RuntimeError('Could not read the TV casting app state')
            response = result.stdout.strip()
            # This TV returns 1 for the normal "not running" result (verified
            # against its installed app_launcher), and 0 for "running".
            if result.exit_code == 1 and response == 'result: not running':
                return False
            if result.exit_code == 0 and response == 'result: running':
                return True
            raise RuntimeError('The TV returned an unrecognized casting app state')

        if not await running():
            return
        result = await connection.execute(
            '/usr/bin/app_launcher -u 5001 -t MoonLightS.MoonlightWasm', 5)
        if result.exit_code or result.timed_out:
            raise RuntimeError('The TV casting app did not exit cleanly; try again later')
        for _ in range(20):
            await asyncio.sleep(0.25)
            if not await running():
                return
        raise RuntimeError('The TV casting app is still exiting; try again later')

    async def _cast_operation(self, start, tv_only):
        async with self.cast_lock:
            prepared = None
            try:
                if not start:
                    outcome = await asyncio.to_thread(self.audio.stop)
                else:
                    prepared = await asyncio.to_thread(self.audio.prepare, tv_only)
                    if not prepared['ok']:
                        outcome = prepared
                    elif prepared.get('alreadyActive'):
                        active = (await asyncio.to_thread(self.cast_monitor.status))['streamConnected']
                        outcome = {'ok': active, 'message': 'PC casting is already running' if active else 'The casting app is running, but the TV stream is not confirmed yet'}
                    else:
                        if not self.connection:
                            raise RuntimeError('TV connection lost; restoring PC audio settings')
                        self.cast_operation['message'] = 'Starting the casting app on the TV…'
                        await self._prepare_tv_cast_app()
                        marker = await asyncio.to_thread(self.cast_monitor.begin_attempt)
                        if not marker['ok']:
                            raise RuntimeError(marker['message'])
                        result = await self.connection.execute(f'/usr/bin/dotnet /home/owner/share/tmp/sdk_tools/tvcitation-cast/MoonlightDeepLink.dll {MOONLIGHT_SERVER_UID} {MOONLIGHT_DESKTOP_APP_ID}', 15)
                        if result.exit_code or result.timed_out:
                            raise RuntimeError('The TV failed to start casting')
                        for _ in range(100):
                            if (await asyncio.to_thread(self.cast_monitor.check_attempt, marker))['newConnection']:
                                break
                            await asyncio.sleep(0.5)
                        else:
                            raise RuntimeError('The TV received the start request but has not started the desktop stream yet')
                        outcome = {'ok': True, 'message': 'PC casting connected'}
            except Exception as error:
                recovery = {}
                if prepared and prepared.get('ok') and not prepared.get('alreadyActive'):
                    recovery = await asyncio.to_thread(self.audio.stop)
                outcome = {'ok': False, 'message': str(error)[:1200],
                           'needsRecovery': recovery.get('needsRecovery', False)}
            self.cast_operation = {**outcome, 'busy': False,
                                   'state': 'completed' if outcome.get('ok') else 'error'}

loop = asyncio.new_event_loop()
controller = Controller()

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def reply(self, code, data, content_type='application/json', etag=None):
        payload = data if isinstance(data, bytes) else json.dumps(data, ensure_ascii=False).encode('utf8')
        self.send_response(code)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(payload)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Connection', 'close')
        if etag:
            self.send_header('ETag', '"' + etag + '"')
        self.end_headers()
        try:
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def authorized(self):
        host = self.headers.get('Host', '')
        origin = self.headers.get('Origin')
        if host not in (f'127.0.0.1:{PORT}', f'localhost:{PORT}') or origin not in (None, f'http://127.0.0.1:{PORT}', f'http://localhost:{PORT}'):
            self.reply(403, {'error': 'Local desktop client required'})
            return False
        if not hmac.compare_digest(self.headers.get('X-TVCitation-Token', ''), API_TOKEN):
            self.reply(401, {'error': 'Authentication required'})
            return False
        controller.last_client = time.monotonic()
        return True

    def do_GET(self):
        if not self.authorized():
            return
        path = urllib.parse.urlsplit(self.path).path
        if path == '/status':
            self.reply(200, controller.status())
        elif path == '/player':
            self.reply(200, (ROOT / 'backend/player.html').read_bytes(), 'text/html; charset=utf-8')
        elif path == '/player.js':
            self.reply(200, (ROOT / 'backend/player.js').read_bytes(), 'text/javascript; charset=utf-8')
        elif path == '/frame':
            self.reply(410, {'error': 'Screenshot preview is disabled by user request'})
        elif path in ('/video', '/video-frames'):
            direct_frames = path == '/video-frames'
            try:
                if direct_frames:
                    subscription, codec = controller.native_video.subscribe_frames()
                else:
                    subscription, mime = controller.native_video.subscribe()
            except RuntimeError as error:
                self.reply(503, {'error': str(error)})
                return
            try:
                self.connection.settimeout(5)
                self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                self.send_response(200)
                if direct_frames:
                    self.send_header('Content-Type', 'application/x-tvc-au')
                    self.send_header('X-Video-Codec', codec)
                    self.send_header('X-Video-Width', '1920')
                    self.send_header('X-Video-Height', '1080')
                else:
                    self.send_header('Content-Type', 'video/mp4')
                    self.send_header('X-Video-Mime', mime)
                self.send_header('Cache-Control', 'no-store')
                self.send_header('X-Content-Type-Options', 'nosniff')
                self.send_header('Connection', 'close')
                self.end_headers()
                self.close_connection = True
                while chunk := subscription.next_chunk():
                    self.wfile.write(chunk)
                    self.wfile.flush()
            except (OSError, ConnectionError, TimeoutError):
                pass
            finally:
                subscription.close()
        else:
            self.reply(404, {'error': 'Not found'})

    def do_POST(self):
        if not self.authorized():
            return
        try:
            size = int(self.headers.get('Content-Length', '0'))
            if not 0 <= size <= 16000 or not self.headers.get('Content-Type', '').startswith('application/json'):
                raise ValueError('Invalid request body')
            data = json.loads(self.rfile.read(size) or '{}')
            if not isinstance(data, dict):
                raise ValueError('Expected JSON object')
            result = asyncio.run_coroutine_threadsafe(controller.action(self.path, data), loop).result(125)
            self.reply(202 if result.get('pending') else 200, result)
        except ValueError as error:
            self.reply(400, {'error': str(error)})
        except Exception as error:
            self.reply(500, {'error': str(error)[:1200]})

def main():
    threading.Thread(target=loop.run_forever, daemon=True).start()
    asyncio.run_coroutine_threadsafe(controller.watch(), loop)
    server = ThreadingHTTPServer(('127.0.0.1', PORT), Handler)
    print(json.dumps({'ready': True, 'port': PORT, 'pid': os.getpid()}), flush=True)
    try:
        server.serve_forever()
    finally:
        asyncio.run_coroutine_threadsafe(controller.disconnect(wait=True), loop).result(15)
        server.server_close()

if __name__ == '__main__':
    main()
