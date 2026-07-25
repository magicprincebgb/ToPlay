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
let watchdogTimer = null;

// Own MediaStream per kind instead of trusting the host's stream ids: the video
// element must stay muted (so autoplay is never blocked) while PC sound plays
// through its own <audio>. Mixing them into one stream would mute the sound.
let videoStream = null;
let audioStream = null;


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
  acquireWakeLock();

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
      if (!audioStream) { audioStream = new MediaStream(); pcaudio.srcObject = audioStream; }
      if (!audioStream.getAudioTracks().includes(e.track)) audioStream.addTrack(e.track);
      if (pcaudio.srcObject !== audioStream) pcaudio.srcObject = audioStream;
      pcaudio.play().then(hideSoundNudge).catch(showSoundNudge);
      return;   // audio alone is NOT proof the picture arrived
    }

    gotVideo = true;
    if (!videoStream) videoStream = new MediaStream();
    if (!videoStream.getVideoTracks().includes(e.track)) videoStream.addTrack(e.track);
    if (video.srcObject !== videoStream) video.srcObject = videoStream;
    video.play().catch(() => {});
    minimizeLatency(e.receiver);

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
  startWatchdog();
}

// ---------------------------------------------------------------- watchdog
// A connection can look "connected" and still show nothing: a decoder that
// never gets a frame, or an input channel that never opens. That is the worst
// possible state for the user (black screen, dead touch, no explanation), so we
// verify the two things that actually matter — a decoded picture and an open
// input channel — and self-heal if either is missing.
function hasPicture() {
  return !!video.videoWidth && video.readyState >= 2;
}

function startWatchdog() {
  clearWatchdog();
  // 4 s, not 8: the PC now always answers (or politely refuses) within 3 s, so
  // anything still blank after this really is broken — and the fallback should
  // kick in while you're still looking at the screen, not long after.
  watchdogTimer = setTimeout(() => {
    watchdogTimer = null;
    if (!started) return;
    const picture = hasPicture();
    const input = dc && dc.readyState === 'open';
    if (picture && input) return;                 // all good

    // PC sound is the only optional part of the pipeline, so drop it first.
    if (disableAudio(picture ? 'Touch input' : 'Video')) {
      teardown();
      setTimeout(() => start(), 300);
      return;
    }
    onDisconnected(picture ? 'Input unavailable' : 'No video from PC');
  }, 4000);
}

function clearWatchdog() {
  clearTimeout(watchdogTimer);
  watchdogTimer = null;
}

// Permanently turns PC sound off for this device and remembers the choice, so
// the user is never stuck on a broken stream. Returns false when sound was
// already off (i.e. the problem is something else).
function disableAudio(what) {
  if (!audioEnabled) return false;
  audioEnabled = false;
  localStorage.setItem('toplay_audio', 'off');
  try { selAudio.value = 'off'; } catch {}
  hideSoundNudge();
  flash(`${what} needs PC sound off — switching to video only`);
  return true;
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
      if (disableAudio('PC sound')) {
        teardown();
        setTimeout(() => start(), 400);
        return;
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
    // The host refuses an answer that would have dropped video/touch (that only
    // happens when this browser mishandles the extra audio track). Retry once
    // with PC sound off instead of leaving the user on a dead screen.
    if (disableAudio('PC sound')) {
      teardown();
      setTimeout(() => start(), 400);
      return;
    }
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
  if (audioEnabled && !gotVideo) disableAudio('PC sound');
  setStatus(reason + ' — reconnecting…');
  showOverlay(true);
  teardown();
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(() => { started = false; start(); }, 1500);
}

function teardown() {
  started = false;
  gotVideo = false;
  clearWatchdog();
  stopPing();
  closeKbd();
  hideSoundNudge();
  releaseWakeLock();
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
  videoStream = null;
  audioStream = null;
}


// ---------------------------------------------------------------- low latency
// The browser holds received video in a jitter buffer before showing it. That
// buffer is sized for smooth movie playback, so by default Chrome/Android can
// sit on 50–200 ms of already-decoded picture — pure, invisible input lag in a
// game. On a LAN there is almost no jitter to absorb, so we ask for the
// smallest buffer the browser will give us and render frames the moment they
// land. Every property here is optional and browser-specific (Safari has
// neither), so each assignment is guarded: a missing feature must never break
// the stream.
function minimizeLatency(receiver) {
  if (!receiver) return;
  // Standards-track (Chrome 118+): target playout delay in seconds.
  try { if ('jitterBufferTarget' in receiver) receiver.jitterBufferTarget = 0; } catch {}
  // Legacy hint honoured by older Chrome/Edge builds.
  try { receiver.playoutDelayHint = 0; } catch {}
}

// ---------------------------------------------------------------- latency HUD
// Every second we bounce a tiny {t:'ping',ts} off the host; it echoes {t:'pong'}
// with the same timestamp so we can show the real round-trip time. This is the
// data-channel RTT — the same path touches take — so it reflects input lag.
// The same tick samples WebRTC stats for the decoded frame rate, because a
// healthy ping with a low fps still plays badly and the user deserves to see
// which of the two is wrong.
let fpsShown = 0;
let lastFrames = 0;
let lastFramesAt = 0;

function startPing() {
  stopPing();
  pingEl.classList.add('show');
  const tick = () => {
    if (dc && dc.readyState === 'open') {
      try { dc.send(JSON.stringify({ t: 'ping', ts: Date.now() })); } catch {}
    }
    sampleFps();
  };
  tick();
  pingTimer = setInterval(tick, 1000);
}

function stopPing() {
  clearInterval(pingTimer);
  pingTimer = null;
  fpsShown = lastFrames = lastFramesAt = 0;
  pingEl.classList.remove('show');
  pingEl.textContent = '–';
  pingEl.className = pingEl.className.replace(/\bping-(good|ok|bad)\b/g, '').trim();
}

// Frames actually decoded per second, from the inbound video stats. Derived
// from a delta so it stays accurate no matter how the interval drifts.
async function sampleFps() {
  if (!pc) return;
  let stats;
  try { stats = await pc.getStats(); } catch { return; }
  stats.forEach((s) => {
    if (s.type !== 'inbound-rtp' || s.kind !== 'video') return;
    const frames = s.framesDecoded || 0;
    const at = s.timestamp || Date.now();
    const dt = (at - lastFramesAt) / 1000;
    if (lastFramesAt && dt > 0.2) fpsShown = Math.round((frames - lastFrames) / dt);
    lastFrames = frames;
    lastFramesAt = at;
  });
}

function showPing(rtt) {
  pingEl.textContent = fpsShown > 0 ? `${rtt} ms · ${fpsShown} fps` : `${rtt} ms`;
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

// ---------------------------------------------------------------- wake lock
// Keep the phone's display awake while streaming. Watching a stream generates
// long touch-free stretches, so without this the screen dims and locks
// mid-game. Re-acquired on return from background (the OS releases it there).
let wakeLock = null;
async function acquireWakeLock() {
  try { wakeLock = await navigator.wakeLock?.request('screen'); } catch { /* unsupported/denied */ }
}
function releaseWakeLock() {
  try { wakeLock?.release(); } catch {}
  wakeLock = null;
}
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && started) acquireWakeLock();
});

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

// Touch events are sent the instant the browser hands them to us — never
// deferred to the next animation frame. Waiting for rAF batches nicely but
// costs up to a whole frame (8–16 ms) of input lag on EVERY drag, and in a
// competitive game that delay is the difference between landing a skill shot
// and missing it. One touchmove event already carries the latest position for
// each finger that moved (e.changedTouches), so sending it straight away is
// both the fastest and the smallest possible message.
//
// The only thing we guard against is a congested link: if the data channel is
// already backed up, an extra MOVE would just queue behind stale ones and
// arrive late, so we drop it. Downs and ups are never dropped — losing those
// would strand a contact or miss a tap.
const MOVE_BACKLOG_LIMIT = 8 * 1024;

function movesAreBackedUp() {
  return !!dc && dc.bufferedAmount > MOVE_BACKLOG_LIMIT;
}

function handleTouch(type, e) {
  if (!started) return;
  e.preventDefault();
  if (type === 'm' && movesAreBackedUp()) return;

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
stage.addEventListener('mousemove', (e) => { if (!started || !mouseDown || movesAreBackedUp()) return; const { nx, ny } = normalize(e.clientX, e.clientY); sendEvents([{ t: 'm', id: -1, x: nx, y: ny }]); });
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
  // Build <option>s via the DOM (not innerHTML) so labels can never be
  // interpreted as markup.
  selPreset.replaceChildren(...data.presets.map(p => new Option(p.name, p.id)));
  selPreset.value = data.activePresetId;
  selMonitor.replaceChildren(...data.monitors.map(m => new Option(m.label, m.index)));
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
