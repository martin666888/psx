using System.Diagnostics;

namespace PSX.Models;

public sealed class TerminalSession : IDisposable
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public ShellProfile ShellProfile { get; }
    public TerminalSize Size { get; set; }
    public Process? Process { get; set; }
    public CancellationTokenSource CancellationTokenSource { get; } = new();
    public IntPtr ConPtyHandle { get; set; }
    public Microsoft.Win32.SafeHandles.SafeFileHandle? InputPipeWrite { get; set; }
    public Microsoft.Win32.SafeHandles.SafeFileHandle? OutputPipeRead { get; set; }
    public DateTime CreatedAt { get; } = DateTime.Now;

    private bool _disposed;

    public TerminalSession(ShellProfile profile, TerminalSize size)
    {
        ShellProfile = profile;
        Size = size;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { CancellationTokenSource.Cancel(); }
        catch { }

        if (Process is not null && !Process.HasExited)
        {
            try { Process.Kill(entireProcessTree: true); }
            catch { }
        }

        if (ConPtyHandle != IntPtr.Zero)
        {
            Helpers.NativeMethods.ClosePseudoConsole(ConPtyHandle);
            ConPtyHandle = IntPtr.Zero;
        }

        InputPipeWrite?.Dispose();
        OutputPipeRead?.Dispose();
        Process?.Dispose();
        CancellationTokenSource.Dispose();
    }
}
