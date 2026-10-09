using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Severino.App.Services;

/// <summary>
/// Windows' own warning for adding or removing a trusted root opens with no owner window, so it
/// can land behind the app while the app waits for it. This runs the call on the UI thread and,
/// while it blocks, attaches each warning to the main window: it stays in front and the window
/// behaves as under any modal dialog.
/// </summary>
internal static partial class WindowsPrompt
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static T Run<T>(Func<T> action)
    {
        var app = Application.Current;
        if (app is null)
            return action(); // tests
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Run(action));

        var owner = app.MainWindow is { } main ? new WindowInteropHelper(main).Handle : IntPtr.Zero;
        var handled = new HashSet<IntPtr>();
        // Ticks inside the warning's own message loop, which keeps the dispatcher running.
        var timer = new DispatcherTimer(PollInterval, DispatcherPriority.Normal, (_, _) =>
        {
            foreach (var warning in FindWarnings())
            {
                if (!handled.Add(warning))
                    continue;
                if (owner != IntPtr.Zero)
                {
                    SetWindowLongPtr(warning, GwlpHwndParent, owner);
                    EnableWindow(owner, false);
                }
                SetForegroundWindow(warning);
            }
        }, app.Dispatcher);

        try
        {
            return action();
        }
        finally
        {
            timer.Stop();
            if (owner != IntPtr.Zero)
                EnableWindow(owner, true);
        }
    }

    /// <summary>Visible, ownerless dialog-class windows of this process: the warnings.</summary>
    private static List<IntPtr> FindWarnings()
    {
        var found = new List<IntPtr>();
        var processId = Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var windowProcess);
            if (windowProcess == processId && IsWindowVisible(window) && GetWindow(window, GwOwner) == IntPtr.Zero
                && ClassName(window) == "#32770")
                found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string ClassName(IntPtr window)
    {
        var name = new char[64];
        return new string(name, 0, GetClassName(window, name, name.Length));
    }

    private const int GwlpHwndParent = -8;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(IntPtr window, [Out] char[] name, int capacity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);
}
