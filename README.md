# ToPlay — play your PC on your phone

Stream your Windows desktop to a phone over your local Wi‑Fi with low latency,
and use the phone's **multi‑touch screen as a touchpad/controller for the PC**.
Great for driving an Android emulator (MLBB, PUBG Mobile, …) that renders on the
powerful PC while you touch‑control it from your phone.

- **Video:** WebRTC H.264 straight to the phone's browser (no app to install).
- **PC → phone audio (opt‑in):** hear your PC's sound — footsteps, gunshots,
  music — right on the phone. Off by default; flip it on from the on‑screen
  **Settings**. Paced for tight A/V sync so it stays in step with the video, and
  if a browser can't negotiate audio it silently falls back to video‑only so the
  stream never goes black.
- **Input:** phone touches → Windows Touch Injection (real multi‑touch), plus a
  **Back** button (tap = Esc, hold = Alt+F4) and an **on‑screen keyboard** (the
  **Keys** button) that types straight into the focused app/field on the PC.
- **Live latency readout:** a colour‑coded round‑trip **ping** meter in the
  corner shows your real input lag at a glance (green under 60 ms) — measured on
  the very data channel your touches travel over — next to the **live fps** the
  phone is actually decoding.
- **Tuned for competitive play:** touch events are sent the instant they happen
  (never batched), the phone's video buffer is pinned to its minimum, the encoder
  runs with zero look‑ahead and no B‑frames, and the host asks Windows for 1 ms
  timers so nothing waits for the next clock tick. See §5 for the full list.



- **Accounts:** created **on the PC**, used to **log in from phones** on the LAN.
- **Trusted HTTPS:** a tiny built‑in Certificate Authority makes the browser on
  the **PC** trust ToPlay automatically — no "Not secure" warning. Phones install
  one small certificate once (see §3) to get the same clean, warning‑free padlock.
- **iPhone‑friendly:** works in Safari and as a full‑screen PWA ("Add to Home
  Screen") — the site itself shows step‑by‑step install instructions.
- **Works on iPhone *and* Android** — same URL, same login, no app store.


> ⚠️ "Zero latency" isn't physically possible. On a good 5 GHz Wi‑Fi / wired LAN
> with hardware encoding you can expect roughly **30–80 ms** glass‑to‑glass, which
> feels great for most games. Congested 2.4 GHz Wi‑Fi will be worse.

---

## 1. Install (for everyone)

1. **Download `ToPlaySetup.exe`** and double‑click it (approve the UAC prompt).
2. In the setup window, keep the defaults and click **Install**. It:
   - copies the app to `C:\Program Files\ToPlay`,
   - installs the **bundled ffmpeg** (already inside the setup — no download),
   - opens the **Windows Firewall** for your local network, and
   - adds **ToPlay** to the Start Menu (and optionally the Desktop).

3. When it finishes, launch **ToPlay** — the Control Panel opens.

That's it. The target PC does **not** need .NET or anything else installed —
`ToPlaySetup.exe` is fully self‑contained. To remove it later, use
**Settings → Apps → ToPlay → Uninstall**.

> Requirements: **Windows 10 (1507+) or Windows 11** (touch injection is
> Windows‑only) and a **hardware H.264 encoder** (NVIDIA NVENC, Intel QuickSync,
> or AMD AMF) for the best latency. Software `libx264` works but uses more CPU.

---

## 2. Run the host — the Control Panel

Launch **ToPlay** from the Start Menu / Desktop. `ToPlay.exe` is a small GUI that
does everything for you:

- **Start / Stop / Restart** the streaming server.
- **First‑time setup** — (re)fetch ffmpeg and open the firewall, if you skipped
  it during install.
- A live **Log** view of the server output.
- The exact **phone URL** (auto‑detected LAN IP) with **Copy** and **Open here**.
- **Settings** — Quality preset, Encoder, Monitor and HTTP/HTTPS ports — saved to
  `data/config.json`. Restart the server to apply.
- **Accounts** — opens the account manager on this PC.

The URL shown at the top is what you open on the phone, e.g.
`https://192.168.1.23:8443/`.

### Create an account
Click **Accounts** in the Control Panel (or open **https://localhost:8443/admin.html**
*on the PC itself*). The **first** account you create automatically becomes the
admin. Accounts can only be created from the PC (loopback) or by an admin — phones
can't self‑register.

### Shut the host down
- Click **Stop** in the Control Panel (or just close it — it stops the server on exit).
- Press **Ctrl + Alt + Shift + Q** anywhere in Windows — this is a **global
  hotkey**, so it works even while a game is fullscreen on another monitor. It stops
  streaming, releases any held touches and exits cleanly.

---

## 3. Connect from your iPhone / phone

1. Make sure the phone is on the **same Wi‑Fi**.
2. Open the phone URL from the Control Panel, e.g. `https://192.168.1.23:8443/`.
3. **Install the ToPlay certificate once (removes the "Not secure" warning).**
   ToPlay runs its own little Certificate Authority; installing its public
   certificate on the phone makes the padlock green and unlocks PWA install.

   Open the guided page **over plain http** — iOS refuses to download a
   certificate from a site it doesn't trust yet, so this address must be `http`
   and port **8080**:

   ```
   http://192.168.1.23:8080/trust.html
   ```

   (Use your own PC's IP — the Control Panel shows it, and the host prints the
   exact link in its console banner. You can also tap *Fix it in 3 taps* in the
   on‑screen install dialog.) The page detects your phone and walks you through:
   - **iPhone/iPad:** tap **Download**, then *Settings → General → VPN & Device
     Management → ToPlay Local CA → Install*, **then — this step is the one
     everyone misses —** *Settings → General → About → Certificate Trust
     Settings* and turn **ToPlay Local CA ON**. Without that switch iOS still
     shows "Not secure".
   - **Android:** tap **Download**, then *Settings → Security → Encryption &
     credentials → Install a certificate → CA certificate* → pick the downloaded
     `ToPlay-CA.crt`.

   Then tap **Open ToPlay securely** on the same page to jump to `https://…:8443/`.

   (You can skip all of this and just tap *Continue/Advanced → proceed* each
   time, but installing it once is cleaner and lets you "Add to Home Screen"
   with the proper icon.)

   > If your PC's LAN IP changes (new router, new Wi‑Fi, DHCP lease), ToPlay
   > automatically issues a fresh server certificate for the new address. The CA
   > you installed stays valid — no need to repeat these steps.

4. **Log in** with the account you created.
5. **Add to Home Screen** for a true full‑screen, landscape experience. ToPlay
   shows an in‑page dialog with the exact steps when you're not already installed:
   - **iPhone (Safari):** Share icon → *Add to Home Screen*.
   - **Android (Chrome):** ⋮ menu → *Install app* / *Add to Home screen*.
6. Tap **"Tap to start"**, rotate to landscape, and play. Your touches now drive
   the PC. On‑screen **Settings/HUD** live in the **right** letterbox bar — that's
   also where the **Back** button lives: a quick **tap** sends **Esc** (go back /
   close a menu — and the Android *Back* button inside emulators like LDPlayer, so
   it backs out in MLBB); **press and hold** sends **Alt+F4** to close the focused
   window/program. The same bar also has a **Keys** button — it pops your phone's
   keyboard and types straight into whatever field is focused on the PC — and a
   small **latency (ping) readout** that turns green when input lag is low. The
   HUD auto‑hides while you play; tap the tiny **⋮ grip** at the top to bring it
   back.



> **One phone at a time.** Each host serves a single viewer. If a second phone
> (or the same phone after a dropped Wi‑Fi connection) connects, it **takes over**
> and the previous session is disconnected — so you're never locked out waiting
> for a stale connection to time out. You can run ToPlay on several PCs on the
> same network; each is independent and still one‑phone‑at‑a‑time.

---

## 4. Settings (switchable at runtime)

Open the on‑screen **Settings** panel (gear button on the right) from the player.
You can switch:

- **Quality preset** — 540p60 (esports), 720p60, 1080p60, 1080p30, or Native/60.
  Lower = less latency & bandwidth.

- **Encoder** — Auto, NVENC, QuickSync, AMF, or Software. If one doesn't work on
  your machine, just pick another; Auto falls back down the list automatically.
- **PC sound** — turn **PC → phone audio** on or off (default **off**, video
  only). Switching it briefly reconnects the stream so the change takes effect
  right away. On iOS you may get a one‑tap "enable PC sound" prompt the first time.
- **Monitor** — pick which display to stream (multi‑monitor setups).

- **Fit** — *contain* (letterboxed, correct aspect) or *fill* (stretch to screen).
- **Edge margin** — how much safe‑area padding to keep around the video
  (None / Minimal / Small / Large) to clear a notch or home indicator.

Changes are saved to `data/config.json` and the encoder hot‑restarts.

---

## 5. How it works

```
 Phone browser (PWA)                         Windows host (this app)
 ┌───────────────────┐   WebSocket signaling  ┌──────────────────────────┐
 │ RTCPeerConnection │◄──────/ws/signal──────►│ ASP.NET Core (Kestrel)   │
 │  video  ◄─────────┼───H.264 over WebRTC────┤ SIPSorcery RTCPeerConn.  │
 │  data   ──────────┼───touch JSON──────────►│ ScreenStreamer (ffmpeg)  │
 └───────────────────┘                        │   gdigrab → H.264        │
        ▲  touches                            │ TouchInjector (WinAPI)   │
        │                                     └──────────────────────────┘
        └─ multi‑touch normalized [0..1] → mapped to the chosen monitor
```

- **Capture/encode:** `ffmpeg` grabs the selected monitor (`gdigrab`) and encodes a
  low‑latency H.264 Annex‑B stream; the host parses it into per‑frame access units
  and pushes them onto the WebRTC video track.
- **Signaling:** a tiny WebSocket endpoint (`/ws/signal`) exchanges the SDP
  offer/answer and ICE candidates. LAN‑only, so no STUN/TURN is needed.
- **Input:** the browser sends touch events on a reliable WebRTC **DataChannel**;
  the host injects them via the Windows synthetic‑pointer API, so games see
  genuine touch contacts (with a keep‑alive so holds don't drop).

### Built for low latency (what ToPlay does for competitive play)

Every stage between your finger and the pixels was tuned to remove waiting, not
to look prettier:

- **Touches are sent immediately.** No animation‑frame batching, which would have
  added up to a full frame (8–16 ms) to every single drag. If the link is
  genuinely congested, stale *move* events are dropped instead of queued — taps
  and releases are never dropped.
- **The phone's video buffer is pinned to its minimum** (`jitterBufferTarget` /
  `playoutDelayHint` = 0). Browsers default to holding 50–200 ms of decoded video
  to smooth out internet jitter; on a LAN that is pure, invisible input lag.
- **Encoder tuned for now, not for filesize:** no B‑frames (each one costs a whole
  frame of delay), no look‑ahead, no scene‑cut detection, CBR, and the picture
  format the GPU encoders want natively (NV12 — no per‑frame conversion). NVENC
  uses `p1 + ull` (its lowest‑latency mode), AMF `ultralowlatency`, QuickSync
  `low_power 0` with `async_depth 1`.
- **Frames never queue on the PC.** Capture output is read on a dedicated,
  above‑normal‑priority thread that hands each finished frame straight to WebRTC,
  instead of a thread‑pool slot shared with the web server.
- **1 ms Windows timers.** By default Windows wakes threads on a ~15.6 ms tick, so
  a frame finished just after a tick waits for the next one. ToPlay raises the
  timer resolution while it runs (and hands it back on exit).
- **No GC hiccups.** Server GC plus sustained‑low‑latency mode keeps garbage
  collection out of the frame path, so you don't get the occasional freeze.
- **A 540p60 "esports" preset.** Fewer, smaller packets spend less time queued in
  a busy router — usually the real cause of sudden lag spikes mid‑match.
- **You can see both numbers** (ping *and* decoded fps) live in the HUD, so it's
  obvious whether a bad moment was the network or the encoder.

**Tips for the lowest possible lag:** use **5 GHz** Wi‑Fi (or the PC on Ethernet),
stand near the router, pick **540p60/720p60**, leave **PC sound off** (audio adds
its own buffering), and prefer a **hardware encoder** over Software.

### Project layout

```
src/ToPlay.Host/          the streaming server (ToPlay.Host.exe)
  Program.cs              ASP.NET host, endpoints, WebSocket signaling
  Config/                 config.json model + quality presets
  Data/ Services/         SQLite accounts + BCrypt auth + sessions
  Display/                monitor enumeration (P/Invoke)
  Input/                  multi‑touch injection + input routing
  Media/                  ffmpeg locate/args + Annex‑B parser (ScreenStreamer)
  WebRtc/                 per‑viewer StreamSession + StreamHost coordinator
  Security/               self‑signed dev cert (LAN IP SANs)
  wwwroot/                login / admin / player PWA (brand icons drawn by make-icons.ps1)
src/ToPlay.App/           the GUI Control Panel shipped as ToPlay.exe

src/ToPlay.Installer/     the self‑contained installer (ToPlaySetup.exe)
scripts/
  build-installer.cmd     builds dist\ToPlaySetup.exe (the distributable)
  make-icons.ps1          draws all app/PWA icons (GDI+) — no source image needed
  run.cmd                 dev: builds & runs ToPlay.exe from source (elevated)

  get-ffmpeg.ps1          downloads ffmpeg into the host tools folder
  allow-firewall.ps1      opens the LAN firewall + prints the phone URL
```

---


## 6. Troubleshooting

- **"ffmpeg.exe not found"** — click the Control Panel's **First‑time setup**, or
  set `FfmpegPath` in `data/config.json`, or put `ffmpeg.exe` on your `PATH`.
- **Phone can't reach the URL** — re‑run **First‑time setup** (it opens the
  firewall) and make sure your Wi‑Fi profile is **Private**, not Public. Confirm
  both devices are on the same subnet.
- **iPhone/Android "Not secure" warning** — open
  `http://<your‑pc‑ip>:8080/trust.html` (plain **http**, port **8080**) and follow
  the 3 taps. On iPhone the warning survives installing the profile until you also
  turn the CA **ON** in *Settings → General → About → Certificate Trust Settings*.
- **Home‑screen icon is blank on iPhone** — install the certificate first (Safari
  won't fetch icons from an untrusted origin), then remove and re‑add the
  shortcut.

- **Video won't start / stalls** — try a lower preset (720p60) or a different
  encoder in Settings. Some laptops need the discrete GPU enabled for NVENC.
- **Touches land in the wrong spot** — make sure the streamed **monitor** in
  Settings matches where your game/emulator window is, and try **Fit = fill** if
  your phone and monitor aspect ratios differ.
- **Reset accounts** — stop the app and delete `data/toplay.db` (also `data/*.pfx`
  to regenerate the cert) inside the install folder.

---

## 7. Security notes

ToPlay grants a phone the ability to inject real input into your PC, so access is
locked down even though it only runs on your LAN:

- **LAN‑only, login‑gated.** The TLS cert is self‑signed for local use — don't
  expose these ports to the public internet.
- **Passwords** are hashed with **BCrypt**; sessions are in‑memory bearer tokens
  that are dropped when the host exits.
- **"Remember me" uses single‑use, rotating tokens.** The phone stores a
  `selector.verifier` pair (16 + 32 random bytes); the host keeps only
  `SHA‑256(verifier)` and compares it in constant time. Every successful resume
  *deletes* the old row and issues a fresh token, so a captured token dies after
  one use and a replay is rejected. Tokens expire after 30 days, are capped at 8
  devices per account, are revoked on sign‑out and on account deletion, and live
  in `localStorage` rather than a cookie (no CSRF surface). Failed resumes count
  against the same login throttle as passwords.
- **Login throttling + no username enumeration.** After several failed attempts a
  client is locked out briefly, and every login runs one BCrypt verify (a dummy
  hash when the user doesn't exist) so "bad user" and "bad password" take the same
  time and return the same generic error.
- **Tokens stay out of logs.** The signaling WebSocket carries its token in the
  `Sec‑WebSocket‑Protocol` header (`toplay.auth`, `<token>`) instead of the URL
  query string, so bearer tokens never appear in request logs.
- **Off‑box clients are forced onto HTTPS.** Non‑loopback HTTP requests are
  redirected to HTTPS so tokens, video and touch input are never sent in the clear.
- **Anti‑DNS‑rebinding.** Requests are only served for hostnames/IPs this machine
  actually answers to (localhost, its hostname, and its LAN IPs); anything else
  gets `421 Misdirected Request`. This stops a malicious web page from resolving
  its own name to `127.0.0.1` to reach the loopback‑privileged endpoints.
- **Strict security headers / CSP.** `Content‑Security‑Policy` limits scripts to
  `'self'` (no inline scripts), plus `X‑Content‑Type‑Options`, `X‑Frame‑Options:
  DENY`, and `Referrer‑Policy: no‑referrer`.
- **Loopback‑only privileges.** Account creation and management require either a
  request from the PC itself (loopback) or an admin session; phones can't
  self‑register. You also can't delete the last admin account.
- **Abuse limits.** Signaling messages are size‑capped (64 KB) and only one viewer
  is served at a time (a new connection takes over the old one).

---

## 8. Credits & acknowledgements

ToPlay stands on the shoulders of some excellent open‑source work — huge thanks
to the people behind these projects:

- **[Sunshine](https://github.com/LizardByte/Sunshine)** by **LizardByte**
  (GPL‑3.0). Sunshine is a self‑hosted, LAN game‑streaming host, and it was the
  inspiration for ToPlay's whole approach — in particular the idea of a local,
  self‑managed Certificate Authority that's trusted on the host so the stream
  gets a clean, warning‑free HTTPS padlock. ToPlay is an independent project and
  does **not** reuse Sunshine's code, but it wouldn't exist without the ideas and
  the trail Sunshine (and its companion client **Moonlight**) blazed. ⭐ Please
  go star their repo.
- **[SIPSorcery](https://github.com/sipsorcery-org/sipsorcery)** (BSD/MIT) — the
  pure‑C# WebRTC stack that carries the video track and touch data channel.
- **[FFmpeg](https://ffmpeg.org/)** (LGPL/GPL) — screen capture (`gdigrab`) and
  low‑latency H.264 encoding. FFmpeg is bundled unmodified as a standalone
  executable and invoked as a separate process.
- **[BCrypt.Net‑Next](https://github.com/BcryptNet/bcrypt.net)** (MIT) — password
  hashing.
- The **WebRTC**, **PWA**, and **Windows Touch Injection** platform APIs that make
  a no‑install, browser‑only phone client possible.

> **Note on licensing:** Sunshine is GPL‑3.0. Because ToPlay only takes
> *inspiration* from it (no Sunshine source is copied or linked), ToPlay isn't
> required to be GPL — but if you ever do incorporate GPL‑3.0 code, your project
> must comply with the GPL. FFmpeg is redistributed as a separate, unmodified
> binary, which keeps its LGPL/GPL terms self‑contained.

If you appreciate ToPlay, please also consider giving **Sunshine** and
**Moonlight** a star — they made low‑latency, self‑hosted streaming approachable
for everyone.
