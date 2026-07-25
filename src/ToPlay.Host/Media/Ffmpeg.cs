using System.Diagnostics;
using ToPlay.Host.Config;
using ToPlay.Host.Display;

namespace ToPlay.Host.Media;

/// <summary>
/// Locates ffmpeg.exe, detects which H.264 hardware encoders are usable on this
/// machine, and builds the low-latency capture+encode command line.
/// </summary>
public static class Ffmpeg
{
    /// <summary>Encoder backend -> ffmpeg codec name.</summary>
    private static readonly (EncoderBackend backend, string codec)[] Order =
    [
        (EncoderBackend.Nvenc,     "h264_nvenc"),
        (EncoderBackend.QuickSync, "h264_qsv"),
        (EncoderBackend.Amf,       "h264_amf"),
        (EncoderBackend.Software,  "libx264"),
    ];

    public static string? Locate(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // ./tools/ffmpeg.exe next to the app
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var localFlat = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(localFlat)) return localFlat;

        // PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore malformed PATH entries */ }
        }
        return null;
    }

    /// <summary>Lists codec names ffmpeg reports it can encode with.</summary>
    public static HashSet<string> ListEncoders(string ffmpegPath)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);

            foreach (var (_, codec) in Order)
                if (output.Contains(codec, StringComparison.OrdinalIgnoreCase))
                    found.Add(codec);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ffmpeg] Could not list encoders: {ex.Message}");
        }
        return found;
    }

    // Caches probe results so we don't re-run the (slightly slow) encoder tests
    // every time the user tweaks a setting. Results don't change within a run.
    private static readonly Dictionary<string, bool> ProbeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ProbeGate = new();

    /// <summary>
    /// Verifies an encoder can actually be *initialized* on this machine, not
    /// merely that it was compiled into ffmpeg. This matters because e.g.
    /// h264_nvenc/h264_qsv/h264_amf are always listed by "-encoders" but fail at
    /// runtime when the GPU/driver/runtime DLL is missing (Cannot load
    /// nvcuda.dll, Error creating a MFX session, amfrt64.dll failed to open...),
    /// which previously left the phone on a pitch-black screen with no fallback.
    /// </summary>
    public static bool IsEncoderUsable(string ffmpegPath, string codec)
    {
        lock (ProbeGate)
            if (ProbeCache.TryGetValue(codec, out var cached))
                return cached;

        bool ok = ProbeEncoder(ffmpegPath, codec);
        lock (ProbeGate) ProbeCache[codec] = ok;
        Console.WriteLine($"[ffmpeg] encoder probe {codec}: {(ok ? "OK" : "unavailable")}");
        return ok;
    }

    private static bool ProbeEncoder(string ffmpegPath, string codec)
    {
        try
        {
            // Encode a handful of tiny black frames from a synthetic source and
            // throw the output away. Non-zero exit == this encoder can't run here.
            string args = string.Join(' ',
                "-hide_banner -loglevel error",
                "-f lavfi -i color=c=black:s=256x144:r=15",
                "-frames:v 5 -an",
                $"-c:v {codec} -pix_fmt yuv420p",
                "-f null -");

            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            string err = p.StandardError.ReadToEnd();

            if (!p.WaitForExit(10000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            try { outTask.Wait(500); } catch { }

            if (p.ExitCode != 0)
            {
                var firstLine = err.Split('\n')
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                if (!string.IsNullOrEmpty(firstLine))
                    Console.WriteLine($"[ffmpeg] {codec} probe failed: {firstLine}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ffmpeg] {codec} probe error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolves the encoder codec to use given the user's preference and what is
    /// actually usable on this machine. Probes each candidate so we never hand
    /// back an encoder that opens to a black screen, and always ends at libx264
    /// so ANY PC gets a picture without the user manually cycling encoders.
    /// </summary>
    public static (EncoderBackend backend, string codec) ResolveEncoder(
        EncoderBackend preferred, string ffmpegPath, HashSet<string> compiledIn)
    {
        bool Usable(string codec) =>
            compiledIn.Contains(codec) && IsEncoderUsable(ffmpegPath, codec);

        if (preferred != EncoderBackend.Auto)
        {
            var wanted = Order.First(o => o.backend == preferred);
            if (Usable(wanted.codec)) return wanted;
            Console.WriteLine($"[ffmpeg] Preferred encoder {wanted.codec} is not usable on this PC; auto-selecting a working one.");
        }

        foreach (var o in Order)
            if (Usable(o.codec))
                return o;

        // libx264 should always be present in full builds; last resort anyway.
        Console.WriteLine("[ffmpeg] No encoder passed probing; falling back to libx264.");
        return (EncoderBackend.Software, "libx264");
    }

    /// <summary>
    /// Builds the ffmpeg argument string that captures the given monitor and
    /// emits a low-latency Annex-B H.264 elementary stream to stdout.
    /// </summary>
    public static string BuildArgs(string codec, MonitorInfo monitor, QualityPreset preset)
    {
        int fps = Math.Clamp(preset.Fps, 15, 120);
        int bitrate = Math.Clamp(preset.BitrateKbps, 1000, 60000);
        int gop = fps * 2;

        // Scale to target height (keep aspect, even width) unless native (0).
        string scale = preset.Height > 0
            ? $"scale=-2:{preset.Height}:flags=fast_bilinear,"
            : string.Empty;

        // Hardware encoders consume NV12 natively; handing them yuv420p forces an
        // extra colour conversion inside the encoder on every single frame. Only
        // libx264 actually wants planar yuv420p.
        bool hardware = codec is "h264_nvenc" or "h264_qsv" or "h264_amf";
        string pixFmt = hardware ? "nv12" : "yuv420p";

        // Every option here exists to remove a frame of delay:
        //   -bf 0          no B-frames: a B-frame cannot be encoded until the
        //                  NEXT frame arrives, so it costs a full frame of lag.
        //                  x264's zerolatency tune sets this, but QSV and AMF
        //                  happily use B-frames unless told not to.
        //   lookahead 0    the encoder must never hold frames back to plan ahead.
        //   ull / ultralowlatency
        //                  the vendors' lowest-latency rate-control mode.
        //   no-scenecut    a scene change must not silently insert an IDR (and a
        //                  bitrate spike) mid-fight.
        string encoderOpts = codec switch
        {
            "h264_nvenc"  => "-preset p1 -tune ull -rc cbr -zerolatency 1 -delay 0 -forced-idr 1 "
                           + "-rc-lookahead 0 -no-scenecut 1 -bf 0",
            "h264_qsv"    => "-preset veryfast -low_power 0 -async_depth 1 -look_ahead 0 -bf 0",
            "h264_amf"    => "-usage ultralowlatency -rc cbr -quality speed -preanalysis 0 -bf 0",
            _             => "-preset ultrafast -tune zerolatency -bf 0 "
                           + "-x264-params \"nal-hrd=cbr:repeat-headers=1:sliced-threads=1:sync-lookahead=0:rc-lookahead=0\"",
        };

        // gdigrab captures a rectangle of the virtual desktop at an offset.
        // "-fflags nobuffer -flags low_delay" keep ffmpeg from queueing frames
        // on the input side — every buffered frame is glass-to-glass latency.
        // "-thread_queue_size 4" caps the capture->encoder handoff queue: a deep
        // queue hides jitter by adding delay, which is exactly the wrong trade
        // for competitive play (better to drop a frame than to play it late).
        return string.Join(' ',
            "-hide_banner -loglevel warning -nostats",
            "-fflags nobuffer -flags low_delay -avioflags direct",
            $"-f gdigrab -framerate {fps} -draw_mouse 1 -thread_queue_size 4",
            $"-offset_x {monitor.X} -offset_y {monitor.Y}",
            $"-video_size {monitor.Width}x{monitor.Height} -i desktop",
            $"-vf \"{scale}format={pixFmt}\"",
            $"-c:v {codec} {encoderOpts}",
            $"-b:v {bitrate}k -maxrate {bitrate}k -bufsize {bitrate / 2}k",
            $"-g {gop} -keyint_min {fps} -pix_fmt {pixFmt}",
            "-bsf:v h264_metadata=aud=insert",
            "-f h264 -"                       // Annex-B elementary stream to stdout
        );

    }
}
