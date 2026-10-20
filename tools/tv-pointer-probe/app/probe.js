(function () {
  'use strict';
  var events = window.tvcInputEvents = [];
  var state = window.tvcProbeState = {
    schemaVersion: 1, packageId: 'TVCPtrT001', buildId: '__BUILD_ID__',
    pointingDeviceSupport: 'enable', maxEvents: 100, trustedReceived: 0,
    untrustedIgnored: 0, moveEventsCoalesced: 0, uiTicks: 0, networkUsed: false,
    sourceFrameRateVerified: false, startedUtc: new Date().toISOString()
  };
  var start = performance.now(), lastMove = {}, active = true, frameHandle = null;
  var counter = document.getElementById('counter'), elapsed = document.getElementById('elapsed');
  var marker = document.getElementById('marker'), moving = document.getElementById('moving');
  var safeKeys = {13:true,27:true,32:true,37:true,38:true,39:true,40:true,10009:true};

  function finite(value) { return typeof value === 'number' && isFinite(value) ? Math.round(value * 100) / 100 : null; }
  function record(event) {
    if (!event.isTrusted) { state.untrustedIgnored++; return; }
    if ((event.type === 'keydown' || event.type === 'keyup') && !safeKeys[event.keyCode]) return;
    state.trustedReceived++;
    var now = performance.now();
    if (/move$/.test(event.type)) {
      if (lastMove[event.type] !== undefined && now - lastMove[event.type] < 50) { state.moveEventsCoalesced++; return; }
      lastMove[event.type] = now;
    }
    var entry = {
      sequence: state.trustedReceived, atMs: finite(now - start), type: event.type, trusted: true,
      x: finite(event.clientX), y: finite(event.clientY), screenX: finite(event.screenX), screenY: finite(event.screenY),
      button: finite(event.button), buttons: finite(event.buttons), deltaY: finite(event.deltaY),
      keyCode: safeKeys[event.keyCode] ? event.keyCode : null,
      pointerType: ['mouse','touch','pen'].indexOf(event.pointerType) >= 0 ? event.pointerType : null,
      targetId: event.target && ['red','green','blue','white','track','moving','stage'].indexOf(event.target.id) >= 0 ? event.target.id : null
    };
    events.push(entry);
    if (events.length > 100) events.splice(0, events.length - 100);
    document.getElementById('eventCount').textContent = String(state.trustedReceived);
    document.getElementById('lastEvent').textContent = event.type + (entry.x === null ? '' : '  ' + entry.x + ', ' + entry.y);
    if (entry.x !== null && entry.y !== null) {
      marker.style.display = 'block'; marker.style.left = entry.x + 'px'; marker.style.top = entry.y + 'px';
    }
    if (event.type === 'click' && entry.targetId && ['red','green','blue','white'].indexOf(entry.targetId) >= 0) {
      var buttons = document.querySelectorAll('.swatches button');
      for (var i = 0; i < buttons.length; i++) buttons[i].classList.remove('selected');
      event.target.classList.add('selected');
    }
    if (event.type === 'contextmenu') event.preventDefault();
  }
  ['pointermove','pointerdown','pointerup','mousemove','mousedown','mouseup','click','dblclick','contextmenu','wheel','keydown','keyup'].forEach(function (name) {
    document.addEventListener(name, record, true);
  });
  function tick(now) {
    if (!active) return;
    state.uiTicks++;
    state.elapsedMs = finite(now - start);
    counter.textContent = ('00000000' + state.uiTicks).slice(-8);
    elapsed.textContent = ((now - start) / 1000).toFixed(1) + ' s';
    var width = Math.max(0, document.getElementById('track').clientWidth - 138);
    var phase = ((now - start) % 5000) / 2500;
    moving.style.left = Math.round(width * (phase <= 1 ? phase : 2 - phase)) + 'px';
    frameHandle = requestAnimationFrame(tick);
  }
  document.addEventListener('visibilitychange', function () { state.visibility = document.visibilityState; });
  window.addEventListener('pagehide', function () { active = false; if (frameHandle !== null) cancelAnimationFrame(frameHandle); }, {once:true});
  document.getElementById('red').focus();
  frameHandle = requestAnimationFrame(tick);
})();
