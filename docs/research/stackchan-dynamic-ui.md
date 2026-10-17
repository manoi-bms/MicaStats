# StackChan living display and buffered rendering

Date: 2026-10-14. Scope: the installed M5Stack StackChan UIFlow2 image and the
existing MicaStats RAM companion. DESIGN.md is the current design contract;
this note supersedes the earlier screen research's restrained animation advice.

The owner wants a lively character, useful rotating information and immediate
abnormal-event alerts, and reported visible screen flicker during development.
The renderer previously erased the face on the LCD before drawing its features.
It now clears a reusable buffer off screen and transfers the finished face once.
The 210x108 large buffer uses 45,360 bytes; the 84x54 overview buffer uses 9,072.
Both use built-in MicroPython modules, with no additional device dependency.

## Official API evidence

UiFlow's display documentation describes off-screen canvas animation, supporting
the decision to present completed frames instead of individual feature strokes.
See the pinned [display documentation](https://github.com/m5stack/uiflow-micropython/blob/69846518a8b0893987ba0512558c4b01c9161454/docs/source/hardware/display.rst).

The chosen existing LCD binding is `drawRawBuf(buf,x,y,w,h,len,swap=False)`.
Its implementation obtains the buffer, establishes an address window and calls
`writePixels` once. The length argument is a **pixel count**, so this renderer
passes `w*h`, rather than the bytearray's `w*h*2` length. See the pinned
[UiFlow binding](https://github.com/m5stack/uiflow-micropython/blob/69846518a8b0893987ba0512558c4b01c9161454/m5stack/components/M5Unified/mpy_m5gfx.cpp).

MicroPython RGB565 framebuffers store native 16-bit pixels. The LCD transfer
therefore enables byte swapping. This is grounded in the pinned
[framebuffer source](https://github.com/micropython/micropython/blob/78ff170de9e32c79db6e64d3e33d2bd60002bdcd/extmod/modframebuf.c)
and [M5GFX writePixels implementation](https://github.com/m5stack/M5GFX/blob/9bcedf4e19d4cab7a0793de0598ae5390b4689fe/src/lgfx/v1/LGFXBase.hpp).
Built-in [`framebuf.ellipse`](https://docs.micropython.org/en/latest/library/framebuf.html)
renders standard upright eyes, irises and mouths; other rotations use the
existing bounded geometry inside the same buffer.

## Physical evidence and limits

The COM9 RAM-only probe loaded the actual source with temporary timing counters.
It observed 373 face frames, 107 distinct poses and the companion, overview and
focus views. Maximum face rendering was 36ms: clear up to 3ms, features up to
24ms and transfer up to 10ms (stage maxima need not occur in the same frame).
The animation interval is 80ms. USB acknowledgements had a 121.7ms median and
242.9ms maximum during this synthetic cycle. The firmware's cooperative loop
continues polling between frames; a 20ms sleep is not a 20ms response guarantee.

Alert interruption/recovery and a still Quiet display passed. No head command,
calibration write, firmware flash, reset or device-file write was issued. The
probe report is `artifacts/stackchan-dynamic-ui/hardware-probe.json`.

This establishes bounded rendering and removes the visible erase pass. It does
not measure panel scan tearing or substitute for the owner's visual assessment.
Arbitrary manual rotations retain Python geometry and may take longer than
normal presets. The animated desktop preview approximates the built-in fonts
and framebuffer rasterization. A RAM companion still needs to be started again
after the robot restarts.
