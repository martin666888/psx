using System.Diagnostics;
using System.Text;

namespace PSX.Services;

internal sealed record NpmRuntimeProcessResult(
    AcpRuntimeOperationKind Kind,
    string Message,
    int? ExitCode,
    string Stdout);

/// <summary>
/// Runs the portable npm CLI with uniform timeout, cancellation, redirected
/// stream draining, logging, and network-error classification.
/// </summary>
internal sealed class NpmRuntimeProcessRunner
{
    private readonly TimeSpan _timeout;
    private readonly Action<string> _log;

    public NpmRuntimeProcessRunner(TimeSpan timeout, Action<string> log)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<NpmRuntimeProcessResult> RunAsync(
        string nodePath,
        string npmCliPath,
        string workingDirectory,
        string label,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(npmCliPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        _log($"Starting: {label} in {workingDirectory}");
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _log($"Failed to start {label}: {ex}");
            return new(AcpRuntimeOperationKind.Failed, $"Failed to start {label}: {ex.Message}", null, "");
        }

        if (process == null)
            return new(AcpRuntimeOperationKind.Failed, $"{label} did not start.", null, "");
        using var processLifetime = process;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            // A descendant may inherit npm's redirected handles and keep the
            // pipes open after npm itself exits. Keep stream drain inside the
            // same hard deadline so callers always receive a terminal result.
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RuntimeProcessCleanup.TerminateAndDrainAsync(process, stdoutTask, stderrTask, _log)
                .ConfigureAwait(false);
            _log($"{label} timed out after {_timeout.TotalMinutes:0.##} minutes.");
            return new(AcpRuntimeOperationKind.Failed, $"{label} timed out.", null, "");
        }
        catch (OperationCanceledException)
        {
            await RuntimeProcessCleanup.TerminateAndDrainAsync(process, stdoutTask, stderrTask, _log)
                .ConfigureAwait(false);
            _log($"{label} was cancelled.");
            return new(AcpRuntimeOperationKind.Cancelled, $"{label} was cancelled.", null, "");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var exitCode = process.ExitCode;
        if (!string.IsNullOrWhiteSpace(stdout))
            _log($"stdout:\n{stdout.Trim()}");
        if (!string.IsNullOrWhiteSpace(stderr))
            _log($"stderr:\n{stderr.Trim()}");
        _log($"{label} exited with code {exitCode}.");

        if (exitCode != 0)
        {
            var kind = LooksLikeNetworkError(stderr)
                ? AcpRuntimeOperationKind.NetworkUnavailable
                : AcpRuntimeOperationKind.Failed;
            return new(kind, $"{label} failed with exit code {exitCode}.", exitCode, stdout);
        }

        return new(AcpRuntimeOperationKind.Success, $"{label} completed successfully.", exitCode, stdout);
    }

    internal static bool LooksLikeNetworkError(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return false;
        var lowered = stderr.ToLowerInvariant();
        return lowered.Contains("etimedout")
            || lowered.Contains("enotfound")
            || lowered.Contains("econnrefused")
            || lowered.Contains("network")
            || lowered.Contains("registry.npmjs.org")
            || lowered.Contains("getaddrinfo");
    }
}
