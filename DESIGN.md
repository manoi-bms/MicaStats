# Design

## Source of truth

Status: Active. Updated 2026-10-05. Scope: Meeting Assistant and Settings → Meeting in the Windows desktop application. Evidence: `Conference/MeetingWindow.xaml`, its partial classes, `SettingsWindow.xaml`, `Services/Ai/AskPalette.cs`, `Ai/AskThemeApplier.cs`, `Ai/ChatStyles.xaml`, `docs/meeting-assistant.md`, and the conference specification. No external visual reference was supplied; the existing app themes are the visual baseline.

## Brand

Calm, precise, capable Windows software. Trust comes from visible recording state, explicit actions, understandable sources and honest configuration status. Avoid decorative dashboards, invented activity meters, oversized marketing headings and hardcoded service identities or hosts.

## Product goals

Make service setup, listening state and the next useful action immediately clear. Help an attendee follow a transcript, prepare supported responses and choose when to speak or save. Preserve explicit capture, local manual export and configured-provider boundaries. Success means an unconfigured user can find setup, and an active user can identify inputs, status, supporting evidence and any listening gap at a glance.

Non-goals: automatic conference replies, attendee identification, new audio capabilities or a replacement for the whole application's theme system.

## Personas and jobs

Primary user: a conference attendee monitoring live speech while multitasking. They need readable transcript context, concise assistance and predictable audio controls. A first-time user configures their own compatible services before using the workflow. Public-repository users must never inherit another user's service address.

## Information architecture

Meeting Assistant has a session header, setup/readiness feedback, device and session controls, useful session facts, then Transcript / Summary & answers / Reference notes / Speech workspaces. Keep Stop reachable and distinct from Speak. The header links to dedicated Settings → Meeting. Settings groups the two ASR profiles and TTS separately, identifies compatible protocols/models, uses empty endpoint inputs and an explicit Save action.

## Design principles

State precedes detail. Data density serves reading; spacing and alignment group related controls. Display real state and counts only. Use clear empty states and explain disabled actions. Configuration changes stop voice work before applying new destinations. Service URLs belong in user settings, never branded defaults or repository examples.

## Visual language

Reuse dynamic `Ask.*` palette resources and ModernWpf controls. Use the existing Segoe UI Variable / Segoe UI typefaces, approximately 13–14 px body text, 11–12 px secondary labels, 16–18 px section titles and a restrained 24–28 px window heading. Use a consistent 4/8 px spacing rhythm, 16–24 px region padding, subtle one-pixel borders and moderate corner radii. Accent the primary action and state sparingly. Avoid unnecessary gradients, large shadows and animation. Use familiar Segoe Fluent/MDL2 icons with text labels.

## Components

Reuse buttons, text inputs, selectors, tab workspaces and existing theme tokens. Meeting-local card, caption and status styles may compose those tokens; do not add a second palette. Session facts may show source selection, finalized segment count, selected-reference count, actual latest transcript time, and AI allowance. Never label latest transcript time as elapsed live duration. Empty transcript/answer states explain the next step. Preserve selectable transcript and answer text.

## Accessibility

Target WCAG 2.2 AA principles as applicable to desktop WPF; this is a target, not a certification. Keep controls keyboard-reachable with visible focus, meaningful automation names, persistent labels and readable text contrast in each inherited theme. State must use words as well as color. Maintain logical Tab order and avoid changing focus during asynchronous updates. No flashing or motion required.

## Responsive behavior

Support the declared minimum window size and typical 1120–1280 px desktop widths, including Windows display scaling. Wrap long status/help text, give primary workspaces remaining height, and use scrolling where content exceeds space. Avoid rigid side panels that squeeze transcript content at minimum width. Verify normal and minimum sizes with hardware-free rendered fixtures.

## Interaction states

Unconfigured: visible setup callout; relevant Start, Speak or voice refresh actions are unavailable and guards prevent programmatic activation. Configured/stopped: ready for an explicit action, with no service contact. Listening/paused/faulted: show actual state and precise available recovery. Synthesis and playback have separate informative statuses; TTS playback explains the listening gap. Empty, loading, unavailable references, missing evidence, quota exhaustion and provider errors retain readable context. Saving service settings first stops active voice work and resets service-specific voice choices.

## Content voice

Use short, direct labels: Start listening, Stop, Speak, Stop speaking, Configure services, Save Markdown. Explain effects in plain language. Keep endpoint values and implementation diagnostics out of session status. Use reserved `.example` URLs only as settings hints and test fixtures. Do not expose private development services in documentation, examples or screenshots.

## Implementation constraints

WPF on .NET 8 for Windows, existing ModernWpf/Ask themes, no new dependencies. Preserve meeting generation, cancellation, credential filtering and quota behavior. All endpoint defaults are empty. Run targeted fake-provider/WPF tests, then full repository checks and a staged Release at BelowNormal priority, serialized. Inspect rendered screenshots for clipping and visual hierarchy; screenshots must use synthetic content and no real capture/network. Hardware recognition and playback remain a separate controlled manual check.

## Open questions

No blocking design questions. Model IDs and protocol capabilities remain those of the existing ASR/TTS adapters; arbitrary provider protocols and authentication are outside this change.
