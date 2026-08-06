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
    /// It also doubles as the Kimi refresh seed (package.json / .npmrc) and is
    /// never written to.
    /// </summary>
    public required string BundledKimiDirectory { get; init; }

    /// <summary>
    /// Self-updated Kimi Code install under the writable <c>runtime/</c> root.
    /// When structurally valid it takes precedence over
    /// <see cref="BundledKimiDirectory"/>; deleting it falls back to the
    /// bundled copy.
    /// </summary>
    public required string KimiCurrentDirectory { get; init; }

    /// <summary>
    /// Staging directory for a background Kimi Code update. Never read by the
    /// live session; promoted to <see cref="KimiCurrentDirectory"/> on the
    /// next PSX launch when the pointer says "next".
    /// </summary>
    public required string KimiNextDirectory { get; init; }

    /// <summary>
    /// Pending-update marker for the Kimi runtime, mirroring
    /// <see cref="AcpActivePointerFile"/> ("current" or "next").
    /// </summary>
    public required string KimiActivePointerFile { get; init; }

    /// <summary>
    /// Root of the bundled Qwen Code CLI install shipped inside the release zip
    /// at <c>{InstallDir}/tools/qwen/</c>. Pre-installed at build time and never
    /// populated by a runtime <c>npm ci</c>. Contains
    /// <c>package-lock.json</c> and <c>node_modules/@qwen-code/qwen-code/</c>.
    /// The directory is not guaranteed to exist in a development build. It also
    /// doubles as the Qwen refresh seed (package.json / .npmrc) and is never
    /// written to.
    /// </summary>
    public required string BundledQwenDirectory { get; init; }

    /// <summary>
    /// Self-updated Qwen Code install under the writable <c>runtime/</c> root.
    /// When structurally valid it takes precedence over
    /// <see cref="BundledQwenDirectory"/>; deleting it falls back to the bundled copy.
    /// </summary>
    public required string QwenCurrentDirectory { get; init; }

    /// <summary>
    /// Staging directory for a background Qwen Code update. Never read by the live
    /// session; promoted to <see cref="QwenCurrentDirectory"/> on the next PSX
    /// launch when the pointer says "next".
    /// </summary>
    public required string QwenNextDirectory { get; init; }

    /// <summary>
    /// Pending-update marker for the Qwen runtime, mirroring
    /// <see cref="AcpActivePointerFile"/> ("current" or "next").
    /// </summary>
    public required string QwenActivePointerFile { get; init; }

    /// <summary>
    /// Path to the bundled Qoder seed directory (contains package.json,
    /// package-lock.json, .npmrc). Source of truth for user-confirmed
    /// installation into <see cref="QoderCurrentDirectory"/>; never written to.
    /// </summary>
    public required string QoderSeedDirectory { get; init; }

    /// <summary>
    /// Live Qoder CLI install under the writable <c>runtime/</c> root. Populated
    /// only after explicit user confirmation (npm install of the pinned seed
    /// package). Agent mode loads exclusively from here for the process lifetime.
    /// </summary>
    public required string QoderCurrentDirectory { get; init; }

    /// <summary>
    /// Staging directory for a user-requested Qoder update. Never read by the
    /// live session; promoted to <see cref="QoderCurrentDirectory"/> on the
    /// next PSX launch when the pointer says "next".
    /// </summary>
    public required string QoderNextDirectory { get; init; }

    /// <summary>
    /// Pending-update marker for the Qoder runtime, mirroring
    /// <see cref="AcpActivePointerFile"/> ("current" or "next").
    /// </summary>
    public required string QoderActivePointerFile { get; init; }

    /// <summary>
    /// Root of the bundled OpenCode install shipped inside the release zip at
    /// <c>{InstallDir}/tools/opencode/</c>. Pre-installed at build time and
    /// never populated by a runtime <c>npm ci</c>. Contains
    /// <c>package-lock.json</c> and <c>node_modules/opencode-windows-x64/</c>
    /// (a native Bun-compiled binary, not a Node script). The directory is not
    /// guaranteed to exist in a development build. It also doubles as the
    /// OpenCode refresh seed (package.json / .npmrc) and is never written to.
    /// </summary>
    public required string BundledOpencodeDirectory { get; init; }

    /// <summary>
    /// Self-updated OpenCode install under the writable <c>runtime/</c> root.
    /// When structurally valid it takes precedence over
    /// <see cref="BundledOpencodeDirectory"/>; deleting it falls back to the
    /// bundled copy. On machines without AVX2 this is also where the
    /// user-confirmed <c>opencode-windows-x64-baseline</c> install lands, since
    /// the bundled binary cannot run there.
    /// </summary>
    public required string OpencodeCurrentDirectory { get; init; }

    /// <summary>
    /// Staging directory for a background OpenCode update. Never read by the
    /// live session; promoted to <see cref="OpencodeCurrentDirectory"/> on the
    /// next PSX launch when the pointer says "next".
    /// </summary>
    public required string OpencodeNextDirectory { get; init; }

    /// <summary>
    /// Pending-update marker for the OpenCode runtime, mirroring
    /// <see cref="AcpActivePointerFile"/> ("current" or "next").
    /// </summary>
    public required string OpencodeActivePointerFile { get; init; }
}
