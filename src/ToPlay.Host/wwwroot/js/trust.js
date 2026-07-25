// Certificate-help page logic.
//
// Deliberately a plain (non-module) script with no imports: this page has to
// work over plain http on a phone that doesn't trust ToPlay yet, before any
// login exists. It only reads /api/pubinfo (unauthenticated, non-sensitive) so
// the download link can point at the http port and the finish button at https.

(function () {
  const host = location.hostname;
  const dl = document.getElementById('dl');
  const dlnote = document.getElementById('dlnote');
  const go = document.getElementById('go');
  const ver = document.getElementById('ver');

  const iosBlocks = [document.getElementById('ios'), document.getElementById('ios2')];
  const androidBlock = document.getElementById('android');
  const tabIos = document.getElementById('tab-ios');
  const tabAndroid = document.getElementById('tab-android');
  const lastN = document.getElementById('lastn');

  function selectPlatform(ios) {
    iosBlocks.forEach(el => el.classList.toggle('hide', !ios));
    androidBlock.classList.toggle('hide', ios);
    tabIos.classList.toggle('on', ios);
    tabAndroid.classList.toggle('on', !ios);
    lastN.textContent = ios ? '4' : '3';
  }

  // iPadOS reports as Mac, so also check for a touch-capable "Mac".
  const isApple = /iPhone|iPad|iPod/.test(navigator.userAgent) ||
    (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1);
  selectPlatform(isApple);
  tabIos.addEventListener('click', () => selectPlatform(true));
  tabAndroid.addEventListener('click', () => selectPlatform(false));

  fetch('/api/pubinfo')
    .then(r => r.json())
    .then(info => {
      // Certificate download MUST be plain http: iOS/Safari refuses to download
      // a profile from a host whose certificate it doesn't trust yet.
      dl.href = `http://${host}:${info.httpPort}/toplay-ca.crt`;
      if (info.useHttps) go.href = `https://${host}:${info.httpsPort}/`;
      if (info.certReady === false) {
        dlnote.textContent = 'HTTPS is turned off on the PC, so no certificate is needed.';
        dl.classList.add('hide');
      }
      if (info.version) ver.textContent = `ToPlay ${info.version}`;
    })
    .catch(() => {
      dlnote.textContent = 'Could not reach the PC. Make sure ToPlay is running and you are on the same Wi-Fi.';
    });
})();
