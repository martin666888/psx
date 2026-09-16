# Compact package and native window chrome

`tools/build-release.ps1` produces both the existing portable ZIP and a compact
ZIP from the same validated package payload. Compact is a directory layout,
not a reduced-function edition: Pi, Kimi, Qwen, OpenCode and Portable Node remain
bundled. The root contains `PSX.exe`, `psx.ini` and `app/`. The native root apphost
loads `app/PSX.dll` in the same process; no installed .NET or script launcher is
required. `app/psx-compact-layout.json` identifies layout version 1.

Settings stay beside the public EXE. Backups and temporary settings files live
in `app/state/settings`, and WebView2 data in `app/state/webview2`. Existing user
history, credentials and user-level diagnostics keep their existing locations.
Resources and managed runtime updates are relative to `app/`. Move the complete
directory when relocating a compact installation.

For an upgrade, exit PSX, preserve `psx.ini` and `app/runtime`, and replace the
application payload with the new compact package. To move from portable to
compact, extract into a fresh directory and copy `psx.ini` plus the old
`runtime/` into `app/runtime/`. Keep the original installation until verified.
Do not blindly overlay one package layout on the other.

## Window ownership

The 48-DIP top row contains the existing per-column web tabs and one native
138-DIP WPF caption area. `WindowChrome` supplies the window frame; the shell
WebView's HWND region excludes the caption rectangle. It is not a z-order
overlay. WPF owns minimize, maximize/restore, snap hit-testing and normal close.
The shell receives only a `window_chrome` layout notification. Logo and unused
tab-row regions allow native window dragging. Third-party content receives no
window command bridge.

When maximized, the host insets the shared content root by the portion of the
client HWND outside the current monitor work area. This restores the existing
top spacing for tabs and History instead of clipping it behind the invisible
resize frame. The inset is removed on restore and recalculated for DPI/layout
changes. Caption buttons and their enclosing panel have no outline border.

The last tab strip loses 138 pixels while content rectangles stay unchanged.
Under 320 pixels the strip uses a 32-pixel compact list button. If a severely
squeezed last column cannot fit even that button, its header borrows only the
minimum space from the adjacent header; body widths and ratios never change.
This ensures the last column's workspace list remains reachable.

If native clipping fails, the host reserves a separate native top row and logs
the failure instead of allowing the WebView to cover window controls. A browser
initialization failure cannot disable the native buttons.

## Validation

Full runs `PSX.CaptionProbe`, checking the actual HWND region, multiple window
widths and the native maximize hit-test. `--package-smoke <TestResults/path>`
starts the packaged EXE in an isolated diagnostic mode before user services
are constructed, verifies configuration and Pi paths and loads the real shell
with a separate WebView profile. It never opens the user's history or accounts.
Run this mode separately for both package layouts. Real multi-monitor DPI
transitions and the Windows snap flyout still require desktop acceptance.
