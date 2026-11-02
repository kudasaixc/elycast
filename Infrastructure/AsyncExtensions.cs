using Elysium_Cast_IPTV.Services;

namespace Elysium_Cast_IPTV.Infrastructure;

public static class AsyncExtensions
{
    /// <summary>
    /// Runs a task without awaiting it while still observing its failure.
    /// Replaces ad-hoc <c>async void</c> helpers, whose exceptions escape to the
    /// dispatcher and can take the whole application down.
    /// </summary>
    public static void Forget(this Task task, string context)
    {
        task.ContinueWith(
            t =>
            {
                var error = t.Exception?.GetBaseException();
                if (error is OperationCanceledException) return;
                if (error != null) DebugConsole.Exception(context, error);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Body of a WPF event handler that must be <c>async void</c>: any failure is
    /// logged instead of reaching the global unhandled-exception dialog.
    /// </summary>
    public static async void FireAndLog(Func<Task> work, string context)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DebugConsole.Exception(context, ex); }
    }
}
