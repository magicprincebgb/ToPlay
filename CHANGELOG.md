# Changelog

All notable changes to **ToPlay** are documented here. This project follows
[Semantic Versioning](https://semver.org/).

## [2.0.0] — 2026-07-25

A polish release focused on three things: **lower latency**, **tighter
security**, and a **smoother experience** on the phone. Fully compatible with
existing installs — settings, accounts and certificates carry over.

### Performance
- **Rewrote the H.264 stream parser** on the host. The old parser copied the
  encoded video byte-by-byte through growing lists (an O(n²)-style hotspot at
  high bitrates); it now uses a zero-copy sliding window with block copies and
  tracks keyframes incrementally. Noticeably lower host CPU at 30–60 Mbps,
  which means fewer frame-time spikes on weaker PCs.
- **Server garbage collection** enabled for the host — background collections
  no longer pause the stream pipeline the way workstation GC could.
- **ffmpeg runs at AboveNormal priority** so background tasks can't starve the
  capture/encode loop and cause stutter.
- **New low-latency capture flags** (`-fflags nobuffer -flags low_delay`) keep
  ffmpeg from queueing frames on the input side — every buffered frame is
  glass-to-glass latency you can feel.
- **Touch moves are now coalesced per display frame** in the player. Modern
  phones sample touch at 120–240 Hz; sending every sample as its own message
  just queued work ahead of the video. Taps (down/up) still send instantly, so
  aiming stays exact while sustained drags get cheaper for both sides.
- Static assets (JS/CSS/icons) are now cached by the phone for 5 minutes
  instead of re-fetched on every page load.

### Security
- **Session tokens are no longer accepted from URL query strings** — only the
  `Authorization` header (and the WebSocket subprotocol slot). URLs end up in
  logs and browser history; tokens don't belong there.
- **Stronger password policy**: new accounts require at least 8 characters
  (existing accounts keep working unchanged).
- **More hardening headers** on every response: `Strict-Transport-Security`
  (on HTTPS), `Permissions-Policy` (camera/mic/geolocation/USB all denied),
  `Cross-Origin-Opener-Policy` and `Cross-Origin-Resource-Policy`.
- The server no longer advertises its stack (`Server` header removed).
- Expired sessions are now actively swept from memory instead of lingering
  until someone happens to present the stale token.
- Settings dropdowns in the player are built via the DOM instead of HTML
  strings, so labels can never be interpreted as markup.

### GUI / UX
- **The phone screen stays awake while streaming** (Screen Wake Lock). No more
  display dimming and locking mid-game during touch-free stretches; the lock
  is re-acquired automatically when you return from the background.
- **Expired sessions now return you to the sign-in page** with a fresh state,
  instead of leaving you on a player where every action silently fails.
- **Deleting an account now asks for confirmation** and reports the result —
  including a clear message when the last admin can't be removed.
- The sign-in button shows **"Signing in…"** while the request is in flight.
- The sign-in page shows the app version.
- **Fixed the Control Panel rendering glitch** where buttons and checkboxes
  left overlapping "ghost" copies of themselves (the glass controls now paint
  opaquely instead of relying on WinForms' fragile simulated transparency).
- The version shown in **Add/Remove Programs** is now derived from the
  installer itself and can no longer go stale.

### Notes
- Windows SmartScreen may warn on the new unsigned `ToPlaySetup.exe` — choose
  **More info → Run anyway**. Installing over v1.x is supported; your
  accounts, settings and installed certificates are preserved.

## [1.0.2] — 2026-07-24

### Added
- **PC → phone audio (opt‑in).** Hear your PC's sound — footsteps, gunshots,
  music — right on the phone. It's **off by default** and toggled from the
  on‑screen **Settings**. Capture uses WASAPI loopback (NAudio) encoded to Opus
  (Concentus) and is carried on the same WebRTC connection as the video.
- Audio is **paced for tight A/V sync** so it stays in step with the low‑latency
  video — important for competitive games.

### Changed
- New **ToPlay brand app/PWA icons** drawn directly at build time (no source
  image needed); replaces the old generic placeholder that showed as a plain
  letter on the Home Screen.
- Documentation: README now covers the **on‑screen keyboard** (Keys button),
  the **live latency/ping readout**, the **PC sound** toggle, and clearer
  certificate‑trust steps for iPhone (Safari) so the padlock goes clean and
  "Add to Home Screen" is unlocked.


### Fixed
- **Enabling audio mid‑session no longer breaks the stream.** A teardown/reconnect
  race could momentarily flip audio back to *off* and, in some cases, blank the
  video. The client now cancels its pending reconnect and detaches socket/peer
  handlers during an intentional restart, so only genuine drops reconnect.
- **Self‑healing fallback.** If a browser can't negotiate audio, ToPlay now
  silently drops to **video‑only** for that session instead of failing — the
  stream never goes black.
- Hardened the host's audio negotiation (answer creation and track add are fully
  guarded) so an audio hiccup can never take down video or touch input.

### Notes
- Video, multi‑touch input, and the **Back** (Esc / Alt+F4) button are unchanged
  and fully compatible.
- The distributable `ToPlaySetup.exe` is a self‑contained installer; the target
  PC needs nothing pre‑installed.

## [1.0.1]

- Earlier maintenance release (baseline before the PC→phone audio feature).

## [1.0.0]

- Initial public release: WebRTC H.264 desktop streaming to a phone browser,
  Windows Touch Injection multi‑touch control, LAN accounts with a built‑in
  Certificate Authority for trusted HTTPS, and a self‑contained installer.
