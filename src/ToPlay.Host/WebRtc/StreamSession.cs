using System.Text;
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
    private readonly string _id;

    private bool _viewerAdded;
    private volatile bool _canSend;
    private volatile bool _waitingForKeyframe = true;
    private bool _disposed;

    public string Id => _id;

    /// <summary>Fired for locally gathered ICE candidates (for trickle signaling).</summary>
    public event Action<RTCIceCandidate>? LocalIceCandidate;

    /// <summary>Fired when the peer connection state changes.</summary>
    public event Action<RTCPeerConnectionState>? StateChanged;

    /// <summary>Fired exactly once when this session is disposed/closed.</summary>
    public event Action? Closed;

    public StreamSession(string id, ScreenStreamer streamer, InputRouter input)
    {
        _id = id;
        _streamer = streamer;
        _input = input;

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
        dc.onmessage += (_, _, data) =>
        {
            try { _input.Handle(Encoding.UTF8.GetString(data)); }
            catch (Exception ex) { Console.WriteLine($"[webrtc:{_id}] input error: {ex.Message}"); }
        };
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

    /// <summary>Accepts the browser's SDP offer and returns our SDP answer.</summary>
    public async Task<string?> AcceptOfferAsync(string offerSdp)
    {
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

        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);
        return answer.sdp;
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

        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }

        try { Closed?.Invoke(); } catch { }
    }
}
