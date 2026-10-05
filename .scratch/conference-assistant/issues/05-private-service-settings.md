# 05: Configure voice services without publishing private endpoints

Status: complete

Blocked by: none; user correction supersedes the original endpoint defaults.

- [x] All ASR/TTS base URLs default to empty; no service is contacted before the user configures it.
- [x] Settings → Meeting provides explicit validated configuration for ASR2, ASR1 and TTS. Empty values clear configuration. Examples use reserved domains.
- [x] Start, Speak and voice refresh enforce readiness before capture/network activity.
- [x] Saving service settings stops active voice work before changing destinations; voice catalogs are scoped to the configured service.
- [x] Private endpoints are absent from tracked code, docs and test fixtures, and from reachable local commits intended for publication.
- [x] Meaningful tests cover configured routing, invalid/missing endpoints, no implicit requests, settings persistence and changed-service behavior.

## Verification

Verified 2026-10-05. Client/endpoint tests passed 67/67; configuration/settings tests passed 8/8; meeting UI/lifecycle tests passed 8/8. The final repository suite passed 5,953/5,953 with zero failures/skips (2 m 56 s), and staged Release succeeded with zero warnings/errors. Builds/tests used the serialized BelowNormal runners.

Independent privacy review approved readiness enforcement, immediate capture/speech stop, joining pending voice-catalog work before applying settings, and rejection of stale catalog results. A scan of all 839 publication candidates found no private host. The original feature commit was unpublished and is replaced rather than retained as an ancestor; reachable branch history is audited after replacement. No remote publication, real service request, microphone capture or speaker playback is part of this correction.
