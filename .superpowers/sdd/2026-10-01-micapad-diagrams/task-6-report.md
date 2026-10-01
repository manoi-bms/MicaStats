# Task 6 report: Pictures in the editor

## Implemented
- `MarkdownDocumentCache.OpeningLineOf` / `ClosingLineOf` (fence pairing, kept in `_openings` / `_closings`).
- `FoldingController.ExtraFolds` merged (ordered by start) into every recompute.
- New: `Pad/DiagramServices.cs`, `Pad/DiagramBoard.cs`, `Pad/DiagramGenerator.cs` (verbatim from the brief, one compile fix below).
- `EditorLanguage`: `Diagrams`, `DiagramBoard`, `RefreshDiagrams()`, install with Markdown, remove in `Clear`.
- Tests: `FakeRenderer` appended to `DiagramFakes.cs`; `DiagramBoardTests.cs` (16 tests).

## Tests
- Focused (`DiagramBoardTests|MarkdownRenderingTests|PadFoldingTests|PadLanguageWindowTests`): 69 run, all pass after the fixes below.
- Full suite: Passed 2677, Failed 0.
- TDD note: the implementation files were written in the same step as the tests, so a separate RED run was not captured. The first build did fail on compile errors in the brief's code (below), and the first test run had one failing test (below).

## Deviations
1. `DiagramBoard` (compile): `TextAnchor.Line` is an `int` line number in this AvalonEdit, not a `DocumentLine`. `a.Line == opening` became `a.Line == opening.LineNumber`, and `anchor.Line.LineNumber` became `anchor.Line`. Same intent.
2. `A_block_over_the_limit_says_it_is_too_large_and_is_not_drawn` (test setup, assertions unchanged). Root cause: AvalonEdit splits a 50,001-character line into 463 text rows (visual line height 7389 px, `TextLines.Count` 463), so the closing fence (line 3) is far outside the 400 px view and has no visual line, hence no picture. Fix: after building the board the test scrolls to the end through `IScrollInfo` (`CanVerticallyScroll = true`, which a ScrollViewer normally sets and without which `SetVerticalOffset` is ignored; then offset = extent - viewport) and re-renders. Both assertions are the brief's.
3. Test file: the `—` / `→` escapes in the PlantUML message were repaired after the Write tool decoded them.

## Self-review
- No CR line endings; remaining non-ASCII is in comments only (from the brief).
- Warnings go through `WarnOnce` naming engine and exception type only.

## Concerns
- None blocking. Real documents with a huge single line push the picture far down; that is AvalonEdit's layout, not a defect of the board.

## Fix round 1
1. DiagramGenerator.GetFirstInterestedOffset: returns the closing fence's end only when that line is the visual line's first document line, or the block's opening line is. Test `A_folded_heading_section_ending_in_a_diagram_shows_no_picture` (text lines: the closing fence is line 5, not 4). RED without the fix (fails), GREEN with it.
2. DiagramBoard.AwaitDrawAsync: notes `completedAtOnce` before awaiting and skips the Redraw then. Test `A_renderer_that_answers_at_once_shows_the_picture_in_the_first_render` passes; it also passed without the fix (the nested Redraw is harmless in the test harness), so it guards the behaviour but is not red-proven.
3. Test `An_older_draw_finishing_after_a_newer_request_is_ignored` added (picture under line 5; no code change needed).
4. HiddenFolds: `DefaultClosed` removed, comment explains SetCodeHidden folds the section and a retyped fence comes back unfolded.
Focused tests: 72 passed. Full suite: 2680 passed, 0 failed.
