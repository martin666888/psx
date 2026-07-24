using System.IO;

namespace PSX.Models;

/// <summary>
/// Resolved locations of all runtime artifacts used by PSX at startup.
/// All paths are absolute. None of these are guaranteed to exist on disk
/// at construction time — callers should treat them as "the place to look"
/// and fall back to creation / installation as needed.
/// </summary>
public sealed record RuntimePaths
{
    /// <summary>
    /// Root of the writable runtime directory. Contains the ACP adapter
    /// and any other per-user install state.
    /// Fixed at <c>{AppContext.BaseDirectory}/runtime/</c> — there is no
    /// write-probe fallback to <c>%LOCALAPPDATA%</c> in the current model.
    /// </summary>
    public required string RuntimeRoot { get; init; }

    /// <summary>
    /// The "live" ACP adapter directory — what Agent mode actually loads
    /// <c>node_modules</c> from in this process. This always resolves to
    /// <see cref="AcpCurrentDirectory"/>; pending updates are promoted only
    /// during startup.
    /// </summary>
    public required string AcpActiveDirectory { get; init; }

    /// <summary>
    /// The current ACP directory. This is what <c>npm ci</c> populates after
    /// explicit user confirmation, and what Agent mode loads for the lifetime
    /// of the process.
    /// </summary>
    public required string AcpCurrentDirectory { get; init; }

    /// <summary>
    /// The "staging" ACP directory. Background <c>npm update</c> writes here
    /// so it never collides with the directory the live Agent session is
    /// reading from. After a successful update, an <c>acp-active.txt</c>
    /// pending marker is flipped; the next PSX launch promotes this directory
    /// to <see cref="AcpCurrentDirectory"/>.
    /// </summary>
    public required string AcpNextDirectory { get; init; }

    /// <summary>
    /// Pending-update marker. Contents: "current" or "next". "next" means
    /// <see cref="AcpNextDirectory"/> should be promoted during startup; it
    /// does not make the current process load <see cref="AcpNextDirectory"/>.
    /// </summary>
    public required string AcpActivePointerFile { get; init; }

    /// <summary>
    /// Path to the bundled portable Node.js executable, or <c>null</c> if not shipped
    /// (e.g. development build without <c>tools/node/</c>).
    /// </summary>
    public required string? PortableNodePath { get; init; }

    /// <summary>
    /// Path to the bundled <c>npm-cli.js</c>, resolved relative to
    /// <see cref="PortableNodePath"/>. <c>null</c> when portable Node is not available.
    /// </summary>
    public required string? PortableNpmCliPath { get; init; }

    /// <summary>
    /// Path to the WebView2 evergreen bootstrapper, or <c>null</c> if not shipped.
    /// </summary>
    public required string? WebView2BootstrapperPath { get; init; }

    /// <summary>
    /// Path to a bundled Fixed Version WebView2 Runtime directory containing
    /// <c>msedgewebview2.exe</c>, or <c>null</c> when the app should use the
    /// system Evergreen Runtime.
    /// </summary>
    public required string? WebView2FixedRuntimePath { get; init; }

    /// <summary>
    /// Path to the bundled ACP seed directory (contains package.json, package-lock.json, .npmrc).
    /// This is the source of truth for user-confirmed installation; never written to.
    /// </summary>
    public required string AcpSeedDirectory { get; init; }

    /// <summary>
    /// Directory containing the PSX executable (AppContext.BaseDirectory).
    /// </summary>
    public required string InstallDirectory { get; init; }

    /// <summary>
    /// Root of the bundled Kimi Code CLI install shipped inside the release zip
    /// at <c>{InstallDir}/tools/kimi/</c>. Unlike the ACP adapter directories,
    /// this is pre-installed at build time (Kimi Code is MIT-licensed) and is
    /// never populated by a runtime <c>npm ci</c>. Contains
    /// <c>package-lock.json</c> and <c>node_modules/@moonshot-ai/kimi-code/</c>.
    /// The directory is not guaranteed to exist in a development build.
    /// </summary>
    public required string BundledKimiDirectory { get; init; }
}
