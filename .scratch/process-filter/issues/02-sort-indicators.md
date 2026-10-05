# 02: Show the active process sort direction

Status: complete

Blocked by: none.

- [x] Default CPU descending order has a visible down arrow.
- [x] Clicking any of the nine headers moves the indicator to that column; clicking again reverses it.
- [x] Inactive headers have no arrow, narrow headers remain legible, and labels still map to their original sort keys.
- [x] WPF regression drives real header clicks and checks rendered glyphs; existing sorting and virtualization checks pass.

## Verification

On 2026-10-05, the real-window regression failed before implementation because headers had no sort glyph. It now verifies the initial CPU down arrow, both directions for all nine header clicks, inactive headers and accessible direction descriptions. A rendered preview at the 840-pixel minimum width was inspected: the dark theme is preserved and all nine labels are readable. The preview contains no process rows and is saved under ignored `artifacts/process-ui/sort-headers-minimum.png`.

The final full suite passed 5,978 tests with no failures or skips; staged Release succeeded with zero warnings and errors. Tests and build ran serially at BelowNormal priority.
