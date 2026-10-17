# StackChan presence: seeing the owner through the robot camera

Date: 2026-10-17. Status: approved. The owner said "please continue, select best
option for me" after reviewing the calls.

## Intent

The owner wants StackChan to notice people through its own camera and react in ways
that impress: greet them, follow them, protect their screen, nap when they leave and
look after them during long sessions. Detection runs in MicaStats on the PC's best
GPU (on this notebook the NVIDIA RTX PRO 1000, not the AMD Radeon 890M), chosen
automatically. The owner also asked for the head servos to *find* the person, not
just look at a fixed angle: today the head rests at the M5 "center" pitch of 45°,
which on a desk points the camera at the ceiling, and the owner sees that as "not
facing me".

The owner asked for all six proposed ideas plus servo searching. The six ideas, in the
owner's words when proposed: it notices you, its eyes follow you, it guards your
screen, it dozes when you leave, it nags you kindly, and it shows off the GPU.

## What the 2026-10-17 spike proved

Throwaway tests on the owner's robot (UIFlow 2.0, COM9) and notebook established:

- The robot camera works under UIFlow: `camera.init(pixformat=RGB565,
  framesize=QVGA)`, `camera.snapshot()` about 48 ms, and `camera.capture_to_jpg(60)`
  about 150 ms giving about 3.2 KB per 320x240 JPEG.
- Streaming base64 JPEG lines over USB reached about 6.5 frames per second.
- **USB output from the robot is lost unless the PC asserts DTR.** With DTR off, lines
  were truncated and merged; with DTR on and RTS off, all 15 frames arrived intact and
  the robot did not reset (Windows device arrival time unchanged). The current
  transport opens the port with DTR off, so it must change.
- ONNX Runtime DirectML runs YuNet face detection (MIT licence, 232 KB,
  fixed 1x3x640x640 BGR input; pad the 320x240 frame) in about 2.5 ms per frame on
  the NVIDIA GPU. DirectML device 0 was the AMD and device 1 the NVIDIA; the
  `performance_preference=high_performance` option selected the NVIDIA by itself.
- The robot also has an on-device detector (`dl.ObjectDetector(HUMAN_FACE_DETECT)`,
  about 100 ms per frame) and a pedestrian model. This design does not use them.
- With servo power on and torque off, `get_servo_angle` read X 3.4°, Y 45.3°. The
  official calibration puts Y zero with the display perpendicular to the base and
  M5's examples use Y 45 as "center", which looks upward from a desk.
- Spike scripts and test pictures stayed in the session scratchpad and the pictures
  were deleted.

## Goals

1. **Presence detection mode.** One switch in Settings > StackChan turns on the
   robot camera and host detection. It works only over USB with the companion
   running.
2. **It notices you.** When the owner comes back after being away (my call: at least 2
   minutes), StackChan wakes, looks at them, glows warm and shows "Welcome back" with
   what happened while away: alert count, AI tasks finished, meetings, and peak
   CPU/RAM/GPU. The greeting shows for about 15 seconds and then returns to the
   normal rotation.
3. **Its eyes follow you.** The on-screen eyes track the main face horizontally and
   vertically. The current firmware gaze is horizontal only, so add vertical.
4. **Head aims and searches.** When "Allow automatic head movement" is on, the head
   pans and tilts smoothly to keep the main face centred. If no face is seen for a
   few seconds, the head sweeps slowly through a bounded search pattern. It stops
   when a face is found. If the sweep fails it returns to rest and retries later
   (my call: every 2 minutes) rather than sweeping continuously.
5. **It guards your screen.** When a second face appears and stays (my call: at least
   1.5 seconds), StackChan looks concerned and MicaStats:
   - locks the MicaPad credential vault, as "Lock now" does;
   - covers every open MicaPad window with a privacy blur, saying "Someone is behind
     you" in Thai or English. The note stays open underneath.

   The blur lifts when only the owner is seen again for 2 seconds, or when the owner
   clicks it. The vault stays locked until the owner unlocks it as usual. Both actions
   are on by default and can be switched off. Locking Windows on an onlooker is not
   offered, because a colleague walking past would force a sign-in. (The owner asked
   me to pick the option; this is my call: vault plus MicaPad blur.)
6. **It dozes when you leave.** After no face for a while (my call: 10 seconds to
   doze), it shows a sleepy face and dims the LEDs. There is an optional "Lock Windows
   when I leave" after a set time (my call: off by default, 5 minutes when on).
7. **It nags you kindly.** After continuous presence of 50 minutes (my call,
   configurable) it plays a stretch animation and suggests a break. This is
   suppressed during meetings and Quiet mode. The timer resets after the owner has
   been away for 5 minutes.
8. **It shows off the GPU.** Settings shows the chosen GPU name, milliseconds per frame
   and frames per second. A live preview shows boxes around faces, but only while
   the Settings page is open.

## Non-goals

- Recognising *who* someone is. Faces are counted and located, never identified.
  `dl.HumanFaceRecognizer` and any face embeddings are out of scope.
- Bluetooth camera streaming. BLE is too slow; presence is USB only.
- Using the notebook webcam. The owner asked for the robot's camera.
- Pausing or changing meeting recording. Meeting capture is never altered by presence.

## Anti-goals (failure even if it works)

- Pictures written to disk, logged, sent to an AI service, or sent anywhere off the
  PC. Frames live only in memory and are dropped after detection; the preview reads
  the same in-memory frame.
- The camera running while the owner believes it is off. When it is on, the robot
  shows a small, clear camera indicator and Settings says it is on.
- Unexpected or violent head motion. All motion goes through the firmware safe
  ranges and speed limits. Quiet mode and the existing "Allow automatic head
  movement" switch stop all automatic motion. Manual head controls keep their
  current hold.
- Flicker or slow telemetry on the robot screen. The existing dashboard and alert
  behaviour must keep working while the camera streams.
- Jamming the robot's USB input again (the 2026-10-16 outage). The stream must not
  starve command handling.
- A presence decision flipping back and forth. Use hysteresis.

## Constraints

- Follow AGENTS.md: tests via `test.ps1` at below-normal priority, Release staged
  under `artifacts/`, never built into the running app's folder.
- The companion stays a RAM-only program started by "Start temporary USB companion":
  no flashing, no robot files, no calibration writes.
- Physical head motion has never been verified on this robot. The first automatic
  motion must be small and slow, and the owner confirms the direction before
  tracking is trusted.
- The servo calibration in NVS is never changed by MicaStats.
- Thai and English UI text with Thai as the default, through the existing catalogs.
- The notebook may be on battery. GPU use should stay modest by capping the frame
  rate (my call: at most 6 frames per second). It also stops entirely when presence
  is off or the robot is disconnected.

## Decided how

- **Detection on the host GPU** with ONNX Runtime DirectML and the bundled YuNet ONNX
  model. Pick the high-performance GPU automatically and show its name. Fall back to
  the CPU, and say so in Settings, when no DirectX 12 GPU is available.
- **The robot streams JPEG frames** (QVGA, quality about 60) over the existing USB
  companion link, interleaved with the current JSON commands and acknowledgements.
- **The PC asserts DTR (RTS stays off)** on the USB port.
- **The host decides and the robot obeys.** The PC turns detections into a presence
  state and sends the robot gaze targets, expressions, LED changes, screen views and
  bounded head commands through the companion protocol.

## Calls made after the builder check (mine, owner may override)

- **Head range.** Stay strictly inside the current firmware limits: yaw ±45° and
  pitch 15–65°. With Y zero at a vertical display, 15–65° looks upward, which
  suits a desk robot below a seated face. No limit is widened.
- **Direction check.** It runs once, the first time automatic tracking is
  enabled. The head makes one small slow move and the owner confirms or flips each
  axis. The answer is saved in config (`yaw invert`, `pitch invert`). It can be
  redone from Settings.
- **Packaging.** Bundle ONNX Runtime DirectML (pinned version, x64 only) and the YuNet
  model in every build, accepting the larger download (about 40 MB of native DLLs).
  There is no on-demand download.
- **Who wins the robot screen and lights**, highest first:
  1. real diagnostic alert;
  2. onlooker warning;
  3. welcome back greeting;
  4. break reminder;
  5. manual face/LED hold;
  6. doze;
  7. normal rotation.

  Alerts wake StackChan from doze. In Quiet mode the screen reactions still show,
  but LEDs stay dim and no new head motion starts. During a meeting, the greeting
  and onlooker reactions still show and the break reminder is suppressed. The
  onlooker *protective action* runs regardless of Quiet or meetings.

## Left to the builder

- Frame wire format and how the transport demultiplexes frames from correlated
  responses. Today the transport reads only while waiting for a response; streaming
  needs a continuous reader, and the 2048-byte line limit does not fit a ~4.3 KB
  base64 frame.
- Flow control: how the PC paces or acknowledges frames so commands and telemetry
  keep their latency.
- How to obtain the high-performance adapter from C#. The DirectML provider in the C#
  API takes a device id. One option is to enumerate DXGI adapters by GPU preference
  and map to the DirectML index; another is provider options if the chosen ORT
  version supports them. Pin a NuGet version.
- YuNet decoding, score threshold and non-maximum suppression.
- Exact hysteresis numbers, the search sweep pattern, the tracking gains and the rest
  pose. M5's example uses a 0.06 dead zone and 0.1 smoothing. The yaw safe range is
  ±45° and pitch is 15–65° today.
- Where the away summary data comes from (alerts, CompanionActivity, metrics history)
  and how it is kept in memory.
- How the preview frame reaches WPF without copies piling up.
- Glossary entries in CONTEXT.md for presence, onlooker and away.

## Verification the owner expects

- Unit tests for the presence state machine, onlooker rule, break timer, YuNet
  decoding on fixed tensors, frame demultiplexing and adapter selection logic.
- A physical check on COM9:
  - frames arrive intact with DTR on, at the measured rate;
  - detections appear in the preview;
  - Settings names the NVIDIA GPU, and NVIDIA usage rises while AMD does not;
  - the head finds and follows the owner;
  - doze, welcome back and onlooker vault lock happen.
- Telemetry and alerts on the robot screen keep working while streaming.
