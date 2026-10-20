"""Observe the owned Sunshine host; never launch/stop it or contact the TV.

CLIENT CONNECTED is protocol evidence, not proof of TV frames/audio. Arm an
attempt after preparing Sunshine and before sending the TV launch request.
"""
from __future__ import annotations

import ctypes
from ctypes import wintypes
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import threading
import urllib.request
import uuid
import xml.etree.ElementTree as ET


class CastMonitor:
    def __init__(self, root: Path | str | None = None, private_dir: Path | str | None = None,
                 *, log_path: Path | str | None = None, persist: bool = True,
                 owner_probe=None, server_probe=None):
        self.root = Path(root or Path(__file__).resolve().parent.parent).resolve()
        local = Path(os.environ.get('LOCALAPPDATA', str(self.root))) / 'TVCITATION'
        self.private = Path(private_dir or local / 'Control').resolve()
        self.log_path = Path(log_path or self.root / 'diagnostics' / 'cast' / 'sunshine.log').resolve()
        self.state_path = self.private / 'cast-monitor.json'
        self.owner_receipt = local / 'Sunshine' / 'owned-process.json'
        self.host_exe = self.root / 'tools' / 'cast' / 'sunshine' / 'Sunshine' / 'sunshine.exe'
        self.host_config = self.root / 'desktop-cast' / 'sunshine.conf'
        self._owner_probe = owner_probe or self._read_owner
        self._server_probe = server_probe or self._read_server
        self._persist = persist
        self._lock = threading.RLock()
        self._persistence_error = False
        self._state = self._load_state()

    @staticmethod
    def _utc() -> str:
        return datetime.now(timezone.utc).isoformat()

    @staticmethod
    def _path_key(path) -> str:
        return os.path.normcase(os.path.abspath(path))

    def _read_owner(self) -> dict | None:
        """Verify receipt PID, executable and creation time against the live process."""
        if os.name != 'nt':
            return None
        try:
            owner = json.loads(self.owner_receipt.read_text(encoding='utf-8-sig'))
            if (self._path_key(owner['Executable']) != self._path_key(self.host_exe)
                    or self._path_key(owner['Config']) != self._path_key(self.host_config)):
                return None
            pid = int(owner['ProcessId'])
            if pid <= 0:
                return None
            started = datetime.fromisoformat(owner['StartedUtc']).timestamp()
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel.OpenProcess.restype = wintypes.HANDLE
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                        wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
            kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
            kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
            handle = kernel.OpenProcess(0x1000, False, pid)
            if not handle:
                return None
            try:
                code = wintypes.DWORD()
                if not kernel.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != 259:
                    return None
                path = ctypes.create_unicode_buffer(32768)
                length = wintypes.DWORD(len(path))
                if not kernel.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(length)):
                    return None
                if self._path_key(path.value) != self._path_key(self.host_exe):
                    return None
                created, exited, kerneltime, usertime = (wintypes.FILETIME() for _ in range(4))
                if not kernel.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited),
                                              ctypes.byref(kerneltime), ctypes.byref(usertime)):
                    return None
                ticks = (created.dwHighDateTime << 32) | created.dwLowDateTime
                created_unix = ticks / 10_000_000 - 11_644_473_600
                # Start-CastHost writes its receipt immediately after Start-Process.
                if not -2 <= started - created_unix <= 30:
                    return None
                return {'key': str(pid) + ':' + str(ticks), 'pid': pid}
            finally:
                kernel.CloseHandle(handle)
        except Exception:
            return None

    @staticmethod
    def _read_server() -> dict:
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            with opener.open('http://127.0.0.1:47989/serverinfo', timeout=1) as response:
                document = ET.fromstring(response.read(128 * 1024))
            game = document.findtext('currentgame')
            if game is None or not game.isdigit():
                return {'known': False, 'reachable': True}
            return {'known': True, 'reachable': True, 'appId': int(game),
                    'applicationRunning': int(game) != 0}
        except Exception:
            return {'known': False, 'reachable': False}

    def _blank_state(self) -> dict:
        return {'schemaVersion': 1, 'logPath': str(self.log_path), 'ownerKey': None,
                'fileIdentity': None, 'offset': 0, 'anchor': None, 'generation': 0,
                'sequence': 0, 'connected': False, 'lastConnectSequence': 0,
                'lastConnectUtc': None, 'lastEvent': None, 'reason': 'uninitialized',
                'baselineUtc': None}

    def _load_state(self) -> dict:
        blank = self._blank_state()
        if not self._persist:
            return blank
        try:
            if self.state_path.stat().st_size > 64 * 1024:
                return blank
            data = json.loads(self.state_path.read_text(encoding='utf-8-sig'))
            if (data.get('schemaVersion') != 1 or data.get('logPath') != str(self.log_path)
                    or not isinstance(data.get('connected'), bool)):
                return blank
            if any(type(data.get(key)) is not int or data[key] < 0
                   for key in ('offset', 'generation', 'sequence', 'lastConnectSequence')):
                return blank
            if not all(key in data for key in blank):
                return blank
            return data
        except Exception:
            return blank

    def _save_state(self) -> None:
        if not self._persist:
            return
        temporary = None
        try:
            # The existing backend owns creation/ACL of this directory.
            if not self.private.is_dir():
                self._persistence_error = True
                return
            handle, temporary = tempfile.mkstemp(prefix='cast-monitor-', suffix='.tmp', dir=self.private)
            with os.fdopen(handle, 'w', encoding='utf-8') as stream:
                json.dump(self._state, stream, ensure_ascii=False)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.state_path)
            self._persistence_error = False
        except Exception:
            self._persistence_error = True
        finally:
            if temporary and os.path.exists(temporary):
                try:
                    os.unlink(temporary)
                except OSError:
                    pass

    @staticmethod
    def _identity(stat) -> str:
        return str(stat.st_dev) + ':' + str(stat.st_ino)

    @staticmethod
    def _anchor(stream, offset: int) -> str:
        stream.seek(max(0, offset - 256))
        return hashlib.sha256(stream.read(min(offset, 256))).hexdigest()

    def _reset_tail(self, owner_key, reason: str) -> None:
        state = self._state
        state.update(ownerKey=owner_key, connected=False, lastConnectSequence=0,
                     lastConnectUtc=None, lastEvent=None, reason=reason,
                     generation=state['generation'] + 1, offset=0,
                     fileIdentity=None, anchor=None, baselineUtc=self._utc())
        try:
            with self.log_path.open('rb') as stream:
                stat = os.fstat(stream.fileno())
                state['fileIdentity'] = self._identity(stat)
                state['offset'] = stat.st_size
                state['anchor'] = self._anchor(stream, stat.st_size)
        except OSError:
            pass

    def _consume(self, owner_key) -> None:
        state = self._state
        if state['ownerKey'] != owner_key or state['reason'] == 'uninitialized':
            self._reset_tail(owner_key, 'hostChanged' if state['reason'] != 'uninitialized' else 'baselineAtCurrentEnd')
            return
        if not owner_key:
            state['connected'] = False
            state['reason'] = 'ownedHostUnavailable'
            return
        try:
            with self.log_path.open('rb') as stream:
                stat = os.fstat(stream.fileno())
                identity = self._identity(stat)
                if identity != state['fileIdentity'] or stat.st_size < state['offset']:
                    self._reset_tail(owner_key, 'logReplacedOrTruncated')
                    return
                if self._anchor(stream, state['offset']) != state['anchor']:
                    self._reset_tail(owner_key, 'logRewritten')
                    return
                if stat.st_size - state['offset'] > 8 * 1024 * 1024:
                    self._reset_tail(owner_key, 'logGapTooLarge')
                    return
                stream.seek(state['offset'])
                content = stream.read(min(1024 * 1024, stat.st_size - state['offset']))
                last_newline = content.rfind(b'\n')
                if last_newline < 0:
                    return
                complete = content[:last_newline + 1]
                for line in complete.decode('utf-8', errors='replace').splitlines():
                    match = re.fullmatch(r'\[([^\]]+)\]:\s+\w+:\s+(CLIENT CONNECTED|CLIENT DISCONNECTED|Process terminated)\s*', line)
                    if not match:
                        # A new log header within an unchanged file also invalidates prior session state.
                        if re.match(r'^\[[^\]]+\]:\s+\w+:\s+Sunshine version:', line):
                            state['connected'] = False
                            state['lastConnectSequence'] = 0
                            state['lastConnectUtc'] = None
                            state['generation'] += 1
                            state['reason'] = 'hostLogRestarted'
                        continue
                    event = match.group(2)
                    observed = None
                    if event == 'CLIENT CONNECTED':
                        try:
                            observed = datetime.fromisoformat(match.group(1)).astimezone(timezone.utc)
                            if observed + timedelta(milliseconds=1) < datetime.fromisoformat(state['baselineUtc']):
                                continue
                        except (TypeError, ValueError):
                            continue
                    state['sequence'] += 1
                    state['lastEvent'] = event
                    state['reason'] = 'observedLogEvent'
                    if event == 'CLIENT CONNECTED':
                        state['connected'] = True
                        state['lastConnectSequence'] = state['sequence']
                        state['lastConnectUtc'] = observed.isoformat()
                    else:
                        # This tool targets one TV. Any disconnect conservatively clears confirmation.
                        state['connected'] = False
                state['offset'] += len(complete)
                state['anchor'] = self._anchor(stream, state['offset'])
        except OSError:
            state['connected'] = False
            state['reason'] = 'logUnavailable'

    def _poll(self) -> dict:
        try:
            owner = self._owner_probe()
        except Exception:
            owner = None
        owner_key = owner.get('key') if owner else None
        before = json.dumps(self._state, sort_keys=True)
        self._consume(owner_key)
        try:
            server = self._server_probe() if owner else {'known': False, 'reachable': False}
        except Exception:
            server = {'known': False, 'reachable': False}
        if server.get('known') and not server.get('applicationRunning'):
            self._state['connected'] = False
            if self._state['lastEvent'] == 'CLIENT CONNECTED':
                self._state['reason'] = 'applicationStopped'
        if json.dumps(self._state, sort_keys=True) != before:
            self._save_state()
        active = bool(owner_key and server.get('known') and server.get('applicationRunning')
                      and self._state['connected'])
        return {'active': active, 'streamConnected': active,
                'applicationRunning': bool(owner_key and server.get('applicationRunning')),
                'appId': server.get('appId', 0) if owner_key else 0,
                'hostOwned': bool(owner_key), 'hostReachable': bool(server.get('reachable')),
                'serverStateKnown': bool(server.get('known')),
                'streamEvidence': 'observedClientConnected' if active else 'notConfirmed',
                'lastStreamEvent': self._state['lastEvent'],
                'lastConnectUtc': self._state['lastConnectUtc'],
                'monitorReason': self._state['reason'], 'generation': self._state['generation'],
                'statePersistenceError': self._persistence_error,
                'tvFramesVerified': False, 'clientIdentityVerified': False}

    def status(self) -> dict:
        with self._lock:
            return self._poll()

    def begin_attempt(self) -> dict:
        """Take a fresh cursor AFTER host preparation and BEFORE the TV request."""
        with self._lock:
            status = self._poll()
            if not status['hostOwned'] or not status['serverStateKnown']:
                return {'ok': False, 'message': 'The Sunshine host for this session is not ready; cannot start connection verification.', **status}
            return {'ok': True, 'attemptId': uuid.uuid4().hex, 'ownerKey': self._state['ownerKey'],
                    'generation': self._state['generation'], 'sequence': self._state['sequence'],
                    'armedUtc': self._utc(), 'alreadyConnected': status['active']}

    def check_attempt(self, attempt: dict) -> dict:
        with self._lock:
            status = self._poll()
            valid = bool(isinstance(attempt, dict) and attempt.get('ok') is True
                         and attempt.get('ownerKey') == self._state['ownerKey']
                         and attempt.get('generation') == self._state['generation']
                         and type(attempt.get('sequence')) is int)
            newer = bool(valid and self._state['lastConnectSequence'] > attempt['sequence'])
            # A newly appended, dated event must also follow arming; reject copied old lines.
            if newer:
                try:
                    newer = (datetime.fromisoformat(self._state['lastConnectUtc']) + timedelta(milliseconds=1)
                             >= datetime.fromisoformat(attempt['armedUtc']))
                except (TypeError, ValueError, KeyError):
                    newer = False
            return {**status, 'attemptValid': valid,
                    'newConnection': bool(valid and newer and status['active']),
                    'message': None if valid else 'Sunshine or its log was restarted/reset; restart this connection verification.'}
