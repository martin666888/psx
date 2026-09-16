# File-edit permission previews

Structured ACP `diff` permission content uses a compact file preview, separately
from ordinary Markdown document and mode-transition cards. The latter retain
their existing layout. No permission response or offered option is synthesized.

`permission_request.editBlocks` and history `messages[].editBlocks` carry an
ordered list of text/diff blocks. Each diff retains the exact `oldText` (nullable)
and `newText`, the resolved full `path`, project-relative `displayPath` when
inside the requesting session's cwd, and `external`. Paths are lexical display
metadata, not filesystem access or symlink-containment checks. Unresolvable
targets retain the provider path. Unknown/incomplete content uses the existing
document fallback. Old transcripts without this optional field remain readable;
Markdown is never parsed back into a structured edit. Snapshot merging copies
the list and retains its immutable blocks.

File headers control collapse and offer full-path copying. Mixed text stays in
provider order; multiple files share one permission action area. Resolved cards
retain their previews but remove inactive option buttons. Allow/reject results
use ACP option kinds, not English option labels; cancellation and interruption
keep their distinct statuses. Explicit technical input remains expandable.

The line comparison preserves unchanged context and end-of-file newline changes.
It bounds the LCS matrix to one million cells after trimming common prefix/suffix;
larger unmatched middles display as exact replacements. Previews initially mount
at most 300 rows per file, with incremental expansion and the shared scrollbar
skin. Ordinary documents and legacy decisions retain their existing renderer.

Validation covers structured live events, persistence, snapshot merge, history
payloads, mixed content, external paths, actual option dispatch, resolved states,
unchanged lines, EOF changes, and the large-input fallback. Run the sequential
Fast gate; inspect light/dark and narrow-column previews when changing layout.
