namespace Assistant.App.Platform;

/// <summary>
/// Lets only one copy of the app run per Windows user session. Starting it a second time asks the
/// running copy to come to the front instead of opening another one (two copies would both be
/// listening to the speakers and fighting over the same settings).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string Prefix = @"Local\ClaudeLiveAssistant.";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle _registration;

    /// <summary>Another copy was started and asked this one to come forward.</summary>
    public event Action? Activated;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
        _registration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => Activated?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Returns the lock if this is the first copy, or null (after telling the first copy to show itself).</summary>
    public static SingleInstance? TryAcquire(string name = "main")
    {
        try
        {
            var mutex = new Mutex(false, Prefix + name + ".lock");
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; } // the previous copy crashed; we own it now
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + name + ".show");
            if (owned) return new SingleInstance(mutex, show);
            show.Set();
            show.Dispose();
            mutex.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            Assistant.Core.AppLog.Error("Single-instance check failed; carrying on", ex);
            return new SingleInstance(new Mutex(), new EventWaitHandle(false, EventResetMode.AutoReset)); // private handles: never blocks the launch
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread */ }
        _mutex.Dispose();
        _show.Dispose();
    }
}
