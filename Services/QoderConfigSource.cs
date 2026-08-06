using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Qoder CLI has no user-editable config files to surface in the「配置」tab.
/// Always returns <c>available</c> with a fixed empty-state note.
/// </summary>
public sealed class QoderConfigSource : IAgentConfigSource
{
    public AgentProviderConfigReport Collect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new AgentProviderConfigReport(
            string.Empty,
            string.Empty,
            string.Empty,
            AgentProviderConfigReport.Available,
            [],
            [],
            [],
            [],
            [AgentConfigNotes.NoEditableConfig]);
    }
}
