namespace TVCitation.Control;

internal static class PlayerInputBridge
{
    internal const string Script = """
        (() => {
          if (location.pathname !== '/player' || window.__tvcitationInputBridge) return;
          window.__tvcitationInputBridge = true;
          let connected = false, pointerEnabled = false, pressed = null, lastPoint = null, lastMove = 0;
          let pressId = null, nextPressId = 0, captureTarget = null, capturePointerId = null;
          const send = message => window.chrome.webview.postMessage(message);
          window.chrome.webview.addEventListener('message', event => {
            const message = event.data;
            if (message && message.type === 'inputState') {
              if (message.connected !== true || message.pointerEnabled !== true) release();
              connected = message.connected === true;
              pointerEnabled = message.pointerEnabled === true;
            }
          });
          const keys = {
            ArrowUp:'KEY_UP', ArrowDown:'KEY_DOWN', ArrowLeft:'KEY_LEFT', ArrowRight:'KEY_RIGHT',
            Enter:'KEY_ENTER', Escape:'KEY_RETURN', Backspace:'KEY_RETURN', Home:'KEY_HOME',
            ' ':'KEY_PLAYPAUSE', '+':'KEY_VOLUP', '-':'KEY_VOLDOWN', m:'KEY_MUTE', M:'KEY_MUTE'
          };
          document.addEventListener('keydown', event => {
            if (!connected || !event.isTrusted || !document.hasFocus()) return;
            if (event.target instanceof Element && event.target.closest('input,textarea,select,[contenteditable="true"]')) return;
            if (event.ctrlKey || event.altKey || event.metaKey) return;
            const key = keys[event.key];
            if (!key) return;
            event.preventDefault(); event.stopPropagation();
            send({type:'remote', key});
          }, true);
          const position = (event, clamp = false) => {
            if (!event || !Number.isFinite(event.clientX) || !Number.isFinite(event.clientY)) return null;
            const surface = window.__tvcitationVideoSurface;
            if (!surface?.ready || !surface.element || !surface.width || !surface.height) return null;
            const bounds = surface.element.getBoundingClientRect();
            const scale = Math.min(bounds.width / surface.width, bounds.height / surface.height);
            const width = surface.width * scale, height = surface.height * scale;
            if (!width || !height) return null;
            const left = bounds.left + (bounds.width - width) / 2, top = bounds.top + (bounds.height - height) / 2;
            const x = (event.clientX - left) / width, y = (event.clientY - top) / height;
            if (!clamp && (x < 0 || x >= 1 || y < 0 || y >= 1)) return null;
            return { x:Math.max(0, Math.min(1919, Math.floor(x * 1920))), y:Math.max(0, Math.min(1079, Math.floor(y * 1080))) };
          };
          // Read-only geometry helper also used by the owned local input-mapping check.
          window.__tvcitationMapPointer = position;
          const available = event => connected && pointerEnabled && event.isTrusted && document.hasFocus();
          const buttonName = button => button === 2 ? 'right' : button === 1 ? 'middle' : 'left';
          // 32ms throttle with a trailing send, so the resting position always arrives.
          // down/up cancel the trailing move: they carry their own coordinate.
          let moveTimer = null, queuedMove = null;
          const cancelMove = () => { clearTimeout(moveTimer); moveTimer = null; queuedMove = null; };
          const sendMove = point => {
            lastMove = performance.now(); lastPoint = point;
            send({type:'pointer',action:'move',...point,button:pressed || 'left',delta:0});
          };
          const flushMove = () => {
            const point = queuedMove; moveTimer = null; queuedMove = null;
            if (point && connected && pointerEnabled && document.hasFocus()) sendMove(point);
          };
          document.addEventListener('pointermove', event => {
            if (!available(event)) return;
            const point = position(event, pressed !== null); if (!point) return;
            const wait = 32 - (performance.now() - lastMove);
            if (wait <= 0) { cancelMove(); sendMove(point); return; }
            queuedMove = point;
            if (moveTimer === null) moveTimer = setTimeout(flushMove, wait);
          }, true);
          document.addEventListener('pointerdown', event => {
            if (!available(event) || event.button !== 0 || pressed !== null) return;
            const point = position(event); if (!point) return;
            cancelMove(); pressed = buttonName(event.button); lastPoint = point; pressId = ++nextPressId;
            captureTarget = event.target; capturePointerId = event.pointerId;
            event.preventDefault();
            try { event.target.setPointerCapture(event.pointerId); } catch {}
            send({type:'pointer',action:'down',...point,button:pressed,pressId,delta:0});
          }, true);
          const release = event => {
            cancelMove();
            if (pressed === null) return;
            const button = pressed, releasedId = pressId, target = captureTarget, pointerId = capturePointerId;
            // Clear before releasing capture: lostpointercapture can fire reentrantly.
            pressed = null; pressId = null; captureTarget = null; capturePointerId = null;
            const point = position(event, true) || lastPoint;
            try { target?.releasePointerCapture(pointerId); } catch {}
            if (!point) return;
            send({type:'pointer',action:'up',...point,button,pressId:releasedId,delta:0});
          };
          document.addEventListener('pointerup', release, true);
          document.addEventListener('pointercancel', () => release(), true);
          document.addEventListener('lostpointercapture', () => release(), true);
          window.addEventListener('blur', () => release());
          document.addEventListener('visibilitychange', () => { if (document.visibilityState !== 'visible') release(); });
          window.addEventListener('pagehide', () => release(), { once:true });
          document.addEventListener('contextmenu', event => { if (pointerEnabled) event.preventDefault(); }, true);
          // player.js owns FPS reporting for both actual canvas draws and MSE
          // presented-frame deltas; no hidden-video zero counter competes here.
        })();
        """;
}
