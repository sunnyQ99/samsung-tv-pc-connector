"""Offline protocol/lifecycle checks. Does not import server or connect to any TV."""
import asyncio
import base64
import hashlib
import json
from pathlib import Path
import tempfile
import time
from types import SimpleNamespace
import unittest

from native_input import NativeInputController, TV


class Writer:
    def __init__(self, reader, delay=0):
        self.reader, self.delay = reader, delay
        self.sent, self.closed = [], False
    def get_extra_info(self, key):
        return (TV, 55001) if key == 'peername' else None
    def write(self, value):
        message = json.loads(value)
        self.sent.append(message)
        if 'id' in message:
            not_before = time.perf_counter() + self.delay
            def reply():
                # Windows' asyncio clock can schedule short timers early. Keep the
                # simulated device delay tied to the same high-resolution clock.
                if time.perf_counter() < not_before:
                    asyncio.get_running_loop().call_later(.001, reply)
                    return
                self.reader.feed_data((json.dumps({'id': message['id'], 'ok': True, 'result': 0,
                    'buttons': message.get('buttons', 0)}) + '\n').encode())
                if message['action'] == 'stop':
                    self.reader.feed_data(b'{"type":"closed","cleanupComplete":true}\n')
            asyncio.get_running_loop().call_later(self.delay, reply)
    async def drain(self):
        await asyncio.sleep(0)
    def close(self):
        if not self.closed:
            self.closed = True
            self.reader.feed_eof()
    async def wait_closed(self):
        await asyncio.sleep(0)
    def is_closing(self):
        return self.closed


class Connection:
    def __init__(self, outputs):
        self.outputs = list(outputs)
        self.commands = []
    async def execute(self, command, timeout):
        self.commands.append(command)
        item = self.outputs.pop(0)
        if isinstance(item, Exception):
            raise item
        return SimpleNamespace(stdout=item, stderr='', exit_code=0, timed_out=False)


class NativeInputChecks(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.c = NativeInputController(Path(self.temp.name))
    async def asyncTearDown(self):
        await self.c.stop()
        self.temp.cleanup()
    async def connected(self, delay=0):
        c = self.c
        c._pid, c._token = 1234, 'a' * 64
        c._stopping = False
        c._launch_known = asyncio.Event()
        c._launch_known.set()
        c._ready_future = asyncio.get_running_loop().create_future()
        c._closed_future = asyncio.get_running_loop().create_future()
        reader = asyncio.StreamReader()
        writer = Writer(reader, delay)
        reader.feed_data((json.dumps({'type':'hello', 'protocol':1, 'token':c._token,
            'pid':1234,'startTime':'789','width':1920,'height':1080})+'\n').encode())
        reader.feed_data(b'{"type":"ready","ok":true,"width":1920,"height":1080,"pointerAvailable":true,"buttons":0}\n')
        task = asyncio.create_task(c._accepted(reader, writer, c._generation))
        await asyncio.wait_for(asyncio.shield(c._ready_future), 1)
        return reader, writer, task

    async def test_auth_order_click_and_real_ack_measurement(self):
        _, w, _ = await self.connected(.005)
        result = await self.c.pointer('click', 1280, 720)
        self.assertEqual(result['buttons'], 0)
        commands = [m for m in w.sent if 'id' in m]
        self.assertEqual([m['action'] for m in commands], ['down','up'])
        self.assertEqual([(m['x'],m['y'],m['buttons']) for m in commands], [(1280,720,1),(1280,720,0)])
        self.assertLess(commands[0]['id'], commands[1]['id'])
        s = self.c.status()
        self.assertEqual(s['ackSampleCount'], 2)
        self.assertGreaterEqual(s['lastAckMilliseconds'], 4)
        self.assertIn('not app visual latency', s['ackMeaning'])
        self.assertIsNone(self.c._token)
        await self.c.stop()
        self.assertTrue(self.c.status()['inputCleanupComplete'])

    async def test_wrong_token_is_closed_without_poisoning_session(self):
        self.c._token = 'a' * 64
        r = asyncio.StreamReader(); w = Writer(r)
        r.feed_data(b'{"type":"hello","protocol":1,"token":"wrong"}\n')
        await self.c._accepted(r, w, self.c._generation)
        self.assertTrue(w.closed)
        self.assertFalse(self.c._ready)
        self.assertEqual(self.c._token, 'a'*64)

    async def test_token_cannot_be_reused(self):
        _, active, _ = await self.connected()
        r = asyncio.StreamReader(); w = Writer(r)
        r.feed_data((json.dumps({'type':'hello','protocol':1,'token':'a'*64})+'\n').encode())
        await self.c._accepted(r,w,self.c._generation)
        self.assertTrue(w.closed)
        self.assertIs(self.c._writer, active)
        self.assertTrue(self.c._ready)

    async def test_move_coalesces_only_before_barrier(self):
        self.c._ready = True
        gate = asyncio.Event(); entered = asyncio.Event(); commands = []
        async def rpc(action, x, y):
            commands.append((action,x,y))
            if len(commands) == 1:
                entered.set(); await gate.wait()
            self.c._buttons = 1 if action == 'down' else 0 if action == 'up' else self.c._buttons
            return {'result':0}
        self.c._rpc = rpc
        self.c._worker_task = asyncio.create_task(self.c._worker())
        tasks = [asyncio.create_task(self.c.pointer('move',1,1))]
        await entered.wait()
        for action,x in [('move',2),('move',3),('down',4),('move',5),('move',6),('up',7)]:
            tasks.append(asyncio.create_task(self.c.pointer(action,x,x)))
        await asyncio.sleep(0)
        self.assertEqual(len(self.c._queue), 4)
        gate.set(); results = await asyncio.gather(*tasks)
        self.assertEqual(commands, [('move',1,1),('move',3,3),('down',4,4),('move',6,6),('up',7,7)])
        self.assertTrue(results[1]['coalesced'])
        self.assertEqual(results[1]['x'],3)

    async def test_cancelled_http_up_is_still_delivered(self):
        _, w, _ = await self.connected(.01)
        await self.c.pointer('down',50,50)
        release = asyncio.create_task(self.c.pointer('up',60,60))
        await asyncio.sleep(0)
        release.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await release
        await asyncio.sleep(.04)
        self.assertEqual(self.c._buttons,0)
        self.assertIn('up',[m.get('action') for m in w.sent])

    async def test_stop_during_inflight_ack_is_clean(self):
        _, w, _ = await self.connected(.03)
        move = asyncio.create_task(self.c.pointer('move',100,100))
        while not any(m.get('action')=='move' for m in w.sent):
            await asyncio.sleep(0)
        await self.c.stop()
        result = await asyncio.gather(move, return_exceptions=True)
        self.assertIsInstance(result[0],ConnectionError)
        self.assertTrue(self.c.status()['inputCleanupComplete'])

    async def test_sample_memory_is_bounded(self):
        await self.connected()
        for i in range(80):
            await self.c.pointer('move',i,i)
        self.assertEqual(self.c.status()['ackSampleCount'],64)
        self.assertGreaterEqual(self.c.status()['maxAckMilliseconds'], self.c.status()['lastAckMilliseconds'])

    async def test_unverified_inputs_never_reach_native(self):
        _, w, _ = await self.connected()
        for args in [('wheel',1,1,'left'),('click',1,1,'right'),('move',1920,1,'left'),('move',True,1,'left')]:
            with self.assertRaises(ValueError):
                await self.c.pointer(*args)
        self.assertEqual(len(w.sent),1)

    async def test_move_waiters_are_bounded_without_dropping_release(self):
        self.c._ready = True
        tasks = [asyncio.create_task(self.c.pointer('move',i,0)) for i in range(257)]
        await asyncio.sleep(0)
        self.assertEqual(len(self.c._queue[0].waiters),256)
        self.assertIsInstance(tasks[-1].exception(),ValueError)
        release = asyncio.create_task(self.c.pointer('up',1,1))
        await asyncio.sleep(0)
        self.assertEqual([x.action for x in self.c._queue],['move','up'])
        self.c._fault('offline test shutdown')
        await asyncio.gather(*tasks,release,return_exceptions=True)

    async def test_new_live_connection_cleans_retired_identity(self):
        folder = '/run/samsung-tv-root/agent-55/native-input-123456abcdef'
        dead = Connection([ConnectionError('old RPC closed')])
        live = Connection(['gone\n','{"type":"closed","cleanupComplete":true}\n',''])
        self.c._connection = dead
        self.c._folder,self.c._pid,self.c._pid_start = folder,1234,'789'
        await self.c._stop_locked(cleanup_connection=live)
        self.assertEqual(dead.commands,[])
        self.assertEqual(len(live.commands),3)
        self.assertFalse(self.c._retired)

    async def test_dead_rpc_retains_identity_for_later_live_cleanup(self):
        folder = '/run/samsung-tv-root/agent-55/native-input-123456abcdef'
        self.c._folder,self.c._pid,self.c._pid_start = folder,1234,'789'
        await self.c._cleanup_remote(Connection([ConnectionError('closed')]))
        self.c._folder = None
        self.assertEqual(len(self.c._retired),1)
        live = Connection(['gone\n','',''])
        await self.c._cleanup_remote(live)
        self.assertEqual(self.c._retired,[])

    async def test_pid_reuse_is_never_signalled(self):
        folder = '/run/samsung-tv-root/agent-55/native-input-123456abcdef'
        fields = ['S']+['0']*18+['999']
        live = Connection(['1234 (other) '+' '.join(fields)+'\n'+base64.b64encode(b'/usr/bin/other\0').decode()+'\n','',''])
        self.assertTrue(await self.c._cleanup_record(live,1234,'789',folder))
        self.assertFalse(any('kill' in s for s in live.commands))

    async def test_owned_identity_and_kill_guard_have_no_rpc_backslashes(self):
        folder = '/run/samsung-tv-root/agent-55/native-input-123456abcdef'
        fields = ['S']+['0']*18+['789']
        argv = ['/usr/bin/dotnet',folder+'/NativeInput.dll','--session',folder+'/session.json']
        cmdline = b'\0'.join(a.encode() for a in argv)+b'\0'
        encoded = base64.b64encode(cmdline).decode()
        wrapped = '\n'.join(encoded[i:i+76] for i in range(0,len(encoded),76))
        live = Connection(['1234 (dotnet) '+' '.join(fields)+'\n'+wrapped+'\n','','gone\n','',''])
        self.assertTrue(await self.c._cleanup_record(live,1234,'789',folder))
        self.assertTrue(any('base64 < /proc/1234/cmdline' in s for s in live.commands))
        guard = next(s for s in live.commands if 'kill -TERM' in s)
        self.assertIn(hashlib.sha256(cmdline).hexdigest(),guard)
        self.assertIn('sha256sum /proc/1234/cmdline',guard)
        self.assertFalse(any('\\' in s for s in live.commands))

    async def test_same_pid_start_different_argv_is_never_signalled(self):
        folder = '/run/samsung-tv-root/agent-55/native-input-123456abcdef'
        fields = ['S']+['0']*18+['789']
        actual = b'/usr/bin/dotnet\0/another/NativeInput.dll\0'
        live = Connection(['1234 (dotnet) '+' '.join(fields)+'\n'+base64.b64encode(actual).decode()+'\n','',''])
        self.assertTrue(await self.c._cleanup_record(live,1234,'789',folder))
        self.assertFalse(any('kill' in s for s in live.commands))


if __name__ == '__main__':
    unittest.main(verbosity=2)
