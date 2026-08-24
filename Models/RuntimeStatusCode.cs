namespace PSX.Models;

/// <summary>
/// Fixed codes for the <c>runtime_status</c> (<c>messageCode</c>) and
/// <c>runtime_update_status</c> (<c>messageCode</c>) bridge events. The
/// frontend maps each code to display copy; raw npm output, paths and
/// composed sentences never cross the bridge.
/// </summary>
public static class RuntimeStatusCode
{
    // runtime_status.messageCode — install lifecycle card.
    public const string NotInstalled = "runtime.not_installed";
    public const string Ready = "runtime.ready";
    public const string PreparingInstall = "runtime.preparing_install";
    public const string InstallSucceeded = "runtime.install_succeeded";
    public const string CancellingInstall = "runtime.cancelling_install";
    public const string InstallCancelled = "runtime.install_cancelled";
    public const string NetworkUnavailable = "runtime.network_unavailable";
    public const string InstallFailed = "runtime.install_failed";

    // runtime_update_status.messageCode — toolbar Update button outcome.
    public const string UpdateFailed = "update.failed";
    public const string UpdateNetworkUnavailable = "update.network_unavailable";

    // Transcript-only workspaces have no runtime at all.
    public const string TranscriptReadOnly = "runtime.transcript_read_only";
}
