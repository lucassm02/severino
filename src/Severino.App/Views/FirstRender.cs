using System.Windows;

namespace Severino.App.Views;

/// <summary>
/// For the dialogs sized by their content under a custom title bar (SizeToContent with
/// ExtendsContentIntoTitleBar): WPF sometimes paints them before their final size and never
/// again, leaving the frame with nothing inside (lepoco/wpfui#1174). Once the content is
/// rendered, the size is worked out again and the window painted anew.
/// </summary>
internal static class FirstRender
{
    public static void Repaint(Window window)
    {
        void OnRendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnRendered;
            var sizing = window.SizeToContent;
            window.SizeToContent = SizeToContent.Manual;
            window.SizeToContent = sizing;
            window.InvalidateVisual();
        }
        window.ContentRendered += OnRendered;
    }

    /// <summary>A dialog sized by its content never grows past the screen: its form scrolls instead.</summary>
    public static void FitScreen(Window window) => window.MaxHeight = SystemParameters.WorkArea.Height - 32;
}
