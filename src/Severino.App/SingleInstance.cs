using System.Runtime.InteropServices;

namespace Severino.App;

/// <summary>
/// Keeps one Severino per Windows session. A second launch signals the first to show its
/// window and exits.
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Severino.SingleInstance";
    private const string ActivateEventName = @"Local\Severino.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            activate, (_, _) => ActivationRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Raised on a thread-pool thread when another launch asks this one to show up.</summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Returns the instance lock, or null after signaling the instance that already holds it.
    /// With <paramref name="wait"/>, a restart: the old instance is on its way out, so this one
    /// waits for the lock instead of handing over.
    /// </summary>
    public static SingleInstance? TryAcquire(TimeSpan wait = default)
    {
        // Both sides create-or-open the event, so a signal sent before the first instance
        // starts waiting stays set until it does.
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew || (wait > TimeSpan.Zero && WaitFor(mutex, wait)))
            return new SingleInstance(mutex, activate);

        // Let the running instance take the foreground; Windows only allows it when the
        // foreground process (this one, just launched by the user) grants it.
        AllowSetForegroundWindow(AsfwAny);
        activate.Set();
        activate.Dispose();
        mutex.Dispose();
        return null;
    }

    private static bool WaitFor(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // The old instance ended without letting go; the lock is ours anyway.
            return true;
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
