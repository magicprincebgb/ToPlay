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

    /// <summary>
    /// Resolves the encoder codec to use given the user's preference and what is
    /// actually available. Falls back down the chain.
    /// </summary>
    public static (EncoderBackend backend, string codec) ResolveEncoder(
        EncoderBackend preferred, HashSet<string> available)
    {
        if (preferred != EncoderBackend.Auto)
        {
            var wanted = Order.First(o => o.backend == preferred);
            if (available.Contains(wanted.codec)) return wanted;
            Console.WriteLine($"[ffmpeg] Preferred encoder {wanted.codec} not available; auto-selecting.");
        }

        foreach (var o in Order)
            if (available.Contains(o.codec))
                return o;

        // libx264 should always be present in full builds; last resort anyway.
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

        string encoderOpts = codec switch
        {
            "h264_nvenc"  => $"-preset p1 -tune ll -rc cbr -zerolatency 1 -delay 0 -forced-idr 1",
            "h264_qsv"    => $"-preset veryfast -low_power 0 -async_depth 1",
            "h264_amf"    => $"-usage lowlatency -rc cbr -quality speed",
            _             => $"-preset ultrafast -tune zerolatency -x264-params \"nal-hrd=cbr:repeat-headers=1\"",
        };

        // gdigrab captures a rectangle of the virtual desktop at an offset.
        return string.Join(' ',
            "-hide_banner -loglevel warning -nostats",
            $"-f gdigrab -framerate {fps} -draw_mouse 1",
            $"-offset_x {monitor.X} -offset_y {monitor.Y}",
            $"-video_size {monitor.Width}x{monitor.Height} -i desktop",
            $"-vf \"{scale}format=yuv420p\"",
            $"-c:v {codec} {encoderOpts}",
            $"-b:v {bitrate}k -maxrate {bitrate}k -bufsize {bitrate / 2}k",
            $"-g {gop} -keyint_min {fps} -pix_fmt yuv420p",
            "-bsf:v h264_metadata=aud=insert",
            "-f h264 -"                       // Annex-B elementary stream to stdout
        );
    }
}
