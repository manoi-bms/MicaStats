# 02 — MicaStats.Pi as a menu-bar dropdown

Status: complete
Blocked by: 01
Labels: complete

## Goal

Owner asked why MicaStats.Pi does not appear on the top menu bar. Wayland
panels are layer-shell surfaces; Avalonia cannot create them (and runs via
Xwayland, where layer-shell does not exist), so the app cannot dock *into* the
bar. Instead it behaves like an iStat Menus dropdown driven by the existing
panel (waybar on the owner's Pi).

## Acceptance criteria

- [x] `--hidden` starts the app without showing a window; sampling continues
      while hidden so history graphs are full when the dropdown opens.
- [x] SIGUSR1 toggles show/hide; the pid file
      (`$XDG_RUNTIME_DIR/micastats-pi.pid`) is written only after the handler
      is installed, so the toggle can never hit the default terminate action.
- [x] `scripts/micastats-toggle` signals the running instance or starts one.
- [x] Borderless, always-on-top, absent from the taskbar (with the labwc
      rule), right-aligned beneath the 28 px bar.
- [x] Closes on a second click, Esc, or focus loss; a toggle within 400 ms of
      a focus-loss hide is ignored (the same click); window-manager close
      hides instead of exiting.
- [x] `install.sh` publishes Release to `$PREFIX/opt/micastats-pi`, installs
      `micastats-pi` and `micastats-toggle`, prints autostart / waybar /
      labwc snippets. It never edits the user's desktop config itself.
- [x] Self-overhead stays low shown and hidden.

## Comments

- 2026-11-12: Verified on the Pi 5 (labwc 0.20.2, waybar 0.12, Xwayland).
  Window geometry after toggle: X=1542 Y=34 W=372 (1920 − 372 − 6, 28 + 6).
  Toggle sequence from visible: Unmapped → Viewable → Unmapped, position
  stable. Focus loss: opening a zenity dialog unmapped the dropdown; the next
  click reopened it, the one after hid it. `_NET_WM_STATE` shows
  SKIP_TASKBAR + ABOVE; its waybar taskbar button is gone. Self CPU 1–2 %
  averaged over 10 s (a one-off ~55 % burst at first show is JIT warm-up).
  An early test looked like a failed hide; investigation showed focus moved
  away right after a labwc reload, the dropdown correctly hid on focus loss,
  and the next toggle re-opened it — behaviour as designed.
