import { api } from '/js/common.js';

const msg = document.getElementById('msg');
const tbody = document.querySelector('#tbl tbody');

function show(text, ok) {
  msg.textContent = text;
  msg.className = 'msg ' + (ok ? 'ok' : 'error');
}

async function refresh() {
  const { ok, data, status } = await api('/api/users');
  if (!ok) {
    show(status === 403
      ? 'Account management is only available on the host PC.'
      : 'Could not load users.', false);
    tbody.innerHTML = '';
    return;
  }
  tbody.innerHTML = '';
  for (const u of data.users) {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td>${escapeHtml(u.username)}</td>
      <td>${u.isAdmin ? '<span class="badge">admin</span>' : '<span class="badge">user</span>'}</td>
      <td style="text-align:right"><button class="danger" data-id="${u.id}">Delete</button></td>`;
    tbody.appendChild(tr);
  }
  tbody.querySelectorAll('button[data-id]').forEach(b => {
    b.addEventListener('click', async () => {
      await api('/api/users/' + b.dataset.id, { method: 'DELETE' });
      refresh();
    });
  });
}

document.getElementById('create').addEventListener('click', async () => {
  const username = document.getElementById('u').value.trim();
  const password = document.getElementById('p').value;
  const { ok, data } = await api('/api/register', { method: 'POST', body: { username, password } });
  if (ok && data?.ok) {
    show('Account created.', true);
    document.getElementById('u').value = '';
    document.getElementById('p').value = '';
    refresh();
  } else {
    show(data?.error || 'Could not create account.', false);
  }
});

document.getElementById('refresh').addEventListener('click', refresh);

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

refresh();
