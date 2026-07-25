import {
  api, setToken, getToken, getRemember, setRemember, resumeSession, showInstallHelp,
} from '/js/common.js';

if (getToken()) location.replace('/player.html');

const form = document.getElementById('form');
const msg = document.getElementById('msg');
const go = document.getElementById('go');
const remember = document.getElementById('remember');
const ver = document.getElementById('ver');

// Show the real host version, so the sign-in page can never drift out of date.
fetch('/api/pubinfo')
  .then(r => r.json())
  .then(i => { if (i?.version) ver.textContent = 'ToPlay v' + i.version; })
  .catch(() => {});

// "Remember me" is on by default for the next visit unless the user turned it
// off last time — a phone you keep in your pocket shouldn't nag you, but the
// choice should stick.
const REMEMBER_PREF = 'toplay_remember_pref';
if (localStorage.getItem(REMEMBER_PREF) === '0') remember.checked = false;
remember.addEventListener('change', () => {
  localStorage.setItem(REMEMBER_PREF, remember.checked ? '1' : '0');
});

// ------------------------------------------------------------ silent sign-in
// The host keeps sessions in memory only, so a PC restart used to send you back
// to this form every time. With a remembered device we swap the stored token
// for a fresh session behind the scenes and go straight to the stream.
if (getRemember() && !getToken()) {
  form.classList.add('busy');
  msg.textContent = 'Signing you in…';
  msg.className = 'msg';
  resumeSession().then((ok) => {
    if (ok) {
      location.replace('/player.html');
      return;
    }
    // Token was expired or revoked — fall back to the normal form.
    form.classList.remove('busy');
    msg.textContent = '';
    msg.className = 'msg';
    showInstallHelp();
  });
} else {
  // Nudge phones/tablets to install ToPlay as a full-screen app (skipped once
  // it's already installed or the user opted out).
  showInstallHelp();
}

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  const username = document.getElementById('u').value.trim();
  const password = document.getElementById('p').value;
  msg.textContent = '';
  msg.className = 'msg';
  go.disabled = true;
  go.textContent = 'Signing in…';

  try {
    const { ok, data } = await api('/api/login', {
      method: 'POST',
      body: { username, password, remember: remember.checked },
    });
    if (ok && data?.ok) {
      setToken(data.token);
      setRemember(data.remember || null);
      location.replace('/player.html');
    } else {
      msg.textContent = data?.error || 'Sign in failed.';
      msg.className = 'msg error';
    }
  } catch (err) {
    msg.textContent = 'Network error: ' + err.message;
    msg.className = 'msg error';
  } finally {
    go.disabled = false;
    go.textContent = 'Sign in';
  }
});
