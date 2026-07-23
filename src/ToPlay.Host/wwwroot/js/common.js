// Shared helpers for the ToPlay web client.

export const TOKEN_KEY = 'toplay_token';

export function getToken() {
  return localStorage.getItem(TOKEN_KEY);
}

export function setToken(t) {
  if (t) localStorage.setItem(TOKEN_KEY, t);
  else localStorage.removeItem(TOKEN_KEY);
}

export function authHeaders(extra = {}) {
  const t = getToken();
  return t ? { ...extra, Authorization: 'Bearer ' + t } : extra;
}

export async function api(path, { method = 'GET', body } = {}) {
  const opts = { method, headers: authHeaders({ 'Content-Type': 'application/json' }) };
  if (body !== undefined) opts.body = JSON.stringify(body);
  const res = await fetch(path, opts);
  let data = null;
  try { data = await res.json(); } catch { /* no body */ }
  return { status: res.status, ok: res.ok, data };
}

export function requireLogin() {
  if (!getToken()) location.replace('/login.html');
}

export function logout() {
  api('/api/logout', { method: 'POST' }).finally(() => {
    setToken(null);
    location.replace('/login.html');
  });
}

// ---------------------------------------------------------------- PWA install
// ToPlay works best when "installed" to the home screen: it then opens
// full-screen with no browser chrome, which is what makes the streamed PC fill
// the phone and keeps touch input from fighting the browser's gestures.

// True when the page is already running as an installed app (standalone).
export function isStandalone() {
  return window.matchMedia('(display-mode: standalone)').matches ||
         window.matchMedia('(display-mode: fullscreen)').matches ||
         window.navigator.standalone === true;
}

// Chromium fires this when the app is installable; we stash it so our own
// "Install app" button can trigger the native prompt on demand.
let deferredInstallPrompt = null;
window.addEventListener('beforeinstallprompt', (e) => {
  e.preventDefault();
  deferredInstallPrompt = e;
  const btn = document.getElementById('pwa-install-btn');
  if (btn) btn.hidden = false;
});

const HIDE_INSTALL_KEY = 'toplay_hide_install';

// Shows a dismissible dialog with platform-specific "Add to Home Screen"
// instructions when NOT already running as a PWA. No-op once installed or if
// the user chose "Don't show again".
export function showInstallHelp() {
  if (isStandalone()) return;
  if (localStorage.getItem(HIDE_INSTALL_KEY) === '1') return;
  if (document.getElementById('pwa-install')) return;

  const ua = navigator.userAgent || '';
  const isIOS = /iPhone|iPad|iPod/.test(ua) ||
                (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
  const isAndroid = /Android/.test(ua);

  const mask = document.createElement('div');
  mask.id = 'pwa-install';
  mask.className = 'install-mask';

  const card = document.createElement('div');
  card.className = 'install-card';

  const h = document.createElement('h2');
  h.textContent = 'Install ToPlay as an app';
  card.appendChild(h);

  const why = document.createElement('p');
  why.textContent = 'Add ToPlay to your home screen so it opens full-screen — the PC fills the whole display and touch controls feel native.';
  card.appendChild(why);

  const steps = document.createElement('ol');
  const stepList = isIOS
    ? ['Tap the Share button (the square with an ↑) in the browser toolbar.',
       'Choose “Add to Home Screen”.',
       'Open ToPlay from the new home-screen icon.']
    : isAndroid
      ? ['Tap the ⋮ menu (top-right) in Chrome.',
         'Choose “Install app” or “Add to Home screen”.',
         'Open ToPlay from the new home-screen icon.']
      : ['Click the install icon in the address bar, or open the ⋮ menu.',
         'Choose “Install ToPlay”.',
         'Launch ToPlay from its own window.'];
  for (const s of stepList) {
    const li = document.createElement('li');
    li.textContent = s;
    steps.appendChild(li);
  }
  card.appendChild(steps);

  // Certificate hint — installing the CA once removes the "Not secure" warning.
  const note = document.createElement('p');
  note.className = 'install-note';
  note.append('First time and seeing “Not secure”? ');
  const certLink = document.createElement('a');
  certLink.href = '/toplay-ca.crt';
  certLink.textContent = 'Install the ToPlay certificate';
  note.appendChild(certLink);
  note.append(' once to trust this PC.');
  card.appendChild(note);

  const row = document.createElement('div');
  row.className = 'btn-row';

  // Native install button (Chromium only — hidden until beforeinstallprompt).
  const installBtn = document.createElement('button');
  installBtn.id = 'pwa-install-btn';
  installBtn.textContent = 'Install app';
  installBtn.hidden = !deferredInstallPrompt;
  installBtn.addEventListener('click', async () => {
    if (!deferredInstallPrompt) return;
    deferredInstallPrompt.prompt();
    try { await deferredInstallPrompt.userChoice; } catch {}
    deferredInstallPrompt = null;
    mask.remove();
  });
  row.appendChild(installBtn);

  const closeBtn = document.createElement('button');
  closeBtn.className = 'secondary';
  closeBtn.textContent = 'Not now';
  closeBtn.addEventListener('click', () => mask.remove());
  row.appendChild(closeBtn);

  card.appendChild(row);

  const dont = document.createElement('button');
  dont.className = 'link-btn';
  dont.textContent = "Don't show again";
  dont.addEventListener('click', () => {
    localStorage.setItem(HIDE_INSTALL_KEY, '1');
    mask.remove();
  });
  card.appendChild(dont);

  mask.appendChild(card);
  document.body.appendChild(mask);
}


