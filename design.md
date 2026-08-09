# PSX UI Design System

Hallmark profile: `modern-minimal / technical restrained / Workbench`.

## Product character

PSX is a developer workbench. The conversation is the primary work surface, the Plan card is floating context, and the Composer is the fixed command surface. The UI should feel dense, calm, and operational: warm neutral surfaces, teal emphasis, direct hierarchy, and terminal-grade detail without terminal typography everywhere.

The shape language is **Soft Workbench**: continuous, generous corner radii on a borders-and-fill hierarchy — macOS-grade smoothness without decorative chrome. Avoid gradients, glow, decorative illustration, card walls, nested cards, animated focus rings, and color-only status indicators. Prefer dividers, spacing, typography, and explicit state labels.

## Layout

- Shell: the WebView owns a permanent 40px full-height activity rail at the left holding the global History, workspace list, create, and Theme buttons, plus a 40px workspace chrome row above the pane contents carrying only the nameplates aligned to each visible pane. There is no second WPF tab or theme row.
- Pane nameplates: the focused pane uses an accent underline and stronger text. Provider/Terminal icon, truncated title, explicit attention text, close, and More occupy the pane's exact top rect, never colliding with the activity rail.
- Agent toolbar: a pane-local status row with working directory and thread actions. At 720px it is complete, at 520–719px metadata truncates, and at 400–519px only the cwd basename and a ⋯ overflow entry remain. The Plan and Update actions remain directly reachable at ≥520px and move into the controlled ⋯ popover below 520px; responsive presentation must never place visible state behind a closed native disclosure.
- Layered shell: History is one global left dock (default 280px, draggable 220–420px) opened from the permanent rail and pushes every pane. Plan is a workspace-local card; below 520px it opens as a pane-local overlay from a summary entry.
- Conversation: one centered reading column per pane, max 920px. Its containing block is the pane rect after the History inset, never the application viewport.
- Composer: shares the reading column. The composer card shares the 24px structure radius with the panels; the circular send button's center lands on the card's corner arc center (footer right/bottom padding = card radius − send radius = 7px). Every Pane keeps the card anchored 24px above the panel bottom at every container width; wrapped placeholder text, drafts, attachments, and decisions grow the card upward instead of changing that cross-pane baseline.
- Pane minimums: existing Agent columns display at a 320px floor; the 400px figure is only the capacity-preview gate for a brand-new Agent column. Terminal `max(400px, 60 measured columns + horizontal padding)`, with a 480px fallback before measurement. When the sum does not fit, columns squeeze proportionally below their pixel floors rather than ever being collected, hidden, or dropped.
- Composer response: at 720px all configuration stays on one row; at 520–719px session mode and provider config controls move into the configuration overlay while Context usage stays in the footer; at 400–519px the Composer keeps controls in the configuration overlay with context hints hidden while attachment, input, configuration, and send remain. Every width-bearing flex ancestor permits shrinkage and no pane paints into its neighbor.

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
  | `--agent-radius-structure` | 24px | the structural panels: Workspace panel + History dock + Composer card (structural, shell.css) |

- The structural shell token `--agent-radius-structure` (24px) stays in shell.css; `--agent-workspace-radius` / `--agent-radius-composer` map onto it and `--agent-radius-context-card` maps onto `--agent-radius-card`. Component CSS never defines its own radius values. The Agent page is a soft workbench: both the History dock and the main panel are free-standing rounded blocks floating on the `--agent-bg` backdrop, spaced by `--agent-workbench-gutter` / `--agent-panel-gap`.
- Pills (`50%` / `999px`) are reserved for genuinely circular or capsule elements: the send button, switches, status dots, badge dots, scrollbar thumbs.
- WPF chrome mirrors the ladder through `ControlCornerRadius` (8), `InputCornerRadius` (10), `CardCornerRadius` (14) in `Themes/Dark.xaml`.
- Borders are 1px. Focus indication uses the single uniform outline ring defined in Interaction states.

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
- Compact metadata rows that pair text with a textual action align the glyphs by baseline; icon-only action groups align by geometric center. Do not compensate individual labels with margins or transforms.
- Focus uses one uniform outline ring: `2px solid var(--agent-focus-ring)` with `outline-offset: 2px`. It shows only on keyboard focus (`:focus-visible`; the Composer card rings via `:has([data-role="input"]:focus-visible)` so inner buttons keep their own ring), never on pointer click, and appears instantly. Menu items may keep their focused surface state without a ring, and edge-anchored controls use an inset ring to avoid clipping. Pane focus is represented only by the nameplate accent underline and foreground strength; never draw a persistent pane border or dim an unfocused pane.
- Attention is never color-only and never a dot. Pane nameplates and the global workspace list use the short labels `需确认`, `待回复`, `出错`, and `已完成`. Same-worktree risk is a themed non-blocking notification emitted only when a conflict forms.
- Disabled controls remain legible, use `textDim` only for nonessential copy, and expose an explanation through their title or adjacent status.
- Motion is limited to the running spinner, disclosure chevrons, and 120–150ms overlay/History entrances using opacity and a short translate. Pane widths, chrome slots, dividers, reading columns, and Terminal geometry never animate. Divider previews update at most once per animation frame; xterm fit and ConPTY resize happen after release. `prefers-reduced-motion` disables every nonessential effect at the `.agent-ui` root.

## Component rules

- **Buttons have one component contract.** Agent React surfaces use the shadcn `Button` primitive and its named variants; component files may add layout geometry but must not recreate hover/active/disabled skins. Remaining non-React shell controls use their local semantic selectors until their owning surface migrates.
- The visible workspace, create, and Theme menus are WebView surfaces and use the active CSS theme tokens. The hidden WPF chrome remains compatibility-only and must not reappear as a parallel navigation surface.
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
