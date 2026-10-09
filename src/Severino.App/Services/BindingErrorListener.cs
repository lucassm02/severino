using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Severino.App.Services;

/// <summary>Sends WPF binding failures to the app log; otherwise they only reach a debugger.</summary>
public sealed class BindingErrorListener(ILogger logger) : TraceListener
{
    public static void Register(ILogger logger)
    {
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener(logger));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message) => logger.LogWarning("WPF binding: {Message}", message);
}
