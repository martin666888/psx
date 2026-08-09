using System.Diagnostics;

namespace PSX.Services;

/// <summary>
/// Completes cancellation and timeout cleanup for short-lived managed runtime
/// processes. Killing a redirected process is not enough on Windows: the
/// process and its asynchronous pipe readers must finish before runtime
/// directories can be replaced or deleted safely.
/// </summary>
internal static class RuntimeProcessCleanup
{
    private static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> TerminateAndDrainAsync(
        Process process,
        Task stdoutTask,
        Task stderrTask,
        Action<string>? log = null,
        TimeSpan? cleanupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(stdoutTask);
        ArgumentNullException.ThrowIfNull(stderrTask);

        var timeout = cleanupTimeout ?? DefaultCleanupTimeout;
        var killRequested = false;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                killRequested = true;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to terminate runtime process {process.Id}: {ex.Message}");
        }

        using var cleanupCts = new CancellationTokenSource(timeout);
        var exited = false;
        try
        {
            await process.WaitForExitAsync(cleanupCts.Token).ConfigureAwait(false);
            exited = true;
        }
        catch (OperationCanceledException)
        {
            log?.Invoke(
                $"Runtime process {process.Id} did not exit within {timeout.TotalSeconds:0.##} seconds after " +
                (killRequested ? "termination was requested." : "cleanup began."));
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed while waiting for runtime process {process.Id} to exit: {ex.Message}");
        }

        var streamsDrained = await DrainRedirectedStreamsAsync(
            stdoutTask,
            stderrTask,
            cleanupCts.Token,
            process.Id,
            log).ConfigureAwait(false);

        return exited && streamsDrained;
    }

    private static async Task<bool> DrainRedirectedStreamsAsync(
        Task stdoutTask,
        Task stderrTask,
        CancellationToken cancellationToken,
        int processId,
        Action<string>? log)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            log?.Invoke($"Redirected output for runtime process {processId} did not close before cleanup timed out.");
            ObserveLateFailure(stdoutTask);
            ObserveLateFailure(stderrTask);
            return false;
        }
        catch (Exception ex)
        {
            // A killed process may complete a redirected read with an I/O
            // exception. The pipe is closed at this point, which is the
            // lifecycle guarantee callers need; retain the detail in logs.
            log?.Invoke($"Runtime process {processId} output closed with an error: {ex.Message}");
            return true;
        }
    }

    private static void ObserveLateFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
