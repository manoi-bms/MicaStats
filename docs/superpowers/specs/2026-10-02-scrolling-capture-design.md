# Screen capture: scrolling capture

Asked by the owner on 2026-10-02: "please modify screen capture to make it support capture area that has scroll bar to auto scroll bar to capture all content in scroll bar area". The owner's standing goal is "autonomous continue implement until finish", so every choice the request leaves open is a ruling marked **(R)** with its reason.

## What it does

A new capture mode, **Scrolling**, joins Region, Window, Screen and All screens. You pick an area that scrolls: a browser page, a document, a chat, a list, or a code editor. MicaStats then scrolls it from the top to the bottom by itself and joins what it saw into one tall image. The image opens in the annotation editor like any other capture. Copy, save, pin and redaction all work on it unchanged.

## What does not change

- The other four modes, their shortcuts and menu items are untouched.
- The editor, file naming, clipboard and auto-save behave as for any capture. **(R)** File names use the existing template unchanged, with the same `{mode}` value every mode gets today.
- Nothing is sent anywhere; this is local screen capture.

## 1. Starting it

- Overlay right-click menu: **Capture Scrolling…**, under the existing capture items.
- Global shortcut: `AppConfig.CaptureHotkeyScrolling`, default **Ctrl+Shift+4**. It is registered with the other capture shortcuts while `CaptureHotkeysEnabled` is on. **(R)** This follows the 1/2/3 numbering. Windows uses Win+Shift+S, not Ctrl+Shift+4.
- **Settings → Capture**: a **Scrolling** test button beside the existing Region and Window test buttons.
- `CaptureMode.Scrolling` is a new enum value, and `CaptureService.Grab` gets a new case for it.

## 2. Picking the area

- The existing region picker opens, with its hint changed to: "Drag around the part that scrolls (a click takes the whole window) · Esc cancel". *(Revised after the final review: the picker's click takes a whole top-level window, so dragging around the scrolling part is the reliable way.)*
- A click takes the window under the pointer; a drag takes a rectangle. Both work as they do in Region mode.
- An area smaller than 50 × 50 pixels is refused. The status card shows "Pick a larger area: at least 50 × 50 px" beside it for 2 seconds. **(R)** Smaller areas cannot be joined reliably.
- **(R)** No new picker. The existing one already returns the area in physical screen pixels and handles mixed DPI.

## 3. Scrolling and grabbing

- **Input.** MicaStats moves the pointer to the centre of the picked area and sends real mouse-wheel input with `SendInput`. The pointer goes back to where it was afterwards. **(R)** Wheel input reaches the view under the pointer in browsers, Office, Explorer, WPF and Win32 apps alike. Posting `WM_MOUSEWHEEL` or `WM_VSCROLL` misses Chromium and UWP content. Keys such as PageDown or Ctrl+End would move a caret or trigger shortcuts.
- **Cursor.** It is never drawn into scrolling frames, even when `CaptureIncludeCursor` is on, because it would repeat in every frame.
- **Grabbing.** Frames come from the same BitBlt engine as the other modes, so they show what is on screen. The picked area must stay visible while it scrolls; the status card says so.
- **Only the picked window.** *(Added after the final review.)*
  - At the first scroll, MicaStats remembers the top-level window under the area's centre.
  - If Windows does not route the wheel to the window under the pointer ("Scroll inactive windows when I hover over them" is off), that window is brought to the front first, and must still be in front before every notch.
  - If the window under the centre changes, or the window closes or is minimised, scrolling stops. The capture is kept with the editor note "MicaStats could not send scrolling to that window" (stop reason InputLost).
  - While Ctrl, Shift, Alt or Win is held, MicaStats waits before sending wheel input.
- **Start at the top.** MicaStats first scrolls up, 10 notches at a time, until the view no longer moves, or until 300 notches have been sent. Capture then starts from there. *(Revised:)* "No longer moves" is a tolerant check (`ScrollStitcher.Unmoved`), so an animation in the area (GIF, video, spinner) does not prevent reaching the top. **(R)** The owner asked for "all content". A wheel scroll moves the view only, never a caret or selection.
- **Step down.** One notch per step, sent as 120 wheel units. **(R)** One notch usually moves 3 lines, about 40 to 150 pixels. That leaves a large overlap for joining. Any app that scrolls a whole page per notch still overlaps enough.
- **Settle.** After each step, grab every 50 ms until two grabs in a row are identical (smooth scrolling has finished), or 800 ms have passed; then take the last grab. **(R)** This handles animated scrolling without a fixed long wait.
- **Stop** at the first of these:
  - a step leaves the view unchanged, meaning the end was reached;
  - the joined image reaches 20,000 pixels tall;
  - 500 steps have been taken;
  - the user presses **Esc**, through a temporary global hotkey held only while capturing;
  - two frames cannot be joined (see 4).
  
  Whatever was joined so far is kept, with one exception: Esc during the scroll to the top cancels the whole capture. **(R)** These limits bound memory (20,000 × 3,840 × 4 bytes is about 300 MB at worst) and time.
- **Status card.** A small topmost window stays outside the picked area, at the top of its monitor or, failing that, the bottom. It reads "Scrolling capture: 3,400 px · Esc to stop". It is excluded from every grab, because the grab is clipped to the picked area and the card never overlaps it.

## 4. Joining frames

The joiner is a pure unit, `ScrollStitcher`, that works on 32-bit pixel rows.

- **Static rows.** Some rows stay identical at the same position in the previous and the new frame, at the top or bottom edge: a sticky header, a toolbar, a fixed footer. Those rows are excluded from the overlap search.
  - The header comes from the first frame.
  - The footer comes from the last frame, so it appears once.
  - Static rows are counted from each edge until the first row that differs.
  - They are fixed only once a real scroll has been seen.
- **Soft footer.** *(Added after the final review.)* Below the detected footer, a bottom margin of up to a quarter of the view is also taken from the last frame only. The margin is limited so a large scroll still overlaps.
  - This keeps floating elements (a chat widget, a back-to-top button, a link preview) and footers that change (a clock) from repeating at every seam.
- **Side panels.** *(Added after the final review.)* Columns at the left and right edges that stay identical while the rest moves are fixed panes, such as a navigation pane or a sticky sidebar.
  - They are left out of the comparison.
  - They are cropped from the result, so the image is the part that scrolled.
- **Scrollbar strip.** *(Revised.)* The ignored strip at the right edge is 24 pixels at 100% display scaling, scaled with the area's monitor.
- **Overlap.** In the moving band between the static rows, find the shift `dy` (1 to the band's height minus 8) for which the rows of the new frame line up with the rows of the previous frame shifted up by `dy`.
  - Rows are compared through a hash per row. The rightmost 24 pixels of each row are ignored, so a scrollbar thumb never breaks a match. **(R)**
  - At least 70% of the overlapping rows must match. **(R)** That threshold tolerates small changes such as a blinking caret or an animated badge.
  - *(Revised after the final review)* Among the shifts that pass, the one with the most matching rows wins, so a short accidental overlap never beats the real scroll. Ties go to the smallest `dy`; a shift of 0 means the view did not move.
- **Append.** Only the new frame's last `dy` rows of the moving band are added to the image.
- **No match.** When no shift reaches the threshold, scrolling stops and the joined image so far is kept. The status line in the editor's title shows "Stopped: the view changed in a way MicaStats could not follow". **(R)** Appending a frame that cannot be joined would produce a broken image without warning.
- **Width.** A frame whose width differs from the first is refused, which stops the capture the same way as no match. **(R)** The picked area never changes size during a capture.

## 5. Result

- The final image is the header, then the joined moving band, then the footer. It goes to `CaptureService` like any grab: editor, clipboard and auto-save, per the settings.
- If only one frame was taken (nothing scrolled), the result is that frame, and the editor title says "Nothing scrolled in that area".
- *(Added after the final review)* A capture cut at a cap says so in the editor title: "Stopped at the 20,000 px limit" or "Stopped at the 500-step limit".
- A pinned capture taller or wider than the screen opens scaled down to fit.
- JPEG cannot hold more than 65,535 pixels on a side. The 20,000 px cap keeps far inside that limit.

## 6. Privacy and safety

- Input goes only to the picked area. The pointer moves only inside it and is put back where it was afterwards. No keys are sent.
- The temporary Esc hotkey is released when the capture ends, even when it fails.
- The diagnostics log records, in the `capture` area, the frame count, final height and stop reason. It never records image contents.

## 7. Testing

- **ScrollStitcher**, with synthetic frames built in tests:
  - a plain scroll joins exactly;
  - a sticky header and a fixed footer appear once;
  - a scrollbar thumb that moves in the rightmost columns still joins;
  - a small changed region (a caret) still joins;
  - an unrelated frame gives no match;
  - a different width is refused;
  - identical frames are the end of the content;
  - a frame that scrolled by most of its height still joins with an 8-row overlap.
- **ScrollCaptureRun**, the loop, with a fake screen and a fake scroller (content taller than the view, scrolled by a set amount per notch, optionally animated):
  - it scrolls to the top first;
  - it stops at the end, at the height cap, at the step cap, on Esc and on no match;
  - the result equals the fake content.
- **Plumbing:** the settings default for the new hotkey, `CaptureHotkeys.Plan` including it, and `CaptureFileNamer` with mode `scrolling`.
- **End to end (owner):** a long web page in Edge or Chrome, a long Explorer list, a long MicaPad note, and a page with a sticky header.

## Not in this feature

Horizontal scrolling, capturing a window that is covered or off-screen (PrintWindow or Windows.Graphics.Capture), choosing the scroll speed in Settings, and video.
