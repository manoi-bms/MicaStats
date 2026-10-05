# Audio coexistence review — 2026-10-05

The requested apps are Teams, Zoom, Webex, Chrome and Firefox. Independent repository review and official-source research support the shared-endpoint design; no live call, microphone capture, sound playback or endpoint-setting change was performed.

## Evidence

- `WasapiMeetingCapture.CreateSource` opens the selected microphone with `WasapiCapture` and the selected render device with `WasapiLoopbackCapture`. Both now explicitly set `AudioClientShareMode.Shared`. The installed NAudio 2.4.0 implementation already defaults to shared capture: [capture source](https://github.com/naudio/NAudio/blob/7c855e6737435f781dcbac782930a0de7a13cb2b/NAudio.Wasapi/WasapiCapture.cs#L74-L89), [loopback source](https://github.com/naudio/NAudio/blob/7c855e6737435f781dcbac782930a0de7a13cb2b/NAudio.Wasapi/WasapiLoopbackCapture.cs#L11-L45).
- `WasapiPlaybackOperationFactory.Create` explicitly creates shared-mode `WasapiOut` on the user's selected playback device. No virtual microphone or conference-app routing is used.
- Repository review found no endpoint volume, mute, default-device, communications-role, ducking or audio-session policy setter in the meeting implementation.
- Capture pause/stop/disposal act on the capture clients this instance created; playback stop/disposal act on its own player. Device or ASR failures stop only the MicaStats meeting. Existing capture/session/speech/playback regressions cover these ownership and cancellation boundaries.
- Windows documents concurrent endpoint access in [shared mode](https://learn.microsoft.com/en-us/windows/win32/api/audiosessiontypes/ne-audiosessiontypes-audclnt_sharemode); [loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) captures a render endpoint's mix and requires shared mode. [Audio-client initialization](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize) documents exclusive-use and invalidated-device failures.

## Conclusion and limits

Inference from the code and Windows contract: monitoring should coexist with conference apps sharing the same endpoints without taking exclusive ownership or changing their routing, volume or mute. Stopping or pausing MicaStats does not pause the conference app. The selected output must match the conference app's output; all audio on that endpoint is included.

This is not a guarantee for every driver, headset or application configuration. An exclusive owner can prevent or interrupt shared capture. TTS remains audible output and may enter a physical microphone; pausing MicaStats capture does not mute the conference microphone. Actual conferencing latency, device behavior and call continuity remain controlled live checks.

| Application | Live coexistence result |
| --- | --- |
| Microsoft Teams | Not tested |
| Zoom | Not tested |
| Webex | Not tested |
| Chrome conference session | Not tested |
| Firefox conference session | Not tested |

The controlled procedure is in `docs/meeting-assistant.md`. Automated evidence is recorded in ticket 08 after the final checks.
