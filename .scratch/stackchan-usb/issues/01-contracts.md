# USB protocol and contracts
Status: complete
Blocked by: none

Acceptance: Verify official stdin/raw-REPL APIs, define separate USB identity and versioned hello/request acknowledgement, keep existing Bluetooth interface compatible.
Evidence: USB PnP and passive serial access verified; extended device/snapshot contracts and optional starter interface defined. Official UIFlow USB-mode docs permit Ctrl+C program interruption. StackChan disables UART REPL; pinned ESP32 console source supports sys.stdin/sys.stdout USB serial/JTAG polling. Official select.poll API supports nonblocking input. USB hello uses companionHello:1 plus requestId; response must have ok:true,type:hello,device:MicaStatsStackChan,protocol:1,transport:usb and matching requestId. USB commands wrap original JSON in command plus requestId, with correlated applied/rejected acknowledgement. All wire records including envelopes remain limited to 2048 bytes. Temporary raw-REPL execution uses no soft reset or device file writes; other serial clients must be closed.
