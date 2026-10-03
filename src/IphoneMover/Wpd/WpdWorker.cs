using System.Collections.Concurrent;

namespace IphoneMover.Wpd;

/// <summary>
/// Runs every WPD call on one dedicated MTA thread. WPD COM objects are created
/// and used only on this thread, so callers on any thread are safe.
/// </summary>
internal sealed class WpdWorker : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;

    public WpdWorker()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "WPD worker" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Loop()
    {
        foreach (Action action in queue.GetConsumingEnumerable())
            action();
    }

    public T Invoke<T>(Func<T> func)
    {
        if (Thread.CurrentThread == thread)
            return func();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action) => Invoke(() => { action(); return true; });

    public void Dispose()
    {
        queue.CompleteAdding();
        thread.Join(3000);
    }
}
