# Physical head gesture verification

Status: needs-info
Blocked by: owner confirmation of official servo-zero calibration alignment and head clearance

Acceptance: after physical calibration alignment is confirmed, exercise a
single gentle bounded gesture and verify return to center. No calibration
writes, flashing or continuous motor rotation. Preserve the owner's Quiet mode
and automatic-motion preferences.

Verification evidence: both stored NVS zero keys now report saved; host/firmware
motor safety and 90-second opt-in throttling pass deterministic tests. No
physical head command was sent. The owner was asked to confirm the alignment
procedure using a structured question; no answer has arrived yet.
