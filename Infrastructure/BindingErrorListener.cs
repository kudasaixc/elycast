using System.Diagnostics;
using Elysium_Cast_IPTV.Services;

namespace Elysium_Cast_IPTV.Infrastructure;

/// <summary>
/// Routes WPF data-binding errors (normally visible only in a debugger's
/// output window) to the ElyCast log, so a broken binding shows up in smoke runs.
/// </summary>
internal sealed class BindingErrorListener : TraceListener
{
    public static void Attach()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(new BindingErrorListener());
        source.Switch.Level = SourceLevels.Error;
    }

    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) DebugConsole.Warn("Binding: " + message);
    }
}
