# Changelog

All notable changes to **ToPlay** are documented here. This project follows
[Semantic Versioning](https://semver.org/).

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
