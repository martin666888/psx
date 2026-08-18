using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PSX.Models;
using PSX.Services;

var resultPath = args.FirstOrDefault();
if (string.IsNullOrWhiteSpace(resultPath))
    return 64;

var result = new DesktopProbeResult();
try
{
    result = await RunProbeAsync(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
}
catch (Exception exception)
{
    result.Error = exception.ToString();
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
await File.WriteAllTextAsync(
    resultPath,
    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return result.Succeeded ? 0 : 2;

static async Task<DesktopProbeResult> RunProbeAsync(string workingDirectory)
{
    using var service = new ConPtyService();
    var output = new StringBuilder();
    var outputLock = new object();
    var exited = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
    service.OutputReceived += (_, eventArgs) =>
    {
        lock (outputLock)
            output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(eventArgs.Data)));
    };
    service.SessionExited += (_, eventArgs) => exited.TrySetResult(eventArgs.SessionId);

    var outputSession = service.CreateSession(CreateProfile(workingDirectory), new TerminalSize
    {
        Columns = 80,
        Rows = 24
    });
    service.WriteInput(outputSession.SessionId, Encoding.UTF8.GetBytes("echo PSX_CONPTY_OK & exit\r"));
    var exitedSessionId = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
    string capturedOutput;
    lock (outputLock)
        capturedOutput = output.ToString();

    var closeSession = service.CreateSession(CreateProfile(workingDirectory), new TerminalSize
    {
        Columns = 80,
        Rows = 24
    });
    var closeProcess = closeSession.Process
        ?? throw new InvalidOperationException("ConPTY did not expose its child process.");
    await Task.Delay(100);
    var processWasRunning = !closeProcess.HasExited;
    service.Resize(closeSession.SessionId, 100, 30);
    service.Resize(closeSession.SessionId, 100, 30);
    service.Resize(closeSession.SessionId, 1, 0);
    await service.CloseSessionAsync(closeSession.SessionId);

    return new DesktopProbeResult
    {
        OutputReceived = capturedOutput.Contains("PSX_CONPTY_OK", StringComparison.Ordinal),
        ExitEventReceived = exitedSessionId == outputSession.SessionId,
        NaturalExitRemovedSession = service.GetSession(outputSession.SessionId) == null,
        ProcessWasRunningBeforeClose = processWasRunning,
        CloseRemovedSession = service.GetSession(closeSession.SessionId) == null,
        CloseTerminatedProcess = HasExited(closeProcess),
        CapturedOutput = capturedOutput
    };
}

static ShellProfile CreateProfile(string workingDirectory) => new()
{
    Id = "desktop-probe-cmd",
    Name = "Desktop Probe Command",
    Command = "cmd.exe",
    Arguments = "/d /q",
    StartingDirectory = workingDirectory
};

static bool HasExited(Process process)
{
    try
    {
        process.Refresh();
        return process.HasExited;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

internal sealed class DesktopProbeResult
{
    public bool OutputReceived { get; set; }
    public bool ExitEventReceived { get; set; }
    public bool NaturalExitRemovedSession { get; set; }
    public bool ProcessWasRunningBeforeClose { get; set; }
    public bool CloseRemovedSession { get; set; }
    public bool CloseTerminatedProcess { get; set; }
    public string CapturedOutput { get; set; } = "";
    public string? Error { get; set; }

    public bool Succeeded =>
        OutputReceived
        && ExitEventReceived
        && NaturalExitRemovedSession
        && ProcessWasRunningBeforeClose
        && CloseRemovedSession
        && CloseTerminatedProcess
        && string.IsNullOrWhiteSpace(Error);
}
