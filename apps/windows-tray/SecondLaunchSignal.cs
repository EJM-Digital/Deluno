namespace Deluno.Tray;

/// <summary>
/// How a second launch reaches the copy that is already running. The second copy sets a named event and exits; the
/// running copy hears it and shows a balloon "Deluno is already running" with the address. No window is needed, and
/// nothing opens a browser.
/// </summary>
public static class SecondLaunchSignal
{
    public const string EventName = @"Global\DelunoTraySecondLaunch";

    /// <summary>Called by the running copy: runs <paramref name="onSecondLaunch"/> each time another copy is started.</summary>
    public static IDisposable Listen(Action onSecondLaunch, string eventName = EventName)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, timedOut) => { if (!timedOut) { onSecondLaunch(); } },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return new Listener(handle, registration);
    }

    /// <summary>Called by the second copy. True when a running copy was told.</summary>
    public static bool Notify(string eventName = EventName)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(eventName);
            return handle.Set();
        }
        catch
        {
            return false;
        }
    }

    private sealed class Listener(EventWaitHandle handle, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            handle.Dispose();
        }
    }
}
