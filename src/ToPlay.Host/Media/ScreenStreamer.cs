using System.Diagnostics;
using ToPlay.Host.Config;
using ToPlay.Host.Display;

namespace ToPlay.Host.Media;

/// <summary>
/// One encoded H.264 access unit (a full frame, including any SPS/PPS/SEI that
/// precede it) in Annex-B format, plus its presentation duration in 90 kHz units.
/// </summary>
public sealed record EncodedFrame(byte[] AnnexB, uint DurationRtp, bool IsKeyframe);

/// <summary>
/// Captures a monitor and encodes it to a low-latency H.264 elementary stream
/// using ffmpeg, parsing the byte stream into per-frame access units.
///
/// The streamer is reference-counted: it only runs ffmpeg while at least one
/// viewer is connected, and can be reconfigured (quality/monitor/encoder) live.
/// </summary>
public sealed class ScreenStreamer : IDisposable
{
    private readonly object _gate = new();
    private readonly string _ffmpegPath;

    private Process? _proc;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private int _viewers;

    private QualityPreset _preset;
    private MonitorInfo _monitor;
    private string _codec;
    private uint _frameDurationRtp;

    /// <summary>Raised on the reader thread for every encoded frame.</summary>
    public event Action<EncodedFrame>? FrameReady;

    public string ActiveCodec => _codec;
    public bool IsRunning => _proc is { HasExited: false };

    public ScreenStreamer(string ffmpegPath, MonitorInfo monitor, QualityPreset preset, string codec)
    {
        _ffmpegPath = ffmpegPath;
        _monitor = monitor;
        _preset = preset;
        _codec = codec;
        _frameDurationRtp = (uint)(90000 / Math.Max(1, preset.Fps));
    }

    /// <summary>Increment viewer count; starts ffmpeg on the first viewer.</summary>
    public void AddViewer()
    {
        lock (_gate)
        {
            _viewers++;
            if (_viewers == 1) StartLocked();
        }
    }

    /// <summary>Decrement viewer count; stops ffmpeg when the last viewer leaves.</summary>
    public void RemoveViewer()
    {
        lock (_gate)
        {
            _viewers = Math.Max(0, _viewers - 1);
            if (_viewers == 0) StopLocked();
        }
    }

    public void Reconfigure(MonitorInfo monitor, QualityPreset preset, string codec)
    {
        lock (_gate)
        {
            _monitor = monitor;
            _preset = preset;
            _codec = codec;
            _frameDurationRtp = (uint)(90000 / Math.Max(1, preset.Fps));
            if (_viewers > 0)
            {
                StopLocked();
                StartLocked();
            }
        }
    }

    private void StartLocked()
    {
        var args = Ffmpeg.BuildArgs(_codec, _monitor, _preset);
        Console.WriteLine($"[stream] ffmpeg {args}");

        var psi = new ProcessStartInfo(_ffmpegPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[stream] Failed to start ffmpeg: {ex.Message}");
            _proc = null;
            return;
        }

        if (_proc == null) return;

        _cts = new CancellationTokenSource();
        var stdout = _proc.StandardOutput.BaseStream;
        var token = _cts.Token;

        _proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine($"[ffmpeg] {e.Data}");
        };
        _proc.BeginErrorReadLine();

        _readTask = Task.Run(() => ReadLoop(stdout, token), token);
    }

    private void StopLocked()
    {
        try { _cts?.Cancel(); } catch { }

        var p = _proc;
        _proc = null;
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }

        _cts?.Dispose();
        _cts = null;
    }

    // ---- Annex-B access-unit parser ---------------------------------------

    private void ReadLoop(Stream stdout, CancellationToken token)
    {
        var readBuf = new byte[1 << 16];
        var pending = new List<byte>(1 << 18);
        var au = new List<byte>(1 << 16);   // current access unit
        bool auHasVcl = false;
        bool aligned = false;

        void EmitAu(bool keyframe)
        {
            if (au.Count == 0) return;
            try { FrameReady?.Invoke(new EncodedFrame(au.ToArray(), _frameDurationRtp, keyframe)); }
            catch (Exception ex) { Console.WriteLine($"[stream] frame handler error: {ex.Message}"); }
            au.Clear();
            auHasVcl = false;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = stdout.Read(readBuf, 0, readBuf.Length);
                if (n <= 0) break; // ffmpeg exited
                for (int k = 0; k < n; k++) pending.Add(readBuf[k]);

                // Align to the first start code once.
                if (!aligned)
                {
                    int first = FindStartCode(pending, 0);
                    if (first < 0) { TrimHead(pending, Math.Max(0, pending.Count - 3)); continue; }
                    if (first > 0) pending.RemoveRange(0, first);
                    aligned = true;
                }

                // Process complete NALs (those terminated by the next start code).
                while (true)
                {
                    int next = FindStartCode(pending, 3);
                    if (next < 0) break; // current NAL not complete yet

                    // NAL = pending[0..next)
                    int scLen = (pending[2] == 1) ? 3 : 4; // 00 00 01 or 00 00 00 01
                    if (next < scLen + 1) { // malformed, drop
                        pending.RemoveRange(0, next);
                        continue;
                    }

                    int nalType = pending[scLen] & 0x1F;
                    bool isVcl = nalType is >= 1 and <= 5;
                    bool isAud = nalType == 9;
                    bool isParamSet = nalType is 7 or 8; // SPS / PPS

                    // A single video frame may be split into several slice NALs
                    // (libx264's "-tune zerolatency" enables sliced-threads, so a
                    // 720p frame on a 6-core CPU arrives as many VCL slices). All
                    // slices of one picture MUST be delivered as ONE access unit;
                    // splitting them makes the decoder paint partial frames, which
                    // shows up on the phone as green/glitchy blocks. The first
                    // slice of a picture has first_mb_in_slice == 0, encoded as a
                    // leading '1' bit (MSB set) in the first slice-header byte (the
                    // byte immediately after the 1-byte NAL header); continuation
                    // slices have first_mb_in_slice > 0 and stay in the same AU.
                    bool firstSliceOfPicture = isVcl
                        && next > scLen + 1
                        && (pending[scLen + 1] & 0x80) != 0;

                    // A new access unit begins at an access-unit delimiter, at the
                    // first slice of a new picture, or at a parameter set that
                    // follows earlier picture data (a keyframe's SPS/PPS).
                    bool startsNewAu = auHasVcl &&
                        (isAud || firstSliceOfPicture || isParamSet);

                    if (startsNewAu)
                        EmitAu(keyframe: ContainsKeyframe(au));

                    // Append this NAL (with its start code) to the current AU.
                    for (int k = 0; k < next; k++) au.Add(pending[k]);
                    if (isVcl) auHasVcl = true;

                    pending.RemoveRange(0, next);
                }

                // Safety valve: never let pending grow unbounded (bad stream).
                if (pending.Count > (4 << 20))
                {
                    pending.Clear();
                    aligned = false;
                }
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Console.WriteLine($"[stream] read loop error: {ex.Message}");
        }
    }

    private static bool ContainsKeyframe(List<byte> au)
    {
        for (int i = 0; i + 4 < au.Count; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1)
            {
                int type = au[i + 3] & 0x1F;
                if (type == 5) return true;
            }
        }
        return false;
    }

    /// <summary>Finds the index of the next 00 00 01 start code at or after <paramref name="from"/>.</summary>
    private static int FindStartCode(List<byte> buf, int from)
    {
        for (int i = Math.Max(0, from); i + 3 <= buf.Count; i++)
        {
            if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1)
                return i;
        }
        return -1;
    }

    private static void TrimHead(List<byte> buf, int keepFrom)
    {
        if (keepFrom > 0 && keepFrom <= buf.Count)
            buf.RemoveRange(0, keepFrom);
    }

    public void Dispose()
    {
        lock (_gate) StopLocked();
    }
}
