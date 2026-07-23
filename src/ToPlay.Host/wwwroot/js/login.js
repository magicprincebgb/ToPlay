import { api, setToken, getToken, showInstallHelp } from '/js/common.js';

if (getToken()) location.replace('/player.html');

// Nudge phones/tablets to install ToPlay as a full-screen app (skipped once
// it's already installed or the user opted out).
showInstallHelp();


const form = document.getElementById('form');
const msg = document.getElementById('msg');
const go = document.getElementById('go');

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  const username = document.getElementById('u').value.trim();
  const password = document.getElementById('p').value;
  msg.textContent = '';
  msg.className = 'msg';
  go.disabled = true;

  try {
    const { ok, data } = await api('/api/login', { method: 'POST', body: { username, password } });
    if (ok && data?.ok) {
      setToken(data.token);
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
  }
});
