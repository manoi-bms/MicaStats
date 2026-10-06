# Finding open notes in MicaPad

Research date: 2026-10-06. The user finds many open tabs difficult to navigate and identify. This brief uses official desktop-product guidance; it proposes a design for MicaPad rather than claiming a measured usability result.

## Evidence and implications

| Primary source | Relevant evidence | MicaPad application |
| --- | --- | --- |
| [Microsoft TabView guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/tab-view) | Document tabs support closing, rearranging, descriptive labels, overflow scrolling and familiar keyboard navigation. Close buttons can appear on the active tab and on hover. | Retain the existing tab order, drag behavior and shortcuts. Reserve readable title space and reduce idle visual clutter. |
| [VS Code user interface](https://code.visualstudio.com/docs/editing/getting-started/userinterface) | Quick Open and the Open Editors list complement editor tabs. | Provide a visible Open notes button and Ctrl+P for searching already-open notes by title or path. |
| [VS Code code navigation](https://code.visualstudio.com/docs/editing/editingevolved) | Keyboard navigation and document identity help users navigate a large set of editors. | Focus the search field, support arrows/Enter/Escape, and display a secondary location for duplicate file names. |

## Selected design

Keep a single horizontal strip for immediate switching. Use readable tab widths, a clear active state and full-title/path tooltips. Keep New note outside the scroll area. Add an always-visible Open notes button with this window's count. Its searchable vertical picker lists all open notes, marks the current note, identifies other windows, and distinguishes scratch notes by their note number. It opens only on request, so the editor keeps its width while writing. Title/path filtering stays local and does not read entire note bodies or send network requests.

Choosing a result uses the existing note/window activation path. Filtering or highlighting a result does not change the active document. Enter or one click commits the switch; Escape cancels. Preserve selection, undo, drag reorder, close/reopen, encrypted persistence and vault boundaries. Keep Ctrl+Tab in its existing tab-order behavior, rather than changing established shortcuts during this redesign.

## Tradeoffs and validation

A permanent vertical rail makes more titles visible but consumes editor width at MicaPad's 420-pixel minimum. The on-demand vertical picker gives similar scanability while preserving the writing surface. Multi-row tabs would move the editor as notes open. Neither is part of this bounded change.

Validate many open notes, long and duplicate titles, matching paths, no-result state, keyboard cancellation/confirmation, cross-window navigation, collection updates, and dark/light layouts at normal and minimum window sizes. These checks establish functional and layout behavior; real user testing remains the way to measure finding time.
