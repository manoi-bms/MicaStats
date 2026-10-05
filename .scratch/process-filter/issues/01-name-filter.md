# 01: Exclude implicit parent-name matches

Status: complete

Blocked by: none.

- [x] Reproduce `codex.exe` including a differently named child using the actual filter function.
- [x] Plain text matches only a process's own executable name; case, whitespace, substring, numeric PID and empty-query behavior remain covered.
- [x] Update the former parent-search regression and current documentation to the corrected contract.
- [x] Run focused process tests, full suite and staged Release serially at BelowNormal priority; no termination actions in regression checks.

## Verification

On 2026-10-05, all four new regression cases failed before the fix: `codex.exe` also returned a `pwsh.exe` child because the predicate searched `ParentName`. Removing that implicit match made all 98 process-focused tests pass. The combined process/audio checks passed 132 tests, and the final full suite passed 5,978 tests with no failures or skips. Staged Release succeeded with zero warnings and errors. No process termination actions were invoked by verification.
