# Process window name filter

The user reports that searching for `codex.exe` shows processes with other executable names. The Process window advertises search by name or PID. Plain text must match the process's own name, not its parent's name; numeric input continues to match PID exactly. Matching remains case-insensitive, trims surrounding whitespace, and allows name substrings. Empty input shows all processes.

This user correction supersedes the implicit parent-name search in the September process-window design. Keep the Parent column and parent sorting. The displayed rows, totals and End all filtered candidates continue to share the same filtered set. No process is terminated during verification.

Implementation and evidence: [01-name-filter.md](issues/01-name-filter.md).

The user also reports missing sort direction feedback. Show a down arrow on the initial CPU sort, move the arrow to the newly selected column, reverse it on the second click, and keep inactive columns unmarked. Preserve header labels as sort keys, all nine columns, stable row ordering and virtualization. Track this in [02-sort-indicators.md](issues/02-sort-indicators.md).
