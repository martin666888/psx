using System.IO;

namespace PSX.Services;

/// <summary>
/// One-shot record of whether the user data root existed before any service
/// could create it. <see cref="Record"/> must run before service resolution:
/// the DSH/Kimi supervisors create <c>~/.psx</c> subdirectories right in
/// their constructors. Until it runs, the safe assumption is an upgrade
/// (keep today's default UI language).
/// </summary>
public static class PsxInstallState
{
    private static readonly object Sync = new();
    private static bool _recorded;
    private static bool _preExistingInstall;

    /// <summary>Same root derivation as AgentThreadStore's parameterless
    /// constructor.</summary>
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".psx");

    public static bool PreExistingInstall
    {
        get
        {
            lock (Sync)
                return !_recorded || _preExistingInstall;
        }
    }

    public static void Record()
    {
        lock (Sync)
        {
            _preExistingInstall = Directory.Exists(RootDirectory);
            _recorded = true;
        }
    }
}
