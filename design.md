# PSX UI Design System

Hallmark profile: `modern-minimal / technical restrained / Workbench`.

## Product character

PSX is a developer workbench. The conversation is the primary work surface, the Inspector is persistent context, and the Composer is the fixed command surface. The UI should feel dense, calm, and operational: warm neutral surfaces, teal emphasis, direct hierarchy, and terminal-grade detail without terminal typography everywhere.

Avoid gradients, glow, decorative illustration, card walls, nested cards, animated focus rings, and color-only status indicators. Prefer dividers, spacing, typography, and explicit state labels.

## Layout

- Shell: tab strip and a compact Terminal / Agent mode switch.
- Agent toolbar: one 44px status row with working directory and thread actions.
- Workbench: conversation fills the flexible column; Inspector stays between 260px and 380px.
- Conversation: every turn uses one centered responsive column. It fills the available conversation pane up to `1120px`, then stops growing; user prompts, Thinking, tools, decisions, prose, code, and tables share this width.
- Composer: the attachment button, input, Send/Stop button, hints, and configuration row use the same centered `1120px` column as the conversation above. Status hints and configuration controls share one compact toolbar whenever that width is available; narrow windows wrap before controls become unreadable. The input action row remains 44px high and the command list opens directly above it.
- Supported minimum window: 900x560. At narrow widths, metadata wraps before primary controls shrink.

## Typography

- UI and prose: `Segoe UI Variable`, then `Segoe UI`, Microsoft YaHei UI, and system sans-serif.
- Paths, commands, code, raw plans, and tool output: `Cascadia Code`, then Consolas and monospace.
- Body copy uses the configured Agent size with a 1.58-1.65 line height. Labels and operational metadata use 12-13px and remain at least 4.5:1 against their surface.
- A turn displays `You` or the Agent name once. Thinking, tools, and the response remain in that turn.

## Spacing and shape

- Base unit: 4px. Use only the `--agent-space-*` tokens.
- Compact controls use a 4px radius; input surfaces and bounded content use 6px; overlays use 8px.
- Borders are 1px. A 2px outline is reserved for `:focus-visible`.
- Shadows are reserved for floating command menus and modal media.

## Color roles

The existing INI theme schema is authoritative; CSS variables are semantic aliases of it.

- `background`: canvas and workbench.
- `surface`: editable or user-authored surfaces.
- `surfaceRaised`: floating menus and secondary controls.
- `surfaceMuted`: selected state and raw output.
- `text`: primary copy.
- `textMuted`: every readable status, label, Plan item, and helper line.
- `textDim`: disabled or decorative marks only.
- `accent`: focus, selection, running state, and links.
- `error` and `warning`: paired with an icon/marker or explicit text.

Primary and small secondary text must be at least 4.5:1 against their rendered surface. Focus indicators must be at least 3:1 against adjacent colors. Theme validation reports all relevant failures without adding INI fields.

## Interaction states

Every interactive element provides default, hover, focus-visible, active, and disabled states. Running, success, warning, and error states use a marker or label in addition to color.

- Hover changes surface or border only on hover-capable devices.
- Active controls move by at most 1px or use a stronger surface.
- Focus uses a static 2px `--agent-focus-ring` outline with 2px offset (inset where clipping requires it).
- Disabled controls remain legible, use `textDim` only for nonessential copy, and expose an explanation through their title or adjacent status.
- Motion is limited to the running spinner and disclosure chevrons. `prefers-reduced-motion` disables both.

## Component rules

- Assistant responses are not cards. User prompts use one low-contrast bounded surface and never form left/right chat bubbles.
- Tool activity is one disclosure region containing a flat divided list. Each row includes an explicit state label; no colored side rail.
- Thinking is a single disclosure row. Completed thinking closes by default.
- Plan and History retain separate DOM, scroll position, and selection state. Completed Plan items keep readable text and pair a check marker with completion styling.
- Commands use combobox/listbox/option semantics and synchronize `aria-activedescendant`.
- Image preview uses a native modal dialog with Escape, backdrop click, explicit close, and focus restoration.
- Existing DOM IDs, command bindings, bridge payloads, ACP protocol, and provider boundaries are compatibility contracts.

## Implementation stamp

Agent CSS carries:

`Hallmark · genre: modern-minimal · macrostructure: Workbench · design-system: design.md · designed-as-app`
