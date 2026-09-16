# Native caption and compact package validation

Validated locally on 2026-09-16. Initial artifacts are PSX 1.2.0, Windows x64.

## Results

- Fast passed: 969 C# cases, 426 Web cases, coverage, typecheck, lint, generated-output verification and .NET formatting.
- Full retry: 977 C# cases and 426 Web cases passed, as did the DSH desktop probe, native caption probe and portable browser smoke. The first attempt encountered a transient locked Kimi test log; the unchanged retry passed.
- Full remains **failed at the legacy visual baseline gate**: 15 scene differences. Compared with `TestResults/pi-bundled-full.log`, all 15 scene names and changed-pixel counts are identical. No baseline images were updated.
- Separate native-caption review passed 37 captures and 2 geometry checks across five themes, with no axe violations. This mode checks geometry/accessibility and produces review captures; it is not a pixel-baseline comparison.
- Native WPF probe verified the actual HWND exclusion at three window widths, intact content below the caption, 46×40 button geometry, HTMAXBUTTON, maximize/restore, minimize and close.
- Both ZIPs passed real EXE startup and browser smoke. Compact also passed from a Chinese/space installation path with a different working directory; its root remained exactly `PSX.exe`, `psx.ini`, `app/`.
- Bundled Pi passed ACP session creation, prompt completion and restoration in both layouts, including the Chinese/space compact path.
- Markdown performance passed. Dependency audit passed: no known NuGet or production npm vulnerabilities; three moderate development dependency findings remain.

## Release artifacts

| Package | SHA-256 |
| --- | --- |
| `PSX-1.2.0-win-x64-portable.zip` | `5b4a99035cee5ddec48f69099e5b0d6c71309c5f75c4a2b3b20cafa44a1f9e31` |
| `PSX-1.2.0-win-x64-compact.zip` | `756c0f3b7db3d63da83eedcd52a98ad60930951f91a1531b127dd18f216af9b3` |

Both archives are under `bin/releases/`. They contain the same validated runtime payload.

## Remaining desktop acceptance

Real 100–200% multi-monitor DPI transitions, drag-to-restore, edge resizing feel and the visible Windows 11 snap flyout need manual desktop acceptance. The native hit-test was verified, but that does not prove the OS flyout's appearance. The clipping-failure fallback is implemented but was not forced in a live desktop run. Browser review captures show only the shell; WPF button ownership is covered by the native probe, not those screenshots.

## Subsequent alignment fixes

The final source also corrects maximized content clipping against the monitor
work area, removes the caption outline, and aligns the logo, tabs and History
controls at a shared 24px center within a 48px header. Fast passed again
(969 C# and 426 Web cases), the native caption probe passed, and the five-theme
review passed 37 captures plus 2 geometry checks with explicit center alignment
assertions. Logs: `TestResults/chrome-alignment-fast.log` and
`TestResults/chrome-alignment-visual.log`.

The archive hashes above describe the initial package validation, before these
alignment fixes. Run `tools/build-release.ps1` to package the final source.
No remote Release was published.
