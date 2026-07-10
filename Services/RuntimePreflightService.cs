using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Status of a single runtime component as reported by the preflight service.
/// </summary>
public enum RuntimeComponentState
{
    /// <summary>The component is present and ready to use.</summary>
    Ready,

    /// <summary>The component is missing but can be installed automatically.</summary>
    NeedsInstall,

    /// <summary>The component is missing and cannot be installed (no portable fallback, no system copy).</summary>
    Unavailable,

    /// <summary>An attempt to install or run the component failed; see <c>ErrorMessage</c>.</summary>
    Failed,
}

public sealed record RuntimeComponentStatus(
    string Name,
    RuntimeComponentState State,
    string? Detail = null,
    string? ErrorMessage = null);

/// <summary>
/// Reports the current state of every runtime component. Pure inspection — does not
/// install, update, or modify anything. The preflight service uses this to drive
/// the install-on-demand flow.
/// </summary>
public sealed class RuntimePreflightService
{
    private readonly RuntimeLocator _locator;

    public RuntimePreflightService(RuntimeLocator locator)
    {
        _locator = locator;
    }

    public RuntimePaths Paths => _locator.Locate();

    public RuntimeComponentStatus CheckDotNet()
    {
        // Self-contained builds always have .NET; framework-dependent builds may rely on
        // a system runtime. We report Ready unconditionally because the user is currently
        // running this code, which means the runtime is functional.
        return new RuntimeComponentStatus(".NET Runtime", RuntimeComponentState.Ready,
            ".NET runtime is provided by the build configuration.");
    }

    public RuntimeComponentStatus CheckWebView2()
    {
        var paths = _locator.Locate();
        if (paths.WebView2FixedRuntimePath != null)
        {
            return new RuntimeComponentStatus("WebView2 Runtime", RuntimeComponentState.Ready,
                $"bundled Fixed Version runtime: {paths.WebView2FixedRuntimePath}");
        }

        // CoreWebView2Environment.GetAvailableBrowserVersionString returns null when
        // WebView2 is not installed. We avoid a hard dependency on the WebView2 SDK at
        // runtime by checking the well-known install keys.
        var version = TryGetWebView2Version();
        if (version != null)
        {
            return new RuntimeComponentStatus("WebView2 Runtime", RuntimeComponentState.Ready,
                $"version {version}");
        }

        return new RuntimeComponentStatus("WebView2 Runtime", RuntimeComponentState.Unavailable,
            "WebView2 runtime is missing. Please install Microsoft Edge WebView2 Runtime and restart PSX.");
    }

    public RuntimeComponentStatus CheckPortableNode()
    {
        var paths = _locator.Locate();
        if (paths.PortableNodePath == null)
        {
            return new RuntimeComponentStatus("Portable Node.js", RuntimeComponentState.Unavailable,
                "No bundled node.exe; Agent mode will not work.");
        }

        // Confirm node actually runs. A present-but-broken node would surface as a
        // cryptic "The system cannot find the file specified" later.
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = paths.PortableNodePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });
            if (probe == null)
            {
                return new RuntimeComponentStatus("Portable Node.js", RuntimeComponentState.Failed,
                    "node.exe could not be started.");
            }
            if (!probe.WaitForExit(5000))
            {
                probe.Kill();
                return new RuntimeComponentStatus("Portable Node.js", RuntimeComponentState.Failed,
                    "node.exe did not respond within 5 seconds.");
            }
            if (probe.ExitCode != 0)
            {
                return new RuntimeComponentStatus("Portable Node.js", RuntimeComponentState.Failed,
                    $"node --version exited with code {probe.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            return new RuntimeComponentStatus("Portable Node.js", RuntimeComponentState.Failed,
                $"Failed to probe node.exe: {ex.Message}");
        }

        return new RuntimeComponentStatus("Portable Node.js", RuntimeComponentState.Ready,
            paths.PortableNpmCliPath != null ? "node + npm CLI located" : "node located, npm CLI missing");
    }

    public RuntimeComponentStatus CheckAcpRuntime()
    {
        var paths = _locator.Locate();
        var adapterPath = Path.Combine(paths.AcpActiveDirectory, "node_modules",
            "@agentclientprotocol", "claude-agent-acp", "dist", "index.js");
        if (File.Exists(adapterPath))
        {
            return new RuntimeComponentStatus("ACP Adapter", RuntimeComponentState.Ready,
                adapterPath);
        }

        if (paths.PortableNodePath == null)
        {
            return new RuntimeComponentStatus("ACP Adapter", RuntimeComponentState.Unavailable,
                "ACP adapter is not installed and no node runtime is available to install it.");
        }

        return new RuntimeComponentStatus("ACP Adapter", RuntimeComponentState.NeedsInstall,
            "Will run `npm install` on first launch.");
    }

    public RuntimeComponentStatus CheckBundledClaudeCode()
    {
        var paths = _locator.Locate();
        // The bundled Claude Code binary lives inside the SDK platform package, not at
        // a stable path we control. We probe the well-known location for win32-x64.
        var bundled = Path.Combine(paths.AcpActiveDirectory, "node_modules",
            "@anthropic-ai", "claude-agent-sdk-win32-x64", "claude.exe");
        if (File.Exists(bundled))
        {
            return new RuntimeComponentStatus("Bundled Claude Code", RuntimeComponentState.Ready,
                bundled);
        }

        return new RuntimeComponentStatus("Bundled Claude Code", RuntimeComponentState.NeedsInstall,
            "Will be installed as part of the ACP runtime.");
    }

    public IReadOnlyList<RuntimeComponentStatus> CheckAll()
    {
        return new[]
        {
            CheckDotNet(),
            CheckWebView2(),
            CheckPortableNode(),
            CheckAcpRuntime(),
            CheckBundledClaudeCode(),
        };
    }

    /// <summary>
    /// Attempt to run the WebView2 evergreen bootstrapper. Caller is responsible for
    /// showing progress to the user. Returns the process exit code (-1 if the bootstrapper
    /// was not present or could not be started).
    /// </summary>
    public int RunWebView2Bootstrapper()
    {
        var paths = _locator.Locate();
        if (paths.WebView2BootstrapperPath == null)
            return -1;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = paths.WebView2BootstrapperPath,
                Arguments = "/silent /install",
                UseShellExecute = true,
                CreateNoWindow = true,
                Verb = "runas", // best-effort elevation; silently fails if user declines
            });
            if (proc == null)
                return -1;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static string? TryGetWebView2Version()
    {
        try
        {
            // Microsoft keeps the installed version under HKLM/HKCU. The official
            // CoreWebView2Environment.GetAvailableBrowserVersionString() is the
            // canonical way, but it requires the WebView2 SDK to be loaded in this
            // process — which is fine here, but we probe the registry first to
            // avoid a hard dep on the SDK for this read-only check.
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            var v = hklm?.GetValue("pv") as string;
            if (!string.IsNullOrEmpty(v)) return v;

            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            v = hkcu?.GetValue("pv") as string;
            if (!string.IsNullOrEmpty(v)) return v;
        }
        catch
        {
            // Registry access denied is common; fall through.
        }
        return null;
    }
}
