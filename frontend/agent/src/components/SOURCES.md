# Third-Party UI Component Sources (CP2)

## shadcn/ui primitives — `frontend/agent/src/components/ui/`

- Source: shadcn/ui registry, style `new-york-v4` (button/dialog/input/popover/
  scroll-area/select fetched 2026-02 from `ui.shadcn.com` during the CP0 probe;
  badge/collapsible/tooltip/button-group/command/dropdown-menu/hover-card/
  input-group/textarea/separator/kbd fetched from
  `https://ui.shadcn.com/r/styles/new-york-v4/{name}.json`; card fetched
  2026-07-28 and switch fetched 2026-07-28 from the same registry).
- License: MIT (`licenses/shadcn/LICENSE`). Runtime deps: `radix-ui`, `cmdk`,
  `class-variance-authority`, `clsx`, `tailwind-merge`, `lucide-react`.
- Local modifications: none.

## AI Elements — `frontend/agent/src/components/ai-elements/`

- Source: Vercel AI Elements shadcn registry,
  `https://registry.ai-sdk.dev/{name}.json` (conversation, message, reasoning,
  tool, prompt-input, loader, task, shimmer).
- License: Apache-2.0 (`licenses/ai-elements/LICENSE`).
- Local modifications (PSX ships no Next.js, AI SDK, motion or nanoid):
  - `message.tsx`: MessageBranch* family removed (no branching requirement);
    the upstream MessageResponse is replaced by PsxMessageResponse, which
    delegates to PSX's `MarkdownContent` security/performance wrapper; `ai`
    types (UIMessage/FileUIPart) are replaced with local equivalents.
  - `reasoning.tsx`: ReasoningContent renders ReactNode children instead of
    Streamdown.
  - `tool.tsx`: `ai` ToolUIPart types replaced with a local ToolState union;
    raw tool output remains plain `pre > code`; Markdown fenced code uses the
    separately loaded Streamdown/Shiki path. CP4 adds optional `badge` (PSX
    ACP tool-state wording) and `titleClassName` (semantic anchor classes)
    props to ToolHeader.
  - `prompt-input.tsx`: `ai` types (ChatStatus/FileUIPart) declared locally;
    `nanoid` replaced with `crypto.randomUUID`; adds optional
    removal/preview chrome, `convertBlobUrls`, the attachment-input data role,
    and composition of consumer textarea keyboard/composition/paste handlers
    for the ComposerView/AttachmentBridge integration.
  - `shimmer.tsx`: motion/react animation reproduced as a CSS keyframe
    (`psx-shimmer` in `frontend/webview/src/css/tailwind.css`).
  - `conversation.tsx`, `loader.tsx`, `task.tsx`: unchanged.
- Not installed (no matching requirement): branch, citation/inline-citation,
  web-preview, image, code-block, and the remaining registry items.

## Streamdown renderer — `frontend/agent/src/markdown/`

- Source: Vercel Streamdown 2.5.0 with the official CJK 1.0.3, code 1.1.1,
  math 1.0.2 and Mermaid 1.0.2 plugins.
- Fenced-code highlighting runs in a local, idle-terminated Shiki Worker; the
  main Agent graph and ordinary Markdown path do not import Shiki.
- License: Apache-2.0 (`licenses/streamdown/LICENSE`). Shiki, KaTeX and
  Mermaid retain their MIT licenses under `licenses/`.
- Decision reversal: the CP2 import intentionally omitted Streamdown and
  Shiki while PSX still used its byte-compatible legacy HTML renderer. The
  Markdown upgrade replaces that renderer to gain CommonMark/GFM compliance,
  incomplete-stream repair, CJK, math and diagrams. PSX does not use the
  upstream MessageResponse directly: `MarkdownContent` enforces URL/image
  hardening, controls, source limits, local assets and lazy plugin loading.

## Maple Mono Normal CN — Agent mono typography

- Source: Maple Mono Normal CN 7.9 release archive
  `MapleMonoNormal-CN.zip` from
  `https://github.com/subframe7536/maple-font/releases/tag/v7.9`.
- Archive SHA-256:
  `0EE9557B3F4C94564B667A45EE9FB22818F880D87BF170687C7B3D0151C584CB`.
- Shipped files: unmodified Regular 400 and SemiBold 600 TTF faces under
  `wwwroot/vendor/fonts/maple-mono/`; CSS exposes them through the local alias
  `PSX Maple Mono` without renaming the font binaries. PSX uses this face only
  for code and other monospaced technical content; natural-language Agent UI
  uses the proportional Windows UI font stack.
- License: SIL Open Font License 1.1 (`licenses/maple-mono/LICENSE.txt`).
