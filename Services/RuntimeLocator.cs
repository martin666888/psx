using System.IO;

namespace PSX.Models;

/// <summary>
/// Resolves filesystem paths for runtime artifacts. Pure function — no side effects,
/// no directory creation, no process spawning. Use <c>AcpRuntimeManager</c> and
/// <c>RuntimePreflightService</c> for the actual work.
/// </summary>
public sealed class RuntimeLocator
{
    private const string RuntimeSubdirectoryName = "runtime";
    private const string AcpCurrentSubdirectoryName = "acp-current";
    private const string AcpNextSubdirectoryName = "acp-next";
    private const string AcpActivePointerFileName = "acp-active.txt";
    private const string NodeSubdirectoryName = "node";
    private const string NodeExecutableName = "node.exe";
    private const string NpmCliRelativePath = "node_modules/npm/bin/npm-cli.js";
    private const string WebView2FixedRuntimeSubdirectoryName = "webview2-fixed";
    private const string WebView2ExecutableName = "msedgewebview2.exe";

    /// <summary>
    /// Compute all runtime paths for the current process. Call once at startup.
    /// The writable runtime lives under <c>{InstallDir}/runtime/</c>:
    ///   <c>acp-current</c> — what Agent mode actually loads
    ///   <c>acp-next</c>    — what background <c>npm update</c> writes to
    ///   <c>acp-active.txt</c> — pending-update marker ("current" or "next")
    /// <see cref="RuntimePaths.AcpActiveDirectory"/> always points at
    /// <see cref="RuntimePaths.AcpCurrentDirectory"/>; pending updates are
    /// promoted to current only during startup, never mid-session.
    /// All three (current/next/pointer) are fixed under <c>runtime/</c> and
    /// never fall back to a legacy <c>tools/acp</c> directory — that would
    /// collapse current and next onto the same path and let
    /// <c>AcpRuntimeManager</c> delete the in-use runtime.
    /// Development builds populate <c>runtime/acp-current</c> on first Agent
    /// message via <c>AcpRuntimeManager.EnsureInstalledCoreAsync</c> (npm ci
    /// from <c>tools/acp-seed</c>); there is no pre-installed fallback.
    /// </summary>
    public RuntimePaths Locate()
    {
        var installDirectory = AppContext.BaseDirectory;

        var nodeDirectory = Path.Combine(installDirectory, "tools", NodeSubdirectoryName);
        var nodePath = Path.Combine(nodeDirectory, NodeExecutableName);
        var portableNode = File.Exists(nodePath) ? nodePath : null;

        string? portableNpm = null;
        if (portableNode != null)
        {
            var npmCandidate = Path.Combine(nodeDirectory, NpmCliRelativePath);
            if (File.Exists(npmCandidate))
                portableNpm = npmCandidate;
        }

        var runtimeRoot = Path.Combine(installDirectory, RuntimeSubdirectoryName);
        var webView2FixedRuntimePath = FindWebView2FixedRuntimeDirectory(
            Path.Combine(runtimeRoot, WebView2FixedRuntimeSubdirectoryName));

        return new RuntimePaths
        {
            RuntimeRoot = runtimeRoot,
            AcpCurrentDirectory = Path.Combine(runtimeRoot, AcpCurrentSubdirectoryName),
            AcpNextDirectory = Path.Combine(runtimeRoot, AcpNextSubdirectoryName),
            AcpActiveDirectory = Path.Combine(runtimeRoot, AcpCurrentSubdirectoryName),
            AcpActivePointerFile = Path.Combine(runtimeRoot, AcpActivePointerFileName),
            PortableNodePath = portableNode,
            PortableNpmCliPath = portableNpm,
            WebView2BootstrapperPath = null,
            WebView2FixedRuntimePath = webView2FixedRuntimePath,
            AcpSeedDirectory = Path.Combine(installDirectory, "tools", "acp-seed"),
            InstallDirectory = installDirectory,
        };
    }

    private static string? FindWebView2FixedRuntimeDirectory(string root)
    {
        if (!Directory.Exists(root))
            return null;

        var directExecutable = Path.Combine(root, WebView2ExecutableName);
        if (File.Exists(directExecutable))
            return root;

        try
        {
            var executable = Directory.EnumerateFiles(root, WebView2ExecutableName, SearchOption.AllDirectories)
                .FirstOrDefault();
            return executable == null ? null : Path.GetDirectoryName(executable);
        }
        catch
        {
            return null;
        }
    }
}
