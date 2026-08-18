using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Provider-declared read-only user-level config source for the global Usage
/// panel「配置」tab. Implementations scan only user-home configuration (never
/// project-level files) and must route every secret-bearing field through
/// <see cref="AgentConfigSanitizer"/>. Shared aggregation never branches on
/// provider identity.
/// </summary>
public interface IAgentConfigSource
{
    /// <summary>
    /// Collect a sanitized provider config report. Must not throw for missing
    /// directories or malformed data — report <c>partial</c>/<c>unavailable</c>
    /// with fixed notes instead. Exception details stay in Debug logs.
    /// </summary>
    AgentProviderConfigReport Collect(CancellationToken cancellationToken);
}
