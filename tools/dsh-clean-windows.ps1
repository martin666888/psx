# Read alias identity without launching Python or the Microsoft Store.
function Test-DshAppInstallerAliasData {
    param([byte[]]$Data, [string]$ExpectedTarget)
    if ($null -eq $Data -or $Data.Length -lt 14 -or -not $ExpectedTarget) { return $false }
    if ([BitConverter]::ToUInt32($Data, 0) -ne 2147483675 -or
        [BitConverter]::ToUInt32($Data, 8) -ne 3) { return $false }
    $length = [BitConverter]::ToUInt16($Data, 4)
    if ($length -lt 6 -or $length + 8 -ne $Data.Length -or ($length % 2) -ne 0) { return $false }
    $strings = [Text.Encoding]::Unicode.GetString($Data, 12, $length - 4).Split([char]0)
    return $strings.Length -ge 4 -and
        $strings[0] -ceq 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -and
        $strings[1] -ceq 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!PythonRedirector' -and
        [string]::Equals($strings[2], $ExpectedTarget, [StringComparison]::OrdinalIgnoreCase)
}

function Test-DshAppInstallerPlaceholder {
    param([string]$Path)
    if (-not $Path -or [IO.Path]::GetFileName($Path) -notin @('python.exe', 'python3.exe')) { return $false }
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
        $package = @(Get-AppxPackage -Name Microsoft.DesktopAppInstaller -ErrorAction Stop |
            Where-Object { $_.PackageFamilyName -ceq 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' })
        if ($package.Count -ne 1) { return $false }
        $expectedTarget = Join-Path $package[0].InstallLocation 'AppInstallerPythonRedirector.exe'
        if (-not ('PsxAliasReparseReader' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class PsxAliasReparseReader {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        IntPtr input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    public static byte[] Read(string path) {
        using (var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
            if (handle.IsInvalid) return null;
            var buffer = new byte[16384];
            uint returned;
            if (!DeviceIoControl(handle, 0x900A8, IntPtr.Zero, 0, buffer,
                (uint)buffer.Length, out returned, IntPtr.Zero)) return null;
            Array.Resize(ref buffer, (int)returned);
            return buffer;
        }
    }
}
'@
        }
        return Test-DshAppInstallerAliasData -Data ([PsxAliasReparseReader]::Read($Path)) -ExpectedTarget $expectedTarget
    } catch {
        # Unreadable/unknown aliases remain disallowed; no path-wide exemption.
        return $false
    }
}

function Get-DshDevelopmentCommands {
    # -All also finds real Python installations hidden behind a Store stub.
    Get-Command python.exe,python3.exe,cl.exe,msbuild.exe -All -CommandType Application -ErrorAction SilentlyContinue
}

function Assert-DshNoDevelopmentCommands {
    foreach ($command in @(Get-DshDevelopmentCommands)) {
        if (Test-DshAppInstallerPlaceholder -Path $command.Source) { continue }
        throw "Development command detected or alias identity unverified: $($command.Name); use a clean VM."
    }
}
