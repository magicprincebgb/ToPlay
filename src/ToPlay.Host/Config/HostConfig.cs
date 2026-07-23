using System.Text.Json;

namespace ToPlay.Host.Config;

/// <summary>
/// Encoder backends we try, in order. The first one that FFmpeg reports as
/// available on this machine wins, unless the user forces a specific one.
/// </summary>
public enum EncoderBackend
{
    Auto = 0,
    Nvenc,      // NVIDIA  (h264_nvenc)
    QuickSync,  // Intel   (h264_qsv)
    Amf,        // AMD     (h264_amf)
    Software    // libx264 (CPU fallback)
}

/// <summary>
/// A named quality preset the user can switch between at runtime from the phone.
/// </summary>
public sealed class QualityPreset
{
    public string Id { get; set; } = "720p60";
    public string Name { get; set; } = "720p 60fps";
    public int Height { get; set; } = 720;   // 0 = native monitor height
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; } = 8000;

    public static QualityPreset[] Defaults() =>
    [
        new() { Id = "720p60",  Name = "720p 60fps (lowest latency)", Height = 720,  Fps = 60, BitrateKbps = 8000 },
        new() { Id = "1080p60", Name = "1080p 60fps",                 Height = 1080, Fps = 60, BitrateKbps = 15000 },
        new() { Id = "1080p30", Name = "1080p 30fps (smoother wifi)", Height = 1080, Fps = 30, BitrateKbps = 10000 },
        new() { Id = "native60",Name = "Native res 60fps",           Height = 0,    Fps = 60, BitrateKbps = 20000 },
    ];
}

/// <summary>
/// Runtime-configurable host settings. Persisted to config.json next to the exe.
/// </summary>
public sealed class HostConfig
{
    public int HttpPort { get; set; } = 8080;
    public int HttpsPort { get; set; } = 8443;
    public bool UseHttps { get; set; } = true;

    /// <summary>Index into the enumerated monitor list (0 = primary).</summary>
    public int MonitorIndex { get; set; } = 0;

    public EncoderBackend Encoder { get; set; } = EncoderBackend.Auto;

    public string ActivePresetId { get; set; } = "720p60";
    public List<QualityPreset> Presets { get; set; } = QualityPreset.Defaults().ToList();

    /// <summary>Path to ffmpeg.exe. Empty = search PATH and ./tools.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    public QualityPreset ActivePreset =>
        Presets.FirstOrDefault(p => p.Id == ActivePresetId) ?? Presets[0];

    // ---- persistence -------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static HostConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<HostConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    if (cfg.Presets.Count == 0) cfg.Presets = QualityPreset.Defaults().ToList();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Failed to load {path}: {ex.Message}. Using defaults.");
        }

        var fresh = new HostConfig();
        fresh.Save(path);
        return fresh;
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Failed to save {path}: {ex.Message}");
        }
    }
}
