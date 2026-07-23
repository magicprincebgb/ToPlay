// Bootstrap: go to the player if logged in, otherwise the login page.
// Kept in an external file so the site can use a strict Content-Security-Policy
// (script-src 'self') with no inline scripts.
const token = localStorage.getItem('toplay_token');
location.replace(token ? '/player.html' : '/login.html');
