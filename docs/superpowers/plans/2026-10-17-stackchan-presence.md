# StackChan Presence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** StackChan's camera streams to MicaStats, which detects faces on the best GPU and drives greeting, eye/head following with search, screen guard, doze, break reminder and a Settings GPU preview.

**Architecture:** The UIFlow companion streams small JPEG frames as text lines on the existing USB link and obeys new `camera`/`presence` commands. The USB transport gains a continuous reader that splits frame lines from correlated JSON responses. On the PC, a pipeline runs: DirectML YuNet detector (high-performance adapter), then a pure presence tracker and head aimer, then a `PresenceService` that drives `StackChanService` reactions, MicaPad privacy and optional Windows lock.

**Tech Stack:** .NET 8 WPF, Microsoft.ML.OnnxRuntime.DirectML, DXGI interop, YuNet ONNX (MIT), MicroPython UIFlow 2.0 (`camera`, `ubinascii`), xUnit, Python unittest.

**Spec:** `docs/superpowers/specs/2026-10-17-stackchan-presence-design.md`

## Global Constraints

- Tests: `./test.ps1 -Filter "<filter>"` (BelowNormal priority built in). Run the full suite once at the end. Firmware: `python -m unittest tests/firmware/test_stackchan_companion.py`.
- Never build into `bin/Release/net8.0-windows`; stage Release under `artifacts/stackchan-presence/`.
- Do not commit. StackChan code is uncommitted owner work on `main`; the owner decides commits.
- Frames never touch disk, logs, AI services or the network. They are dropped after detection, and the preview reuses the in-memory frame.
- No face identity: count and locate only. `dl.HumanFaceRecognizer` and the on-device detectors stay unused.
- Head motion only through `StackChanProtocol.Motion` (yaw −45…45°, pitch 15…65°, speed 100…300). Quiet mode and `StackChanAllowAutoMotion=false` stop all automatic motion. No firmware limit is widened. No NVS or calibration writes.
- Presence works only over USB with the companion running.
- At most 6 frames per second. The camera stops when presence is off, the robot disconnects or MicaStats exits.
- The PC opens the port with DTR **on** and RTS **off**. The spike showed robot output is lost with DTR off.
- Robot screen text is English ASCII, because Montserrat lacks Thai glyphs. PC UI text is Thai and English through `Services/Localization/Catalogs/stackchan.json`, Thai default.
- Screen/LED priority, highest first:
  1. alert;
  2. onlooker;
  3. welcome;
  4. break;
  5. manual hold;
  6. doze;
  7. normal.

  Alerts wake doze. In Quiet mode scenes show but LEDs are at most 5% and no motion runs. During meetings the break reminder is suppressed. Guard actions run regardless.
- Defaults (configurable):
  - welcome after ≥ 2 min away, shown for 15 s;
  - onlooker after ≥ 1.5 s of two faces; guard lifts after 2 s of a single face;
  - doze after 10 s without a face;
  - lock Windows off by default, 5 min when on;
  - break after 50 min present, reset after 5 min away;
  - failed head search retried every 2 min.

## Review Focus

1. **MicaStats crashes or the cable is pulled while the camera is on.** The robot must turn the camera off by itself. Pinned in Task 2 (`camera_turns_off_without_keepalive`).
2. **A face photo or poster stays in view.** The guard must not hold MicaPad blurred forever. Pinned in Task 4 (`static_second_face_is_background_after_30s`).
3. **Dark room, covered camera or a stalled stream.** This is "unknown", never "away". No doze-to-lock-Windows and no welcome storm. Pinned in Task 4 (`no_frames_is_unknown_not_away`).
4. **DirectML or the GPU fails to initialise** (driver update, remote desktop). Detection must fall back to the CPU and say so, not throw at startup. Pinned in Task 3 (`falls_back_to_cpu_when_directml_fails`).
5. **Unplugged while guarded.** The blur must lift, and must not stay up with no way to clear it. Pinned in Task 6 (`disconnect_clears_guard_and_camera`).

---

### Task 1: USB transport: DTR on, continuous reader, frame lines

**Files:**
- Modify: `Services/StackChan/StackChanUsbTransport.cs` (ConfigurePort DTR; replace per-call `WaitForResponseAsync` reads after connect with one reader loop)
- Modify: `Services/StackChan/StackChanContracts.cs` (frame types)
- Modify: `Services/StackChan/StackChanTransport.cs` (forward frames from the USB child)
- Test: `tests/Kil0bitSystemMonitor.Tests/StackChanUsbTransportTests.cs`

**Interfaces:**
- Produces, in `StackChanContracts.cs`:
  - `public sealed record StackChanFrame(int Sequence, byte[] Jpeg, DateTimeOffset ReceivedAt);`
  - `public interface IStackChanFrameSource { event Action<StackChanFrame>? FrameReceived; }`
- `StackChanUsbTransport` and `StackChanTransport` implement `IStackChanFrameSource`. The router raises only frames from the USB child.
- The wire format is set by Task 2: an ASCII line `F<seq>:<base64 jpeg>\n`, where `<seq>` is decimal 0…65535. Frame lines may be up to 16384 bytes; JSON lines keep the 2048-byte limit.

- [ ] **Step 1: Write failing tests** in `StackChanUsbTransportTests`:
  - `Frame_lines_raise_FrameReceived_and_commands_still_correlate`. After connect, the fake endpoint enqueues `F7:<base64 of [0xFF,0xD8,0xFF,0xD9]>\n`, then the applied response for a pending `WriteAsync`. Assert one frame with `Sequence == 7` and `Jpeg` equal to those 4 bytes, and that `WriteAsync` completes.
  - `Malformed_frame_line_is_dropped_without_retiring_the_connection`. Lines `Fx:abc\n` and `F1:!!!\n`: no frame raised, and a following command still completes.
  - `Overlong_frame_line_is_dropped`. A 20000-byte `F1:` line: no frame raised, and the next command completes.
  - `Disconnect_stops_frame_delivery`. After `DisconnectAsync`, enqueued frame lines raise nothing.
- [ ] **Step 2: Run** `./test.ps1 -Filter "FullyQualifiedName~StackChanUsbTransport"`. Expected: the 4 new tests FAIL.
- [ ] **Step 3: Implement.**
  - After `VerifyHelloAsync` succeeds, start one reader task per connection that owns all reads.
  - JSON lines resolve a `ConcurrentDictionary<string, TaskCompletionSource<string>>` keyed by `requestId`. Keep the existing `TryMatchResponse` rules and the rejection text.
  - `F` lines are parsed and raised on the reader thread through a non-throwing handler (follow `RaiseDisconnected`).
  - A reader fault or end retires the connection through `RetireConnectionAsync(..., raiseEvent: true)`.
  - In `ConfigurePort` use `dtr=on rts=off` and `EscapeCommFunction(handle, SetDtr)` (const `SetDtr = 5`) instead of `ClearDtr`. Keep `ClearRts`.
  - Startup (`StartScriptAsync`) and hello stay byte-level, as today.
- [ ] **Step 4: Run** the same filter. Expected: all StackChanUsbTransport tests PASS, including the existing ones.

### Task 2: Firmware: camera stream, keepalive, presence commands, indicator

**Files:**
- Modify: `firmware/uiflow2/micastats_companion.py`
- Test: `tests/firmware/test_stackchan_companion.py` (add a fake `camera` module with `init`, `deinit`, and `capture_to_jpg(q)` returning fixed bytes; add `ubinascii`)

**Interfaces (consumed by Tasks 1 and 5):** commands arrive inside the existing USB `command` envelope and go through `apply_message`. They are answered `applied`, or `invalid-camera` / `invalid-presence`.
- `{"camera":{"on":true,"fps":1-6,"quality":30-80}}` turns the camera on or refreshes it. `{"camera":{"on":false}}` turns it off.
- `{"presence":{"gaze":{"x":-100..100,"y":-100..100}}}` sets an external gaze target. `{"presence":{"gaze":null}}` returns to the robot's own glances.
- `{"presence":{"scene":"welcome|guard|break|doze|none","lines":[up to 3 ASCII strings, each at most 32 chars],"seconds":1..60}}`.
- Frames go out as `F<seq>:<base64>\n` via `sys.stdout`, with `seq` wrapping at 65536.

- [ ] **Step 1: Write failing tests:**
  - `camera_on_streams_frames_at_requested_rate`: fps 4 and fake time advanced 1000 ms across loops gives 4 `F` lines with sequences 0..3 and base64 matching the fake JPEG.
  - `camera_turns_off_without_keepalive`: no `camera` refresh for 10000 ms means `camera.deinit` is called and no more `F` lines.
  - `camera_rejects_out_of_range_fps_and_quality`: fps 7 and quality 90 give `invalid-camera`, and the camera is not initialised.
  - `usb_commands_are_read_before_capture`: with input waiting, `consume_usb` runs before `capture_to_jpg` in the same loop.
  - `camera_indicator_drawn_while_on`: an LCD fill/circle at the top-right indicator rect while on, cleared when off.
  - `gaze_target_moves_eyes_both_axes`: gaze `{x:50,y:-40}` makes the eye feature positions shift right and up versus gaze null.
  - `scene_lines_are_ascii_and_bounded`: a non-ASCII line or a 4th line gives `invalid-presence`.
  - `scene_expires_after_seconds`.
- [ ] **Step 2: Run** `python -m unittest tests/firmware/test_stackchan_companion.py`. Expected: the new tests FAIL and the 67 existing tests PASS.
- [ ] **Step 3: Implement.**
  - Constants: `CAMERA_FRAMESIZE` (QVGA, decided in Step 5), `CAMERA_KEEPALIVE_MS = 10000`, `MAX_FRAME_FPS = 6`.
  - Capture happens in `loop()` after `consume_usb()`, only when that poll found no pending input and `1000 // fps` ms have elapsed.
  - Add vertical gaze (`self.gaze_y`) next to the existing horizontal `self.gaze` in `draw_eye`.
  - Scenes are drawn as a card over the character view with the existing `ScreenText`, and expire on `seconds`.
  - The indicator is a filled 6 px red (0xF43F5E) dot at x=308, y=10.
  - Camera init or capture failure turns the camera off and is reported once in the next `applied` response as `"camera":"failed"`.
- [ ] **Step 4: Run** the firmware tests. Expected: all PASS. Also run `ruff check firmware tests/firmware`.
- [ ] **Step 5: Measure on the robot** (with the owner's consent, as in the spike). Load the companion with Start temporary USB companion and stream at 3, 4 and 6 fps for 30 s each. Keep the highest fps where the face frame interval max is ≤ 120 ms and the USB ACK median is ≤ 250 ms. Use that value for `PresenceService.DefaultFps` (a `public const int` in Task 6; 4 until measured). Record the numbers in `artifacts/stackchan-presence/validation.md`.

### Task 3: GPU choice and YuNet face detector

**Files:**
- Modify: `Kil0bitSystemMonitor.csproj`: add `Microsoft.ML.OnnxRuntime.DirectML`. Pin the newest stable version on nuget.org and write it in the validation file. Copy `Assets/Models/yunet-2023mar.onnx` and `Assets/Models/THIRD-PARTY.txt` (MIT text, © 2020 Shiqi Yu) to output.
- Create: `Services/StackChan/Presence/GpuAdapters.cs`, `Services/StackChan/Presence/YuNetDecoder.cs`, `Services/StackChan/Presence/FaceDetector.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/StackChanPresenceDetectorTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record GpuAdapterInfo(string Name, long Luid, bool IsSoftware);`
  - `internal static class GpuAdapters`, with:
    - `static IReadOnlyList<GpuAdapterInfo> EnumerateInDirectMLOrder()` (IDXGIFactory1::EnumAdapters1 order);
    - `static long? HighPerformanceLuid()` (IDXGIFactory6::EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE));
    - `static int ChooseDevice(IReadOnlyList<GpuAdapterInfo> adapters, long? preferredLuid)`. It returns −1 for CPU.
- Produces `public sealed record DetectedFace(float CenterX, float CenterY, float Width, float Height, float Score);`. Coordinates are normalised 0…1 to the source frame.
- Produces `public static IReadOnlyList<DetectedFace> YuNetDecoder.Decode(IReadOnlyDictionary<string, float[]> outputs, int sourceWidth, int sourceHeight, float threshold = 0.6f, float nmsIou = 0.3f)`. Outputs are the `cls_/obj_/bbox_{8,16,32}` tensors for a 640×640 input.
- Produces `public sealed class FaceDetector : IDisposable` with:
  - `static FaceDetector Create(string modelPath, Func<int, InferenceSession>? sessionFactory = null)`;
  - `string AdapterName`, `bool OnGpu`;
  - `IReadOnlyList<DetectedFace> Detect(byte[] jpeg, out double milliseconds)`.

- [ ] **Step 1: Write failing tests:**
  - `ChooseDevice_prefers_high_performance_luid`: AMD at 0 and NVIDIA at 1, preferred = NVIDIA LUID, gives 1.
  - `ChooseDevice_skips_software_adapter_and_falls_back_to_first_hardware`.
  - `ChooseDevice_returns_cpu_when_no_hardware`: gives −1.
  - `Decode_single_anchor`:
    - stride-8 tensors are 80×80;
    - at row 10, col 20, `cls = obj = 0.9`, bbox `[0,0,ln(4),ln(4)]`, and all other cells 0;
    - source 320×240;
    - expect one face at center (20·8/320, 10·8/240) and size (32/320, 32/240) with score 0.9.
  - `Decode_suppresses_overlapping_boxes`: two adjacent anchors on the same face give one result.
  - `Decode_drops_faces_outside_source_region`: an anchor at col 60 (x = 480 > 320) is ignored.
  - `falls_back_to_cpu_when_directml_fails`: a `sessionFactory` that throws for device ≥ 0 gives `OnGpu == false` and `AdapterName == "CPU"`, with no throw.
- [ ] **Step 2: Run** `./test.ps1 -Filter "FullyQualifiedName~StackChanPresenceDetector"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `FaceDetector.Create`: `SessionOptions.AppendExecutionProvider_DML(device)`, CPU when −1 or on exception.
  - Detect decodes the JPEG with `JpegBitmapDecoder` into `FormatConvertedBitmap(Bgr24)`, then into a float CHW BGR buffer of 0…255, zero-padded to 640×640 at the top-left. The buffer is reused per detector.
  - Decoding uses score = √(cls·obj), centre = (col + b0)·s and (row + b1)·s, size = e^{b2}·s and e^{b3}·s, then NMS.
- [ ] **Step 4: Run** the filter. Expected: PASS. Then run a one-off check through the test host or a scratch console: on this notebook `AdapterName` contains "NVIDIA" and `OnGpu` is true. Record the result in the validation file.

### Task 4: Presence tracker (pure state machine)

**Files:**
- Create: `Services/StackChan/Presence/PresenceTracker.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/StackChanPresenceTrackerTests.cs`

**Interfaces:**
- Produces:
  - `public enum PresenceState { Unknown, Present, Away }`
  - `public enum PresenceEventKind { Arrived, Left, Welcome, OnlookerStarted, OnlookerEnded, BreakDue, LockDue }`
  - `public sealed record PresenceEvent(PresenceEventKind Kind, TimeSpan AwayFor = default);`
  - `public sealed record PresenceOptions(TimeSpan DozeAfter, TimeSpan WelcomeAfterAway, TimeSpan OnlookerAfter, TimeSpan OnlookerClearAfter, TimeSpan BreakAfter, TimeSpan BreakResetAway, TimeSpan? LockAfter);`, with `static PresenceOptions Default` using the Global Constraints values and LockAfter null.
  - `public sealed class PresenceTracker`, constructed with `(PresenceOptions options)`, exposing:
    - `IReadOnlyList<PresenceEvent> Observe(DateTimeOffset at, IReadOnlyList<DetectedFace> faces)`;
    - `IReadOnlyList<PresenceEvent> Tick(DateTimeOffset at)`;
    - `PresenceState State`, `DetectedFace? MainFace` (largest), `bool OnlookerActive`, `void SetBreakSuppressed(bool)`.
- Rules:
  - Present needs a face in ≥ 2 of the last 3 frames.
  - Away comes after `DozeAfter` with frames but no face.
  - Unknown comes when there is no frame for 5 s. Unknown fires nothing and pauses timers.
  - Welcome fires on Arrived when away ≥ `WelcomeAfterAway`.
  - LockDue fires once per away period.
  - BreakDue fires once per present stretch and never while suppressed.
  - Onlooker starts after ≥ 2 faces for `OnlookerAfter` and ends after a single face for `OnlookerClearAfter`.
  - A second face whose box stays still (IoU ≥ 0.9 against its first sighting) for 30 s is background: OnlookerEnded fires and that box is ignored until it moves.

- [ ] **Step 1: Write failing tests**, one per rule:
  - `arrives_after_two_of_three_frames`
  - `dozes_after_10s_without_face`
  - `welcome_only_after_two_minutes_away`
  - `no_frames_is_unknown_not_away`: 60 s with no `Observe` calls, then `Tick` gives no Left and no LockDue.
  - `onlooker_needs_1_5s_of_two_faces`
  - `brief_passerby_under_1_5s_is_ignored`
  - `onlooker_clears_after_2s_single_face`
  - `static_second_face_is_background_after_30s`
  - `break_due_once_after_50min_and_reset_by_5min_away`
  - `break_suppressed_during_meeting`
  - `lock_due_once_per_away_when_enabled`
- [ ] **Step 2: Run** `./test.ps1 -Filter "FullyQualifiedName~StackChanPresenceTracker"`. Expected: FAIL.
- [ ] **Step 3: Implement** `PresenceTracker`. It takes no clock or I/O dependency; time comes in the calls.
- [ ] **Step 4: Run** the filter. Expected: PASS.

### Task 5: Protocol builders and head aimer

**Files:**
- Modify: `Services/StackChan/StackChanProtocol.cs`
- Create: `Services/StackChan/Presence/HeadAimer.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/StackChanProtocolTests.cs`, `tests/Kil0bitSystemMonitor.Tests/StackChanHeadAimerTests.cs`

**Interfaces:**
- Produces the following on `StackChanProtocol`. Each validates ranges and lines like Task 2 and throws `ArgumentException`/`ArgumentOutOfRangeException` like the existing builders:
  - `public static byte[] Camera(bool on, int fps = 4, int quality = 50)`;
  - `public static byte[] Gaze(int? x, int? y)` (both null means release);
  - `public static byte[] Scene(string kind, IReadOnlyList<string> lines, int seconds)`.
- Produces `public sealed record HeadPose(double Yaw, double Pitch);`
- Produces `public sealed class HeadAimer`, constructed with `(bool invertYaw, bool invertPitch)`, exposing:
  - `HeadPose? Next(DateTimeOffset at, DetectedFace? face, HeadPose current)`, which returns null when no command is needed;
  - `static readonly HeadPose Rest = new(0, 25)`.
- Behaviour:
  - Dead zone 0.06 (normalised from frame centre).
  - Step per update is 20°·ex for yaw and 15°·ey for pitch, smoothed by 0.35. Sign is flipped by the invert flags, clamped to ±45 / 15…65, at least 250 ms apart, and skipped when the change is < 1.5°.
  - Search starts after 3 s without a face. It visits yaw [−40, −20, 0, 20, 40] at pitch 25, then the same at pitch 40, with a 1.2 s dwell. It stops on any face.
  - A failed sweep returns `Rest` and waits 120 s before the next sweep.

- [ ] **Step 1: Write failing tests:**
  - Protocol: `Camera_json_shape`, `Camera_rejects_fps_7`, `Gaze_release_writes_null`, `Scene_rejects_non_ascii_and_fourth_line`.
  - Aimer:
    - `centred_face_inside_deadzone_sends_nothing`
    - `face_right_turns_yaw_toward_it_respecting_invert`
    - `pose_is_clamped_to_safe_ranges`
    - `commands_are_rate_limited_to_250ms`
    - `search_starts_after_3s_and_visits_ten_poses`
    - `failed_search_rests_then_waits_120s`
    - `face_during_search_stops_search`
- [ ] **Step 2: Run** `./test.ps1 -Filter "FullyQualifiedName~StackChanProtocol|FullyQualifiedName~StackChanHeadAimer"`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement** the builders and `HeadAimer`.
- [ ] **Step 4: Run** the filter. Expected: PASS.

### Task 6: PresenceService, StackChanService priority, MicaPad guard, Windows lock

**Files:**
- Create: `Services/StackChan/Presence/PresenceService.cs`, `Services/StackChan/Presence/AwaySummary.cs`
- Modify: `Services/StackChan/StackChanService.cs` (presence inputs in `ChooseReaction`; public presence senders)
- Modify: `Models/SystemMetrics.cs` (AppConfig fields below), `App.StackChan.cs` (wiring)
- Create: `Pad/MicaPadWindow.Privacy.cs`. Modify: `Pad/MicaPadWindow.xaml` (a top-level blurred overlay with "Someone is behind you", hidden by default)
- Test: `tests/Kil0bitSystemMonitor.Tests/StackChanPresenceServiceTests.cs`, `StackChanServiceTests.cs`

**Interfaces:**
- Consumes `IStackChanFrameSource` (Task 1), `FaceDetector` (Task 3), `PresenceTracker` (Task 4), `HeadAimer` and the protocol builders (Task 5).
- AppConfig additions, each with a property and backing field in the StackChan block:

  | Property | Default |
  |---|---|
  | `bool StackChanPresenceEnabled` | false |
  | `bool StackChanGuardVault` | true |
  | `bool StackChanGuardBlur` | true |
  | `bool StackChanLockWindowsWhenAway` | false |
  | `int StackChanLockWindowsMinutes` | 5, clamp 1…60 |
  | `int StackChanBreakMinutes` | 50, clamp 15…180 |
  | `bool StackChanHeadInvertYaw` | false |
  | `bool StackChanHeadInvertPitch` | false |
  | `bool StackChanHeadDirectionChecked` | false |

- `StackChanService` additions:
  - `public Task SendPresenceAsync(byte[] json, CancellationToken token)`. It is automatic and best-effort: it returns without sending when the writer is busy, using `SendAsync(..., automatic: true)` on `StackChanChannel.Dashboard`.
  - `public void SetPresence(PresenceReaction reaction)`.
  - `public enum PresenceReaction { None, Onlooker, Welcome, Break, Doze }`.
  - `ChooseReaction` order:
    1. Alert;
    2. Onlooker (Concerned, `#F43F5E`);
    3. Welcome (Happy, `#FDBA74`);
    4. Break (Happy, `#34D399`);
    5. manual hold, unchanged;
    6. Doze (Sleepy, `#64748B`, brightness ≤ 5);
    7. the existing AI, Meeting, Load and Ready reactions.

    Quiet still caps brightness at 5 and blocks motion, but no longer hides presence reactions.
- `PresenceService`, constructed with `(AppConfig config, StackChanService robot, IStackChanFrameSource frames, Func<FaceDetector> detectorFactory, TimeProvider clock, IPresenceActions actions)`, exposing:
  - `PresenceStatus Status` (record: `bool CameraOn`, `string AdapterName`, `bool OnGpu`, `double Milliseconds`, `double Fps`, `PresenceState State`, `int Faces`);
  - `event Action<StackChanFrame, IReadOnlyList<DetectedFace>>? Preview`, raised only while a subscriber exists.
- `public interface IPresenceActions { void LockVault(); void ShowGuard(bool on); void LockWindows(); void ShowBreakToast(); }`. App supplies:
  - `App.PadVault.Lock()`;
  - toggling the overlay on every open `MicaPadWindow` on the dispatcher;
  - `LockWorkStation()`;
  - the existing toast pattern.
- Pipeline behaviour:
  - Latest frame wins on one worker; frames arriving while it is busy are dropped.
  - `Camera(true, DefaultFps)` is sent every 5 s while enabled and connected over USB with the Dashboard capability. `Camera(false)` is sent on disable.
  - Gaze goes out at ≤ 5 Hz from MainFace: x = (cx − 0.5)·200, y = (cy − 0.5)·200.
  - HeadAimer runs only when `StackChanAllowAutoMotion` is on, Quiet is off and `StackChanHeadDirectionChecked` is true.
  - Scenes go out on events. Welcome lines come from `AwaySummary`: alerts count, AI tasks finished, meetings, and peak CPU/RAM/GPU with time. They use English ASCII, at most 32 chars, and `CultureInfo.InvariantCulture` times (the Thai calendar trap).
  - Guard: on OnlookerStarted, LockVault (if `GuardVault`) and ShowGuard(true) (if `GuardBlur`). On OnlookerEnded, ShowGuard(false). The vault stays locked.
  - The MicaPad overlay also hides on click.
  - LockDue calls `LockWindows()` only when enabled.
  - BreakDue shows the break scene and toast. Break is suppressed while `CompanionActivity.IsActive(Meeting)` is true.
  - Disconnect, disable or USB→BLE switch: clear the guard, release gaze, set reaction None and set State Unknown.

- [ ] **Step 1: Write failing tests** with fakes for the frame source, detector factory, robot transport (reuse `FakeTransport` patterns) and `IPresenceActions`:
  - `enabling_sends_camera_on_and_refreshes_every_5s`
  - `disabling_sends_camera_off`
  - `latest_frame_wins_when_detector_busy`
  - `onlooker_locks_vault_and_blurs_then_clears_blur_only`
  - `guard_toggles_respect_settings`
  - `lock_windows_only_when_enabled_and_once`
  - `break_toast_suppressed_in_meeting`
  - `head_does_not_move_until_direction_checked`
  - `head_does_not_move_in_quiet_mode`
  - `disconnect_clears_guard_and_camera`
  - `welcome_scene_lists_away_summary_ascii`

  In `StackChanServiceTests`:
  - `alert_outranks_onlooker_and_welcome`
  - `onlooker_outranks_welcome_and_break`
  - `quiet_shows_presence_reaction_with_dim_leds`
  - `doze_yields_to_alert`
- [ ] **Step 2: Run** `./test.ps1 -Filter "FullyQualifiedName~StackChanPresenceService|FullyQualifiedName~StackChanServiceTests"`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement**, then wire it in `App.StackChan.cs`:
  - create the service after `StackChanService`;
  - subscribe AwaySummary to `s_alerts.Raised`, `CompanionActivity.Changed` and metrics;
  - dispose it in `StopStackChan`.
- [ ] **Step 4: Run** the filter. Expected: PASS. Then run `./test.ps1 -Filter "FullyQualifiedName~StackChan|FullyQualifiedName~Pad"`. Expected: PASS.

### Task 7: Settings: presence section, direction check, GPU preview, text

**Files:**
- Modify: `Companion/StackChanPanel.xaml`, `Companion/StackChanPanel.xaml.cs`
- Modify: `Services/Localization/Catalogs/stackchan.json` (Thai for every new string)
- Modify: `CONTEXT.md` (glossary: **Presence detection**, **Onlooker**, **Away**, **Direction check**)
- Modify: `GUIDE.md` (StackChan section: a presence paragraph and the privacy statement)
- Test: `tests/Kil0bitSystemMonitor.Tests/StackChanPanelTests.cs`

**Interfaces:**
- Consumes `PresenceService.Status`, `PresenceService.Preview`, and the AppConfig fields from Task 6.
- New controls, by AutomationId:
  - `PresenceToggle`
  - `GuardVaultToggle`
  - `GuardBlurToggle`
  - `LockWindowsToggle`
  - `LockWindowsMinutesBox`
  - `BreakMinutesBox`
  - `DirectionCheckButton`
  - `PresenceGpuText` ("NVIDIA RTX PRO 1000 … · 2.5 ms · 6 fps", or "CPU (no compatible GPU)")
  - `PresencePreview` (an Image with face rectangles)
  - `CameraStateText`
- The preview subscribes only while the StackChan section is visible and unsubscribes on Detach. Frames are drawn into one reused `WriteableBitmap`.
- Direction check:
  1. It moves yaw to +10° at speed 100, then asks "Did StackChan turn to its left (your right)?" Yes/No sets `StackChanHeadInvertYaw`.
  2. It repeats for pitch +10° from 25° with "Did it look up?", which sets `StackChanHeadInvertPitch`.
  3. It returns to Rest and sets `StackChanHeadDirectionChecked = true`.

  It is enabled only when connected with Motion capability and Quiet is off.

- [ ] **Step 1: Write failing tests:**
  - `presence_toggle_disabled_without_usb_companion`
  - `preview_subscribes_only_while_visible`
  - `direction_check_saves_invert_answers_and_marks_checked`
  - `gpu_text_shows_adapter_and_timing`
  - `all_new_strings_have_thai` (extend the existing LocalizationCoverage test if it exists, otherwise assert the catalog keys)
- [ ] **Step 2: Run** `./test.ps1 -Filter "FullyQualifiedName~StackChanPanel|FullyQualifiedName~Localization"`. Expected: FAIL.
- [ ] **Step 3: Implement** using existing panel patterns: ToggleSwitch, `loc:Text`, `helpers:ButtonIcon.Glyph`, and DESIGN.md spacing.
- [ ] **Step 4: Run** the filter. Expected: PASS.

### Task 8: Full verification, staged Release, physical checklist

**Files:**
- Create: `artifacts/stackchan-presence/validation.md`

- [ ] **Step 1:** Run `./test.ps1` (the full suite, once). Expected: 0 failed. Run the firmware unittest and ruff. Expected: OK.
- [ ] **Step 2:** Build Release to `artifacts/stackchan-presence/Release` with the user-local SDK at BelowNormal priority. Confirm the output has `onnxruntime.dll`, `DirectML.dll`, `Assets/Models/yunet-2023mar.onnx` and `firmware/uiflow2/micastats_companion.py`.
- [ ] **Step 3:** Ask the owner before deploying. The working tree holds other unfinished WIP (Turzx, MicaPad preview, AI tools). After approval, deploy per the `deploy-release-for-e2e` procedure: normal Quit, backup, copy, hash check, relaunch.
- [ ] **Step 4:** Run the physical checklist on COM9 and record the results in `validation.md`:
  - frames arrive with DTR on;
  - `PresenceGpuText` names NVIDIA, and `nvidia-smi` utilisation rises while presence runs;
  - the preview shows the owner's face box;
  - the direction check completes;
  - the head finds the owner from rest and follows;
  - covering the camera gives Unknown, not doze;
  - leaving gives doze after 10 s; returning after 2 min gives the welcome card with the summary;
  - a second person behind gives the vault locked and MicaPad blurred, which clears when they leave;
  - the dashboard and alerts still update while streaming;
  - disable presence gives the camera indicator off.
- [ ] **Step 5:** Update the memory file `stackchan-hardware.md` with the verified facts (DTR, fps, GPU timing).
