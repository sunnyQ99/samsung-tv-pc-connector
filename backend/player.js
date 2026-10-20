(() => {
  'use strict';
  const video = document.getElementById('tv');
  const canvas = document.getElementById('direct');
  // This channel carries video only. Explicit muting satisfies the default
  // browser autoplay policy without changing Windows or PC-to-TV audio.
  video.defaultMuted = true;
  video.muted = true;
  const overlay = document.getElementById('status');
  const title = document.getElementById('title');
  const detail = document.getElementById('detail');
  const KEEP_SECONDS = 9, MAX_LAG = 0.30, LIVE_MARGIN = 0.10;
  let active = null, stopped = false, statusTimer = null, statusRequest = null;
  let retryAt = 0, retryDelay = 500;
  let directRetryAt = 0, fallbackReason = null;
  window.__tvcitationVideoSurface = { ready: false, element: null, width: 0, height: 0, renderer: 'none' };
  const abortError = () => new DOMException('Video connection cancelled', 'AbortError');
  const bridge = value => window.chrome?.webview?.postMessage(value);
  const show = message => { overlay.style.display = 'flex'; if (message) detail.textContent = message; };

  // Local diagnostics only: buffer lag is not TV-to-display or click-to-display latency.
  const lagSamples = [], STATS_SAMPLE_LIMIT = 60, STATS_INTERVAL_MS = 1000;
  const frameCallbackSupported = typeof video.requestVideoFrameCallback === 'function';
  let waitingCount = 0, seekingCount = 0, presentedFrames = 0, frameCallbacks = 0;
  let previousPresented = null, lastFrameAt = null, frameCallbackId = null;
  let decodedFrames = 0, drawnFrames = 0, droppedFrames = 0, resyncCount = 0;
  let lastStatsAt = performance.now(), lastDecoded = 0, lastDrawn = 0, lastPresented = 0;
  const decodeLatencies = [];
  const milliseconds = value => value === null ? null : Math.round(value * 10) / 10;
  function playbackStats(sample = true) {
    let bufferLagMs = null;
    const ranges = active && !active.controller.signal.aborted ? active.buffer?.buffered : null;
    if (ranges?.length && video.readyState >= 1) {
      const lag = (ranges.end(ranges.length - 1) - video.currentTime) * 1000;
      if (Number.isFinite(lag)) bufferLagMs = Math.max(0, lag);
    }
    if (sample && bufferLagMs !== null) {
      lagSamples.push(bufferLagMs);
      if (lagSamples.length > STATS_SAMPLE_LIMIT) lagSamples.shift();
    }
    const sorted = [...lagSamples].sort((a, b) => a - b);
    const percentile = fraction => sorted.length ? sorted[Math.ceil(sorted.length * fraction) - 1] : null;
    const now = performance.now();
    const direct = active?.kind === 'webcodecs-software';
    const span = Math.max(1, now - lastStatsAt);
    const decoderFps = (decodedFrames - lastDecoded) * 1000 / span;
    const drawFps = (drawnFrames - lastDrawn) * 1000 / span;
    const mseFps = (presentedFrames - lastPresented) * 1000 / span;
    const decodeSorted = [...decodeLatencies].sort((a, b) => a - b);
    const decodePercentile = p => decodeSorted.length ? milliseconds(decodeSorted[Math.ceil(decodeSorted.length * p) - 1]) : null;
    const value = {
      renderer: active?.kind || 'none', fallbackReason,
      decodedFps: direct ? milliseconds(decoderFps) : null,
      drawnFps: direct ? milliseconds(drawFps) : null,
      decodedFrames, drawnFrames, droppedFrames, resyncCount,
      decoderQueueSize: active?.decoder?.state === 'configured' ? active.decoder.decodeQueueSize : 0,
      pendingDecodeFrames: active?.pending?.size || 0,
      decodeLatencyP50Ms: decodePercentile(0.5), decodeLatencyP95Ms: decodePercentile(0.95),
      decoderLatencyMeaning: 'decode submission to output callback; not TV-to-screen latency',
      bufferLagMs: milliseconds(bufferLagMs), bufferLagP50Ms: milliseconds(percentile(0.50)),
      bufferLagP95Ms: milliseconds(percentile(0.95)), bufferLagMaxMs: milliseconds(sorted.at(-1) ?? null),
      bufferLagSamples: sorted.length, sampleLimit: STATS_SAMPLE_LIMIT, sampleIntervalMs: STATS_INTERVAL_MS,
      waitingCount, seekingCount, presentedFrames: direct ? null : presentedFrames, frameCallbacks, frameCallbackSupported,
      paused: direct ? !window.__tvcitationVideoSurface.ready : video.paused,
      readyState: direct ? (window.__tvcitationVideoSurface.ready ? 4 : 0) : video.readyState, playbackRate: direct ? 1 : video.playbackRate,
      lastFrameAgeMs: lastFrameAt === null ? null : milliseconds(Math.max(0, now - lastFrameAt)),
      updatedAtPerformanceMs: milliseconds(now)
    };
    window.__tvcitationPlaybackStats = value;
    if (sample) {
      // One authoritative FPS publisher. The input bridge must not overwrite canvas FPS with a hidden video's zero.
      bridge({ type: 'videoStats', renderer: value.renderer, fps: milliseconds(direct ? drawFps : mseFps),
        decodedFps: value.decodedFps, drawnFps: value.drawnFps });
      lastStatsAt = now; lastDecoded = decodedFrames; lastDrawn = drawnFrames; lastPresented = presentedFrames;
    }
    return value;
  }
  function countPresentedFrame(now, metadata) {
    if (stopped) return;
    if (active?.kind !== 'mse') { frameCallbackId = video.requestVideoFrameCallback(countPresentedFrame); return; }
    frameCallbacks++;
    const count = metadata.presentedFrames;
    if (Number.isFinite(count) && count >= 0) {
      // Count frames skipped between JS callbacks; tolerate a browser counter reset on a new source.
      presentedFrames += previousPresented === null || count < previousPresented ? count : count - previousPresented;
      previousPresented = count;
    }
    lastFrameAt = now;
    frameCallbackId = video.requestVideoFrameCallback(countPresentedFrame);
  }
  playbackStats(false);
  video.addEventListener('emptied', () => { previousPresented = null; });
  if (frameCallbackSupported) frameCallbackId = video.requestVideoFrameCallback(countPresentedFrame);
  const statsTimer = setInterval(() => bridge({ type: 'videoPerf', ...playbackStats() }), STATS_INTERVAL_MS);

  // Register before invoking append/remove so even immediate failures release listeners.
  function eventOperation(target, eventName, signal, operation, timeout = 6000) {
    return new Promise((resolve, reject) => {
      if (signal.aborted) { reject(abortError()); return; }
      let timer;
      const finish = error => {
        clearTimeout(timer);
        target.removeEventListener(eventName, done);
        target.removeEventListener('error', failed);
        target.removeEventListener('abort', failed);
        signal.removeEventListener('abort', cancelled);
        error ? reject(error) : resolve();
      };
      const done = () => finish();
      const failed = () => finish(Error('Video buffer operation failed'));
      const cancelled = () => finish(abortError());
      target.addEventListener(eventName, done);
      target.addEventListener('error', failed);
      target.addEventListener('abort', failed);
      signal.addEventListener('abort', cancelled, { once: true });
      timer = setTimeout(() => finish(Error('Video buffer operation timed out')), timeout);
      try { operation?.(); } catch (error) { finish(error); }
    });
  }

  function catchUp(session) {
    if (active !== session || session.controller.signal.aborted || !session.buffer?.buffered.length || video.readyState < 1) return;
    const ranges = session.buffer.buffered;
    const end = ranges.end(ranges.length - 1), start = ranges.start(ranges.length - 1);
    // This is buffer latency, not a claimed camera/TV-to-screen end-to-end latency.
    if (!video.seeking && (end - video.currentTime > MAX_LAG || video.currentTime < start)) {
      video.currentTime = Math.max(start, end - LIVE_MARGIN);
    }
  }

  async function trim(session, force = false) {
    const buffer = session.buffer, signal = session.controller.signal;
    if (buffer.updating) await eventOperation(buffer, 'updateend', signal);
    catchUp(session);
    if (!buffer.buffered.length) return;
    const end = buffer.buffered.end(buffer.buffered.length - 1);
    const start = buffer.buffered.start(0);
    const cutoff = end - (force ? 8 : KEEP_SECONDS);
    if (cutoff > start + (force ? 0.05 : 0.5)) {
      await eventOperation(buffer, 'updateend', signal, () => buffer.remove(0, cutoff));
    }
  }

  function requestPlay(session) {
    if (active !== session || session.controller.signal.aborted || !video.paused || session.playPending) return;
    session.playPending = true;
    video.play().catch(error => {
      if (active === session && !session.controller.signal.aborted && error.name === 'NotAllowedError') show('Click the picture to start playback');
    }).finally(() => { session.playPending = false; });
  }

  function stopStream() {
    if (active) active.controller.abort();
    window.__tvcitationVideoSurface.ready = false;
    show();
  }

  function surfaceReady(session, element, width, height) {
    if (active !== session || session.controller.signal.aborted) return;
    window.__tvcitationVideoSurface = { ready: true, element, width, height, renderer: session.kind };
    canvas.hidden = element !== canvas; video.hidden = element !== video;
    overlay.style.display = 'none'; retryDelay = 500;
    if (!session.playing) {
      session.playing = true;
      bridge({ type: 'videoPlaying', renderer: session.kind, width, height });
    }
  }

  function annexTypes(bytes) {
    const types = new Set();
    for (let i = 0; i + 3 < bytes.length; i++) {
      if (bytes[i] !== 0 || bytes[i + 1] !== 0) continue;
      const head = bytes[i + 2] === 1 ? i + 3 : bytes[i + 2] === 0 && bytes[i + 3] === 1 ? i + 4 : -1;
      if (head > 0 && head < bytes.length) { types.add(bytes[head] & 31); i = head; }
    }
    return types;
  }

  async function runDirect(session, armIdle) {
    const signal = session.controller.signal;
    const response = await fetch('/video-frames', { signal, cache: 'no-store' });
    if (!response.ok || !response.body || response.headers.get('Content-Type')?.split(';')[0] !== 'application/x-tvc-au')
      throw Error('Native video frames are not available yet');
    session.reader = response.body.getReader();
    const codec = response.headers.get('X-Video-Codec');
    const width = Number(response.headers.get('X-Video-Width')), height = Number(response.headers.get('X-Video-Height'));
    if (!/^avc1\.[a-f0-9]{6}$/i.test(codec || '') || width !== 1920 || height !== 1080) throw Error('Unexpected native video format');
    const support = await VideoDecoder.isConfigSupported({ codec, codedWidth: width, codedHeight: height,
      optimizeForLatency: true, hardwareAcceleration: 'prefer-software' });
    if (!support.supported) throw Error('This PC does not support low-latency video decoding');
    if (signal.aborted) throw abortError();
    canvas.width = width; canvas.height = height;
    const context = canvas.getContext('2d', { desynchronized: true, alpha: false });
    if (!context) throw Error('Could not create the video canvas');
    session.pending = new Map(); session.awaitKey = true;
    const fail = error => { session.failure = error; session.controller.abort(); };
    const reset = () => {
      droppedFrames += session.pending.size; session.pending.clear();
      session.decoder.reset(); session.decoder.configure(support.config);
      session.awaitKey = true; resyncCount++;
      window.__tvcitationVideoSurface.ready = false;
      show('Recovering the live picture');
    };
    session.decoder = new VideoDecoder({
      output: frame => {
        try {
          if (active !== session || signal.aborted) return;
          const submitted = session.pending.get(frame.timestamp);
          session.pending.delete(frame.timestamp);
          if (submitted === undefined) { droppedFrames++; return; }
          const now = performance.now(), latency = now - submitted;
          decodedFrames++; decodeLatencies.push(latency);
          if (decodeLatencies.length > STATS_SAMPLE_LIMIT) decodeLatencies.shift();
          if (latency > 250) { droppedFrames++; reset(); return; }
          if (frame.displayWidth !== width || frame.displayHeight !== height) throw Error('Decoded frame size mismatch');
          context.drawImage(frame, 0, 0, width, height);
          drawnFrames++; lastFrameAt = now; session.lastDrawAt = now;
          surfaceReady(session, canvas, width, height);
        } catch (error) { fail(error); }
        finally { frame.close(); }
      }, error: fail
    });
    session.decoder.configure(support.config);
    const header = new Uint8Array(16);
    let headerUsed = 0, payload = null, payloadUsed = 0, sequence = null, flags = 0, previousSequence = null;
    const consume = () => {
      const key = (flags & 1) !== 0, types = annexTypes(payload);
      if (!types.has(1) && !types.has(5)) throw Error('Native video frame has no picture data');
      if (key !== types.has(5) || (key && (!types.has(7) || !types.has(8)))) throw Error('Native keyframe is missing parameter sets');
      if (session.pending.size >= 6 || session.decoder.decodeQueueSize >= 4) reset();
      if (session.awaitKey && !key) { droppedFrames++; return; }
      session.awaitKey = false;
      const timestamp = Math.round(sequence * 1000000 / 30);
      session.pending.set(timestamp, performance.now());
      session.decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp, data: payload }));
    };
    while (!signal.aborted) {
      const item = await session.reader.read();
      if (item.done) throw Error('Native video stream disconnected');
      if (!item.value?.byteLength) continue;
      armIdle();
      let offset = 0;
      while (offset < item.value.length) {
        if (headerUsed < 16) {
          const count = Math.min(16 - headerUsed, item.value.length - offset);
          header.set(item.value.subarray(offset, offset + count), headerUsed); headerUsed += count; offset += count;
          if (headerUsed < 16) continue;
          const view = new DataView(header.buffer);
          if (view.getUint32(0) !== 0x54564632) throw Error('Invalid native video frame header');
          const length = view.getUint32(4); sequence = view.getUint32(8); flags = view.getUint32(12);
          if (!length || length > 1048576 || flags > 1) throw Error('Invalid native video frame length or flags');
          if (previousSequence !== null && sequence !== ((previousSequence + 1) >>> 0)) throw Error('Native video frame sequence gap');
          if (previousSequence === null && flags !== 1) throw Error('First native video frame is not a keyframe');
          previousSequence = sequence; payload = new Uint8Array(length); payloadUsed = 0;
        }
        const count = Math.min(payload.length - payloadUsed, item.value.length - offset);
        payload.set(item.value.subarray(offset, offset + count), payloadUsed); payloadUsed += count; offset += count;
        if (payloadUsed === payload.length) { consume(); payload = null; headerUsed = 0; }
      }
    }
  }

  async function stream(state) {
    if (active || stopped) return;
    const direct = state.nativeFramesAvailable === true && typeof VideoDecoder === 'function' &&
      typeof EncodedVideoChunk === 'function' && performance.now() >= directRetryAt;
    const session = { kind: direct ? 'webcodecs-software' : 'mse', controller: new AbortController(), reader: null,
      source: null, buffer: null, url: null, decoder: null, idleTimer: null, playPending: false,
      startedAt: performance.now(), lastDrawAt: null, playing: false, failure: null };
    active = session;
    const signal = session.controller.signal;
    const armIdle = () => {
      clearTimeout(session.idleTimer);
      session.idleTimer = setTimeout(() => { session.failure = Error('Video stream interrupted, reconnecting'); show(session.failure.message); session.controller.abort(); }, 6000);
    };
    show('Connecting to the TV video stream');
    try {
      armIdle();
      if (direct) { await runDirect(session, armIdle); return; }
      const response = await fetch('/video', { signal, cache: 'no-store' });
      if (!response.ok || !response.body) throw Error('Video stream not ready yet');
      session.reader = response.body.getReader();
      const mime = response.headers.get('X-Video-Mime') || 'video/mp4; codecs="avc1.64002a"';
      if (!window.MediaSource || !MediaSource.isTypeSupported(mime)) throw Error('This video codec is not supported');
      session.source = new MediaSource();
      session.url = URL.createObjectURL(session.source);
      await eventOperation(session.source, 'sourceopen', signal, () => { video.src = session.url; });
      session.buffer = session.source.addSourceBuffer(mime);
      while (!signal.aborted) {
        const item = await session.reader.read();
        if (item.done) throw Error('Video stream disconnected, reconnecting');
        if (!item.value?.byteLength) continue;
        armIdle();
        await trim(session);
        try { await eventOperation(session.buffer, 'updateend', signal, () => session.buffer.appendBuffer(item.value)); }
        catch (error) {
          if (error.name !== 'QuotaExceededError') throw error;
          await trim(session, true);
          await eventOperation(session.buffer, 'updateend', signal, () => session.buffer.appendBuffer(item.value));
        }
        catchUp(session);
        await trim(session);
        requestPlay(session);
      }
    } catch (caught) {
      const error = session.failure || caught;
      if (direct && error.name !== 'AbortError') { directRetryAt = performance.now() + 30000; fallbackReason = error.message; }
      if (active === session && error.name !== 'AbortError') show(error.message);
    } finally {
      clearTimeout(session.idleTimer);
      session.controller.abort();
      if (session.decoder?.state !== 'closed') { try { session.decoder?.close(); } catch {} }
      session.pending?.clear();
      try { if (session.source?.readyState === 'open' && session.buffer?.updating) session.buffer.abort(); } catch {}
      if (active === session) {
        window.__tvcitationVideoSurface.ready = false;
        video.pause(); video.removeAttribute('src'); video.load(); canvas.hidden = true; video.hidden = true; show();
      }
      if (session.url) URL.revokeObjectURL(session.url);
      if (session.reader) {
        let cancelTimer;
        try {
          await Promise.race([session.reader.cancel().catch(() => {}), new Promise(resolve => { cancelTimer = setTimeout(resolve, 1000); })]);
        } finally { clearTimeout(cancelTimer); try { session.reader.releaseLock(); } catch {} }
      }
      if (active === session) {
        active = null;
        retryAt = performance.now() + retryDelay;
        retryDelay = Math.min(retryDelay * 2, 4000);
      }
    }
  }

  video.addEventListener('playing', () => {
    if (!active || active.kind !== 'mse' || active.controller.signal.aborted || !video.videoWidth) return;
    surfaceReady(active, video, video.videoWidth, video.videoHeight);
  });
  video.addEventListener('waiting', () => {
    if (active?.kind === 'mse' && !active.controller.signal.aborted) { waitingCount++; show('Video is buffering'); }
  });
  video.addEventListener('seeking', () => { if (active && !active.controller.signal.aborted) seekingCount++; });
  video.addEventListener('error', () => { if (active) { show('Video decoding interrupted, reconnecting'); active.controller.abort(); } });
  video.addEventListener('ended', stopStream);
  overlay.addEventListener('click', () => { if (active) requestPlay(active); });
  const liveTimer = setInterval(() => {
    if (active?.kind === 'mse') catchUp(active);
    if (active?.kind === 'webcodecs-software' && performance.now() - (active.lastDrawAt ?? active.startedAt) > 5000) {
      active.failure = Error('The video decoder stopped producing frames'); active.controller.abort();
    }
  }, 250);

  async function pollStatus() {
    if (stopped || statusRequest) return;
    const request = new AbortController();
    statusRequest = request;
    const timeout = setTimeout(() => request.abort(), 4000);
    try {
      const response = await fetch('/status', { signal: request.signal, cache: 'no-store' });
      if (!response.ok) throw Error('Local service unavailable');
      const state = await response.json();
      if (stopped) return;
      title.textContent = state.controlConnected ? 'Live TV picture' : 'TV not connected';
      detail.textContent = state.controlConnected ? (state.videoState || state.message || 'Waiting for the video stream') : (state.message || 'Waiting for the TV to power on');
      if (state.videoConnected || state.nativeFramesAvailable) {
        if (!active && performance.now() >= retryAt) void stream(state);
      } else stopStream();
    } catch {
      if (!stopped) { title.textContent = 'Connecting to the local service'; show('Waiting for the local service'); stopStream(); }
    } finally {
      clearTimeout(timeout);
      if (statusRequest === request) statusRequest = null;
      if (!stopped) statusTimer = setTimeout(pollStatus, 1000);
    }
  }
  window.addEventListener('pagehide', () => {
    stopped = true;
    clearTimeout(statusTimer);
    clearInterval(liveTimer);
    clearInterval(statsTimer);
    if (frameCallbackId !== null && typeof video.cancelVideoFrameCallback === 'function') video.cancelVideoFrameCallback(frameCallbackId);
    playbackStats(false);
    statusRequest?.abort();
    stopStream();
  }, { once: true });
  void pollStatus();
})();
