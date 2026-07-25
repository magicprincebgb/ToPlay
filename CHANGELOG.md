# Changelog

All notable changes to **ToPlay** are documented here. This project follows
[Semantic Versioning](https://semver.org/).

## [2.2.1] — 2026-07-26

**"Remember me" now actually keeps you signed in.** Quitting a game no longer
signs you out of your phone, so you can jump back in and connect with a single
tap. Drop-in upgrade; accounts and settings carry over.

### Fixed — Quit no longer signs you out
- **Tapping *Quit* now just stops the stream.** It closes the connection and
  brings back the "Tap to start" screen — you stay signed in on that phone.
  Come back to ToPlay later (even hours later) and you can connect straight
  away, with no username and password.
- Previously *Quit* signed you out completely, which asked for your password
  every single time and made *Remember me* look like it wasn't working.

### New — a proper "Sign out" button
- The play screen's button strip now has its own **Sign out** button, next to
  Quit. That's the one that really leaves your account on this phone: it asks
  you to confirm, then forgets the device on the PC too, so the next visit
  needs your username and password again.
- Sign out is styled differently and sits at the end of the strip, so it can't
  be mistaken for Quit mid-game. On short screens the whole strip shrinks
  slightly so all five buttons stay reachable.

### Fixed — the app icon was stuck on an old version
- **The Accounts page (and every other page) now shows the current ToPlay
  icon.** Phones and browsers were holding on to an icon they had cached from an
  earlier version, so the browser tab and home-screen shortcut kept showing the
  old artwork. Icons are now stamped with the version, and pages are always
  re-checked for updates, so a new release shows its new icon right away.
- The Accounts page was also missing a few icon sizes that iPhones and iPads
  look for; it now has the full set like the rest of ToPlay.
- Removed a leftover, unused image file from an earlier design.

---

## [2.2.0] — 2026-07-26

**No more typing your password every time you want to play.** Tick *Remember me*
once and your phone signs itself in from then on — even after you restart the PC.
Drop-in upgrade; accounts and settings carry over.

### New — stay signed in on this device
- **"Remember me on this device"** is now on the phone's sign-in page, ticked by
  default. Sign in once and the next time you open ToPlay it takes you straight
  to the play screen.
- **It survives a PC restart.** Before, ToPlay forgot every sign-in the moment
  the PC app closed, so you had to type your username and password again. Now
  only the *play* session is temporary — the device itself stays trusted.
- **Playing on someone else's phone?** Just untick the box before you sign in and
  nothing is remembered.
- **Tapping "Sign out" forgets the device immediately**, on the phone *and* on
  the PC. Deleting an account from the PC's user list also forgets every phone
  that account was signed in on.
- **It expires by itself after 30 days** of not being used, so a phone you stop
  using quietly loses access.
- The sign-in page now shows the version it's actually running, instead of a
  number that had to be edited by hand.

### Security — how "Remember me" is kept safe
- **Your password is never stored on the phone.** The phone keeps a long random
  pass instead, and that pass is only good for one use.
- **It is swapped for a brand-new one every single time it's used.** If someone
  ever managed to copy an old one, it is already dead and the attempt is refused.
- **The PC doesn't store the pass either** — only a scrambled fingerprint of it,
  so even someone who reads ToPlay's data file can't sign in as you.
- **Guessing is pointless.** A wrong pass counts against the same lock-out that
  protects the password box, and it's checked in a way that leaks nothing about
  the right answer.
- **A limit of 8 remembered devices per account** stops old phones piling up
  forever.

### Fixed
- **A stale sign-in no longer leaves the phone spinning.** If the PC had
  forgotten your play session, the phone would keep retrying the connection
  forever; it now returns to the sign-in page and signs itself straight back in.
- **The public source now actually builds.** One file — the accounts database
  code — was being skipped by an over-broad ignore rule (it lives in a folder
  called `Data`, and the rule that hides ToPlay's *runtime* `data` folder matched
  it too on Windows). Anyone cloning the repo got a project that wouldn't
  compile. The released `ToPlaySetup.exe` was always complete; only the source
  copy was affected.

---

## [2.1.1] — 2026-07-25

**Play anywhere, including on your phone's hotspot.** A small but important
fix for anyone who streams away from their home Wi-Fi. Drop-in upgrade;
accounts and settings carry over.

### Fixed — the stream now gets through on "Public" networks
- **On some networks the ToPlay page opened on the phone, the PC accepted the
  connection, and then nothing ever appeared** — the phone just kept trying
  again and again. Phone hotspots are the usual case, because Windows treats
  them as a *public* network and blocks far more than it does on your home
  Wi-Fi.
- **The cause:** ToPlay only asked Windows Firewall to let in the two doors the
  web page uses. The picture and sound themselves travel a different way, on
  ports Windows picks fresh every time, so there was no way to open them by
  number in advance. On a home network Windows let them through anyway; on a
  public network it silently dropped them, and the phone was left waiting for a
  picture that could never arrive.
- **The fix:** ToPlay now asks Windows to trust the streaming program itself
  rather than a list of port numbers, on every kind of network — home, work and
  public. That covers the picture, the sound and your touches, whatever ports
  they end up using.
- This happens automatically during install, and you can also re-apply it any
  time from the Control Panel with **First-time setup**. Uninstalling removes
  the new permissions again, exactly like the old ones.

---

## [2.1.0] — 2026-07-25


**The competitive gaming release.** Nothing new to learn and nothing to
reconfigure — ToPlay just responds faster. Every change below removes waiting
somewhere between your finger and the pixels on your phone. Drop-in upgrade;
accounts and settings carry over.

### Fixed — PC sound no longer costs you a black screen
- **Turning PC sound on could leave the phone black for about eight seconds**
  before it gave up and switched itself back to video only. The PC was getting
  stuck while agreeing the sound connection, and because it got stuck in the
  middle of the conversation with the phone, nothing else could get through
  either — no picture, no touch, no explanation.
- **The PC can no longer get stuck there.** Setting up the connection now runs
  on its own with a three-second limit. If anything takes too long, the PC says
  so straight away and the phone starts playing in video-only mode
  immediately — you get the game, not a black screen.
- **The phone stops waiting sooner too:** four seconds instead of eight, so if
  something is wrong you find out (and are already playing) while you're still
  looking at the screen.
- **The PC log now shows every step of the handshake with its timing**
  (`offer received`, `audio track attached`, `offer applied`, `answer created`,
  `answer ready`), so if sound ever misbehaves on your hardware, the log points
  straight at the step that misbehaved instead of going silent.

### Faster touch response
- **Touches are sent the moment they happen.** They used to be bundled up and
  released once per animation frame, which was efficient but added up to **8–16 ms
  to every single drag** — exactly the delay you feel when a skill shot lands late.
  Each event now goes out immediately.
- **Congestion is handled without adding lag.** If the connection is genuinely
  backed up, stale *move* events are dropped instead of queued behind each other.
  Taps and releases are never dropped, so a finger can't get stuck down.

### Faster picture
- **The phone no longer sits on already-decoded video.** Browsers hold a buffer of
  received video (50–200 ms on Chrome/Android) to smooth out internet jitter. On
  your own Wi-Fi there is nothing to smooth, so that buffer was pure invisible
  input lag. ToPlay now asks for the smallest buffer the browser allows and paints
  frames as they arrive. **This is the single biggest improvement in this release.**
- **The encoder stopped working ahead of itself.** B-frames (each one costs a whole
  frame of delay), look-ahead and scene-cut detection are all off, and the stream
  is strict constant-bitrate. NVIDIA runs in its lowest-latency mode (`p1`+`ull`),
  AMD in `ultralowlatency`, Intel with a single-deep pipeline.
- **No more per-frame colour conversion on GPU encoders.** Frames are captured in
  the exact format NVENC/QuickSync/AMF want (NV12), so the encoder no longer
  converts every single frame before compressing it.
- **Frames leave the PC without queueing.** Capture output is now read on its own
  dedicated, prioritised thread that hands each finished frame straight to the
  connection, instead of sharing a slot with the web server's work.

### Steadier frame pacing (fewer random stutters)
- **1 ms timers while ToPlay runs.** Windows normally wakes threads on a ~15.6 ms
  tick, so a frame that finished just after a tick waited for the next one. The
  timer resolution is raised at startup and politely handed back on exit, so your
  battery life is unaffected once ToPlay closes.
- **Garbage collection kept out of the video path**, which removes the occasional
  freeze that had no obvious cause.
- **The host is scheduled promptly** (above-normal priority) so background updaters
  and indexers can't make your stream wait — the game itself is untouched.

### New
- **540p60 "esports / weak wifi" preset.** Fewer, smaller packets spend less time
  queued inside a busy router, which is usually the real cause of those sudden
  lag spikes mid-match. The picture is softer; the response is sharper. Existing
  installs get the new preset automatically without losing their own settings.
- **Live fps in the HUD**, right next to the ping — now you can tell instantly
  whether a bad moment was the network (ping spike) or the PC (fps drop).
- **Encoder options are verified against the bundled ffmpeg before release**
  (`scripts/probe-encoder-args.ps1`), so a bad flag can never ship as a black
  screen on someone's phone.

### Documentation
- New README section explaining exactly what makes ToPlay fast, plus a short list
  of practical tips for the lowest possible lag (5 GHz Wi-Fi, 540p60/720p60, PC
  sound off, hardware encoder).

---

## [2.0.1] — 2026-07-25


A fix release for three reported problems: **PC sound killing the picture and
touch**, the **“Not secure” warning on iPhone**, and the **missing icon when
adding ToPlay to the home screen**. Drop-in upgrade — accounts and settings
carry over.

### Fixed — PC sound no longer breaks video or touch
- **Audio capture moved off the connection thread.** Turning “PC sound” on
  started Windows audio capture (WASAPI/COM) *inside* the WebRTC connection
  callback. That init can take hundreds of milliseconds, and while it ran the
  connection couldn't finish its handshake — so the data channel never opened
  (dead touch) and no keyframe was requested (black screen). Capture now starts
  on its own thread after the connection is live.
- **Video and audio no longer share one encryption context unsafely.** Both were
  writing through the same SRTP cipher from different threads, which could
  corrupt packets and stall the decoder. All sends are now serialized.
- **Audio can't be sent before it's negotiated**, and per-frame send errors are
  rate-limited to one message every 5 seconds (previously they could flood the
  Control Panel log dozens of times a second and slow the host down).
- **The host now sanity-checks the browser's answer.** If enabling sound would
  have cost you video or touch, the host refuses that connection instead of
  handing you a dead screen.
- **New client watchdog.** 8 seconds after connecting, ToPlay verifies it really
  has a decoded picture *and* an open input channel. If not, it automatically
  turns PC sound off, remembers the choice for that phone, tells you why, and
  reconnects. You can no longer get stuck on a black screen.

### Fixed — “Not secure” on iPhone / Android
- **New guided help page at `/trust.html`** (linked from the startup banner and
  the install dialog): platform-aware, three tappable steps, with the exact
  iPhone path most people miss — **Settings → General → About → Certificate
  Trust Settings → turn ToPlay ON**. Installing the profile alone is *not*
  enough on iOS, which is why the warning kept coming back.
- **Removed the HSTS header.** With it, Safari/Chrome made the certificate
  warning *un-bypassable*, so a phone that hadn't installed the certificate yet
  could not reach ToPlay at all. The warning is now skippable again while you
  finish the certificate setup.
- **The certificate is reissued automatically when your PC's IP changes.** After
  a router reboot or new Wi-Fi lease, the old certificate no longer matched the
  new address and every phone showed a warning again. ToPlay now detects this at
  startup and issues a fresh certificate covering the current addresses.
- The whole trust flow (help page, its script, styles and the `.crt` file) is
  reachable over plain http, because iOS refuses to download a certificate from
  a site it doesn't trust yet.

### Fixed — home-screen icon on iPhone
- **The web app manifest is now served as `application/manifest+json`.** It was
  being sent as a generic binary type, so iOS ignored it entirely.
- **Added the icon sizes iOS actually asks for** (152 and 167 px alongside 180),
  plus `apple-touch-icon-precomposed`, explicit `sizes` attributes, and an
  `apple-mobile-web-app-title` so the home-screen label reads “ToPlay”. Missing
  size hints are why iOS fell back to a screenshot of the page.
- The 180 px icon is now listed in the manifest too, and `manifest-src` was
  added to the security policy so the manifest is never blocked.

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
