using ToPlay.Host.Display;

namespace ToPlay.Host.Input;

/// <summary>
/// A sink that turns normalized [0..1] phone touch points into OS input.
/// Two implementations exist: <see cref="TouchInjector"/> (true multi-touch via
/// the Windows synthetic-pointer API — needed for games like MLBB) and
/// <see cref="MouseInjector"/> (single-pointer mouse fallback for older OSes).
/// </summary>
public interface IPointerSink : IDisposable
{
    /// <summary>Prepares the sink. Returns false if this backend is unusable
    /// on the current OS, so the caller can fall back to another.</summary>
    bool Initialize();

    void SetMonitor(MonitorInfo monitor);

    void Down(long id, double nx, double ny);
    void Move(long id, double nx, double ny);
    void Up(long id);

    /// <summary>Releases every active contact (page hidden, disconnect, etc.).</summary>
    void CancelAll();
}
