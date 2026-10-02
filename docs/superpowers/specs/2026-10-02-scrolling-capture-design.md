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

- The existing region picker opens, with its title hint changed to: "Click the part that scrolls, or drag around it. Esc cancels."
- A click takes the window or pane under the pointer; a drag takes a rectangle. Both work as they do in Region mode.
- An area smaller than 50 × 50 pixels is refused with a short notice. **(R)** Smaller areas cannot be joined reliably.
- **(R)** No new picker. The existing one already returns the area in physical screen pixels and handles mixed DPI.

## 3. Scrolling and grabbing

- **Input.** MicaStats moves the pointer to the centre of the picked area and sends real mouse-wheel input with `SendInput`. The pointer goes back to where it was afterwards. **(R)** Wheel input reaches the view under the pointer in browsers, Office, Explorer, WPF and Win32 apps alike. Posting `WM_MOUSEWHEEL` or `WM_VSCROLL` misses Chromium and UWP content. Keys such as PageDown or Ctrl+End would move a caret or trigger shortcuts.
- **Cursor.** It is never drawn into scrolling frames, even when `CaptureIncludeCursor` is on, because it would repeat in every frame.
- **Grabbing.** Frames come from the same BitBlt engine as the other modes, so they show what is on screen. The picked area must stay visible while it scrolls; the status card says so.
- **Start at the top.** MicaStats first scrolls up, one wheel notch at a time, until two frames in a row are identical, or until 300 notches have been sent. Capture then starts from there. **(R)** The owner asked for "all content". A wheel scroll moves the view only, never a caret or selection.
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
- **Overlap.** In the moving band between the static rows, find the shift `dy` (1 to the band's height minus 8) for which the rows of the new frame line up with the rows of the previous frame shifted up by `dy`.
  - Rows are compared through a hash per row. The rightmost 24 pixels of each row are ignored, so a scrollbar thumb never breaks a match. **(R)**
  - The shift chosen is the one with the most matching rows, and at least 70% of the overlapping rows must match. **(R)** That threshold tolerates small changes such as a blinking caret or an animated badge.
  - Ties go to the smallest `dy`.
- **Append.** Only the new frame's last `dy` rows of the moving band are added to the image.
- **No match.** When no shift reaches the threshold, scrolling stops and the joined image so far is kept. The status line in the editor's title shows "Stopped: the view changed in a way MicaStats could not follow". **(R)** Appending a frame that cannot be joined would produce a broken image without warning.
- **Width.** A frame whose width differs from the first is refused, which stops the capture the same way as no match. **(R)** The picked area never changes size during a capture.

## 5. Result

- The final image is the header, then the joined moving band, then the footer. It goes to `CaptureService` like any grab: editor, clipboard and auto-save, per the settings.
- If only one frame was taken (nothing scrolled), the result is that frame, and the editor title says "Nothing scrolled in that area".
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
