using System.Runtime.Versioning;
using System.Text.Json;

namespace ToPlay.Host.Input;

/// <summary>
/// Parses the compact touch protocol sent by the phone over the WebRTC data
/// channel and forwards it to the <see cref="MouseInjector"/>, which drives the
/// real system mouse cursor.
///
/// Wire format (JSON, one message may batch several events):
///   { "e": [ {"t":"d","id":1,"x":0.42,"y":0.83}, {"t":"m",...}, {"t":"u","id":1} ] }
///   t = d(own) | m(ove) | u(p) | c(ancel all)
///   x,y = normalized [0..1] relative to the streamed video image.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InputRouter
{
    private readonly IPointerSink _injector;

    public InputRouter(IPointerSink injector) => _injector = injector;


    public void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("e", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in arr.EnumerateArray())
                    HandleEvent(ev);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                HandleEvent(root);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[input] Bad message: {ex.Message}");
        }
    }

    private void HandleEvent(JsonElement ev)
    {
        if (!ev.TryGetProperty("t", out var tEl)) return;
        var type = tEl.GetString();

        switch (type)
        {
            case "c":
                _injector.CancelAll();
                return;

            case "k":
                // Whole-system key press (e.g. the phone's Back button). Routed
                // to SendInput so it acts on whatever program is focused.
                var key = ev.TryGetProperty("key", out var kEl) ? kEl.GetString() : null;
                KeyboardInjector.Send(key);
                return;

            case "d":
            case "m":
            case "u":
                long id = ev.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
                if (type == "u") { _injector.Up(id); return; }

                double x = ev.TryGetProperty("x", out var xEl) ? xEl.GetDouble() : 0;
                double y = ev.TryGetProperty("y", out var yEl) ? yEl.GetDouble() : 0;

                if (type == "d") _injector.Down(id, x, y);
                else _injector.Move(id, x, y);
                return;
        }
    }
}
