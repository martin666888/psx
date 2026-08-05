# Agent brand icon sources

The original monochrome SVGs in `lobehub-1.94.0/` come from
`@lobehub/icons-static-svg@1.94.0`:

- `claude.svg`
- `kimi.svg`
- `qwen.svg`
- `qoder.svg`

PSX converts these pinned SVG paths to WPF `StreamGeometry` resources in
`Themes/AgentIcons.xaml`. The application never downloads icons at runtime.
See `licenses/lobe-icons/LICENSE` and `THIRD-PARTY-NOTICES.md`.
