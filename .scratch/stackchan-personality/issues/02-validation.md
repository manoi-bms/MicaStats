# Integrated validation and deployment

Status: complete
Blocked by: 01-behavior

Acceptance: targeted firmware/.NET checks and staged build run serially at BelowNormal; review changes; normal-exit deployment; real COM9 RAM startup/LED/dashboard acknowledgements and calibration check. Physically exercise motor only when stored calibration is verified. Record results and limitations.

Verification evidence: staged Release clean; normal-exit deployment verifies
64 files; COM9 live probe observes all three views, changing RGB, event view,
manual hold/clear and Quiet alert. Installed bundle RAM startup and automatic
PC telemetry accepted. Both stored calibration keys saved; no head commands
sent. Physical alignment remains unconfirmed, so motor verification is deferred
to issue 03. Evidence: artifacts/stackchan-personality/validation.md.
