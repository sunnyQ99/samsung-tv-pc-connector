"""Bounded recovery of this workspace's Windows SDB daemon before root bootstrap.

Import/construction never runs SDB. Recovery does not run TV commands, kill-server,
or process-name termination. Tests inject an entirely offline platform.
"""
from __future__ import annotations

from contextlib import contextmanager
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time


class SdbHealthError(RuntimeError):
    pass


class CommandTimeout(SdbHealthError):
    pass


@dataclass(frozen=True)
class ProcessIdentity:
    pid: int
    executable: str
    argv: tuple[str, ...]
    created_ticks: int


@dataclass(frozen=True)
class Inventory:
    processes: tuple[ProcessIdentity, ...]
    listener_pid: int | None


def same_path(left, right):
    return os.path.normcase(os.path.realpath(left)) == os.path.normcase(os.path.realpath(right))


def select_daemon(inventory: Inventory, executable: Path):
    """Fail closed: an unknown SDB process might be a concurrent workspace client."""
    if any(not p.executable or not p.created_ticks or not p.argv for p in inventory.processes):
        raise SdbHealthError('Could not verify the local SDB process identity; the existing service was left as is.')
    owned = [p for p in inventory.processes if same_path(p.executable, executable)]
    daemons = [p for p in owned if p.argv[1:] == ('fork-server', 'server', '--only-detect-tizen')]
    if len(daemons) != 1:
        raise SdbHealthError('No single project SDB server with matching arguments was found; no recovery was attempted.')
    daemon = daemons[0]
    # argv[0] may be simply "sdb"; actual executable path is checked separately.
    if Path(daemon.argv[0]).name.lower() not in ('sdb', 'sdb.exe'):
        raise SdbHealthError('SDB server arguments do not match; no recovery was attempted.')
    if any(p.pid != daemon.pid for p in owned):
        raise SdbHealthError('Another client is using the project SDB server; recovery waits until it finishes.')
    if inventory.listener_pid != daemon.pid:
        raise SdbHealthError('The SDB port is held by another or unknown process; no recovery was attempted.')
    return daemon


class _WindowsHandle:
    """Hold the process object throughout validation/termination; never reopen by PID."""
    def __init__(self, kernel, expected):
        self.kernel, self.expected = kernel, expected
        self.handle = kernel.OpenProcess(0x00100000 | 0x1000 | 0x0001, False, expected.pid)
        if not self.handle:
            raise SdbHealthError('Could not open the verified SDB server process; no recovery was attempted.')
        try:
            self.verify()
        except BaseException:
            self.close()
            raise

    def verify(self):
        if self.kernel.WaitForSingleObject(self.handle, 0) != 0x102:
            raise SdbHealthError('The SDB server exited or its identity changed; it was not terminated.')
        length = wintypes.DWORD(32768)
        image = ctypes.create_unicode_buffer(length.value)
        creation, exit_time, kernel_time, user_time = (wintypes.FILETIME() for _ in range(4))
        if not self.kernel.QueryFullProcessImageNameW(self.handle, 0, image, ctypes.byref(length)):
            raise SdbHealthError('Could not verify the SDB server path; it was not terminated.')
        if not self.kernel.GetProcessTimes(self.handle, ctypes.byref(creation), ctypes.byref(exit_time),
                                           ctypes.byref(kernel_time), ctypes.byref(user_time)):
            raise SdbHealthError('Could not verify the SDB server start time; it was not terminated.')
        ticks = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
        if not same_path(image.value, self.expected.executable) or ticks != self.expected.created_ticks:
            raise SdbHealthError('The SDB process identity changed; it was not terminated.')

    def terminate(self):
        self.verify()
        if not self.kernel.TerminateProcess(self.handle, 1):
            raise SdbHealthError('The local SDB server did not stop; waiting for the next connection.')

    def wait(self, seconds):
        return self.kernel.WaitForSingleObject(self.handle, int(seconds * 1000)) == 0

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


class WindowsPlatform:
    # Only used on the exceptional recovery path. No caller-provided shell text.
    INVENTORY_SCRIPT = r'''
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$rows = @(Get-CimInstance Win32_Process -Filter "Name='sdb.exe'" | ForEach-Object {
    $item = $_
    $process = [System.Diagnostics.Process]::GetProcessById([int]$item.ProcessId)
    try {
        [pscustomobject]@{pid=[int]$item.ProcessId; executable=$item.ExecutablePath;
            commandLine=$item.CommandLine; createdTicks=$process.StartTime.ToFileTimeUtc().ToString()}
    } finally { $process.Dispose() }
})
$listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object {$_.LocalPort -eq 26099})
$owners = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
[pscustomobject]@{processes=$rows; listenerPids=$owners} | ConvertTo-Json -Depth 4 -Compress
'''

    def __init__(self):
        if os.name != 'nt':
            raise SdbHealthError('Automatic local SDB recovery is only supported on Windows.')
        k = self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k.OpenProcess.restype = wintypes.HANDLE
        k.CloseHandle.argtypes = [wintypes.HANDLE]
        k.CloseHandle.restype = wintypes.BOOL
        k.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        k.WaitForSingleObject.restype = wintypes.DWORD
        k.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        k.QueryFullProcessImageNameW.restype = wintypes.BOOL
        k.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        k.GetProcessTimes.restype = wintypes.BOOL
        k.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        k.TerminateProcess.restype = wintypes.BOOL
        k.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
        k.CreateMutexW.restype = wintypes.HANDLE
        k.ReleaseMutex.argtypes = [wintypes.HANDLE]
        k.ReleaseMutex.restype = wintypes.BOOL
        k.LocalFree.argtypes = [wintypes.HLOCAL]
        k.LocalFree.restype = wintypes.HLOCAL
        self.shell = ctypes.WinDLL('shell32', use_last_error=True)
        self.shell.CommandLineToArgvW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_int)]
        self.shell.CommandLineToArgvW.restype = ctypes.POINTER(wintypes.LPWSTR)

    @staticmethod
    def _run(args, timeout):
        # SDB can spawn a daemon that inherits stdout. Files avoid communicate() waiting
        # forever for that unrelated long-lived process to close an inherited pipe.
        with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
            child = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=stdout,
                                     stderr=stderr, creationflags=0x08000000)
            try:
                child.wait(timeout=timeout)
            except subprocess.TimeoutExpired as error:
                # Only this call's client handle; never its daemon/descendants.
                child.kill()
                child.wait(timeout=1)
                raise CommandTimeout('Local SDB check timed out.') from error
            stdout.seek(0)
            stderr.seek(0)
            out, err = stdout.read(1024 * 1024 + 1), stderr.read(1024 * 1024 + 1)
        if len(out) + len(err) > 1024 * 1024:
            raise SdbHealthError('Unexpected local SDB check output.')
        return subprocess.CompletedProcess(args, child.returncode, out.decode('utf-8', 'replace'), err.decode('utf-8', 'replace'))

    def run_sdb(self, executable, arguments, timeout):
        return self._run([str(executable), *arguments], timeout)

    @contextmanager
    def exclusive(self, executable):
        digest = hashlib.sha256(os.path.normcase(str(executable)).encode('utf-8')).hexdigest()[:32]
        handle = self.kernel.CreateMutexW(None, False, 'Local\\TVCITATION.SdbHealth.' + digest)
        if not handle:
            raise SdbHealthError('Could not lock the local SDB check.')
        acquired = False
        try:
            result = self.kernel.WaitForSingleObject(handle, 0)
            acquired = result in (0, 0x80)  # Acquired or owner exited while holding it.
            if not acquired:
                raise SdbHealthError('Another connection is checking the local SDB server; try again later.')
            yield
        finally:
            if acquired:
                self.kernel.ReleaseMutex(handle)
            self.kernel.CloseHandle(handle)

    def _argv(self, value):
        if not value:
            return ()
        count = ctypes.c_int()
        values = self.shell.CommandLineToArgvW(value, ctypes.byref(count))
        if not values:
            raise SdbHealthError('Could not parse the SDB process arguments.')
        try:
            return tuple(values[i] for i in range(count.value))
        finally:
            self.kernel.LocalFree(ctypes.cast(values, wintypes.HLOCAL))

    def inventory(self):
        powershell = Path(os.environ['SystemRoot']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
        result = self._run([str(powershell), '-NoProfile', '-NonInteractive', '-Command', self.INVENTORY_SCRIPT], 8)
        if result.returncode:
            raise SdbHealthError('Could not verify the SDB process and port; no recovery was attempted.')
        try:
            doc = json.loads(result.stdout)
            owners = doc['listenerPids']
            if len(owners) > 1:
                raise SdbHealthError('The SDB port has more than one owner; no recovery was attempted.')
            if not owners:
                listener = None
            else:
                listener = int(owners[0])
            rows = tuple(ProcessIdentity(int(p['pid']), p['executable'] or '', self._argv(p['commandLine']),
                                         int(p['createdTicks'])) for p in doc['processes'])
            return Inventory(rows, listener)
        except (KeyError, ValueError, TypeError) as error:
            raise SdbHealthError('The SDB process check was incomplete; no recovery was attempted.') from error

    def open_verified(self, expected):
        return _WindowsHandle(self.kernel, expected)


class SdbHealth:
    DEVICE_TIMEOUT = 2.0
    COOLDOWN = 300.0

    def __init__(self, executable: Path, private: Path, recovery_blocker, *, platform=None, wall_clock=time.time):
        self.executable = Path(executable).resolve()
        self.receipt = Path(private) / 'sdb-recovery.json'
        self.recovery_blocker = recovery_blocker
        self.platform = platform
        self.clock = wall_clock
        self._lock = threading.Lock()
        self._state = 'unchecked'
        self._message = 'Waiting for the local connection check.'
        self._last_attempt = None

    def status(self):
        remaining = 0 if self._last_attempt is None else min(self.COOLDOWN, max(0, self._last_attempt + self.COOLDOWN - self.clock()))
        return {'sdbHealthState': self._state, 'sdbHealthMessage': self._message,
                'sdbRecoveryRetryAfterSeconds': round(remaining, 1)}

    def _set(self, state, message):
        self._state, self._message = state, message

    @staticmethod
    def _check_cancelled(cancelled):
        if cancelled is not None and cancelled.is_set():
            raise SdbHealthError('This connection check was cancelled; local SDB recovery did not continue.')

    def _check_blocker(self, cancelled):
        self._check_cancelled(cancelled)
        reason = self.recovery_blocker()
        if reason:
            raise SdbHealthError('Not recovering the local SDB server: ' + str(reason))

    def _load_cooldown(self):
        if self.receipt.exists():
            try:
                doc = json.loads(self.receipt.read_text('utf-8'))
                value = float(doc['attemptEpoch'])
                if not math.isfinite(value) or value <= 0 or not same_path(doc['executable'], self.executable):
                    raise ValueError('invalid receipt')
                self._last_attempt = value
            except (OSError, KeyError, ValueError, TypeError) as error:
                raise SdbHealthError('The local SDB recovery record could not be verified; recovery was not repeated.') from error
        remaining = self.status()['sdbRecoveryRetryAfterSeconds']
        if remaining > 0:
            self._set('cooldown', f'The local connection service is still not responding; recovery cooldown {remaining:g} s remaining.')
            raise SdbHealthError(self._message)

    def _record_attempt(self, daemon):
        # Written before any termination; a failed attempt or app restart cannot reset cooldown.
        stamp = self.clock()
        temporary = self.receipt.with_suffix(f'.{os.getpid()}.{threading.get_ident()}.tmp')
        doc = {'version': 1, 'attemptEpoch': stamp, 'executable': str(self.executable),
               'daemonPid': daemon.pid, 'daemonCreatedTicks': str(daemon.created_ticks)}
        try:
            temporary.write_text(json.dumps(doc, indent=2), encoding='utf-8')
            os.replace(temporary, self.receipt)
        except OSError as error:
            raise SdbHealthError('Could not save the local SDB recovery cooldown record; no recovery was attempted.') from error
        finally:
            temporary.unlink(missing_ok=True)
        self._last_attempt = stamp

    def ensure_ready(self, cancelled=None):
        if not self._lock.acquire(blocking=False):
            raise SdbHealthError('A local SDB check is already running.')
        try:
            platform = self.platform or WindowsPlatform()
            with platform.exclusive(self.executable):
                self._check_cancelled(cancelled)
                self._set('checking', 'Checking the local connection service.')
                for attempt in range(2):
                    self._check_cancelled(cancelled)
                    try:
                        result = platform.run_sdb(self.executable, ('devices',), self.DEVICE_TIMEOUT)
                    except CommandTimeout:
                        if attempt == 0:
                            continue
                        break
                    if result.returncode != 0:
                        raise SdbHealthError('Local SDB check failed; retrying later.')
                    self._set('ready', 'The local connection service is healthy.')
                    return self.status()

                self._load_cooldown()
                self._check_blocker(cancelled)
                daemon = select_daemon(platform.inventory(), self.executable)
                handle = platform.open_verified(daemon)
                try:
                    # Inspect again with the exact process handle held; reject PID reuse,
                    # new clients or an activity change before the irreversible call.
                    if select_daemon(platform.inventory(), self.executable) != daemon:
                        raise SdbHealthError('The SDB server identity changed; no recovery was attempted.')
                    self._check_blocker(cancelled)
                    self._record_attempt(daemon)
                    self._check_blocker(cancelled)
                    self._set('recovering', 'Recovering the local connection service.')
                    handle.terminate()
                    if not handle.wait(3):
                        raise SdbHealthError('The local SDB server has not exited; no replacement was started.')
                finally:
                    handle.close()
                released = platform.inventory()
                if released.listener_pid is not None or any(not p.executable or same_path(p.executable, self.executable) for p in released.processes):
                    raise SdbHealthError('The local SDB process or port is not released yet; no replacement was started.')
                self._check_blocker(cancelled)
                started = platform.run_sdb(self.executable, ('start-server',), 5)
                if started.returncode:
                    raise SdbHealthError('The local SDB server failed to restart; retrying later.')
                result = platform.run_sdb(self.executable, ('devices',), self.DEVICE_TIMEOUT)
                if result.returncode:
                    raise SdbHealthError('The local SDB server is still not responding after recovery; retrying later.')
                select_daemon(platform.inventory(), self.executable)
                self._set('recovered', 'The local connection service recovered; reconnecting to the TV.')
                return self.status()
        except SdbHealthError as error:
            if self._state != 'cooldown':
                self._set('unavailable', str(error))
            raise
        except Exception as error:
            self._set('unavailable', 'The local connection service check did not finish; retrying later.')
            raise SdbHealthError(self._message) from error
        finally:
            self._lock.release()
