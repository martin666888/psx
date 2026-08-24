using System.Text.RegularExpressions;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

/// <summary>
/// Guards the "no PSX-authored Chinese crosses the bridge" contract on the
/// C# side: wire-producing services must not contain CJK string literals.
/// Files whose Chinese strings are provably internal-only (runtime progress
/// events, diagnostics logging, psx.ini file headers, store-level fail
/// messages that never reach the wire) are allowlisted with the reason.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class WireProducerChineseScanTests
{
    private static readonly string[] AllowedFiles =
    [
        // StatusChanged progress text stays internal: AgentRuntimeCoordinator
        // re-broadcasts states with fixed codes only.
        "AcpRuntimeManager.cs",
        "KimiCodeAcpRuntime.cs",
        "QwenCodeAcpRuntime.cs",
        "OpencodeAcpRuntime.cs",
        // Store-level validation messages never cross the wire (the bridge
        // publishes the fixed errorClass only).
        "PsxEnvironmentSettingsStore.cs",
        // Diagnostics-only logging.
        "TabManagementService.cs",
        // Header comments written into the psx.ini file itself (file content,
        // not UI).
        "SettingsService.cs",
        // Registry preset label is WPF-side data, never a wire sentence.
        "DshRegistryDescriptor.cs",
    ];

    [TestMethod]
    public void WireProducerServices_ContainNoChineseStringLiterals()
    {
        var repoRoot = TestWorkspace.RepositoryRoot;
        var services = Path.Combine(repoRoot, "Services");
        var pattern = new Regex("\"[^\"]*\\p{IsCJKUnifiedIdeographs}[^\"]*\"");
        var problems = new List<string>();

        foreach (var path in Directory.EnumerateFiles(services, "*.cs"))
        {
            var name = Path.GetFileName(path);
            if (AllowedFiles.Contains(name)) continue;

            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                // Strip line and block comments so only literals remain.
                var withoutComments = Regex.Replace(line, @"//.*$", "");
                if (pattern.IsMatch(withoutComments))
                    problems.Add($"{name}:{index + 1}: {line.Trim()[..Math.Min(90, line.Trim().Length)]}");
            }
        }

        Assert.IsEmpty(
            problems,
            "Chinese literals in wire-producing services (map to fixed codes + frontend locales): "
            + string.Join("; ", problems));
    }
}
