"""Authenticated, bounded TV access-unit receiver and lossless fMP4 relay.

No screen polling, decoding, encoding or TV operation happens at import time.
Only the owning root-agent session may launch the fixed native sender.
"""
import asyncio
import base64
from collections import deque
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import struct
import subprocess
import threading
import time

PC = '192.168.1.75'
TV = '192.168.1.5'
MAX_BOX = 8 * 1024 * 1024
MAX_REPLAY = 16 * 1024 * 1024
MAX_SUBSCRIBERS = 3
MAX_AU = 1024 * 1024
MAX_FRAME_REPLAY = 8 * 1024 * 1024
MAX_REPLAY_FRAMES = 120
MAX_MUX_QUEUE_FRAMES = 30
MAX_MUX_QUEUE_BYTES = 4 * 1024 * 1024
FRAME_HEADER = struct.Struct('>4sIII')


def access_unit_info(data):
    """Inspect bounded Annex-B NAL headers; never decode or modify video data."""
    types, codec = set(), None
    position = data.find(b'\x00\x00\x01')
    if position not in (0, 1) or any(data[:position]):
        raise ValueError('Access unit must start with Annex-B')
    while position >= 0:
        start = position + 3
        following = data.find(b'\x00\x00\x01', start)
        end = following if following >= 0 else len(data)
        if start >= end:
            raise ValueError('Empty Annex-B NAL')
        kind = data[start] & 31
        if data[start] & 128 or not 1 <= kind <= 23:
            raise ValueError('Invalid Annex-B NAL header')
        types.add(kind)
        if kind == 7:
            if end - start < 4:
                raise ValueError('Truncated SPS')
            value = 'avc1.' + data[start + 1:start + 4].hex()
            if codec is not None and codec != value:
                raise ValueError('Conflicting SPS configuration')
            codec = value
        position = following
    return 5 in types, {5, 7, 8}.issubset(types), codec


def boxes(data, start=0):
    """Strict bounded ISO-BMFF box iterator, also used for nested metadata."""
    position = start
    while position < len(data):
        if len(data) - position < 8:
            raise ValueError('Truncated MP4 box')
        size, kind = struct.unpack_from('>I4s', data, position)
        header = 8
        if size == 1:
            if len(data) - position < 16:
                raise ValueError('Truncated MP4 extended box')
            size = struct.unpack_from('>Q', data, position + 8)[0]
            header = 16
        if size < header or size > MAX_BOX or position + size > len(data):
            raise ValueError('Invalid MP4 box size')
        yield kind, data[position + header:position + size]
        position += size


def fragment_info(moof):
    """Return sample count and true random-access flag; never infer from FPS."""
    samples, first_flags = 0, None
    for kind, payload in boxes(moof, 8):
        if kind != b'traf':
            continue
        default_flags = 0
        for child, content in boxes(payload):
            if child == b'tfhd':
                flags = int.from_bytes(content[1:4], 'big')
                offset = 8
                for bit, length in ((1, 8), (2, 4), (8, 4), (16, 4)):
                    if flags & bit:
                        offset += length
                if flags & 32:
                    default_flags = struct.unpack_from('>I', content, offset)[0]
            elif child == b'trun':
                flags = int.from_bytes(content[1:4], 'big')
                count = struct.unpack_from('>I', content, 4)[0]
                if count > 600:
                    raise ValueError('Oversized video fragment')
                samples += count
                offset = 8 + (4 if flags & 1 else 0)
                explicit_first = None
                if flags & 4:
                    explicit_first = struct.unpack_from('>I', content, offset)[0]
                    offset += 4
                if count and first_flags is None:
                    if explicit_first is not None:
                        first_flags = explicit_first
                    elif flags & 0x400:
                        offset += (4 if flags & 0x100 else 0) + (4 if flags & 0x200 else 0)
                        first_flags = struct.unpack_from('>I', content, offset)[0]
                    else:
                        first_flags = default_flags
    return samples, first_flags is not None and not bool(first_flags & 0x10000)


class VideoSubscription:
    def __init__(self, owner, next_sequence, initial):
        self.owner, self.next_sequence, self.initial = owner, next_sequence, initial
        self.closed = False

    def next_chunk(self, timeout=6):
        with self.owner.condition:
            if self.owner.mux_failed:
                raise ConnectionError('MSE fallback is unavailable')
            if self.initial:
                value, self.initial = self.initial, b''
                return value
            deadline = time.monotonic() + timeout
            while not self.closed:
                if self.owner.mux_failed:
                    raise ConnectionError('MSE fallback is unavailable')
                ring = self.owner.fragments
                if ring and self.next_sequence < ring[0][0]:
                    raise ConnectionError('Video subscriber fell behind')
                for sequence, data, _, _ in ring:
                    if sequence == self.next_sequence:
                        self.next_sequence += 1
                        return data
                if self.owner.closed:
                    return None
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError('Video stream stalled')
                self.owner.condition.wait(remaining)
        return None

    def close(self):
        with self.owner.condition:
            if not self.closed:
                self.closed = True
                self.owner.subscribers -= 1
                self.owner.condition.notify_all()


class FrameSubscription:
    """No reader-private queue: a slow reader is closed once replay evicts it."""
    def __init__(self, owner, next_sequence):
        self.owner, self.next_sequence = owner, next_sequence
        self.closed = False

    def next_chunk(self, timeout=6):
        with self.owner.condition:
            deadline = time.monotonic() + timeout
            while not self.closed:
                ring = self.owner.frames
                if ring and self.next_sequence < ring[0][0]:
                    raise ConnectionError('Video frame subscriber fell behind')
                for sequence, data, _, _ in ring:
                    if sequence == self.next_sequence:
                        self.next_sequence += 1
                        return data
                if self.owner.closed:
                    return None
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError('Video frame stream stalled')
                self.owner.condition.wait(remaining)
        return None

    def close(self):
        with self.owner.condition:
            if not self.closed:
                self.closed = True
                self.owner.frame_subscribers -= 1
                self.owner.condition.notify_all()


class NativeVideoReceiver:
    """One token, one native TCP sender, one ffmpeg, bounded shared replay ring."""
    def __init__(self, ffmpeg, *, bind_ip=PC, peer_ip=TV, allow_loopback_test=False):
        if (bind_ip, peer_ip) != (PC, TV) and not (allow_loopback_test and bind_ip == peer_ip == '127.0.0.1'):
            raise ValueError('Video endpoint is fixed')
        self.ffmpeg = str(ffmpeg)
        self.bind_ip, self.peer_ip = bind_ip, peer_ip
        self.token = secrets.token_hex(32)
        self.condition = threading.Condition()
        self.stop_event = threading.Event()
        self.listener = self.socket = self.process = None
        self.port = 0
        self.threads = []
        self.closed = False
        self.authenticated = False
        self.init = b''
        self.mime = None
        self.fragments = deque()
        self.fragment_bytes = 0
        self.sequence = self.samples = self.subscribers = 0
        self.counts = deque()
        self.started = time.monotonic()
        self.last_frame = 0.0
        self.error = ''
        self.stderr_tail = deque(maxlen=12)
        self.frames = deque()
        self.frame_bytes = self.frame_subscribers = self.native_samples = 0
        self.native_codec = None
        self.native_counts = deque()
        self.last_native_frame = 0.0
        self.mux_queue = deque()
        self.mux_queue_bytes = self.mux_inflight_bytes = 0
        self.mux_input_done = self.mux_failed = False

    def start(self):
        listener = socket.socket()
        if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        for _ in range(40):
            port = secrets.randbelow(16384) + 49152
            try:
                listener.bind((self.bind_ip, port))
                break
            except OSError:
                continue
        else:
            listener.close()
            raise RuntimeError('Could not create the TV video receive port')
        listener.listen(2)
        listener.settimeout(.5)
        self.listener, self.port = listener, port
        self._thread(self._receive, 'tv-native-receive')
        return self

    def _thread(self, target, name):
        thread = threading.Thread(target=target, name=name, daemon=True)
        self.threads.append(thread)
        thread.start()

    def _accept(self):
        deadline = time.monotonic() + 20
        attempts = 0
        while not self.stop_event.is_set() and time.monotonic() < deadline and attempts < 10:
            try:
                peer, address = self.listener.accept()
            except socket.timeout:
                continue
            attempts += 1
            if address[0] != self.peer_ip:
                peer.close()
                continue
            peer.settimeout(3)
            header = bytearray()
            try:
                while len(header) < 96:
                    value = peer.recv(1)
                    if not value:
                        break
                    header.extend(value)
                    if value == b'\n':
                        break
                if not hmac.compare_digest(bytes(header), ('TVCN2 ' + self.token + '\n').encode('ascii')):
                    peer.close()
                    continue
                peer.sendall(b'OK\n')
                peer.settimeout(6)
                self.socket, self.authenticated = peer, True
                self.token = ''  # Consumed; listener is closed before reading any video bytes.
                self.listener.close()
                self.listener = None
                return peer
            except OSError:
                peer.close()
        raise TimeoutError('No TV video connection received')

    def _heartbeat(self):
        while not self.stop_event.wait(2):
            try:
                self.socket.sendall(b'H')
            except OSError:
                self.stop_event.set()
                break

    def _stderr(self):
        # Never log raw remote/session paths or credentials. Keep diagnostics in memory only.
        try:
            # Read bounded chunks even if FFmpeg produces a very long line.
            # This drain is independent of both the native socket and mux writer.
            for chunk in iter(lambda: self.process.stderr.read(1024), b''):
                self.stderr_tail.append(chunk.decode('utf8', errors='replace')[:1000])
        except (OSError, ValueError):
            pass

    def _start_mux(self):
        command = [self.ffmpeg, '-hide_banner', '-loglevel', 'error', '-nostdin',
                   '-fflags', '+genpts', '-probesize', '32768', '-analyzeduration', '0',
                   '-framerate', '30', '-f', 'h264', '-i', 'pipe:0',
                   '-map', '0:v:0', '-c:v', 'copy', '-an',
                   '-movflags', '+empty_moov+default_base_moof+frag_keyframe',
                   '-frag_duration', '66666', '-flush_packets', '1', '-f', 'mp4', 'pipe:1']
        # 30 is only the native sender's requested clock, not a measurement.
        try:
            self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                            stderr=subprocess.PIPE, bufsize=0,
                                            creationflags=0x08000000 if os.name == 'nt' else 0)
        except OSError:
            self._disable_mux()
            return
        self._thread(self._stderr, 'tv-native-ffmpeg-errors')
        self._thread(self._mux_output, 'tv-native-fmp4')
        self._thread(self._mux_feed, 'tv-native-mux-feed')

    def _disable_mux(self):
        """A fallback failure must never close the native socket or heartbeat."""
        with self.condition:
            if self.mux_failed:
                return
            self.mux_failed = True
            self.mux_queue.clear()
            self.mux_queue_bytes = 0
            self.fragments.clear()
            self.fragment_bytes = 0
            self.init = b''
            self.condition.notify_all()
        # Terminating this owned child releases a blocked stdin writer. Do not
        # wait here: this may be called on the native receive/dispatch thread.
        try:
            if self.process and self.process.poll() is None:
                self.process.terminate()
        except OSError:
            pass

    def _queue_mux(self, data):
        with self.condition:
            if self.mux_failed or self.stop_event.is_set():
                return
            full = (len(self.mux_queue) + bool(self.mux_inflight_bytes) >= MAX_MUX_QUEUE_FRAMES or
                    self.mux_queue_bytes + self.mux_inflight_bytes + len(data) > MAX_MUX_QUEUE_BYTES)
            if not full:
                self.mux_queue.append(data)
                self.mux_queue_bytes += len(data)
                self.condition.notify_all()
                return
        self._disable_mux()

    def _mux_feed(self):
        try:
            while True:
                with self.condition:
                    while not self.mux_queue and not (self.stop_event.is_set() or self.mux_failed or self.mux_input_done):
                        self.condition.wait()
                    if self.stop_event.is_set() or self.mux_failed:
                        break
                    if not self.mux_queue:  # Native EOF: all queued AUs are drained.
                        break
                    data = self.mux_queue.popleft()
                    self.mux_queue_bytes -= len(data)
                    self.mux_inflight_bytes = len(data)
                remaining = memoryview(data)
                while remaining:
                    written = self.process.stdin.write(remaining)
                    if not written:
                        raise BrokenPipeError('Video remux stopped')
                    remaining = remaining[written:]
                with self.condition:
                    self.mux_inflight_bytes = 0
        except (OSError, ValueError):
            if not self.stop_event.is_set():
                self._disable_mux()
        finally:
            with self.condition:
                self.mux_inflight_bytes = 0
                self.condition.notify_all()
            # Only this thread writes/closes stdin, so shutdown cannot deadlock
            # trying to close a Windows pipe while another thread is writing it.
            try:
                self.process.stdin.close()
            except (OSError, ValueError):
                pass

    def _receive(self):
        try:
            peer = self._accept()
            self._thread(self._heartbeat, 'tv-native-heartbeat')
            self._start_mux()
            sequence = 0
            while not self.stop_event.is_set():
                header = self._read_socket_exact(peer, FRAME_HEADER.size)
                if header is None:
                    break
                magic, length, received_sequence, flags = FRAME_HEADER.unpack(header)
                if magic != b'TVF2' or not 0 < length <= MAX_AU or received_sequence != sequence or flags & ~1:
                    raise ValueError('Invalid native access-unit header')
                data = self._read_socket_exact(peer, length)
                if data is None:
                    raise ValueError('Missing native access unit')
                keyframe, standalone_key, codec = access_unit_info(data)
                if bool(flags & 1) != keyframe:
                    raise ValueError('Native access-unit key flag mismatch')
                # Publish immediately; direct consumers do not wait for the raw
                # demuxer's next-AU lookahead or MP4 fragment closure.
                self._publish_frame(header + data, sequence, standalone_key, codec)
                sequence += 1
                self._queue_mux(data)
            with self.condition:
                self.mux_input_done = True
                self.condition.notify_all()
            if self.process:
                try:
                    self.process.wait(2)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(2)
        except (OSError, ValueError, TimeoutError) as error:
            if not self.stop_event.is_set():
                self.error = 'TV video connection lost, retrying' if self.authenticated else 'Waiting for the TV video connection'
        finally:
            self.stop_event.set()
            for stream in (self.socket, self.listener):
                if stream:
                    try:
                        stream.close()
                    except OSError:
                        pass
            if self.process and self.process.poll() is None:
                self.process.kill()
                self.process.wait(2)
            with self.condition:
                self.closed = True
                self.condition.notify_all()

    @staticmethod
    def _read_socket_exact(peer, length):
        result = bytearray()
        while len(result) < length:
            piece = peer.recv(length - len(result))
            if not piece:
                if result:
                    raise ValueError('Truncated native access-unit frame')
                return None
            result.extend(piece)
        return bytes(result)

    @staticmethod
    def _read_exact(stream, length):
        result = bytearray()
        while len(result) < length:
            piece = stream.read(length - len(result))
            if not piece:
                if result:
                    raise ValueError('Truncated fMP4 output')
                return None
            result.extend(piece)
        return bytes(result)

    def _mux_output(self):
        init, pending = bytearray(), None
        try:
            while not self.stop_event.is_set():
                header = self._read_exact(self.process.stdout, 8)
                if header is None:
                    if not self.mux_input_done and not self.stop_event.is_set():
                        self._disable_mux()
                    break
                size, kind = struct.unpack('>I4s', header)
                if size < 8 or size > MAX_BOX:
                    raise ValueError('Invalid fMP4 output size')
                payload = self._read_exact(self.process.stdout, size - 8)
                if payload is None and size > 8:
                    raise ValueError('Truncated fMP4 output')
                box = header + (payload or b'')
                if kind in (b'ftyp', b'moov'):
                    init.extend(box)
                    if len(init) > 2 * 1024 * 1024:
                        raise ValueError('Oversized video initialization')
                    if kind == b'moov':
                        offset = box.find(b'avcC')
                        if offset < 4 or len(box) < offset + 8 or box[offset + 4] != 1:
                            raise ValueError('Missing AVC decoder configuration')
                        codec = box[offset + 5:offset + 8].hex()
                        with self.condition:
                            if not self.mux_failed:
                                self.init, self.mime = bytes(init), 'video/mp4; codecs="avc1.' + codec + '"'
                elif kind == b'moof':
                    pending = box
                elif kind == b'mdat' and pending:
                    count, keyframe = fragment_info(pending)
                    self._publish(pending + box, count, keyframe)
                    pending = None
        except (OSError, ValueError, struct.error):
            if not self.stop_event.is_set():
                self._disable_mux()

    def _publish(self, data, count, keyframe):
        now = time.monotonic()
        with self.condition:
            if self.mux_failed:
                return
            self.sequence += 1
            # Keep the previous GOP briefly for existing readers; late join starts at the newest key.
            self.fragments.append((self.sequence, data, keyframe, now))
            self.fragment_bytes += len(data)
            while self.fragments and (self.fragment_bytes > MAX_REPLAY or now - self.fragments[0][3] > 10):
                self.fragment_bytes -= len(self.fragments.popleft()[1])
            self.samples += count
            self.last_frame = now
            self.counts.append((now, self.samples))
            while len(self.counts) > 2 and now - self.counts[0][0] > 5:
                self.counts.popleft()
            self.condition.notify_all()

    def _publish_frame(self, data, sequence, standalone_key, codec):
        now = time.monotonic()
        with self.condition:
            if codec and self.native_codec and codec != self.native_codec:
                raise ValueError('Native video codec changed during session')
            if codec:
                self.native_codec = codec
            self.frames.append((sequence, data, standalone_key, now))
            self.frame_bytes += len(data)
            while self.frames and (len(self.frames) > MAX_REPLAY_FRAMES or self.frame_bytes > MAX_FRAME_REPLAY):
                self.frame_bytes -= len(self.frames.popleft()[1])
            self.native_samples += 1
            self.last_native_frame = now
            self.native_counts.append((now, self.native_samples))
            while len(self.native_counts) > 2 and now - self.native_counts[0][0] > 5:
                self.native_counts.popleft()
            self.condition.notify_all()

    def status(self):
        with self.condition:
            age = time.monotonic() - self.last_frame if self.last_frame else None
            mux_ready = bool(not self.closed and not self.mux_failed and self.init and self.fragments and age is not None and age < 4
                             and any(item[2] for item in self.fragments))
            native_age = time.monotonic() - self.last_native_frame if self.last_native_frame else None
            frames_ready = bool(not self.closed and self.native_codec and native_age is not None and native_age < 4
                                and any(item[2] for item in self.frames))
            ready = mux_ready or frames_ready
            counts = self.native_counts if self.native_samples else self.counts
            age = native_age if self.native_samples else age
            fps = 0.0
            if ready and len(counts) >= 2:
                seconds = counts[-1][0] - counts[0][0]
                if seconds >= .5:
                    fps = (counts[-1][1] - counts[0][1]) / seconds
            return {'videoConnected': ready, 'videoFps': round(fps, 1), 'fps': round(fps, 1),
                    'videoFramesReceived': self.native_samples or self.samples,
                    'videoFpsSource': 'receivedH264AccessUnits' if self.native_samples else 'receivedH264Samples',
                    'nativeFramesAvailable': frames_ready, 'nativeVideoCodec': self.native_codec,
                    'codec': self.native_codec, 'mseVideoAvailable': mux_ready,
                    'frameAgeSeconds': round(age, 2) if age is not None else None,
                    'width': 1920, 'height': 1080,
                    'videoState': 'Live TV picture connected' if ready else self.error or 'Connecting the live TV picture…'}

    def subscribe(self):
        with self.condition:
            if self.subscribers >= MAX_SUBSCRIBERS:
                raise RuntimeError('Too many video players')
            if self.closed or self.mux_failed or not self.init:
                raise RuntimeError('TV video not ready yet')
            starts = [item[0] for item in self.fragments if item[2]]
            if not starts:
                raise RuntimeError('Waiting for a complete TV video frame')
            self.subscribers += 1
            return VideoSubscription(self, starts[-1], self.init)

    def subscribe_frames(self):
        with self.condition:
            if self.frame_subscribers >= MAX_SUBSCRIBERS:
                raise RuntimeError('Too many video players')
            if self.closed or not self.native_codec:
                raise RuntimeError('TV video not ready yet')
            starts = [item[0] for item in self.frames if item[2]]
            if not starts:
                raise RuntimeError('Waiting for a complete TV video frame')
            self.frame_subscribers += 1
            return FrameSubscription(self, starts[-1])

    def stop(self):
        self.stop_event.set()
        with self.condition:
            self.mux_input_done = True
            self.mux_queue.clear()
            self.mux_queue_bytes = 0
            self.condition.notify_all()
        if self.socket:
            try:
                self.socket.sendall(b'S')
                self.socket.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
        if self.listener:
            self.listener.close()
        if self.process and self.process.poll() is None:
            # Closing a pipe that another thread is writing can deadlock on Windows.
            self.process.terminate()
        for thread in tuple(self.threads):
            if thread is not threading.current_thread():
                thread.join(2)
        if self.process:
            for pipe in (self.process.stdin, self.process.stdout, self.process.stderr):
                try:
                    pipe.close()
                except (OSError, ValueError):
                    pass
        with self.condition:
            self.closed = True
            self.fragments.clear()
            self.fragment_bytes = 0
            self.frames.clear()
            self.frame_bytes = 0
            self.condition.notify_all()


class NativeVideoController:
    def __init__(self, root):
        self.root = Path(root)
        self.receiver = None
        self.connection = None
        self.folder = None
        self.sender_pid = None
        self.sender_start = None
        self.retry_at = 0.0
        self.error = ''
        self.lock = asyncio.Lock()
        self.last_failure = None

    def status(self):
        if self.receiver:
            return self.receiver.status()
        return {'videoConnected': False, 'videoFps': 0, 'fps': 0, 'width': 1920, 'height': 1080,
                'nativeFramesAvailable': False, 'nativeVideoCodec': None, 'codec': None, 'mseVideoAvailable': False,
                'frameAgeSeconds': None, 'videoState': self.error or 'Connecting the live TV picture…'}

    async def ensure_connected(self, connection):
        if self.lock.locked():
            return
        async with self.lock:
            if self.receiver and not self.receiver.closed and not self.receiver.stop_event.is_set():
                return
            if time.monotonic() < self.retry_at:
                return
            if self.connection and self.connection is not connection:
                # Same fixed TV, new root-agent connection: use it to confirm the
                # retired sender is gone rather than retaining a dead RPC forever.
                self.connection = connection
            try:
                await self._stop()
                ffmpeg = shutil.which('ffmpeg')
                if not ffmpeg:
                    raise RuntimeError('FFmpeg is missing on this PC')
                output = self.root / 'tools/tv-native-stream/out'
                names = ('TvNativeStream.dll', 'TvNativeStream.deps.json', 'TvNativeStream.runtimeconfig.json')
                payloads = [(name, (output / name).read_bytes()) for name in names]
                self.connection = connection
                self.folder = '/run/samsung-tv-root/agent-' + str(int(connection.identity.pid)) + '/tv-native-stream'
                # RootAgent WRITEFILE deliberately uses FileMode.CreateNew. The
                # preceding _stop confirmed any retired sender exited; remove only
                # these exact owned deployment files before creating a replacement.
                owned_files = ' '.join(self.folder + '/' + name for name in (*names, 'session.json'))
                made = await connection.execute('mkdir -p ' + self.folder + ' && chmod 700 ' + self.folder +
                                                ' && rm -f ' + owned_files, 5)
                if made.exit_code != 0:
                    raise RuntimeError('Could not prepare the TV video channel')
                for name, data in payloads:
                    result = await connection.write_file(self.folder + '/' + name, data, 10)
                    if result.sha256 != hashlib.sha256(data).hexdigest():
                        raise RuntimeError('TV video component verification failed')
                receiver = NativeVideoReceiver(ffmpeg).start()
                self.receiver = receiver
                config = {'protocolVersion': 2, 'port': receiver.port, 'token': receiver.token,
                          'parentPid': int(connection.identity.pid), 'maxSessionSeconds': 14400}
                await connection.write_file(self.folder + '/session.json', json.dumps(config).encode('ascii'), 5)
                launch = ('chmod 600 ' + self.folder + '/session.json; rm -f ' + self.folder + '/stop ' + self.folder + '/cleanup.json; '
                          'unset LD_LIBRARY_PATH LD_PRELOAD GST_PLUGIN_PATH GST_PLUGIN_SYSTEM_PATH GST_PLUGIN_PATH_1_0 GST_PLUGIN_SYSTEM_PATH_1_0; '
                          '/usr/bin/dotnet ' + self.folder + '/TvNativeStream.dll --session ' + self.folder +
                          '/session.json >' + self.folder + '/sender.log 2>&1 </dev/null & echo $!')
                result = await connection.execute(launch, 5)
                if result.exit_code != 0 or not result.stdout.strip().isdigit():
                    raise RuntimeError('TV video failed to start')
                self.sender_pid = int(result.stdout.strip())
                # This TV's root-agent shell argument quoting doubles backslashes.
                # Base64 preserves NUL-separated argv without shell escape sequences.
                identity = await connection.execute('cat /proc/' + str(self.sender_pid) + '/stat; base64 </proc/' + str(self.sender_pid) + '/cmdline', 3)
                lines = identity.stdout.splitlines()
                fields = lines[0].rsplit(')', 1)[-1].strip().split() if lines else []
                expected = ['/usr/bin/dotnet', self.folder + '/TvNativeStream.dll', '--session', self.folder + '/session.json']
                try:
                    argv = base64.b64decode(''.join(lines[1:]), validate=True).decode('utf8').rstrip('\0').split('\0')
                except (ValueError, UnicodeError):
                    argv = []
                if identity.exit_code != 0 or len(fields) < 20 or argv != expected:
                    self.last_failure = {'stage': 'sender-identity', 'exitCode': identity.exit_code,
                                         'statFields': len(fields), 'argv': argv, 'stderr': identity.stderr[-1000:]}
                    raise RuntimeError('Could not verify the TV video process identity')
                self.sender_start = fields[19]
                self.error = ''
            except Exception as error:
                self.error = str(error) if isinstance(error, RuntimeError) else 'TV video temporarily unavailable, retrying'
                original_error = self.error
                await self._record_failure(connection, error)
                try:
                    await self._stop()
                except RuntimeError as stop_error:
                    # Preserve the initiating error; cleanup failures are secondary.
                    self.error = original_error + '; ' + str(stop_error)
                self.retry_at = time.monotonic() + 5

    async def _record_failure(self, connection, error):
        receipt = {'stage': 'native-video-start', 'errorType': type(error).__name__, 'message': str(error)[:500],
                   'senderPid': self.sender_pid, 'senderStart': self.sender_start, 'identity': self.last_failure}
        if self.folder:
            try:
                # Missing READFILE terminates the root-agent; bounded shell tail
                # with an existence guard cannot invalidate it on a missing log.
                result = await connection.execute('if test -f ' + self.folder + '/sender.log; then tail -c 8192 ' +
                                                  self.folder + '/sender.log; fi', 3)
                receipt['senderLogTail'] = result.stdout[-8192:]
            except Exception as diagnostic_error:
                receipt['logReadError'] = type(diagnostic_error).__name__
        try:
            target = self.root / 'diagnostics/remote-view/native-video-last-error.json'
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding='utf8')
        except OSError:
            pass

    async def _stop(self):
        receiver, self.receiver = self.receiver, None
        if receiver:
            await asyncio.to_thread(receiver.stop)
        connection, folder = self.connection, self.folder
        if connection and folder:
            try:
                # A fixed stop file is fallback only. Never kill a process by name/PID.
                await connection.execute('touch ' + folder + '/stop; rm -f ' + folder + '/session.json', 3)
                if self.sender_pid:
                    deadline = time.monotonic() + 3
                    while time.monotonic() < deadline:
                        result = await connection.execute('if test -d /proc/' + str(self.sender_pid) +
                                                          '; then cat /proc/' + str(self.sender_pid) + '/stat; else echo GONE; fi', 1)
                        # Zombies have exited native code; their file locks are already released.
                        state = result.stdout.rsplit(')', 1)[-1].strip().split()
                        replaced = bool(self.sender_start and len(state) >= 20 and state[19] != self.sender_start)
                        if result.stdout.strip() == 'GONE' or replaced or (state and state[0] == 'Z'):
                            break
                        await asyncio.sleep(.25)
                    else:
                        raise RuntimeError('TV video is still stopping; retry after resources are released')
            except RuntimeError:
                raise
            except Exception:
                # Keep the retired identity: an unavailable root connection is not proof
                # that its hardware cleanup completed. The sender also owns a global flock.
                raise RuntimeError('Confirming the TV video channel has stopped')
        self.connection = self.folder = self.sender_pid = self.sender_start = None

    async def stop(self):
        async with self.lock:
            await self._stop()
            self.retry_at = 0

    def subscribe(self):
        receiver = self.receiver
        if receiver is None:
            raise RuntimeError('TV video not ready yet')
        return receiver.subscribe(), receiver.mime

    def subscribe_frames(self):
        receiver = self.receiver
        if receiver is None:
            raise RuntimeError('TV video not ready yet')
        return receiver.subscribe_frames(), receiver.native_codec
