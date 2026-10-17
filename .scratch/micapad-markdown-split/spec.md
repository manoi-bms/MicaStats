# MicaPad Markdown split preview

Status: complete

The user requests original Markdown text on the left and a live rendered preview on the right, with a friendly, professional and informative interface. Follow root DESIGN.md and the existing Pad light/dark themes.

## Behavior

- Add an explicit, labeled Split preview action with a meaningful two-pane icon, menu entry and Ctrl+Shift+M shortcut. Remember the user's view preference and divider position without modifying note text or task metadata.
- For Markdown notes, show editable original source on the left and a selectable, read-only rendered preview on the right. Show Source / Preview captions and truthful updating/live/empty/limited/error feedback. Preserve caret, selection, undo/redo, autosave and file export.
- While split preview is visible, keep Markdown markers, image syntax and diagram fences visible in the source editor. Restore its ordinary inline formatting when the preview is closed or temporarily unavailable. Language resolution and task tracking continue to identify the note as Markdown.
- Reuse existing Markdown parsing/rendering and local diagram capabilities. Support headings, emphasis, lists, task checkboxes, quotes, tables and fenced code; retain understandable source fallbacks for unsupported content or drawing failures. Do not execute note HTML or initiate AI requests.
- Refresh after a short typing pause, immediately refresh when changing notes, reject obsolete callbacks, and preserve the preview's scroll position within a note. Long notes need an explicit bounded-preview message rather than freezing typing or implying a truncated preview is complete.
- Keep the history/version preview separate and preserve its read-only editing guards. Search, AI and History retain their existing actions and width preference; selecting Split preview returns the right pane to Markdown. Temporarily replacing the preview must release the displayed note content and restore the selected view when appropriate.
- Clear pending updates and retained preview content on vault entry, note changes, hide and exit. Credential scrubbing must immediately remove stale displayed content. Respect existing diagram/network settings and safe-link behavior.
- Use a draggable, keyboard-accessible divider with a balanced default, remembered ratio and double-click reset. Keep both panels usable at 420x260, with synchronized scrolling, wrapping headers, visible focus, accessible names and light/dark themes. The later scrolling request is specified and verified in ../micapad-markdown-scroll-sync/spec.md.

## Verification

Use synthetic notes only. Cover source immutability, render updates, task/code/table output, debounce/coalescing and long-note feedback; view persistence, shortcut, tab/language changes, history/search/AI and vault lifecycle; width fitting, resize/reset, theme and minimum-size geometry. Run meaningful targeted tests, the full suite via test.ps1 and a staged Release build serially at BelowNormal priority. Inspect light/dark screenshots at normal and minimum sizes and smoke-test the deployed local application.

No new dependencies. GitHub release publication is outside this feature request.

## Implemented rendering boundaries

The right pane reuses the existing local FlowDocument renderer with a presentation-only checkbox
transformation. It renders headings, emphasis, lists, task glyphs, quotes, tables, fenced code and
local Mermaid. Copy/Source/Retry controls have meaningful Fluent icons and accessible labels.
Links show selectable labels and addresses without navigation. Images, HTML, math and other
extended syntax retain readable text fallbacks; the pane does not fetch web images or run AI.
Preview input is limited to 50,000 characters and 2,000 lines, with a visible Limited notice that
reports the original document length. Source documents and encrypted task metadata are unaffected.

Search, AI and History temporarily use the right-hand utility column; closing them resumes the
chosen preview. The preview's divider ratio remains separate from the existing utility width.
Credential-card entry clears rendered content synchronously; card-close restoration waits until
the synchronous storage callback finishes so scrubbed credentials cannot reappear in the preview.
Hide, exit and note changes release the preview and pending callbacks.
