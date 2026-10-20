"""Offline loss recovery checks. Extract Controller without executing live server setup."""
import ast
import asyncio
import copy
from pathlib import Path
import types
import unittest


class AudioLossChecks(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.now = 100.0
        self.session = {'id': 'a' * 32, 'phase': 'prepared', 'tvOnlyAudio': True,
                        'defaultsSaved': True, 'configSaved': True}
        self.cast = {'streamConnected': True, 'hostOwned': True, 'hostReachable': True,
                     'serverStateKnown': True, 'applicationRunning': True,
                     'lastStreamEvent': 'CLIENT CONNECTED'}
        self.host = {'owned': True, 'known': True, 'active': True, 'otherCount': 0}
        self.stops = 0
        self.fail_restore = False
        self.on_session_read = None
        self.on_host_read = None
        self.audio = types.SimpleNamespace(_busy=False, _read_session=self.read_session,
                                           _host_state=self.read_host, stop=self.stop_audio)
        source = Path(__file__).with_name('server.py')
        tree = ast.parse(source.read_text(encoding='utf-8-sig'))
        controller = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == 'Controller')
        namespace = dict(asyncio=asyncio, time=types.SimpleNamespace(monotonic=lambda: self.now),
                         ROOT=Path('offline-only'), PRIVATE=Path('offline-only'),
                         SDB=Path('offline-only/sdb.exe'), SdbHealth=lambda *args: None,
                         NativeVideoController=lambda *args: None, NativeInputController=lambda *args: None,
                         SamsungRemote=lambda: None, CastAudioCoordinator=lambda *args: self.audio,
                         CastMonitor=lambda *args: types.SimpleNamespace(status=lambda: copy.deepcopy(self.cast)))
        exec(compile(ast.Module(body=[controller], type_ignores=[]), str(source), 'exec'), namespace)
        self.controller = namespace['Controller']()

    async def asyncTearDown(self):
        task = self.controller.cast_task
        if task and not task.done():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)

    def read_session(self):
        if self.on_session_read:
            self.on_session_read()
        return copy.deepcopy(self.session)

    def read_host(self):
        if self.on_host_read:
            self.on_host_read()
        return dict(self.host)

    def stop_audio(self):
        self.stops += 1
        self.session['phase'] = 'recoveryNeeded' if self.fail_restore else 'completed'
        return {'ok': not self.fail_restore, 'needsRecovery': self.fail_restore}

    def drop(self):
        self.cast.update(streamConnected=False, lastStreamEvent='CLIENT DISCONNECTED')

    async def tick(self, seconds=0):
        self.now += seconds
        await self.controller._maintain_cast_audio()

    async def finish(self):
        if self.controller.cast_task:
            await asyncio.wait_for(self.controller.cast_task, 2)

    async def test_debounce_and_recover_once_after_confirmed_cast(self):
        await self.tick()
        self.drop()
        await self.tick()
        await self.tick(4.9)
        self.assertEqual(self.stops, 0)
        await self.tick(.2)
        await self.finish()
        self.assertEqual(self.stops, 1)
        await self.tick(20)
        self.assertEqual(self.stops, 1)

    async def test_never_confirmed_or_non_tv_audio_is_not_recovered(self):
        self.drop()
        await self.tick()
        await self.tick(30)
        self.assertEqual(self.stops, 0)
        self.session['tvOnlyAudio'] = False
        self.cast.update(streamConnected=True)
        await self.tick()
        self.drop()
        await self.tick(30)
        self.assertEqual(self.stops, 0)

    async def test_reconnect_during_grace_resets_timer(self):
        await self.tick()
        self.drop()
        await self.tick()
        self.cast.update(streamConnected=True, lastStreamEvent='CLIENT CONNECTED')
        await self.tick(4)
        self.drop()
        await self.tick(4)
        await self.tick(4.9)
        self.assertEqual(self.stops, 0)
        await self.tick(.2)
        await self.finish()
        self.assertEqual(self.stops, 1)

    async def test_unknown_http_state_alone_does_not_stop_client(self):
        await self.tick()
        self.cast.update(streamConnected=False, hostReachable=False, serverStateKnown=False)
        await self.tick()
        await self.tick(30)
        self.assertIsNone(self.controller.cast_task)

    async def test_prepared_owned_host_crash_after_observed_stream_recovers(self):
        await self.tick()
        self.cast.update(streamConnected=False, hostOwned=False, hostReachable=False, serverStateKnown=False)
        self.host.update(owned=False, known=True, active=False)
        await self.tick()
        await self.tick(5)
        await self.finish()
        self.assertEqual(self.stops, 1)

    async def test_other_sunshine_is_not_stopped_and_failure_is_bounded(self):
        await self.tick()
        self.drop()
        self.host['otherCount'] = 1
        await self.tick()
        await self.tick(5)
        await self.finish()
        self.assertEqual(self.stops, 0)
        self.assertTrue(self.controller.cast_operation['needsRecovery'])
        previous = self.controller.cast_task
        await self.tick(30)
        self.assertIs(self.controller.cast_task, previous)

    async def test_running_cast_task_lock_and_audio_busy_block_recovery(self):
        await self.tick()
        self.drop()
        pending = asyncio.create_task(asyncio.Event().wait())
        self.controller.cast_task = pending
        await self.tick(30)
        self.assertIsNone(self.controller.audio_loss_since)
        pending.cancel()
        await asyncio.gather(pending, return_exceptions=True)
        async with self.controller.cast_lock:
            await self.tick(30)
            self.assertIsNone(self.controller.audio_loss_since)
        self.audio._busy = True
        await self.tick(30)
        self.assertIsNone(self.controller.audio_loss_since)
        self.audio._busy = False
        await self.tick()
        self.assertEqual(self.stops, 0)

    async def test_task_started_during_await_prevents_recovery_queue(self):
        await self.tick()
        self.drop()
        await self.tick()
        pending = asyncio.create_task(asyncio.Event().wait())
        self.on_session_read = lambda: setattr(self.controller, 'cast_task', pending)
        await self.tick(5)
        self.assertIs(self.controller.cast_task, pending)
        self.assertEqual(self.stops, 0)

    async def test_new_session_and_late_client_return_cancel_restore(self):
        await self.tick()
        self.drop()
        await self.tick()
        self.session['id'] = 'b' * 32
        await self.tick(10)
        self.assertIsNone(self.controller.cast_task)
        self.cast.update(streamConnected=True, lastStreamEvent='CLIENT CONNECTED')
        await self.tick()
        self.drop()
        await self.tick()
        self.on_host_read = lambda: self.cast.update(streamConnected=True, lastStreamEvent='CLIENT CONNECTED')
        await self.tick(5)
        await self.finish()
        self.assertEqual(self.stops, 0)
        self.assertEqual(self.controller.audio_auto_recovery['state'], 'cancelled')

    async def test_failed_restore_is_not_repeated_automatically(self):
        await self.tick()
        self.drop()
        self.fail_restore = True
        await self.tick()
        await self.tick(5)
        await self.finish()
        await self.tick(60)
        self.assertEqual(self.stops, 1)
        self.assertTrue(self.controller.cast_operation['needsRecovery'])

    async def test_closed_gui_preserves_stream_then_recovers_later_loss(self):
        self.controller.last_client = -1000
        self.controller.auto_connect = False
        await self.tick()
        await self.tick(200)
        self.assertEqual(self.stops, 0)
        self.drop()
        await self.tick()
        await self.tick(5)
        await self.finish()
        self.assertEqual(self.stops, 1)


if __name__ == '__main__':
    unittest.main(verbosity=2)
