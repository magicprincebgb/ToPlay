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

        // Capture/encode should not be starved by other processes: a stalled
        // ffmpeg means dropped frames on the phone. AboveNormal (not High) is
        // enough to win against background tasks without hurting the game.
        try { _proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

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
        // Array-backed window (buf[head..tail)) instead of the old List<byte>
        // pipeline: List.Add per byte + O(n) RemoveRange per NAL made the
        // parser itself a CPU hotspot at high bitrates. BlockCopy + a moving
        // head index does the same job with near-zero per-frame overhead.
        var readBuf = new byte[1 << 16];
        byte[] buf = new byte[1 << 20];
        int head = 0;                       // first unconsumed byte
        int tail = 0;                       // one past the last valid byte
        var au = new MemoryStream(1 << 17); // current access unit
        bool auHasVcl = false;
        bool auHasIdr = false;              // tracked incrementally (no rescan on emit)
        bool aligned = false;

        void EmitAu()
        {
            if (au.Length == 0) return;
            try { FrameReady?.Invoke(new EncodedFrame(au.ToArray(), _frameDurationRtp, auHasIdr)); }
            catch (Exception ex) { Console.WriteLine($"[stream] frame handler error: {ex.Message}"); }
            au.SetLength(0);
            auHasVcl = false;
            auHasIdr = false;
        }

        // Finds the next 00 00 01 start code at or after head+from; returns
        // its offset relative to head, or -1 when not present yet.
        int FindStartCode(int from)
        {
            for (int i = head + Math.Max(0, from); i + 3 <= tail; i++)
            {
                if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1)
                    return i - head;
            }
            return -1;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = stdout.Read(readBuf, 0, readBuf.Length);
                if (n <= 0) break; // ffmpeg exited

                // Make room for the new chunk: compact the live window to the
                // front of the buffer, growing it only if truly necessary.
                if (tail + n > buf.Length)
                {
                    int live = tail - head;
                    if (live + n > buf.Length)
                    {
                        var bigger = new byte[Math.Max(buf.Length * 2, live + n)];
                        Buffer.BlockCopy(buf, head, bigger, 0, live);
                        buf = bigger;
                    }
                    else
                    {
                        Buffer.BlockCopy(buf, head, buf, 0, live);
                    }
                    head = 0;
                    tail = live;
                }
                Buffer.BlockCopy(readBuf, 0, buf, tail, n);
                tail += n;

                // Align to the first start code once.
                if (!aligned)
                {
                    int first = FindStartCode(0);
                    if (first < 0) { head = Math.Max(head, tail - 3); continue; }
                    head += first;
                    aligned = true;
                }

                // Process complete NALs (those terminated by the next start code).
                while (true)
                {
                    int next = FindStartCode(3);
                    if (next < 0) break; // current NAL not complete yet

                    // NAL = buf[head .. head+next)
                    int scLen = (buf[head + 2] == 1) ? 3 : 4; // 00 00 01 or 00 00 00 01
                    if (next < scLen + 1) { // malformed, drop
                        head += next;
                        continue;
                    }

                    int nalType = buf[head + scLen] & 0x1F;
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
                        && (buf[head + scLen + 1] & 0x80) != 0;

                    // A new access unit begins at an access-unit delimiter, at the
                    // first slice of a new picture, or at a parameter set that
                    // follows earlier picture data (a keyframe's SPS/PPS).
                    bool startsNewAu = auHasVcl &&
                        (isAud || firstSliceOfPicture || isParamSet);

                    if (startsNewAu)
                        EmitAu();

                    // Append this NAL (with its start code) to the current AU.
                    au.Write(buf, head, next);
                    if (isVcl) auHasVcl = true;
                    if (nalType == 5) auHasIdr = true; // IDR slice => keyframe AU

                    head += next;
                }

                // Safety valve: never let pending grow unbounded (bad stream).
                if (tail - head > (4 << 20))
                {
                    head = 0;
                    tail = 0;
                    aligned = false;
                    au.SetLength(0);
                    auHasVcl = false;
                    auHasIdr = false;
                }
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Console.WriteLine($"[stream] read loop error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate) StopLocked();
    }
}