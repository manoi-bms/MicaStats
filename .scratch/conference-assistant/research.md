# Conference assistant evidence

Checked 2026-10-05. Service documentation, two synthetic-silence ASR requests, a user-requested synthetic-text VoxCPM synthesis test, and one TTS-to-ASR2 round-trip check were inspected; no microphone or meeting audio was captured. Private deployment addresses are intentionally omitted from this public repository. These observations describe those test deployments, not a bundled or default hosted service.

This research preceded implementation. The user subsequently approved the plan and NAudio dependency; implementation and final verification are tracked in [the specification](spec.md).

## ASR2, preferred

- Base: `<user-configured service address>`; model currently advertised: `Qwen/Qwen3-ASR-1.7B`.
- Transcription: `POST /v1/audio/transcriptions` with a file, model, optional language, response format and stream flag.
- Despite OpenAPI declaring form-urlencoded, a multipart request with `file`, `model`, `response_format=json`, `stream=false`, and omitted language succeeded.
- Test signal: one second of generated silence, PCM16 little-endian, mono, 16 kHz WAV (32,044 bytes). HTTP 200 returned `{"text":"language None<asr_text>","usage":{"type":"duration","seconds":1}}`.
- The client must suppress this empty-recognition marker. Qwen's official parser also defines `language Thai<asr_text>recognized speech`: extract the transcript after the protocol marker, preserving the recognized words. Apply this normalization only to ASR2; ordinary unwrapped responses remain plain text.
- File-based final JSON replies are verified. Continuous audio input, SSE event semantics, timestamps and non-silent recognition remain unverified.

Sources: Swagger (private test deployment; address omitted), OpenAPI (private test deployment; address omitted), current models (private test deployment; address omitted). Local evidence: `artifacts/asr-research/asr2-synthetic-contract-result.json` and its repeatable validation script. These ignored artifacts are evidence, not app inputs.

## ASR1, selectable alternative

The published service is Typhoon ASR Backend API. Its compatible transcription route is `POST /v1/audio/transcriptions`, explicitly multipart, with model default `typhoon-asr-realtime` and language default `th`. It also documents routes for text, word timestamps and batched transcription. The meeting assistant will use the ordinary compatible route and allow selection before a session; automatic switching is not part of the proposed first version.

The same synthetic WAV succeeded with fields `file`, `model=typhoon-asr-realtime`, `language=th`, `response_format=json`: HTTP 200, `{"text":"","processing_time":0.1}`. Evidence: `artifacts/asr-research/asr1-synthetic-contract-result.json` and its validation script.

Sources: Swagger (private test deployment; address omitted), OpenAPI (private test deployment; address omitted).

## Language and audio format

Qwen's official model documentation lists Thai and English and describes language identification. Its upstream audio preparation normalizes to mono 16 kHz. This supports the proposed ASR2 default of omitted language, but does not establish the hosted endpoint's recognition accuracy or every upstream feature. Local chunk boundaries will provide approximate session times, not claimed word-aligned timestamps.

Sources: [Qwen3-ASR-1.7B model](https://huggingface.co/Qwen/Qwen3-ASR-1.7B), [upstream audio utilities](https://github.com/QwenLM/Qwen3-ASR/blob/main/qwen_asr/inference/utils.py).

## Windows capture

WASAPI loopback captures the shared audio played through a render endpoint. Selecting an output device does not isolate the conference application or identify attendees. Microphone and loopback capture should remain separate sources; acoustic speaker playback may appear in both. Endpoint formats can differ, so each must be converted independently. Device removal invalidates active audio clients and needs explicit recovery or a stopped session.

Sources: [loopback recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording), [mix format](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getmixformat), [capture buffer lifecycle](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer), [invalid device recovery](https://learn.microsoft.com/en-us/windows/win32/coreaudio/recovering-from-an-invalid-device-error).

## Proposed audio dependency

Recommend one direct package reference to `NAudio.Wasapi` 2.4.0, with `NAudio.Core` 2.4.0 transitively. The MIT-licensed .NET Standard package supports the existing .NET 8 target, selected microphone/render devices, managed resampling and in-memory WAV output. This avoids owning equivalent COM/audio-format/threading interop. The NAudio meta-package adds unnecessary audio subsystems; the newer 3.x line requires a newer runtime.

Keep capture behind an app-owned adapter, pin the compatible version and audit restored transitive packages during implementation. Loopback may produce no callbacks during silence, so elapsed session time must not be derived only from the count of received samples. No package has been added: AGENTS requires explicit agreement for new dependencies.

Sources: [package](https://www.nuget.org/packages/NAudio.Wasapi/2.4.0), [capture source](https://github.com/naudio/NAudio/blob/v2.4.0/NAudio.Wasapi/WasapiCapture.cs), [loopback documentation](https://github.com/naudio/NAudio/blob/v2.4.0/Docs/WasapiLoopbackCapture.md), [resampler](https://github.com/naudio/NAudio/blob/v2.4.0/NAudio.Core/Wave/SampleProviders/WdlResamplingSampleProvider.cs), [releases](https://github.com/naudio/NAudio/releases).

## VoxCPM speech output

The service exposes `POST /v1/audio/speech`, with JSON `input`, `model` (default `voxcpm-thai`), `voice` (default `default`), `response_format` (`wav` or `mp3`), `speed` and `stream`. The HTML docs say speed is ignored. Finite WAV is the selected implementation path; streaming semantics are not established. `GET /v1/voices` supplies named presets and `GET /v1/models` currently returns `voxcpm-thai`. Default text limit is 4,096 characters, configurable server-side. Unknown voice IDs silently fall back to the default, so the app should validate selections against the explicit catalog. Authentication is not declared.

Sources: service documentation (private test deployment; address omitted), OpenAPI (private test deployment; address omitted), voice catalog (private test deployment; address omitted). The browser tool received a 403; direct GET from the workspace retrieved the public documentation and schema successfully.

One bare synthetic request for `Hello. This is a speech test.` returned HTTP 200 and `audio/wav`: 199,724 bytes, RIFF PCM16, mono, 48 kHz, 2.080 seconds. Response headers arrived after 355 ms in this single check, not a latency guarantee. No device playback occurred. OpenAPI's JSON response label does not match the actual binary reply. Test-only output and metadata are stored under `artifacts/tts-research/`; speech generation is verified, while human intelligibility and broad language coverage are not.

The generated WAV was then uploaded once to ASR2 using the verified multipart contract, with no new TTS generation. It returned HTTP 200 in 501 ms and `language English<asr_text>Hello. This is a speech test.`. After provider-specific wrapper normalization, the recognized text exactly matched the input sentence. This proves the tested service-to-service format and phrase compatibility, not the unimplemented application pipeline. ASR2 reported three usage seconds for the 2.080-second WAV; use actual capture/sample timing for transcript positions, not rounded service usage. Evidence: `artifacts/tts-research/roundtrip-asr2.json` and its reproducible script.

The proposed NAudio packages also support selected-device playback through an in-memory WAV reader and shared-mode output, with Windows format conversion. Dispose player/reader/stream/device after playback has stopped, and stop/join outside the completion callback to avoid a thread-join deadlock. No additional dependency is needed for PCM WAV playback.

Sources: [selected-device output](https://github.com/naudio/NAudio/blob/v2.4.0/NAudio.Wasapi/WasapiOut.cs), [stream WAV reader](https://github.com/naudio/NAudio/blob/v2.4.0/NAudio.Core/Wave/WaveStreams/WaveFileReader.cs), [playback completion lifecycle](https://github.com/naudio/NAudio/blob/main/Docs/PlaybackStopped.md).

The user confirmed manual Speak with listening paused during playback. The application must expose the resulting gap and must not resume a stopped meeting. The request for TTS does not authorize automatic replies, virtual-microphone injection or voice enrollment.

## Explicit limits of the evidence

The ASR contracts do not establish retention/deletion policies, reliable attendee identities, upload limits or a latency guarantee. Authentication is not declared in the retrieved voice-service schemas; all four synthetic service requests worked anonymously. Client-side non-persistence can be tested; server-side non-retention cannot be promised. No real conference, microphone, device playback or end-to-end application test has run. Synthetic service checks do not establish general recognition or speech quality.
