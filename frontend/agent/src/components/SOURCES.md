# Third-Party UI Component Sources (CP2)

## shadcn/ui primitives — `frontend/agent/src/components/ui/`

- Source: shadcn/ui registry, style `new-york-v4` (button/dialog/input/popover/
  scroll-area/select fetched 2026-02 from `ui.shadcn.com` during the CP0 probe;
  badge/collapsible/tooltip/button-group/command/dropdown-menu/hover-card/
  input-group/textarea/separator/kbd fetched from
  `https://ui.shadcn.com/r/styles/new-york-v4/{name}.json`).
- License: MIT (`licenses/shadcn/LICENSE`). Runtime deps: `radix-ui`, `cmdk`,
  `class-variance-authority`, `clsx`, `tailwind-merge`, `lucide-react`.
- Local modifications: none.

## AI Elements — `frontend/agent/src/components/ai-elements/`

- Source: Vercel AI Elements shadcn registry,
  `https://registry.ai-sdk.dev/{name}.json` (conversation, message, reasoning,
  tool, prompt-input, loader, task, shimmer).
- License: Apache-2.0 (`licenses/ai-elements/LICENSE`).
- Local modifications (campaign plan fixed mappings — PSX ships no Next.js,
  AI SDK, Streamdown, motion, nanoid or Shiki):
  - `message.tsx`: MessageBranch* family removed (no branching requirement);
    Streamdown-based MessageResponse removed (CP4 adds PsxMessageResponse over
    the existing sanitized `renderMarkdown()` HTML pipeline); `ai` types
    (UIMessage/FileUIPart) replaced with local equivalents.
  - `reasoning.tsx`: ReasoningContent renders ReactNode children instead of
    Streamdown.
  - `tool.tsx`: `ai` ToolUIPart types replaced with a local ToolState union;
    AI Elements CodeBlock (Shiki) replaced with plain `pre > code` (the PSX
    fenced-code HTML pipeline styles it).
  - `prompt-input.tsx`: `ai` types (ChatStatus/FileUIPart) declared locally;
    `nanoid` replaced with `crypto.randomUUID`.
  - `shimmer.tsx`: motion/react animation reproduced as a CSS keyframe
    (`psx-shimmer` in `frontend/webview/src/css/tailwind.css`).
  - `conversation.tsx`, `loader.tsx`, `task.tsx`: unchanged.
- Not installed (no matching requirement): branch, citation/inline-citation,
  web-preview, image, code-block, and the remaining registry items.
