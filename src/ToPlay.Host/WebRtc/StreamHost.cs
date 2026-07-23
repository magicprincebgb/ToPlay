using System.Runtime.Versioning;
using ToPlay.Host.Config;
using ToPlay.Host.Display;
using ToPlay.Host.Input;
using ToPlay.Host.Media;

namespace ToPlay.Host.WebRtc;

/// <summary>
/// Central coordinator: owns the encoder, mouse injector and current settings,
/// and mints per-viewer <see cref="StreamSession"/> instances.
/// </summary>

[SupportedOSPlatform("windows10.0.10240")]
public sealed class StreamHost : IDisposable
{
    private readonly object _gate = new();
    private readonly string _configPath;

    public HostConfig Config { get; }
    public bool Ready { get; private set; }
    public string StatusMessage { get; private set; } = "Initializing...";
    public string ActiveCodec { get; private set; } = "n/a";

    private readonly IPointerSink _injector;
    private readonly InputRouter _inputRouter;

    private ScreenStreamer? _streamer;
    private string? _ffmpegPath;
    private StreamSession? _activeSession;

    public StreamHost(HostConfig config, string configPath)
    {
        Config = config;
        _configPath = configPath;

        var monitor = Monitors.Get(config.MonitorIndex);

        // Prefer real multi-touch (needed for games like MLBB); fall back to the
        // single-pointer mouse driver if the synthetic-pointer API is missing
        // (Windows < 10 1809) or refuses to create a device.
        IPointerSink injector = new TouchInjector(monitor);
        if (!injector.Initialize())
        {
            Console.WriteLine("[host] Multi-touch unavailable; falling back to single-pointer mouse input.");
            injector.Dispose();
            injector = new MouseInjector(monitor);
            injector.Initialize();
        }
        _injector = injector;
        _inputRouter = new InputRouter(_injector);

        Initialize(monitor);
    }

    private void Initialize(MonitorInfo monitor)
    {
        _ffmpegPath = Ffmpeg.Locate(Config.FfmpegPath);
        if (_ffmpegPath == null)
        {
            Ready = false;
            StatusMessage = "ffmpeg.exe not found. Place it in ./tools/ or set FfmpegPath in config.json.";
            Console.WriteLine($"[host] {StatusMessage}");
            return;
        }

        var encoders = Ffmpeg.ListEncoders(_ffmpegPath);
        var (_, codec) = Ffmpeg.ResolveEncoder(Config.Encoder, encoders);
        ActiveCodec = codec;

        _streamer = new ScreenStreamer(_ffmpegPath, monitor, Config.ActivePreset, codec);
        Ready = true;
        StatusMessage = $"Ready. Encoder: {codec}. Monitor: {monitor.Label}.";
        Console.WriteLine($"[host] {StatusMessage}");
    }

    /// <summary>True while a phone is connected (only one is allowed at a time).</summary>
    public bool HasViewer
    {
        get { lock (_gate) { return _activeSession != null; } }
    }

    /// <summary>
    /// Mints a viewer session. Only ONE phone may be connected to this host at a
    /// time: a new connection takes over and disconnects any previous one. This
    /// also lets a phone that dropped uncleanly reconnect without being locked out
    /// waiting for the stale session to time out.
    /// </summary>
    public StreamSession? CreateSession(string id)
    {
        lock (_gate)
        {
            if (!Ready || _streamer == null) return null;

            if (_activeSession != null)
            {
                Console.WriteLine($"[host] Viewer {id} is taking over from {_activeSession.Id}; only one phone may connect at a time.");
                try { _activeSession.Dispose(); } catch { }
                _activeSession = null;
            }

            var session = new StreamSession(id, _streamer, _inputRouter);
            session.Closed += () =>
            {
                lock (_gate) { if (ReferenceEquals(_activeSession, session)) _activeSession = null; }
            };
            _activeSession = session;
            return session;
        }
    }

    /// <summary>Applies new settings from the UI and hot-restarts the encoder.</summary>
    public void ApplySettings(int? monitorIndex, string? presetId, EncoderBackend? encoder)
    {
        lock (_gate)
        {
            if (monitorIndex.HasValue) Config.MonitorIndex = monitorIndex.Value;
            if (!string.IsNullOrEmpty(presetId) &&
                Config.Presets.Any(p => p.Id == presetId))
                Config.ActivePresetId = presetId!;
            if (encoder.HasValue) Config.Encoder = encoder.Value;

            Config.Save(_configPath);

            var monitor = Monitors.Get(Config.MonitorIndex);
            _injector.SetMonitor(monitor);

            if (_ffmpegPath != null)
            {
                var encoders = Ffmpeg.ListEncoders(_ffmpegPath);
                var (_, codec) = Ffmpeg.ResolveEncoder(Config.Encoder, encoders);
                ActiveCodec = codec;

                if (_streamer == null)
                    _streamer = new ScreenStreamer(_ffmpegPath, monitor, Config.ActivePreset, codec);
                else
                    _streamer.Reconfigure(monitor, Config.ActivePreset, codec);

                Ready = true;
                StatusMessage = $"Ready. Encoder: {codec}. Monitor: {monitor.Label}.";
            }
        }
    }

    public object Status()
    {
        var monitors = Monitors.Enumerate()
            .Select(m => new { index = m.Index, label = m.Label, primary = m.IsPrimary, width = m.Width, height = m.Height })
            .ToList();

        return new
        {
            ready = Ready,
            message = StatusMessage,
            activeCodec = ActiveCodec,
            hasViewer = HasViewer,
            monitorIndex = Config.MonitorIndex,
            activePresetId = Config.ActivePresetId,
            encoder = Config.Encoder.ToString(),
            monitors,
            presets = Config.Presets.Select(p => new { id = p.Id, name = p.Name, height = p.Height, fps = p.Fps, bitrateKbps = p.BitrateKbps })
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _activeSession?.Dispose(); } catch { }
            _activeSession = null;
        }
        _streamer?.Dispose();
        _injector.Dispose();
    }
}
