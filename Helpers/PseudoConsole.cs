using System.Runtime.InteropServices;
using static PSX.Helpers.NativeMethods;

namespace PSX.Helpers;

/// <summary>
/// IDisposable wrapper around a Windows ConPTY handle.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    public IntPtr Handle { get; }

    private PseudoConsole(IntPtr handle)
    {
        Handle = handle;
    }

    /// <summary>
    /// Creates a new pseudo console with the given pipes and initial size.
    /// </summary>
    public static PseudoConsole Create(COORD size, IntPtr inputPipeRead, IntPtr outputPipeWrite)
    {
        var hr = CreatePseudoConsole(size, inputPipeRead, outputPipeWrite, 0, out var handle);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        return new PseudoConsole(handle);
    }

    /// <summary>
    /// Resizes the pseudo console to the new character cell dimensions.
    /// </summary>
    public void Resize(COORD newSize)
    {
        var hr = ResizePseudoConsole(Handle, newSize);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            ClosePseudoConsole(Handle);
        }
    }
}
