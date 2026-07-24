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
const pingEl   = document.getElementById('ping');
const kbd      = document.getElementById('kbd');
const kbdInput = document.getElementById('kbd-input');
const pcaudio  = document.getElementById('pcaudio');
const soundNudge = document.getElementById('sound-nudge');


let pc = null;
let ws = null;
let dc = null;
let started = false;
let userQuit = false;
let reconnectTimer = null;
let pingTimer = null;
let kbdLastValue = '';
let gotVideo = false;

let fitMode = localStorage.getItem('toplay_fit') || 'contain';
video.style.objectFit = fitMode;

// Buffer kept clear of the phone's edge gesture zones (notification pull-down
// bar / iPhone home indicator) so the PC's top window buttons and bottom
// taskbar become reachable. Applied to the CSS var that pads #stage.
let edgeMargin = localStorage.getItem('toplay_edge') || '4';

document.documentElement.style.setProperty('--edge-margin', edgeMargin + 'px');

// PC->phone audio is opt-in and OFF by default: the default experience stays
// exactly as before (video only). When ON, the offer carries an audio
// transceiver so the host adds an Opus track.
let audioEnabled = localStorage.getItem('toplay_audio') === 'on';



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

// PC-sound autoplay may be blocked until the user taps (iOS). Show a one-tap
// prompt only in that case; hide it as soon as audio is actually playing.
function showSoundNudge() { if (audioEnabled) soundNudge.classList.remove('hidden'); }
function hideSoundNudge() { soundNudge.classList.add('hidden'); }
soundNudge.addEventListener('click', () => {
  pcaudio.play().then(hideSoundNudge).catch(() => {});
});



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
  gotVideo = false;
  pc = new RTCPeerConnection({ iceServers: [] });

  // Reliable, ordered channel for touch input.
  dc = pc.createDataChannel('input', { ordered: true });
  dc.onopen = () => { flash('Input connected'); startPing(); };
  dc.onclose = () => stopPing();
  dc.onmessage = onDataChannelMessage;


  pc.addTransceiver('video', { direction: 'recvonly' });

  // Only ask for audio when the user turned "PC sound" on. When off we send no
  // m=audio line, so the host stays video-only and nothing changes.
  if (audioEnabled) pc.addTransceiver('audio', { direction: 'recvonly' });

  pc.ontrack = (e) => {
    if (e.track.kind === 'audio') {
      // Route PC audio into its own (non-muted) element. The <video> stays
      // muted so its autoplay never gets blocked; audio plays here instead.
      let s = pcaudio.srcObject;
      if (!(s instanceof MediaStream)) { s = new MediaStream(); pcaudio.srcObject = s; }
      if (!s.getAudioTracks().includes(e.track)) s.addTrack(e.track);
      pcaudio.play().then(hideSoundNudge).catch(showSoundNudge);
    } else {
      gotVideo = true;
      if (video.srcObject !== e.streams[0]) {
        video.srcObject = e.streams[0];
        video.play().catch(() => {});
      }
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
    try {
      await pc.setRemoteDescription({ type: 'answer', sdp: msg.sdp });
    } catch (err) {
      // The browser rejected our SDP answer. When PC sound was requested, the
      // added audio track is by far the likeliest cause — never leave the user
      // stuck on a black screen (no video, no touch): permanently fall back to
      // the rock-solid video-only path for this session and reconnect.
      if (audioEnabled) {
        audioEnabled = false;
        localStorage.setItem('toplay_audio', 'off');
        try { selAudio.value = 'off'; } catch {}
        flash('PC sound not supported here — video only');
      }
      onDisconnected('Negotiation failed');
    }
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
  // Safety net: if PC sound was on but we never got a working video stream,
  // the audio negotiation is the likely culprit on this device. Permanently
  // fall back to reliable video-only so the user is never stuck on black.
  if (audioEnabled && !gotVideo) {
    audioEnabled = false;
    localStorage.setItem('toplay_audio', 'off');
    try { selAudio.value = 'off'; } catch {}
    flash('PC sound not supported here — video only');
  }
  setStatus(reason + ' — reconnecting…');
  showOverlay(true);
  teardown();
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(() => { started = false; start(); }, 1500);
}

function teardown() {
  started = false;
  gotVideo = false;
  stopPing();
  closeKbd();
  hideSoundNudge();
  // Cancel any pending auto-reconnect so an INTENTIONAL bounce (toggling PC
  // sound, applying settings, quitting) can never race with a second reconnect
  // — that race used to reset gotVideo and spuriously trip the audio→video
  // fallback, flipping PC sound back off when you re-enabled it mid-session.
  clearTimeout(reconnectTimer);
  reconnectTimer = null;
  try { pcaudio.pause(); pcaudio.srcObject = null; } catch {}
  // Detach handlers BEFORE closing so this intentional teardown doesn't fire
  // onDisconnected() (only a genuine, unexpected drop should reconnect).
  if (pc) { try { pc.onconnectionstatechange = null; } catch {} }
  if (ws) { try { ws.onclose = null; ws.onerror = null; ws.onmessage = null; } catch {} }
  try { dc && dc.close(); } catch {}
  try { pc && pc.close(); } catch {}
  try { ws && ws.close(); } catch {}
  dc = pc = ws = null;
}


// ---------------------------------------------------------------- latency HUD
// Every second we bounce a tiny {t:'ping',ts} off the host; it echoes {t:'pong'}
// with the same timestamp so we can show the real round-trip time. This is the
// data-channel RTT — the same path touches take — so it reflects input lag.
function startPing() {
  stopPing();
  pingEl.classList.add('show');
  const tick = () => {
    if (dc && dc.readyState === 'open') {
      try { dc.send(JSON.stringify({ t: 'ping', ts: Date.now() })); } catch {}
    }
  };
  tick();
  pingTimer = setInterval(tick, 1000);
}

function stopPing() {
  clearInterval(pingTimer);
  pingTimer = null;
  pingEl.classList.remove('show');
  pingEl.textContent = '–';
  pingEl.className = pingEl.className.replace(/\bping-(good|ok|bad)\b/g, '').trim();
}

function showPing(rtt) {
  pingEl.textContent = rtt + ' ms';
  const level = rtt < 60 ? 'ping-good' : rtt < 120 ? 'ping-ok' : 'ping-bad';
  pingEl.classList.remove('ping-good', 'ping-ok', 'ping-bad');
  pingEl.classList.add(level, 'show');
}

function onDataChannelMessage(ev) {
  let msg;
  try { msg = JSON.parse(typeof ev.data === 'string' ? ev.data : ''); } catch { return; }
  if (!msg || typeof msg !== 'object') return;

  if (msg.t === 'pong' && typeof msg.ts === 'number') {
    showPing(Math.max(0, Math.round(Date.now() - msg.ts)));
  }
}

// ---------------------------------------------------------------- on-screen keyboard
// The ⌨ (Keys) button focuses a hidden text field, which makes the phone's
// native keyboard slide up. We stream keystrokes to the PC's focused control:
//   • typed characters  → {t:'txt', s:'...'}   (Unicode injection host-side)
//   • Enter / Backspace  → {t:'key', k:'enter'|'backspace'}
// A common-prefix diff of the field's value handles autocorrect/replace too.
function sendText(s) {
  if (s && dc && dc.readyState === 'open') dc.send(JSON.stringify({ e: [{ t: 'txt', s }] }));
}
function sendCtrlKey(k) {
  if (dc && dc.readyState === 'open') dc.send(JSON.stringify({ e: [{ t: 'key', k }] }));
}

function openKbd() {
  if (!started) { flash('Connect first'); return; }
  kbd.classList.remove('hidden');
  kbdInput.value = '';
  kbdLastValue = '';
  // Focus must happen in the click handler for iOS to raise the keyboard.
  kbdInput.focus();
}

function closeKbd() {
  kbd.classList.add('hidden');
  kbdInput.value = '';
  kbdLastValue = '';
  try { kbdInput.blur(); } catch {}
}

function onKbdInput() {
  const cur = kbdInput.value;
  const prev = kbdLastValue;

  // Longest common prefix, then delete the rest of the old text and type the new.
  let i = 0;
  const min = Math.min(cur.length, prev.length);
  while (i < min && cur[i] === prev[i]) i++;

  const del = prev.length - i;
  for (let k = 0; k < del; k++) sendCtrlKey('backspace');
  const add = cur.slice(i);
  if (add) sendText(add);

  kbdLastValue = cur;
}

function onKbdKeydown(e) {
  if (e.key === 'Enter') {
    e.preventDefault();
    sendCtrlKey('enter');
  } else if (e.key === 'Backspace' && kbdInput.value === '') {
    // Field already empty — forward the backspace so it deletes on the PC.
    e.preventDefault();
    sendCtrlKey('backspace');
  }
}

document.getElementById('btn-kbd').addEventListener('click', openKbd);
document.getElementById('kbd-done').addEventListener('click', closeKbd);
kbdInput.addEventListener('input', onKbdInput);
kbdInput.addEventListener('keydown', onKbdKeydown);


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
const selAudio   = document.getElementById('audio');
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

// PC sound is negotiated in the SDP offer, so switching it requires a fresh
// connection. We bounce the stream (same as an encoder change) so the new
// setting takes effect immediately without the user reconnecting by hand.
selAudio.value = audioEnabled ? 'on' : 'off';
selAudio.addEventListener('change', () => {
  audioEnabled = selAudio.value === 'on';
  localStorage.setItem('toplay_audio', audioEnabled ? 'on' : 'off');
  hideSoundNudge();
  if (started) {
    flash(audioEnabled ? 'PC sound on — reconnecting' : 'PC sound off — reconnecting');
    teardown();
    setTimeout(() => start(), 400);
  } else {
    flash(audioEnabled ? 'PC sound on' : 'PC sound off');
  }
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
  selAudio.value = audioEnabled ? 'on' : 'off';
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
