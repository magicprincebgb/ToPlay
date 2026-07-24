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
    private bool _listenerAdded;
    private volatile bool _canSend;
    private volatile bool _waitingForKeyframe = true;
    private bool _disposed;

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
                if (_audioEnabled && _audio != null && !_listenerAdded)
                {
                    _listenerAdded = true;
                    _audio.AudioFrameReady += OnAudioFrame;
                    _audio.AddListener();
                }
                _canSend = true;
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
            _pc.SendVideo(frame.DurationRtp, frame.AnnexB);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[webrtc:{_id}] SendVideo failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pushes one 20 ms Opus frame. The frame is paced by the sound-card clock,
    /// so advancing the RTP timestamp by a fixed 960 units keeps audio locked to
    /// real time (and therefore to the equally real-time video track).
    /// </summary>
    private void OnAudioFrame(byte[] opus)
    {
        if (!_canSend || _disposed) return;
        try
        {
            _pc.SendAudio(960, opus);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[webrtc:{_id}] SendAudio failed: {ex.Message}");
        }
    }


    /// <summary>Accepts the browser's SDP offer and returns our SDP answer.</summary>
    public async Task<string?> AcceptOfferAsync(string offerSdp)
    {
        // PC->phone audio is opt-in: the phone only puts an m=audio line in its
        // offer when the user turned "PC sound" on. We must add our matching
        // send-only Opus track BEFORE setRemoteDescription so it lands in the
        // same BUNDLE group and gets answered. With the toggle OFF there is no
        // m=audio, we add nothing, and the video-only path is byte-for-byte
        // unchanged.
        if (_audio != null && ContainsAudioMedia(offerSdp))
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

        if (setResult != SetDescriptionResultEnum.OK)
        {
            Console.WriteLine($"[webrtc:{_id}] setRemoteDescription failed: {setResult}");
            return null;
        }

        RTCSessionDescriptionInit answer;
        try
        {
            answer = _pc.createAnswer(null);
            await _pc.setLocalDescription(answer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[webrtc:{_id}] createAnswer failed: {ex.Message}");
            return null;
        }

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

        return answer.sdp;
    }

    private static bool ContainsAudioMedia(string sdp)
    {
        foreach (var line in sdp.Split('\n'))
            if (line.StartsWith("m=audio", StringComparison.Ordinal))
                return true;
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

        if (_listenerAdded && _audio != null)
        {
            _listenerAdded = false;
            try { _audio.AudioFrameReady -= OnAudioFrame; } catch { }
            try { _audio.RemoveListener(); } catch { }
        }


        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }

        try { Closed?.Invoke(); } catch { }
    }
}
