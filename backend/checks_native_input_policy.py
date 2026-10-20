"""Offline companion lifecycle checks; no sockets, server import, TV or subprocess."""
import asyncio
import base64
import copy
import json
from pathlib import Path
import re
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from native_input import NativeInputController, POLICY_FILES

FOLDER = '/run/samsung-tv-root/agent-55/native-input-123456abcdef'
ARGV = ['/usr/bin/dotnet', FOLDER + '/NativeInput.dll',
        '--policy-session', FOLDER + '/policy-session.json']
OWNER = dict(pid=4321, startTime='987', instance='f' * 32, inputPid=1234,
             inputStartTime='789', rootPid=55, rootStartTime='456', argv=ARGV)
READY = {**{k: OWNER[k] for k in ('pid', 'startTime', 'instance')},
         'mode': 'owned-false-to-true', 'policyReady': True}


def receipt(**overrides):
    return {**copy.deepcopy(OWNER), 'mode': 'owned-false-to-true',
            'cleanupComplete': True, 'neverEnabled': False, 'restoreRequired': True,
            'restored': True, 'initialValue': False, 'finalValue': False,
            'exitCode': 0, 'reason': 'release', **overrides}


def envelope(owner, result, heartbeat=True):
    lines = ['directory-present']
    for name, value in [('policy-owner.json', owner), ('policy-result.json', result)]:
        lines.append(name)
        if value is not None:
            lines.append(base64.b64encode(json.dumps(value).encode()).decode())
        lines.append('end-file')
    lines.append('heartbeat-present' if heartbeat else 'heartbeat-missing')
    return '\n'.join(lines) + '\n'


def stat(state='S', start='987', argv=ARGV):
    fields = [state] + ['0'] * 18 + [start]
    cmdline = b'\0'.join(v.encode() for v in argv) + b'\0'
    return '4321 (dotnet) ' + ' '.join(fields) + '\n' + base64.b64encode(cmdline).decode() + '\n'


class Root:
    def __init__(self, controller, owner=OWNER, result=None, processes=None, heartbeat=True):
        self.c = controller
        self.owner, self.result, self.heartbeat = copy.deepcopy(owner), result, heartbeat
        self.processes = list(processes or ['gone\n'])
        self.commands, self.deleted = [], False
        self.search = ''
        self.remove_error = False
        self.read_error = False

    async def execute(self, command, timeout):
        self.commands.append(command)
        if '\\' in command:
            raise AssertionError('Legacy RPC must not contain backslashes')
        output, code = '', 0
        if command.startswith('if test -d /proc/1234'):
            output = 'gone\n'
        elif command.startswith('if test -d /proc/4321'):
            output = self.processes[0]
            if len(self.processes) > 1:
                self.processes.pop(0)
        elif 'echo directory-present' in command:
            if self.read_error:
                raise ConnectionError('Root RPC disappeared')
            output = envelope(self.owner, self.result, self.heartbeat)
        elif command.startswith('grep '):
            output = self.search
            pattern = re.search(r"-E '([^']+)'", command)[1]
            if re.search(pattern, command):
                raise AssertionError('Process search matches its own command')
            if not re.search(pattern, FOLDER + '/NativeInput.dll'):
                raise AssertionError('Process search misses the owned DLL')
        elif command.startswith('tail '):
            output = '{"type":"closed","cleanupComplete":true}\n'
        elif command.startswith('rm -f '):
            # Independently persisted evidence must exist before remote deletion.
            proof = json.loads(self.evidence_path().read_text('utf8'))
            if proof.get('policyCleanupComplete') is not True:
                raise AssertionError('Deletion before policy restoration proof')
            for name in POLICY_FILES:
                if FOLDER + '/' + name not in command or FOLDER + '/' + name + '.tmp' not in command:
                    raise AssertionError('Missing exact owned cleanup file')
            self.deleted = not self.remove_error
            code = 1 if self.remove_error else 0
        else:
            raise AssertionError('Unexpected offline command: ' + command)
        return SimpleNamespace(stdout=output, stderr='simulated removal error' if code else '',
                               exit_code=code, timed_out=False)

    def evidence_path(self):
        return self.c.root / 'diagnostics/remote-view/native-input-policy-cleanup-native-input-123456abcdef.json'


class PolicyChecks(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.c = NativeInputController(Path(self.temp.name))
        self.c._policy_required.add(FOLDER)
        self.c._remember_policy_ready(FOLDER, READY)

    async def asyncTearDown(self):
        self.temp.cleanup()

    async def clean(self, root):
        self.c._folder, self.c._pid, self.c._pid_start = FOLDER, 1234, '789'
        await self.c._cleanup_remote(root)
        return not self.c._retired

    async def test_owned_restore_proof_precedes_exact_file_deletion(self):
        root = Root(self.c, result=receipt())
        self.assertTrue(await self.clean(root))
        proof = json.loads(root.evidence_path().read_text('utf8'))
        self.assertEqual(proof['owner']['argv'], ARGV)
        self.assertEqual(proof['result']['argv'], ARGV)
        self.assertFalse(proof['result']['finalValue'])
        self.assertTrue(root.deleted)
        self.assertTrue(self.c.status()['inputCleanupComplete'])
        self.assertNotIn(FOLDER, self.c._policy_required)
        self.assertFalse(any('kill' in cmd for cmd in root.commands))

    async def test_preexisting_true_never_restored_false(self):
        for final in (True, False):
            with self.subTest(final=final):
                self.c._policy_required.add(FOLDER)
                self.c._policy_evidence.clear()
                self.c._remember_policy_ready(FOLDER, {**READY, 'mode': 'preexisting-true'})
                root = Root(self.c, result=receipt(mode='preexisting-true', initialValue=True,
                            neverEnabled=True, restoreRequired=False, restored=None, finalValue=final))
                self.assertTrue(await self.clean(root))
                self.assertFalse(any('kill' in s or 'set-bool' in s for s in root.commands))

    async def test_preexisting_true_requires_actual_final_readback(self):
        result = receipt(mode='preexisting-true', initialValue=True, neverEnabled=True,
                         restoreRequired=False, restored=None, finalValue=None)
        root = Root(self.c, result=result)
        self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)
        self.assertFalse(self.c.status()['inputCleanupComplete'])

    async def test_pending_failed_preflight_never_enabled_is_valid(self):
        self.c._policy_evidence.clear()  # Failure before ready.
        root = Root(self.c, result=receipt(mode='pending', initialValue=None, finalValue=None,
                    neverEnabled=True, restoreRequired=False, restored=None, exitCode=3))
        self.assertTrue(await self.clean(root))

    async def test_invalid_restore_states_are_not_accepted(self):
        invalid = [
            dict(finalValue=True), dict(restored=False), dict(cleanupComplete=False),
            dict(neverEnabled=True), dict(initialValue=True), dict(finalValue=0),
            dict(mode='unknown', neverEnabled=True, restoreRequired=False, restored=None),
        ]
        for change in invalid:
            with self.subTest(change=change):
                self.assertFalse(self.c._policy_result_complete(receipt(**change)))

    async def test_ready_requires_explicit_policy_ready(self):
        for flag in (False, None, 1):
            with self.subTest(flag=flag), self.assertRaises(RuntimeError):
                self.c._remember_policy_ready(FOLDER, {**READY, 'policyReady': flag})

    async def test_child_alive_with_result_waits_for_exit_without_signal(self):
        root = Root(self.c, result=receipt(), processes=[stat(), 'gone\n'])
        self.assertTrue(await self.clean(root))
        self.assertGreaterEqual(sum('/proc/4321' in cmd for cmd in root.commands), 2)
        self.assertFalse(any('kill' in s for s in root.commands))

    async def test_child_still_alive_retains_all_files_and_identity(self):
        root = Root(self.c, result=receipt(), processes=[stat()])
        with patch('native_input.POLICY_CLEANUP_SECONDS', .02):
            self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)
        self.assertEqual(self.c._retired, [(1234, '789', FOLDER)])
        self.assertFalse(self.c.status()['inputCleanupComplete'])

    async def test_missing_restoration_receipt_never_infers_success_from_exit(self):
        root = Root(self.c)
        self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)

    async def test_result_argv_mismatch_is_rejected(self):
        root = Root(self.c, result=receipt(argv=['/usr/bin/other']))
        self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)

    async def test_result_instance_mismatch_is_rejected(self):
        root = Root(self.c, result=receipt(instance='0' * 32))
        self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)

    async def test_authenticated_ready_identity_must_match_owner(self):
        self.c._remember_policy_ready(FOLDER, {**READY, 'instance': '0' * 32})
        root = Root(self.c, result=receipt())
        self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)

    async def test_reused_pid_not_signalled_after_verified_restoration(self):
        root = Root(self.c, result=receipt(), processes=[stat(start='999')])
        self.assertTrue(await self.clean(root))
        self.assertFalse(any('kill' in s for s in root.commands))

    async def test_same_start_different_argv_not_proven_exited(self):
        root = Root(self.c, result=receipt(), processes=[stat(argv=['/usr/bin/other'])])
        with patch('native_input.POLICY_CLEANUP_SECONDS', .02):
            self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)
        self.assertFalse(any('kill' in s for s in root.commands))

    async def test_preowner_interruption_no_seed_no_process_is_safe(self):
        self.c._policy_evidence.clear()
        root = Root(self.c, owner=None, heartbeat=False)
        self.assertTrue(await self.clean(root))
        self.assertTrue(root.deleted)
        self.assertTrue(any(s.startswith('grep ') for s in root.commands))

    async def test_no_owner_but_living_child_is_retained(self):
        self.c._policy_evidence.clear()
        root = Root(self.c, owner=None, heartbeat=False)
        root.search = '/proc/4321/cmdline\n'
        with patch('native_input.POLICY_CLEANUP_SECONDS', .02):
            self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)

    async def test_no_owner_but_seed_is_not_safe(self):
        self.c._policy_evidence.clear()
        root = Root(self.c, owner=None, heartbeat=True)
        with patch('native_input.POLICY_CLEANUP_SECONDS', .02):
            self.assertFalse(await self.clean(root))
        self.assertFalse(root.deleted)

    async def test_dead_rpc_retains_then_new_rpc_verifies_and_removes(self):
        dead = Root(self.c, result=receipt())
        dead.read_error = True
        self.assertFalse(await self.clean(dead))
        old_command_count = len(dead.commands)
        self.c._folder = None
        live = Root(self.c, result=receipt())
        await self.c._cleanup_remote(live)
        self.assertEqual(len(dead.commands), old_command_count)
        self.assertFalse(self.c._retired)
        self.assertTrue(live.deleted)
        self.assertTrue(self.c.status()['inputCleanupComplete'])

    async def test_no_rpc_cannot_report_cleanup_complete(self):
        self.c._last_cleanup = True
        self.assertFalse(await self.clean(None))
        self.assertFalse(self.c.status()['inputCleanupComplete'])

    async def test_removal_error_retains_record_despite_restore_success(self):
        root = Root(self.c, result=receipt())
        root.remove_error = True
        self.assertFalse(await self.clean(root))
        self.assertFalse(self.c.status()['inputCleanupComplete'])
        self.assertIn(FOLDER, self.c._policy_required)
        self.assertTrue(self.c._policy_evidence[FOLDER]['policyCleanupComplete'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
