# PSX UI Design System

Hallmark profile: `modern-minimal / technical restrained / Workbench`.

## Product character

PSX is a developer workbench. The conversation is the primary work surface, the Plan card is floating context, and the Composer is the fixed command surface. The UI should feel dense, calm, and operational: warm neutral surfaces, teal emphasis, direct hierarchy, and terminal-grade detail without terminal typography everywhere.

The shape language is **Soft Workbench**: continuous, generous corner radii on a borders-and-fill hierarchy — macOS-grade smoothness without decorative chrome. Avoid gradients, glow, decorative illustration, card walls, nested cards, animated focus rings, and color-only status indicators. Prefer dividers, spacing, typography, and explicit state labels.

## Layout

- Shell: tab strip and a compact Terminal / Agent mode switch.
- Agent toolbar: one 44px status row with working directory and thread actions.
- Layered shell: History is a single global left dock (default 280px, draggable 220–420px, 220px effective while narrow); the conversation canvas sits above it; the Plan card is a content-height overlay below the toolbar at the top-right (fixed 320px, no resizer).
- Conversation: one collision-aware centered reading column, max 920px. With History open it moves right only enough to clear the dock; it never reserves a mirrored dock-width strip.
- Composer: shares the reading column. The composer card is the one large-radius signature surface (24px); the circular send button's center lands on the card's corner arc center (footer right/bottom padding = card radius − send radius = 7px).
- Supported minimum window: 900x560. At narrow widths, metadata wraps before primary controls shrink.

## Typography

- UI and prose: `Segoe UI Variable`, then `Segoe UI`, Microsoft YaHei UI, and system sans-serif.
- Paths, commands, code, raw plans, and tool output: `Cascadia Code`, then Consolas and monospace.
- Body copy uses the configured Agent size with a 1.58-1.65 line height. Labels and operational metadata use 12-13px and remain at least 4.5:1 against their surface.
- A turn displays `You` or the Agent name once. Thinking, tools, and the response remain in that turn.

## Spacing and shape

- Base unit: 4px. Use only the `--agent-space-*` tokens.
- Corner radii use the five-step semantic ladder, never ad-hoc values:

  | Token | Value | Used for |
  |---|---|---|
  | `--agent-radius-control` | 8px | buttons, icon buttons, chips, list rows, menu items, inline code |
  | `--agent-radius-input` | 10px | text inputs, search fields, select triggers |
  | `--agent-radius-card` | 14px | tool/decision/runtime/recovery/Plan cards, popovers, menus, tooltips, image previews, code blocks |
  | `--agent-radius-workspace-canvas` | 18px | the workspace canvas edge (structural, shell.css) |
  | `--agent-radius-composer` | 24px | the composer card (structural, shell.css) |

- The structural shell tokens `--agent-radius-context-card` / `--agent-radius-workspace-canvas` / `--agent-radius-composer` stay in shell.css and map onto this ladder; component CSS never defines its own radius values.
- Pills (`50%` / `999px`) are reserved for genuinely circular or capsule elements: the send button, switches, status dots, badge dots, scrollbar thumbs.
- WPF chrome mirrors the ladder through `ControlCornerRadius` (8), `InputCornerRadius` (10), `CardCornerRadius` (14) in `Themes/Dark.xaml`.
- Borders are 1px. PSX intentionally renders no standalone focus outline.

## Depth doctrine: borders, fills, and five floating layers

PSX builds hierarchy with borders and surface lightness steps. Shadows are reserved for exactly five floating layers:

1. the workspace canvas edge (`--agent-shadow-canvas`),
2. the composer card (`--agent-shadow-composer`),
3. context cards such as Plan (`--agent-shadow-context-card`),
4. floating menus and popovers (`--agent-shadow-popover`),
5. modal media (`--agent-shadow-dialog`).

Content cards (tool, decision, runtime, recovery) are `surface` + hairline border with **no** shadow. Controls (buttons, inputs, list rows) are hairline-only. All shadows derive from `--agent-shadow` via color-mix; never hardcode shadow colors.

## Color roles

The existing INI theme schema is authoritative; CSS variables are semantic aliases of it.

- `background`: canvas and workbench.
- `surface`: editable or user-authored surfaces.
- `surfaceRaised`: floating menus and secondary controls.
- `surfaceMuted`: selected state and raw output.
- `text`: primary copy.
- `textMuted`: every readable status, label, Plan item, and helper line.
- `textDim`: disabled or decorative marks only.
- `accent`: selection, running state, and links.
- `error` and `warning`: paired with an icon/marker or explicit text.

Primary and small secondary text must be at least 4.5:1 against their rendered surface. Theme validation reports all relevant failures without adding INI fields.

## Interaction states

Every interactive element provides default, hover, active, and disabled states. Running, success, warning, and error states use a marker or label in addition to color.

- Hover changes surface or border only on hover-capable devices.
- Active controls move by at most 1px or use a stronger surface.
- Keyboard navigation, programmatic focus, Escape handling, and focus restoration remain functional behavior, but PSX deliberately adds no outline, border, glow, or shadow solely to visualize focus in either WebView2 or WPF. Do not reintroduce an independent focus ring on pointer interaction, keyboard navigation, or `:focus-within`.
- Disabled controls remain legible, use `textDim` only for nonessential copy, and expose an explanation through their title or adjacent status.
- Motion is limited to the running spinner and disclosure chevrons. `prefers-reduced-motion` disables both.

## Component rules

- **Buttons have one component contract.** Agent React surfaces use the shadcn `Button` primitive and its named variants; component files may add layout geometry but must not recreate hover/active/disabled skins. Remaining non-React shell controls use their local semantic selectors until their owning surface migrates.
- WPF chrome buttons use `SoftWorkbenchButtonStyle`, `SoftWorkbenchToggleButtonStyle`, or `SoftWorkbenchIconButtonStyle` from `Themes/Dark.xaml`; icons are XAML `Path` geometry, never font glyphs.
- Assistant responses are not cards. User prompts use one low-contrast bounded surface and never form left/right chat bubbles.
- Tool activity is one disclosure region containing a flat divided list. Each row includes an explicit state label; no colored side rail.
- Thinking is a single disclosure row. Completed thinking closes by default.
- Plan and History retain separate DOM, scroll position, and selection state. Completed Plan items keep readable text and pair a check marker with completion styling. History rows highlight with a full-row rounded fill — no inset accent stripes.
- Commands use combobox/listbox/option semantics and synchronize `aria-activedescendant`.
- Image preview uses a native modal dialog with Escape, backdrop click, explicit close, and focus restoration.
- Existing DOM IDs, command bindings, bridge payloads, ACP protocol, and provider boundaries are compatibility contracts.

## Implementation stamp

Agent CSS carries:

`Hallmark · genre: modern-minimal · macrostructure: Workbench · design-system: design.md · designed-as-app`
