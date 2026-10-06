# Design

## Source of truth

Status: Active. Updated 2026-10-06. Scope: Meeting Assistant, Settings, and MicaPad task tracking, open-note navigation/appearance and action surfaces in the Windows desktop application. Evidence: `Conference/MeetingWindow.xaml`, its partial classes, `SettingsWindow.xaml`, `Ai/AiSettingsPanel.xaml`, `Pad/SearchSettingsPanel.xaml`, `Services/Ai/AskPalette.cs`, `Ai/AskThemeApplier.cs`, `Ai/ChatStyles.xaml`, `docs/meeting-assistant.md`, the conference specification, `Pad/MicaPadWindow.xaml`, `Pad/MicaPadWindow.Tasks.cs`, `Pad/TaskDateEditor.xaml`, `Services/Pad/MarkdownTasks.cs`, `Services/Pad/NoteTabColors.cs`, `Helpers/ButtonIcon.cs`, `.scratch/ui-icon-compliance/spec.md`, `.scratch/micapad-tab-colors/spec.md`, `.scratch/micapad-task-date-editing/spec.md`, `docs/research/micapad-open-note-navigation.md` and `docs/research/micapad-todo-productivity.md`. The existing app themes are the visual baseline; research cites official product guidance.

## Brand

Calm, precise, capable Windows software. Trust comes from visible recording state, explicit actions, understandable sources and honest configuration status. Avoid decorative dashboards, invented activity meters, oversized marketing headings and hardcoded service identities or hosts.

The owner requests professional, modern, informative and user-friendly UI for every new dialog and modification. Use meaningful icons or suitable images on key actions and section headers, paired with clear text. Prefer existing Fluent icon fonts and theme resources. Visual cues should help recognition without adding clutter. Keep common actions easy to find, preserve familiar keyboard behavior, show useful feedback, and fit small windows before adding decoration. Audit popup commands and secondary Settings/panel actions as well as primary buttons; verify icon semantics and changing labels, not just presence in initial markup.

## Product goals

Make service setup, listening state and the next useful action immediately clear. Help an attendee follow a transcript, prepare supported responses and choose when to speak or export. Preserve explicit capture, automatic local transcript recovery, manual full-report export and configured-provider boundaries. Success means an unconfigured user can find setup, and an active user can identify inputs, status, supporting evidence, persistence state and any listening gap at a glance.

Non-goals: automatic conference replies, attendee identification, new audio capabilities or a replacement for the whole application's theme system.

## Personas and jobs

Primary user: a conference attendee monitoring live speech while multitasking. They need readable transcript context, concise assistance and predictable audio controls. A first-time user configures their own compatible services before using the workflow. Public-repository users must never inherit another user's service address.

## Information architecture

MicaPad uses readable horizontal document tabs for immediate switching and an Open notes button with this window's count for discovery. Ctrl+P opens an on-demand vertical list of all open notes, filtered locally by title or source path. Rows show title, location or scratch-note identity, and current/other-window context. Retain Ctrl+Tab tab order, numeric shortcuts, drag reorder and the existing activation/persistence paths. Keep New note reachable outside overflow. The picker preserves editor width while writing; a permanent rail or multi-row strip is outside this change.

Meeting Assistant has a session header, setup/readiness feedback, device and session controls, useful session facts, then Transcript / Summary & answers / Reference notes / Speech workspaces. Keep Stop reachable and distinct from Speak. The footer shows transcript save status and a **Saved transcripts** action that opens the local meetings folder. The header links to dedicated Settings → Meeting. Settings groups the two ASR profiles and TTS separately, identifies compatible protocols/models, uses empty endpoint inputs and an explicit Save action.

One compatible ASR endpoint is sufficient in single-service mode; the explicit ASR2 + ASR1 comparison mode requires both. Explain beside that choice that every audio chunk goes to both services and doubles uploads. Keep one segment per chunk with labeled alternative readings and honest uncertainty. A failed provider remains disabled until the next meeting, with a persistent status even during silence. TTS remains optional. Each service card provides Test connection / Cancel test plus a wrapping inline result; tests act on draft inputs independently of Save and describe the synthetic request before dispatch.

Summary & answers contains an AI language selector: Follow transcript, Thai and English. Keep this distinct from ASR language capabilities. Remember the selection; changing it clears old derived answers/prepared speech and refreshes active analysis while capture continues. Preserve the source transcript and names/technical terms. Explicit language choices take precedence; Follow transcript uses the conversation language rather than the application's English labels.

## Design principles

State precedes detail. Data density serves reading; spacing and alignment group related controls. Display real state and counts only. Use clear empty states and explain disabled actions. Configuration changes stop voice work before applying new destinations. Service URLs belong in user settings, never branded defaults or repository examples.

## Visual language

Reuse dynamic `Ask.*` palette resources and ModernWpf controls. Use the existing Segoe UI Variable / Segoe UI typefaces, approximately 13–14 px body text, 11–12 px secondary labels, 16–18 px section titles and a restrained 20 px window heading. Use a consistent 4/8 px spacing rhythm, 16–24 px region padding, subtle one-pixel borders and moderate corner radii. Accent the primary action and state sparingly. Avoid unnecessary gradients, large shadows and animation. Use restrained Fluent/MDL2 icons beside readable labels; retain established navigation cues, explicit automation names and words for every state. Check glyph meanings against [Microsoft's icon catalog](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-ui-symbol-font); keep labels for actions with no exact glyph and use swatches for colors.

## Components

MicaPad tabs use Pad palette resources, a readable minimum title width, a distinct active background/accent edge and semibold title, and full-title/path tooltips. Close buttons appear for the current or hovered/focused tab, retaining their reserved width to avoid label movement. Open notes is a non-modal searchable picker with a virtualized vertical list, a visible current marker, meaningful automation names and an empty state. Use metadata only; no content preview or network search.

Each note has a stable saved color, automatically chosen for separation from other open notes. Use a small marker and soft theme-aware tab tint, with a matching active underline and neutral titles. Repeat the marker in Open notes. A named Tab color submenu uses swatches and a checked current choice; color changes update appearance without changing note text or file dirty state. Preserve color during rename/reorder/moves/restart. Titles and active words remain readable without distinguishing colors; verify title and marker contrast across the hue range. See `.scratch/micapad-tab-colors/spec.md`.

Reuse buttons, text inputs, selectors, tab workspaces and existing theme tokens. Meeting-local card, caption and status styles may compose those tokens; do not add a second palette. Session facts may show source selection, finalized segment count, selected-reference count, actual latest transcript time, and AI allowance. Never label latest transcript time as elapsed live duration. Empty transcript/answer states explain the next step. Preserve selectable transcript and answer text.

Labeled action buttons use a small decorative glyph plus their existing content. Preserve the content string, style, command and accessible name when a label changes to loading/cancel or a vault mode changes. Decorative glyphs do not enter the WPF automation tree. Menu checks and icons occupy separate columns, retaining a selected color swatch. Action groups wrap where adding icons would otherwise clip labels. See `.scratch/ui-icon-compliance/spec.md` and `Helpers/ButtonIcon.cs`.

Two compact audio cards show the selected microphone and output device, a three-second peak waveform, digital peak level and a plain-language state. Read actual existing capture buffers; never animate invented activity or equate sound with recognized speech. Silence/no recent buffers become flat, near-clipping has a text warning, and paused/stopped/faulted capture clears the display. Keep source names available when Audio setup is collapsed. Poll bounded amplitude data at ten frames per second only while the visible window is listening; no extra capture client, network call or audio persistence. These are amplitude envelopes, not frequency spectra or speaker identification.

Transcript rows use small source labels and time ranges above selectable body text. Keep internal source IDs in label tooltips and Markdown export, with clear ASR disagreement notes and listening gaps. Update existing content incrementally: follow incoming text at the bottom, preserve scroll position and selection while reading earlier content, and provide a Latest action.

Persist every received transcript update as crash-safe JSON recovery state plus readable Markdown under `%APPDATA%\MicaStats\Meetings`. Keep transcript times, source labels and IDs, dual alternatives, and listening gaps; exclude raw audio, service endpoints, reference notes, and all AI output. Retain earlier meetings. Manual **Save Markdown** remains a distinct full report with current analysis.

Summary & answers has visible empty-state guidance, AI settings access, a labeled question input and Enter-to-submit. Manual requests show progress and reject duplicate clicks; validation and failures stay visible. Preserve the selected question when it still exists after refreshed analysis. Copy includes the displayed answer, citations and missing information, with confirmation. Prepare speech transfers only the answer to Speech and explains the next explicit Speak action. Start is required for private questions; received answers remain copyable after Stop.

## Accessibility

Target WCAG 2.2 AA principles as applicable to desktop WPF; this is a target, not a certification. Keep controls keyboard-reachable with visible focus, meaningful automation names, persistent labels and readable text contrast in each inherited theme. State must use words as well as color. Maintain logical Tab order and avoid changing focus during asynchronous updates. No flashing or motion required.

## Responsive behavior

MicaPad retains a single scrolling strip at its 420-pixel minimum width. The Open notes/New note actions stay visible. The picker fits the window width and available height, scrolls internally, and wraps long titles while keeping paths secondary. Validate synthetic many-note fixtures at 900x640 and 420x260 in both Pad themes.

Footer information and actions wrap at narrow widths instead of clipping caret position or labels. Bound long status text to the available footer width. History actions wrap below their version caption. Popup fixtures span the whole client area, matching actual Popup bounds even when the footer grows.

The task-date popup uses persistent labels above the fields and existing theme colors. Bound its width and height to the owner, update those bounds when resized, and scroll fields internally at short heights. Keep Save and Cancel in a visible footer. Enter in a date field saves; Enter/Space on a button activates that button's action. Verify synthetic fixtures at normal and minimum window sizes in both themes.

Support the declared minimum window size and typical 1120–1280 px desktop widths, including Windows display scaling. Wrap long status/help text, give primary workspaces remaining height, and use scrolling where content exceeds space. Avoid rigid side panels that squeeze transcript content at minimum width. Verify normal and minimum sizes with hardware-free rendered fixtures.

Audio setup can collapse after Start to give transcript and answers more room while keeping Stop and both waveforms visible. Keep session facts in the footer instead of squeezing the status text between buttons and counters. Small workspaces with multiple controls must scroll internally.

## Interaction states

Opening Open notes focuses its search. Up/Down changes the highlighted result without changing the note; Enter or one click activates it, and Escape cancels. Return focus to the editor only for keyboard cancellation or a chosen note; outside-click dismissal must not steal focus. Preserve current selection and caret on cancellation. Refresh an open picker after rename, close, reorder or window movement while retaining a still-valid highlighted note. Clear its rows and query when dismissed and on vault/hide/exit so stale note references do not outlive the surface.

MicaPad tasks remain editable Markdown source. Show local Gregorian created/finished dates as muted read-only annotations using the existing palette, with no strike-through. Store dates in encrypted metadata separately from the note text. The annotation occupies visual wrapping space but no document characters: typing, selection, copy, history and Markdown/file export operate on plain task text. The latest user instruction enables explicit corrections through right-click **Edit task dates…**: a compact popup with Start, optional Finish, Now/Clear, Save/Cancel and inline validation. Use Gregorian `yyyy-MM-dd HH:mm`; keep untouched precision and require Finish on/after Start. Set Finish to complete, clear it to reopen. Save is one undoable action even when only metadata changes; Escape/outside click cancels. Keep draft keystrokes inside the form, clear drafts on note change/hide/vault/exit, and reject stale targets. Ctrl+Enter and Complete task / Reopen task toggle completion; Enter continues the checklist with a fresh pending item and independent creation date, while a blank task exits the list. End and Shift+End use ordinary editor behavior. Undo/redo restores task metadata alongside the original text change without resampling the clock. Exclude fenced examples, math blocks, front matter, tables and other note languages. Existing undated tasks record when first observed; do not imply knowledge of historical dates. Migrate old generated suffixes once for existing stored notes, preserving valid dates; newly typed date-like text cannot set metadata. See `.scratch/micapad-task-date-editing/spec.md`, which supersedes the prohibition on date editing in `.scratch/micapad-task-metadata/spec.md` while retaining separate storage.

Unconfigured: visible setup callout; relevant Start, Speak or voice refresh actions are unavailable and guards prevent programmatic activation. Configured/stopped: ready for an explicit action, with no service contact. Opening the window may restore the latest nonempty transcript stopped, but never starts devices, network requests, or AI work. Starting a new session confirms that the workspace will reset while its saved files remain. Listening/paused/faulted: show actual state and precise available recovery. Synthesis and playback have separate informative statuses; TTS playback explains the listening gap. Empty, loading, unavailable references, missing evidence, quota exhaustion and provider or save errors retain readable context. A failed save keeps the window open for retry or manual export. Saving service settings first stops active voice work and resets service-specific voice choices.

Settings tests contact a service only after an explicit Test connection click. Show protocol progress, cancellation, safe errors and elapsed time. A changed address clears its previous result and cancels pending work; a late completion must not replace the new state. Closing Settings cancels tests. A successful test confirms a compatible response to synthetic input, not recognition quality, saved configuration or device operation.

## Content voice

Use short, direct labels: Start listening, Stop, Speak, Stop speaking, Configure services, Save Markdown, Saved transcripts. Explain effects in plain language. Save status distinguishes saved, pending and failed without implementation diagnostics. Use reserved `.example` URLs only as settings hints and test fixtures. Do not expose private development services in documentation, examples or screenshots.

## Implementation constraints

WPF on .NET 8 for Windows, existing ModernWpf/Ask themes, no new dependencies. Reuse the MicaPad AtomicFile/AutosaveWriter persistence pattern. Flush on normal window close and application exit; sudden termination can still lose pending results or writes, so do not promise universal zero loss. Preserve meeting generation, cancellation, credential filtering and quota behavior. All endpoint defaults are empty. Run targeted fake-provider/WPF tests, then full repository checks and a staged Release at BelowNormal priority, serialized. Inspect rendered screenshots for clipping and visual hierarchy; screenshots must use synthetic content and no real capture/network. Hardware recognition and playback remain a separate controlled manual check.

## Open questions

No blocking design questions. Model IDs and protocol capabilities remain those of the existing ASR/TTS adapters; arbitrary provider protocols and authentication are outside this change.
