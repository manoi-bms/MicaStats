# Phase 3: AI, Meeting Assistant, Screen Capture & StackChan UX Fixes

Status: complete
Blocked by: none

## Acceptance criteria

- `AskTurnView` / `AskWindow` updates suggested action pills after execution and requires confirmation before `EndProcess` kills a process; turn-level `CopyButton` shows transient `"Copied"` feedback; `OnNewConversation` prompts before clearing an active or non-empty conversation and binds `Ctrl+N`.
- `MeetingIntelligence` and `MeetingWindow` allow asking private questions whenever `Transcript.Count > 0` even after stopping capture or restoring a crash-recovery transcript; transcript citations in Summary/Answers can navigate to the cited segment; Audio Setup can collapse when listening starts.
- `CaptureEditorWindow` updates the selected annotation (`Canvas.Selected`) live when changing color, stroke thickness, or redact style, clears `_cropPreview` when switching tools, and guards unapplied crop regions on Copy/Save.
- `StackChanPanel` separates connection lifecycle locking from quick manual command dispatch so face/LED/head buttons do not flash the entire panel disabled, adds a `"Center (0°, 0°)"` head reset action, and preserves auto-reconnect when switching transport mode.
