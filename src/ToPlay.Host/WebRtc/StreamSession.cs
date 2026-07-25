using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using ToPlay.Host.Input;
using ToPlay.Host.Media;


namespace ToPlay.Host.WebRtc;

/// <summary>
/// A single browser viewer session: owns one RTCPeerConnection, pushes encoded
/// desktop frames out on a send-only H.264 video track, and routes the inbound
/// touch data channel into the OS touch injector.
/// </summary>
public sealed class StreamSession : IDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly ScreenStreamer _streamer;
    private readonly InputRouter _input;
    private readonly AudioStreamer? _audio;
    private readonly string _id;

    private bool _viewerAdded;
    private bool _audioEnabled;      // phone asked for PC sound (m=audio in offer)
    private volatile bool _canSend;
    private volatile bool _waitingForKeyframe = true;
    private volatile bool _disposed;

    // Audio attach/detach is refcounted on the shared AudioStreamer, so it must
    // happen exactly once per session even though it is started off-thread.
    private readonly object _audioGate = new();
    private bool _audioAttached;
    private volatile bool _audioNegotiated;

    // SRTP protects every outgoing packet with ONE shared cipher context, so the
    // video thread and the audio thread must never encrypt at the same time —
    // concurrent sends corrupt packets (black picture, dead data channel).
    private readonly object _sendGate = new();
    private long _lastSendErrorLog;

    private RTCDataChannel? _dc;




    public string Id => _id;

    /// <summary>Fired for locally gathered ICE candidates (for trickle signaling).</summary>
    public event Action<RTCIceCandidate>? LocalIceCandidate;

    /// <summary>Fired when the peer connection state changes.</summary>
    public event Action<RTCPeerConnectionState>? StateChanged;

    /// <summary>Fired exactly once when this session is disposed/closed.</summary>
    public event Action? Closed;

    public StreamSession(string id, ScreenStreamer streamer, InputRouter input, AudioStreamer? audio = null)
    {
        _id = id;
        _streamer = streamer;
        _input = input;
        _audio = audio;


        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            // LAN only: host ICE candidates are enough, no STUN/TURN needed.
            iceServers = new List<RTCIceServer>()
        });

        var videoTrack = new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.H264, 96),
            MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(videoTrack);

        _pc.onicecandidate += c =>
        {
            if (c != null) LocalIceCandidate?.Invoke(c);
        };

        _pc.OnVideoFormatsNegotiated += _ =>
        {
            // Format agreed; sending becomes valid once ICE/DTLS is connected.
        };

        // Only push Opus once the browser has actually agreed an audio format.
        // Sending before that throws once per 20 ms frame, and the resulting
        // 50-lines-a-second error storm alone is enough to stall the stream.
        _pc.OnAudioFormatsNegotiated += _ => _audioNegotiated = true;


        _pc.onconnectionstatechange += OnConnectionStateChange;

        _pc.ondatachannel += OnDataChannel;

        _streamer.FrameReady += OnFrame;
    }

    private void OnDataChannel(RTCDataChannel dc)
    {
        Console.WriteLine($"[webrtc:{_id}] data channel '{dc.label}' opened.");
        _dc = dc;
        dc.onmessage += (_, _, data) =>
        {
            try
            {
                var text = Encoding.UTF8.GetString(data);

                // Latency probe: bounce it straight back so the phone can measure
                // round-trip time. Kept out of the input pipeline so it never
                // competes with touch injection.
                if (text.Contains("\"ping\"") && TryEchoPing(text)) return;

                _input.Handle(text);
            }
            catch (Exception ex) { Console.WriteLine($"[webrtc:{_id}] input error: {ex.Message}"); }
        };
    }

    /// <summary>
    /// If <paramref name="text"/> is a <c>{"t":"ping","ts":N}</c> probe, reply
    /// with the same timestamp as a <c>pong</c> and return true.
    /// </summary>
    private bool TryEchoPing(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("t", out var t) && t.GetString() == "ping")
            {
                var ts = root.TryGetProperty("ts", out var tsEl) ? tsEl.GetRawText() : "0";
                Send($"{{\"t\":\"pong\",\"ts\":{ts}}}");
                return true;
            }
        }
        catch { /* not a ping; fall through to normal handling */ }
        return false;
    }

    /// <summary>Sends a JSON message to the phone over the input data channel (best effort).</summary>
    public void Send(string json)
    {
        var dc = _dc;
        if (dc == null || dc.readyState != RTCDataChannelState.open) return;
        try { dc.send(json); } catch { /* channel may be closing */ }
    }


    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        Console.WriteLine($"[webrtc:{_id}] state = {state}");
        StateChanged?.Invoke(state);

        switch (state)
        {
            case RTCPeerConnectionState.connected:
                if (!_viewerAdded)
                {
                    _viewerAdded = true;
                    _waitingForKeyframe = true;
                    _streamer.AddViewer();
                }

                // Video + touch go live immediately.
                _canSend = true;

                // Sound is started on a worker thread ON PURPOSE. We are on
                // SIPSorcery's connection-event thread here, which also drives
                // DTLS/SCTP setup: opening the WASAPI loopback device can block
                // it for hundreds of milliseconds, which used to delay the data
                // channel and the first keyframe — the "black screen and touch
                // doesn't work when PC sound is on" bug.
                if (_audioEnabled && _audio != null)
                    _ = Task.Run(StartAudioSafe);
                break;



            case RTCPeerConnectionState.disconnected:
            case RTCPeerConnectionState.failed:
            case RTCPeerConnectionState.closed:
                Dispose();
                break;
        }
    }

    private void OnFrame(EncodedFrame frame)
    {
        if (!_canSend || _disposed) return;

        // Don't start mid-GOP: wait for the first keyframe so the decoder has SPS/PPS.
        if (_waitingForKeyframe)
        {
            if (!frame.IsKeyframe) return;
            _waitingForKeyframe = false;
        }

        try
        {
            lock (_sendGate) _pc.SendVideo(frame.DurationRtp, frame.AnnexB);
        }
        catch (Exception ex)
        {
            LogSendError("SendVideo", ex);
        }
    }


    /// <summary>
    /// Pushes one 20 ms Opus frame. The frame is paced by the sound-card clock,
    /// so advancing the RTP timestamp by a fixed 960 units keeps audio locked to
    /// real time (and therefore to the equally real-time video track).
    /// </summary>
    private void OnAudioFrame(byte[] opus)
    {
        // _audioNegotiated: never push RTP the browser hasn't agreed to receive.
        if (!_canSend || _disposed || !_audioNegotiated) return;
        try
        {
            lock (_sendGate) _pc.SendAudio(960, opus);
        }
        catch (Exception ex)
        {
            LogSendError("SendAudio", ex);
        }
    }

    /// <summary>
    /// Attaches this session to the shared loopback capture (starting it if we
    /// are the first listener). Safe to call from a worker thread and safe to
    /// race with <see cref="Dispose"/>.
    /// </summary>
    private void StartAudioSafe()
    {
        var audio = _audio;
        if (audio == null) return;

        lock (_audioGate)
        {
            if (_disposed || _audioAttached) return;
            _audioAttached = true;
            try
            {
                audio.AudioFrameReady += OnAudioFrame;
                audio.AddListener();
                Console.WriteLine($"[webrtc:{_id}] PC sound attached.");
            }
            catch (Exception ex)
            {
                _audioAttached = false;
                try { audio.AudioFrameReady -= OnAudioFrame; } catch { }
                Console.WriteLine($"[webrtc:{_id}] PC sound unavailable: {ex.Message}");
            }
        }
    }

    /// <summary>Detaches from the shared capture (stops it when nobody is left).</summary>
    private void StopAudioSafe()
    {
        var audio = _audio;
        if (audio == null) return;

        lock (_audioGate)
        {
            if (!_audioAttached) return;
            _audioAttached = false;
            try { audio.AudioFrameReady -= OnAudioFrame; } catch { }
            try { audio.RemoveListener(); } catch { }
        }
    }

    /// <summary>
    /// Logs a send failure at most once every 5 seconds. Media is sent 50-120
    /// times per second, so an unthrottled message here becomes a console flood
    /// that stalls the whole host (the Control Panel reads this output).
    /// </summary>
    private void LogSendError(string what, Exception ex)
    {
        var now = Environment.TickCount64;
        if (now - _lastSendErrorLog < 5000) return;
        _lastSendErrorLog = now;
        Console.WriteLine($"[webrtc:{_id}] {what} failed: {ex.Message}");
    }



    /// <summary>
    /// How long the host may spend producing an answer before it gives up. Kept
    /// well inside the phone's own patience so the player never has to sit on a
    /// black screen waiting for us.
    /// </summary>
    private static readonly TimeSpan AnswerDeadline = TimeSpan.FromSeconds(3);

    /// <summary>Accepts the browser's SDP offer and returns our SDP answer.</summary>
    public async Task<string?> AcceptOfferAsync(string offerSdp)
    {
        bool wantAudio = _audio != null && ContainsAudioMedia(offerSdp);
        Console.WriteLine($"[webrtc:{_id}] offer received (PC sound requested: {(wantAudio ? "yes" : "no")}).");

        // Building the answer is synchronous work inside the WebRTC stack, and
        // with an extra audio track in play it can stall. Running it inline froze
        // the whole signalling connection with it — ICE candidates included — so
        // the phone stared at a black screen until its own watchdog gave up
        // seconds later. Now it runs off the signalling loop under a hard
        // deadline: if anything stalls we say "no" at once and the phone drops
        // straight back to video-only instead of freezing.
        var work = Task.Run(() => BuildAnswerAsync(offerSdp, wantAudio));
        var winner = await Task.WhenAny(work, Task.Delay(AnswerDeadline)).ConfigureAwait(false);

        if (winner != work)
        {
            Console.WriteLine($"[webrtc:{_id}] no answer after {AnswerDeadline.TotalSeconds:0.#}s" +
                              (wantAudio ? " with PC sound — falling back to video only." : " — giving up."));
            _audioEnabled = false;
            return null;
        }

        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[webrtc:{_id}] negotiation failed: {ex.Message}");
            _audioEnabled = false;
            return null;
        }
    }

    /// <summary>
    /// The handshake itself. Every step logs how long it took, so if the WebRTC
    /// stack ever stalls again the host log says exactly where.
    /// </summary>
    private async Task<string?> BuildAnswerAsync(string offerSdp, bool wantAudio)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // PC->phone audio is opt-in: the phone only puts an m=audio line in its
        // offer when the user turned "PC sound" on. We must add our matching
        // send-only Opus track BEFORE setRemoteDescription so it lands in the
        // same BUNDLE group and gets answered. With the toggle OFF there is no
        // m=audio, we add nothing, and the video-only path is byte-for-byte
        // unchanged.
        if (wantAudio)
        {
            try
            {
                var audioTrack = new MediaStreamTrack(
                    // 48 kHz stereo Opus — stereo is required for positional audio
                    // (hearing which direction footsteps come from).
                    new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1"),
                    MediaStreamStatusEnum.SendOnly);
                _pc.addTrack(audioTrack);
                _audioEnabled = true;
                Console.WriteLine($"[webrtc:{_id}] audio track attached ({sw.ElapsedMilliseconds} ms).");
            }
            catch (Exception ex)
            {
                // Optional audio must NEVER break the proven video path.
                Console.WriteLine($"[webrtc:{_id}] could not add audio track: {ex.Message}");
                _audioEnabled = false;
            }
        }

        var setResult = _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp
        });
        Console.WriteLine($"[webrtc:{_id}] offer applied: {setResult} ({sw.ElapsedMilliseconds} ms).");

        if (setResult != SetDescriptionResultEnum.OK)
        {
            Console.WriteLine($"[webrtc:{_id}] setRemoteDescription failed: {setResult}");
            _audioEnabled = false;
            return null;
        }

        // Anything thrown from here is reported by the caller (which then falls
        // back to video-only), together with the step timings printed above.
        var answer = _pc.createAnswer(null);
        Console.WriteLine($"[webrtc:{_id}] answer created ({sw.ElapsedMilliseconds} ms).");

        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        Console.WriteLine($"[webrtc:{_id}] answer ready ({sw.ElapsedMilliseconds} ms).");

        // A/V sync note: both tracks are paced to real time — video advances by a
        // fixed RTP duration per frame at the real capture FPS, and audio advances
        // exactly 960 RTP units per sound-card-delivered 20 ms Opus frame. Because
        // neither clock drifts against the wall clock, they cannot drift against
        // each other, so footsteps/gunshots stay aligned with the picture for the
        // whole session. We deliberately do NOT try to force the browser into
        // strict lip-sync buffering (SIPSorcery keeps a separate RTCP CNAME per
        // media stream and exposes no public setter to share one): that buffering
        // would DELAY audio to match the video's larger encode latency, adding lag
        // to exactly the cues a competitive player needs earliest. Rendering each
        // track as soon as it arrives keeps audio latency minimal while the equal
        // real-time pacing keeps the two in step.

        // Sanity-check the answer before we hand it to the phone. If adding the
        // optional audio track upset the negotiation — a missing/rejected video
        // or data section, or no audio section after all — the phone would get a
        // black screen with dead touch. Refusing the answer instead makes the
        // player fall straight back to the rock-solid video-only path.
        if (_audioEnabled)
        {
            bool video = MediaActive(answer.sdp, "video");
            bool audio = MediaActive(answer.sdp, "audio");
            bool data = !MediaActive(offerSdp, "application") || MediaActive(answer.sdp, "application");
            if (!video || !audio || !data)
            {
                Console.WriteLine($"[webrtc:{_id}] PC sound broke the negotiation " +
                                  $"(video={video}, audio={audio}, input={data}); retrying video-only.");
                _audioEnabled = false;
                return null;
            }
        }

        return answer.sdp;
    }

    private static bool ContainsAudioMedia(string sdp) => MediaActive(sdp, "audio");

    /// <summary>
    /// True when the SDP has an <c>m=&lt;kind&gt;</c> section with a non-zero
    /// port (port 0 means the section was rejected).
    /// </summary>
    private static bool MediaActive(string sdp, string kind)
    {
        var prefix = "m=" + kind + " ";
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var port)) return port != 0;
            return true;
        }
        return false;
    }



    public void AddRemoteIceCandidate(string candidate, string? sdpMid, ushort sdpMLineIndex)
    {
        try
        {
            _pc.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMid = sdpMid,
                sdpMLineIndex = sdpMLineIndex
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[webrtc:{_id}] addIceCandidate failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _canSend = false;

        _streamer.FrameReady -= OnFrame;
        if (_viewerAdded)
        {
            _viewerAdded = false;
            try { _streamer.RemoveViewer(); } catch { }
        }

        StopAudioSafe();



        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }

        try { Closed?.Invoke(); } catch { }
    }
}
