"""Offline SDB recovery checks. No SDB, PowerShell, native processes or TV calls."""
import ast
from contextlib import contextmanager
import ctypes
from ctypes import wintypes
from dataclasses import replace
import json
from pathlib import Path
import subprocess
import tempfile
import threading
import types
import unittest
from unittest.mock import patch

from sdb_health import (CommandTimeout, Inventory, ProcessIdentity, SdbHealth,
                        SdbHealthError, _WindowsHandle, select_daemon)


class Handle:
    def __init__(self):
        self.terminated = 0
        self.closed = False
        self.waited = False
        self.exited = True

    def terminate(self):
        self.terminated += 1

    def wait(self, seconds):
        self.waited = True
        return self.exited

    def close(self):
        self.closed = True


class Platform:
    def __init__(self, executable):
        self.daemon = ProcessIdentity(101, str(executable), ('sdb', 'fork-server', 'server', '--only-detect-tizen'), 12345)
        self.new_daemon = replace(self.daemon, pid=102, created_ticks=12346)
        self.old = Inventory((self.daemon,), 101)
        self.new = Inventory((self.new_daemon,), 102)
        self.results = [CommandTimeout('one'), CommandTimeout('two'), 0, 0]
        self.inventories = [self.old, self.old, Inventory((), None), self.new]
        self.calls = []
        self.inventory_calls = 0
        self.opened = []
        self.handle = Handle()
        self.mutex_busy = False
        self.on_inventory = None
        self.on_run = None

    @contextmanager
    def exclusive(self, executable):
        if self.mutex_busy:
            raise SdbHealthError('mock other process holds mutex')
        yield

    def run_sdb(self, executable, arguments, timeout):
        self.calls.append(arguments)
        if self.on_run:
            self.on_run()
        result = self.results.pop(0)
        if isinstance(result, BaseException):
            raise result
        return subprocess.CompletedProcess(arguments, result, 'List of devices attached\n', '')

    def inventory(self):
        self.inventory_calls += 1
        if self.on_inventory:
            self.on_inventory(self.inventory_calls)
        return self.inventories.pop(0)

    def open_verified(self, identity):
        self.opened.append(identity)
        return self.handle


class RecoveryChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name)
        self.exe = self.path / 'sdb.exe'
        self.now = 1000.0
        self.blocker = None
        self.platform = Platform(self.exe)
        self.health = SdbHealth(self.exe, self.path, lambda: self.blocker,
                                platform=self.platform, wall_clock=lambda: self.now)

    def tearDown(self):
        self.temp.cleanup()

    def assert_not_terminated(self):
        self.assertEqual(self.platform.handle.terminated, 0)
        self.assertNotIn(('start-server',), self.platform.calls)

    def test_healthy_single_devices_fast_path_has_no_inventory_or_receipt(self):
        self.platform.results = [0]
        self.assertEqual(self.health.ensure_ready()['sdbHealthState'], 'ready')
        self.assertEqual(self.platform.calls, [('devices',)])
        self.assertEqual(self.platform.inventory_calls, 0)
        self.assertFalse(self.health.receipt.exists())

    def test_one_timeout_then_success_does_not_recover(self):
        self.platform.results = [CommandTimeout('one'), 0]
        self.health.ensure_ready()
        self.assertEqual(self.platform.calls, [('devices',), ('devices',)])
        self.assert_not_terminated()

    def test_non_timeout_failure_does_not_recover(self):
        for values in ([1], [CommandTimeout('one'), 1]):
            self.platform.results = list(values)
            with self.assertRaises(SdbHealthError):
                self.health.ensure_ready()
            self.assert_not_terminated()

    def test_two_timeouts_recover_only_selected_handle_then_verify_new_daemon(self):
        status = self.health.ensure_ready()
        self.assertEqual(status['sdbHealthState'], 'recovered')
        self.assertEqual(status['sdbRecoveryRetryAfterSeconds'], 300)
        self.assertEqual(self.platform.calls, [('devices',), ('devices',), ('start-server',), ('devices',)])
        self.assertEqual(self.platform.opened, [self.platform.daemon])
        self.assertEqual(self.platform.handle.terminated, 1)
        self.assertTrue(self.platform.handle.closed and self.platform.handle.waited)
        self.assertEqual(json.loads(self.health.receipt.read_text())['attemptEpoch'], 1000)

    def test_cooldown_survives_new_instance_and_recovery_failure(self):
        self.platform.handle.exited = False
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready()
        self.assertNotIn(('start-server',), self.platform.calls)
        second_platform = Platform(self.exe)
        second = SdbHealth(self.exe, self.path, lambda: None, platform=second_platform, wall_clock=lambda: 1299)
        with self.assertRaises(SdbHealthError):
            second.ensure_ready()
        self.assertEqual(second.status()['sdbRecoveryRetryAfterSeconds'], 1)
        self.assertEqual(second_platform.inventory_calls, 0)
        self.assertEqual(second_platform.handle.terminated, 0)
        second_platform.results = [CommandTimeout('one'), CommandTimeout('two'), 0, 0]
        second.clock = lambda: 1300
        self.assertEqual(second.ensure_ready()['sdbHealthState'], 'recovered')

    def test_corrupt_or_nonfinite_receipt_fails_closed(self):
        for content in ('{', json.dumps({'attemptEpoch': float('nan'), 'executable': str(self.exe)})):
            self.health.receipt.write_text(content)
            self.platform.results = [CommandTimeout('one'), CommandTimeout('two')]
            with self.assertRaises(SdbHealthError):
                self.health.ensure_ready()
            self.assert_not_terminated()

    def test_active_root_or_native_blocker_prevents_inventory_and_recovery(self):
        self.blocker = 'root/native active'
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready()
        self.assertEqual(self.platform.inventory_calls, 0)
        self.assert_not_terminated()

    def test_activity_begins_during_identity_check(self):
        def entered(count):
            if count == 2:
                self.blocker = 'new activity'
        self.platform.on_inventory = entered
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready()
        self.assert_not_terminated()
        self.assertTrue(self.platform.handle.closed)

    def test_cancelled_bootstrap_never_runs_devices(self):
        cancelled = threading.Event()
        cancelled.set()
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready(cancelled)
        self.assertEqual(self.platform.calls, [])

    def test_cancelled_worker_cannot_recover_after_server_stop_event_is_replaced(self):
        cancelled = threading.Event()
        def entered(count):
            if count == 2:
                cancelled.set()
                self.blocker = None  # A new controller session is otherwise allowed.
        self.platform.on_inventory = entered
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready(cancelled)
        self.assert_not_terminated()
        self.assertTrue(self.platform.handle.closed)

    def test_new_same_path_client_between_inventory_checks_blocks_termination(self):
        client = replace(self.platform.daemon, pid=103, argv=('sdb', 'devices'))
        self.platform.inventories[1] = Inventory((self.platform.daemon, client), 101)
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready()
        self.assert_not_terminated()
        self.assertTrue(self.platform.handle.closed)

    def test_pid_reused_between_checks_does_not_terminate_held_or_new_process(self):
        replacement = replace(self.platform.daemon, created_ticks=99999)
        self.platform.inventories[1] = Inventory((replacement,), 101)
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready()
        self.assert_not_terminated()

    def test_identity_client_listener_and_unknown_path_guards(self):
        d = self.platform.daemon
        invalid = [Inventory((replace(d, executable=str(self.path / 'other/sdb.exe')),), 101),
                   Inventory((d, replace(d, pid=102)), 101),
                   Inventory((d, replace(d, pid=102, argv=('sdb', 'shell'))), 101),
                   Inventory((d,), 333), Inventory((d,), None),
                   Inventory((replace(d, executable=''),), 101),
                   Inventory((replace(d, created_ticks=0),), 101),
                   Inventory((replace(d, argv=('sdb', 'fork-server', 'server')),), 101)]
        for inventory in invalid:
            with self.subTest(inventory=inventory), self.assertRaises(SdbHealthError):
                select_daemon(inventory, self.exe)

    def test_foreign_daemon_is_untouched_when_workspace_daemon_owns_port(self):
        foreign = replace(self.platform.daemon, executable=str(self.path / 'foreign/sdb.exe'), pid=999)
        self.assertEqual(select_daemon(Inventory((self.platform.daemon, foreign), 101), self.exe), self.platform.daemon)

    def test_listener_replaced_after_exit_does_not_start_or_kill_replacement(self):
        foreign = replace(self.platform.daemon, executable=str(self.path / 'foreign/sdb.exe'), pid=999)
        self.platform.inventories[2] = Inventory((foreign,), 999)
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready()
        self.assertEqual(self.platform.handle.terminated, 1)
        self.assertNotIn(('start-server',), self.platform.calls)

    def test_failed_start_retains_cooldown(self):
        self.platform.results[2] = 1
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready()
        self.assertEqual(self.health.status()['sdbRecoveryRetryAfterSeconds'], 300)
        self.assertEqual(self.platform.calls[-1], ('start-server',))

    def test_cannot_persist_receipt_does_not_terminate(self):
        with patch('sdb_health.os.replace', side_effect=OSError('mock denied')):
            with self.assertRaises(SdbHealthError):
                self.health.ensure_ready()
        self.assert_not_terminated()

    def test_cross_process_mutex_busy_does_not_even_start_devices(self):
        self.platform.mutex_busy = True
        with self.assertRaises(SdbHealthError):
            self.health.ensure_ready()
        self.assertEqual(self.platform.calls, [])

    def test_concurrent_call_does_not_duplicate_health_or_recovery(self):
        entered, release = threading.Event(), threading.Event()
        self.platform.results = [0]
        def blocked():
            entered.set()
            release.wait(2)
        self.platform.on_run = blocked
        errors = []
        def work():
            try:
                self.health.ensure_ready()
            except BaseException as error:
                errors.append(error)
        worker = threading.Thread(target=work)
        worker.start()
        try:
            self.assertTrue(entered.wait(1))
            with self.assertRaises(SdbHealthError):
                self.health.ensure_ready()
        finally:
            release.set()
            worker.join(2)
        self.assertFalse(worker.is_alive())
        self.assertEqual(errors, [])
        self.assertEqual(self.platform.calls, [('devices',)])


class Kernel:
    """Only in-memory Win32 stand-in; validates held-handle behavior."""
    def __init__(self, path, ticks):
        self.path, self.ticks = path, ticks
        self.opened = []
        self.killed = []
        self.closed = []
        self.exited = False

    def OpenProcess(self, rights, inherit, pid):
        self.opened.append(pid)
        return 98765

    def WaitForSingleObject(self, handle, milliseconds):
        return 0 if self.exited else 0x102

    def QueryFullProcessImageNameW(self, handle, flags, target, length):
        target.value = self.path
        return True

    def GetProcessTimes(self, handle, creation, *unused):
        value = ctypes.cast(creation, ctypes.POINTER(wintypes.FILETIME)).contents
        value.dwLowDateTime, value.dwHighDateTime = self.ticks & 0xffffffff, self.ticks >> 32
        return True

    def TerminateProcess(self, handle, code):
        self.killed.append(handle)
        self.exited = True
        return True

    def CloseHandle(self, handle):
        self.closed.append(handle)


class HeldHandleChecks(unittest.TestCase):
    def setUp(self):
        self.identity = ProcessIdentity(123, str(Path('fake/sdb.exe').resolve()), ('sdb',), 12345)
        self.kernel = Kernel(self.identity.executable, self.identity.created_ticks)

    def test_termination_uses_same_handle_without_pid_reopen(self):
        h = _WindowsHandle(self.kernel, self.identity)
        h.terminate()
        self.assertTrue(h.wait(3))
        h.close()
        self.assertEqual(self.kernel.opened, [123])
        self.assertEqual(self.kernel.killed, [98765])
        self.assertEqual(self.kernel.closed, [98765])

    def test_path_or_creation_mismatch_never_terminates(self):
        for change in ('path', 'ticks'):
            k = Kernel(self.identity.executable, self.identity.created_ticks)
            if change == 'path':
                k.path = str(Path('foreign/sdb.exe').resolve())
            else:
                k.ticks += 1
            with self.assertRaises(SdbHealthError):
                _WindowsHandle(k, self.identity)
            self.assertEqual(k.killed, [])
            self.assertEqual(k.closed, [98765])

    def test_process_exiting_after_validation_is_not_terminated(self):
        h = _WindowsHandle(self.kernel, self.identity)
        self.kernel.exited = True
        with self.assertRaises(SdbHealthError):
            h.terminate()
        h.close()
        self.assertEqual(self.kernel.killed, [])


class ServerGuardChecks(unittest.TestCase):
    def test_actual_server_guard_covers_cancel_root_video_input_and_retired(self):
        tree = ast.parse(Path(__file__).with_name('server.py').read_text('utf-8-sig'))
        controller = next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == 'Controller')
        method = next(n for n in controller.body if isinstance(n, ast.FunctionDef) and n.name == '_sdb_recovery_blocker')
        ns = {'time': types.SimpleNamespace(monotonic=lambda: 100)}
        exec(compile(ast.Module(body=[method], type_ignores=[]), 'server-guard-only', 'exec'), ns)
        c = types.SimpleNamespace(stop_event=types.SimpleNamespace(is_set=lambda: False), auto_connect=True,
                                  last_client=100, CLIENT_IDLE_TIMEOUT=90, connection=None,
                                  native_video=types.SimpleNamespace(receiver=None, sender_pid=None, connection=None, lock=threading.Lock()),
                                  native_input=types.SimpleNamespace(_connection=None, _writer=None, _pid=0, _retired=[], _ensure_lock=threading.Lock()))
        check = lambda: ns['_sdb_recovery_blocker'](c)
        self.assertIsNone(check())
        for target, attr, value in [(c, 'auto_connect', False), (c, 'last_client', 0), (c, 'connection', object()),
                                    (c.native_video, 'receiver', object()), (c.native_video, 'sender_pid', 1),
                                    (c.native_input, '_pid', 1), (c.native_input, '_retired', [(1,)]),
                                    (c.native_input, '_writer', object())]:
            old = getattr(target, attr)
            setattr(target, attr, value)
            self.assertIsNotNone(check(), attr)
            setattr(target, attr, old)
        c.native_video.lock.acquire()
        self.assertIsNotNone(check())
        c.native_video.lock.release()
        c.native_input._ensure_lock.acquire()
        self.assertIsNotNone(check())
        c.native_input._ensure_lock.release()


if __name__ == '__main__':
    unittest.main(verbosity=2)
