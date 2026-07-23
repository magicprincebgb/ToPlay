import { api, getToken, logout, requireLogin, showInstallHelp } from '/js/common.js';

requireLogin();

// If opened in a normal browser tab (not the installed PWA), show how to add
// ToPlay to the home screen for a proper full-screen experience.
showInstallHelp();


const video    = document.getElementById('video');
const stage    = document.getElementById('stage');
const overlay  = document.getElementById('overlay');
const ovStatus = document.getElementById('ov-status');
const btnConn  = document.getElementById('btn-connect');
const pill     = document.getElementById('pill');

let pc = null;
let ws = null;
let dc = null;
let started = false;
let userQuit = false;
let reconnectTimer = null;
let fitMode = localStorage.getItem('toplay_fit') || 'contain';
video.style.objectFit = fitMode;

// Buffer kept clear of the phone's edge gesture zones (notification pull-down
// bar / iPhone home indicator) so the PC's top window buttons and bottom
// taskbar become reachable. Applied to the CSS var that pads #stage.
let edgeMargin = localStorage.getItem('toplay_edge') || '4';

document.documentElement.style.setProperty('--edge-margin', edgeMargin + 'px');


// ---------------------------------------------------------------- status UI
function setStatus(text) { ovStatus.textContent = text; }
function showOverlay(show) { overlay.classList.toggle('hidden', !show); }
let pillTimer = null;
function flash(text) {
  pill.textContent = text;
  pill.classList.add('show');
  clearTimeout(pillTimer);
  pillTimer = setTimeout(() => pill.classList.remove('show'), 1500);
}

// While playing, show the Settings/Quit HUD briefly then auto-hide it so it
// never covers the game's on-screen controls. Tapping the top-center grip
// (or opening settings) brings it back.
let hudTimer = null;
function showHud() {
  document.body.classList.remove('hud-hidden');
  clearTimeout(hudTimer);
  hudTimer = setTimeout(() => { if (started) document.body.classList.add('hud-hidden'); }, 4000);
}


// ---------------------------------------------------------------- connection
function wsUrl() {
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  return `${scheme}://${location.host}/ws/signal`;
}


async function start() {
  if (started) return;
  started = true;
  userQuit = false;
  setStatus('Connecting…');

  try { await requestFullscreen(); } catch { /* iOS: relies on PWA standalone */ }

  // The auth token rides in the WebSocket subprotocol (not the URL) so it
  // never lands in server request logs. Must match the server's WsAuthProtocol.
  ws = new WebSocket(wsUrl(), ['toplay.auth', getToken()]);
  ws.onclose = () => onDisconnected('Connection closed');
  ws.onerror = () => {};
  ws.onmessage = onSignal;
  ws.onopen = () => negotiate();
}

async function negotiate() {
  pc = new RTCPeerConnection({ iceServers: [] });

  // Reliable, ordered channel for touch input.
  dc = pc.createDataChannel('input', { ordered: true });
  dc.onopen = () => flash('Input connected');

  pc.addTransceiver('video', { direction: 'recvonly' });

  pc.ontrack = (e) => {
    if (video.srcObject !== e.streams[0]) {
      video.srcObject = e.streams[0];
      video.play().catch(() => {});
    }
    showOverlay(false);
    showHud();
    flash('Streaming');
  };

  pc.onicecandidate = (e) => {
    if (e.candidate && ws?.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({
        type: 'candidate',
        candidate: e.candidate.candidate,
        sdpMid: e.candidate.sdpMid,
        sdpMLineIndex: e.candidate.sdpMLineIndex
      }));
    }
  };

  pc.onconnectionstatechange = () => {
    const s = pc.connectionState;
    if (s === 'connected') { showOverlay(false); }
    else if (s === 'failed' || s === 'disconnected') { onDisconnected('Lost connection'); }
  };

  const offer = await pc.createOffer({ offerToReceiveVideo: true });
  await pc.setLocalDescription(offer);
  ws.send(JSON.stringify({ type: 'offer', sdp: offer.sdp }));
}

async function onSignal(ev) {
  let msg;
  try { msg = JSON.parse(ev.data); } catch { return; }

  if (msg.type === 'answer') {
    await pc.setRemoteDescription({ type: 'answer', sdp: msg.sdp });
  } else if (msg.type === 'candidate' && msg.candidate) {
    try {
      await pc.addIceCandidate({
        candidate: msg.candidate,
        sdpMid: msg.sdpMid,
        sdpMLineIndex: msg.sdpMLineIndex
      });
    } catch { /* ignore late candidates */ }
  } else if (msg.type === 'error') {
    setStatus('Host: ' + msg.message);
    showOverlay(true);
    teardown();
  }
}

function onDisconnected(reason) {
  if (userQuit) return;
  setStatus(reason + ' — reconnecting…');
  showOverlay(true);
  teardown();
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(() => { started = false; start(); }, 1500);
}

function teardown() {
  started = false;
  try { dc && dc.close(); } catch {}
  try { pc && pc.close(); } catch {}
  try { ws && ws.close(); } catch {}
  dc = pc = ws = null;
}

// ---------------------------------------------------------------- fullscreen
async function requestFullscreen() {
  const el = document.documentElement;
  if (el.requestFullscreen) return el.requestFullscreen();
  if (el.webkitRequestFullscreen) return el.webkitRequestFullscreen();
  // iOS Safari has no element fullscreen; PWA "Add to Home Screen" gives it.
}

// ---------------------------------------------------------------- touch input
function sendEvents(events) {
  if (dc && dc.readyState === 'open' && events.length) {
    dc.send(JSON.stringify({ e: events }));
  }
}

// Map a client point to normalized [0..1] coords over the actual video image.
function normalize(clientX, clientY) {
  const r = video.getBoundingClientRect();
  let imgW = r.width, imgH = r.height, offX = 0, offY = 0;

  if (fitMode === 'contain' && video.videoWidth && video.videoHeight) {
    const elemA = r.width / r.height;
    const imgA = video.videoWidth / video.videoHeight;
    if (imgA > elemA) { imgW = r.width; imgH = r.width / imgA; offY = (r.height - imgH) / 2; }
    else { imgH = r.height; imgW = r.height * imgA; offX = (r.width - imgW) / 2; }
  }

  let nx = (clientX - r.left - offX) / imgW;
  let ny = (clientY - r.top - offY) / imgH;
  nx = Math.min(1, Math.max(0, nx));
  ny = Math.min(1, Math.max(0, ny));
  return { nx, ny };
}

function handleTouch(type, e) {
  if (!started) return;
  e.preventDefault();
  const events = [];
  for (const t of e.changedTouches) {
    const { nx, ny } = normalize(t.clientX, t.clientY);
    events.push({ t: type, id: t.identifier, x: +nx.toFixed(4), y: +ny.toFixed(4) });
  }
  sendEvents(events);
}

stage.addEventListener('touchstart',  (e) => handleTouch('d', e), { passive: false });
stage.addEventListener('touchmove',   (e) => handleTouch('m', e), { passive: false });
stage.addEventListener('touchend',    (e) => handleTouch('u', e), { passive: false });
stage.addEventListener('touchcancel', (e) => handleTouch('u', e), { passive: false });

// Also support mouse for testing on a desktop browser.
let mouseDown = false;
stage.addEventListener('mousedown', (e) => { if (!started) return; mouseDown = true; const { nx, ny } = normalize(e.clientX, e.clientY); sendEvents([{ t: 'd', id: -1, x: nx, y: ny }]); });
stage.addEventListener('mousemove', (e) => { if (!started || !mouseDown) return; const { nx, ny } = normalize(e.clientX, e.clientY); sendEvents([{ t: 'm', id: -1, x: nx, y: ny }]); });
window.addEventListener('mouseup',  () => { if (!started || !mouseDown) return; mouseDown = false; sendEvents([{ t: 'u', id: -1 }]); });

// Release everything if the page is hidden/backgrounded.
document.addEventListener('visibilitychange', () => {
  if (document.hidden) sendEvents([{ t: 'c' }]);
});
window.addEventListener('pagehide', () => sendEvents([{ t: 'c' }]));

// ---------------------------------------------------------------- HUD + settings
document.getElementById('grip').addEventListener('click', showHud);
btnConn.addEventListener('click', () => { showOverlay(false); start(); });
document.getElementById('btn-quit').addEventListener('click', () => {
  userQuit = true;
  teardown();
  logout();
});

// Back button — controls whatever program is focused on the PC:
//   • quick tap  → Escape (go back / close menus; also the Android "Back"
//                  button inside emulators like LDPlayer, so it backs out in
//                  MLBB too).
//   • press-hold → Alt+F4 (close the current window/program).
function sendKey(key) {
  if (dc && dc.readyState === 'open') { dc.send(JSON.stringify({ e: [{ t: 'k', key }] })); return true; }
  flash('Connect first'); return false;
}

const btnBack = document.getElementById('btn-back');
let backHoldTimer = null;
let backFired = false;

function backPress(e) {
  e.preventDefault();
  backFired = false;
  clearTimeout(backHoldTimer);
  backHoldTimer = setTimeout(() => { backFired = true; if (sendKey('exit')) flash('Close window (Alt+F4)'); }, 600);
  showHud();
}
function backRelease(e) {
  e.preventDefault();
  clearTimeout(backHoldTimer);
  if (!backFired) { if (sendKey('back')) flash('Back (Esc)'); }
}
btnBack.addEventListener('touchstart', backPress,   { passive: false });
btnBack.addEventListener('touchend',   backRelease, { passive: false });
btnBack.addEventListener('mousedown',  backPress);
btnBack.addEventListener('mouseup',    backRelease);


const settings   = document.getElementById('settings');
const selPreset  = document.getElementById('preset');
const selMonitor = document.getElementById('monitor');
const selEncoder = document.getElementById('encoder');
const selFit     = document.getElementById('fit');
const selEdge    = document.getElementById('edge');
const setStatusEl = document.getElementById('set-status');


document.getElementById('btn-settings').addEventListener('click', openSettings);
document.getElementById('set-close').addEventListener('click', () => settings.classList.add('hidden'));
document.getElementById('set-apply').addEventListener('click', applySettings);

selFit.value = fitMode;
selFit.addEventListener('change', () => {
  fitMode = selFit.value;
  video.style.objectFit = fitMode;
  localStorage.setItem('toplay_fit', fitMode);
});

selEdge.value = edgeMargin;
selEdge.addEventListener('change', () => {
  edgeMargin = selEdge.value;
  document.documentElement.style.setProperty('--edge-margin', edgeMargin + 'px');
  localStorage.setItem('toplay_edge', edgeMargin);
});


async function openSettings() {
  settings.classList.remove('hidden');
  const { ok, data } = await api('/api/status');
  if (!ok) { setStatusEl.textContent = 'Could not load status.'; return; }

  setStatusEl.textContent = data.message || '';
  selPreset.innerHTML = data.presets.map(p => `<option value="${p.id}">${p.name}</option>`).join('');
  selPreset.value = data.activePresetId;
  selMonitor.innerHTML = data.monitors.map(m => `<option value="${m.index}">${m.label}</option>`).join('');
  selMonitor.value = data.monitorIndex;
  selEncoder.value = data.encoder || 'Auto';
  selFit.value = fitMode;
  selEdge.value = edgeMargin;
}


async function applySettings() {
  const body = {
    monitorIndex: parseInt(selMonitor.value, 10),
    presetId: selPreset.value,
    encoder: selEncoder.value
  };
  setStatusEl.textContent = 'Applying…';
  const { ok, data } = await api('/api/settings', { method: 'POST', body });
  if (ok) {
    setStatusEl.textContent = data.message || 'Applied.';
    // Close the modal so the reconnect (and the confirmation pill, which sits
    // behind the modal) are actually visible — no manual Close needed.
    settings.classList.add('hidden');
    flash('Settings applied — reconnecting');
    // The encoder restarts host-side; bounce the connection to pick it up.
    if (started) { teardown(); setTimeout(() => start(), 600); }
  } else {
    setStatusEl.textContent = 'Failed to apply settings.';
  }

}

// Auto-start once (muted autoplay is allowed); overlay button is the fallback
// for iOS which may require a tap.
setStatus('Tap to start streaming your PC.');
