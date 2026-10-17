# StackChan USB control

The owner has flashed UIFlow 2.0 on the USB-connected StackChan. Windows currently reports COM8, VID303A/PID1001, and USB identity 7C:4F:AD:AE:3D:14 matching its displayed MAC. A passive eight-second serial read received 8,448 bytes without writes or reset.

Add USB as a selectable connection alongside Bluetooth. Reuse existing companion controls/reactions, quiet mode and conservative motion bounds. Enumerate present Espressif USB serial ports; explicitly select a device and verify a versioned companion handshake before enabling controls. USB identity persistence is separate from Bluetooth even when their MAC values match, and must survive a COM-number change when the USB serial identity is available. No new dependencies.

UIFlow companion receives bounded JSON+LF from USB stdin, with request IDs and acknowledgements; keep BLE available when its helper can start. Generic UIFlow startup is not the companion. Provide an explicit Start USB companion action that temporarily starts the bundled script through MicroPython raw REPL. Explain that it interrupts the current robot program and is lost on robot reboot; do not flash, overwrite device files, or change servo calibration. A normal USB connection never sends control characters or starts a program.

Use finite/cancellable I/O, DTR/RTS disabled, exact acknowledgements, and retire sessions after partial writes/cancellation. Preserve existing BLE behavior. Verify targeted tests then relevant broader checks, hardware handshake/dashboard where supported without head movement, clean staged Release and normal local deployment.
