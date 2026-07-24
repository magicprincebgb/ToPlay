using System.Runtime.Versioning;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ToPlay.Host.Media;

/// <summary>
/// Captures the PC's speaker output (WASAPI loopback) and encodes it to
/// low-latency Opus (48 kHz stereo, 20 ms frames) for the phone.
///
/// Design goals (competitive gaming — positional footsteps / gunshots):
///  • <b>No drift.</b> Encoding is driven directly by the sound-card capture
///    callback, so one 20 ms Opus frame == 960 RTP ticks == real time. The
///    video track also advances at real time, so neither clock drifts against
///    the wall clock and therefore they can't drift against each other — audio
///    stays locked to the picture for the whole session (see
///    <see cref="WebRtc.StreamSession"/> for why we skip strict lip-sync
///    buffering to keep audio latency minimal for gaming).
///  • <b>No stalls on silence.</b> WASAPI loopback stops delivering buffers when
///    the render endpoint is totally silent, which would freeze the audio RTP
///    timeline and desync it from video on resume. We keep a silent render
///    stream playing so the endpoint never goes idle and the callback keeps
///    firing (we then emit silent Opus frames, keeping the timeline moving).
///
/// Reference-counted like <see cref="ScreenStreamer"/>: the capture only runs
/// while at least one listener is attached, so the video-only path (audio
/// toggle OFF) never touches the audio hardware.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioStreamer : IDisposable
{
    // Opus/WebRTC audio is 48 kHz. Stereo is REQUIRED for positional cues
    // (footstep direction); a 20 ms frame is 960 samples per channel.
    private const int TargetRate = 48000;
    private const int Channels = 2;
    private const int FrameSamples = 960;                 // per channel, 20 ms

    private readonly object _gate = new();

    private WasapiLoopbackCapture? _capture;
    private IWavePlayer? _silence;                         // keep-alive render
    private IOpusEncoder? _encoder;
    private int _listeners;
    private bool _disposed;

    // One reusable interleaved [L,R,L,R,...] frame buffer + Opus output buffer.
    private readonly short[] _frameBuf = new short[FrameSamples * Channels];
    private readonly byte[] _encodeBuf = new byte[4000];
    private int _frameFill;

    // Linear-resampler state, carried across capture buffers so there is no
    // click/discontinuity at buffer boundaries when the device rate != 48 kHz.
    private double _phase;
    private float _prevL, _prevR;
    private bool _havePrev;

    // Scratch de-interleaved channel buffers (grown as needed).
    private float[] _scratchL = Array.Empty<float>();
    private float[] _scratchR = Array.Empty<float>();

    /// <summary>Raised on the capture thread for every encoded 20 ms Opus frame.</summary>
    public event Action<byte[]>? AudioFrameReady;

    /// <summary>Increment listener count; starts capture on the first listener.</summary>
    public void AddListener()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _listeners++;
            if (_listeners == 1) StartLocked();
        }
    }

    /// <summary>Decrement listener count; stops capture when the last leaves.</summary>
    public void RemoveListener()
    {
        lock (_gate)
        {
            if (_listeners == 0) return;
            _listeners = Math.Max(0, _listeners - 1);
            if (_listeners == 0) StopLocked();
        }
    }

    private void StartLocked()
    {
        try
        {
            // Build via the factory (uses a native Opus build if the platform
            // has one, else the bundled pure-C# implementation). 128 kbps stereo
            // is transparent for game audio; VBR keeps the baseline tiny during
            // silence (good on Wi-Fi) while never stalling the timeline (DTX
            // stays OFF). In-band FEC lets Opus reconstruct a dropped packet from
            // the next one — cheap insurance against Wi-Fi micro-drops with no
            // added latency.
            var enc = OpusCodecFactory.CreateEncoder(TargetRate, Channels,
                OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY, null);
            enc.Bitrate = 128000;
            enc.Complexity = 5;
            enc.UseVBR = true;
            enc.UseInbandFEC = true;
            _encoder = enc;

            // Reset per-session encode/resample state so a stale partial frame
            // from a previous session can't leak into this one.
            _frameFill = 0;
            _phase = 0;
            _havePrev = false;

            _capture = new WasapiLoopbackCapture();          // default render endpoint
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            // Keep-alive: play digital silence on the same endpoint so loopback
            // keeps delivering buffers even when nothing else is making sound.
            try
            {
                _silence = new WasapiOut(AudioClientShareMode.Shared, 100);
                _silence.Init(new SilenceProvider(_capture.WaveFormat));
                _silence.Play();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[audio] silence keep-alive unavailable: {ex.Message}");
                _silence = null;
            }

            _capture.StartRecording();
            Console.WriteLine($"[audio] loopback capture started ({_capture.WaveFormat}).");
        }
        catch (Exception ex)
        {
            // Audio is best-effort: never let a capture failure break video.
            Console.WriteLine($"[audio] could not start loopback capture: {ex.Message}");
            StopLocked();
        }
    }

    private void StopLocked()
    {
        var cap = _capture;
        _capture = null;
        if (cap != null)
        {
            cap.DataAvailable -= OnDataAvailable;
            cap.RecordingStopped -= OnRecordingStopped;
            try { cap.StopRecording(); } catch { }
            try { cap.Dispose(); } catch { }
        }

        var sil = _silence;
        _silence = null;
        if (sil != null)
        {
            try { sil.Stop(); } catch { }
            try { sil.Dispose(); } catch { }
        }

        try { (_encoder as IDisposable)?.Dispose(); } catch { }
        _encoder = null;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Console.WriteLine($"[audio] recording stopped: {e.Exception.Message}");
    }

    // ---- Capture callback: resample → 48 kHz stereo → Opus -----------------

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var enc = _encoder;
        var fmt = _capture?.WaveFormat;
        if (enc == null || fmt == null || e.BytesRecorded <= 0) return;

        try
        {
            int channels = fmt.Channels;
            int bits = fmt.BitsPerSample;
            int blockAlign = fmt.BlockAlign > 0 ? fmt.BlockAlign : Math.Max(1, channels * (bits / 8));
            int frames = e.BytesRecorded / blockAlign;
            if (frames <= 0) return;

            if (_scratchL.Length < frames)
            {
                _scratchL = new float[frames];
                _scratchR = new float[frames];
            }

            var buf = e.Buffer;
            var encFmt = fmt.Encoding;
            int sampleBytes = bits / 8;
            int off = 0;
            for (int f = 0; f < frames; f++)
            {
                float s0 = ReadSample(buf, off, encFmt, bits);
                float s1 = channels >= 2 ? ReadSample(buf, off + sampleBytes, encFmt, bits) : s0;
                _scratchL[f] = s0;
                _scratchR[f] = s1;
                off += blockAlign;
            }

            ResampleAndAccumulate(_scratchL, _scratchR, frames, fmt.SampleRate);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[audio] capture processing error: {ex.Message}");
        }
    }

    private static float ReadSample(byte[] buf, int pos, WaveFormatEncoding enc, int bits)
    {
        if (enc == WaveFormatEncoding.IeeeFloat && bits == 32)
            return BitConverter.ToSingle(buf, pos);

        switch (bits)
        {
            case 16: return BitConverter.ToInt16(buf, pos) / 32768f;
            case 32: return BitConverter.ToInt32(buf, pos) / 2147483648f;
            case 24:
            {
                int v = buf[pos] | (buf[pos + 1] << 8) | ((sbyte)buf[pos + 2] << 16);
                return v / 8388608f;
            }
            default: return 0f;
        }
    }

    /// <summary>
    /// Linear-resamples the buffer to 48 kHz (a no-op at 48 kHz) and feeds the
    /// result into the fixed 20 ms frame buffer, emitting an Opus frame each
    /// time 960 stereo samples have accumulated.
    /// </summary>
    private void ResampleAndAccumulate(float[] l, float[] r, int count, int srcRate)
    {
        if (count <= 0) return;

        if (srcRate == TargetRate)
        {
            for (int i = 0; i < count; i++) AppendSample(l[i], r[i]);
            return;
        }

        // Combined index space: index 0 = previous buffer's last sample,
        // index i+1 = current buffer sample i. Interpolate between them so the
        // seam between capture buffers stays continuous.
        if (!_havePrev)
        {
            _prevL = l[0];
            _prevR = r[0];
            _havePrev = true;
            _phase = 0;
        }

        double step = (double)srcRate / TargetRate;   // input samples per output sample
        double p = _phase;
        while (p < count)
        {
            int i0 = (int)Math.Floor(p);
            double frac = p - i0;
            float c0L = i0 == 0 ? _prevL : l[i0 - 1];
            float c0R = i0 == 0 ? _prevR : r[i0 - 1];
            float c1L = l[i0];
            float c1R = r[i0];
            AppendSample(
                (float)(c0L * (1 - frac) + c1L * frac),
                (float)(c0R * (1 - frac) + c1R * frac));
            p += step;
        }

        _phase = p - count;
        _prevL = l[count - 1];
        _prevR = r[count - 1];
    }

    private void AppendSample(float lf, float rf)
    {
        _frameBuf[_frameFill++] = FloatToShort(lf);
        _frameBuf[_frameFill++] = FloatToShort(rf);
        if (_frameFill >= _frameBuf.Length)
        {
            _frameFill = 0;
            EncodeAndEmit();
        }
    }

    private void EncodeAndEmit()
    {
        var enc = _encoder;
        if (enc == null) return;
        try
        {
            int len = enc.Encode(_frameBuf, FrameSamples, _encodeBuf, _encodeBuf.Length);
            if (len > 0)
            {
                var packet = new byte[len];
                Buffer.BlockCopy(_encodeBuf, 0, packet, 0, len);
                AudioFrameReady?.Invoke(packet);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[audio] opus encode error: {ex.Message}");
        }
    }

    private static short FloatToShort(float f)
    {
        int v = (int)Math.Round(f * 32767f);
        if (v > short.MaxValue) v = short.MaxValue;
        else if (v < short.MinValue) v = short.MinValue;
        return (short)v;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _listeners = 0;
            StopLocked();
        }
    }
}
