# USB companion implementation
Status: complete
Blocked by: 01-contracts (complete)

Acceptance: Native COM transport with handshake and correlated acknowledgements, routing/persistence, selectable localized UI and temporary companion startup, USB-capable firmware and regression coverage.
Evidence: Native SetupAPI discovery and unbuffered finite COM I/O implemented with separate USB identity, hello and correlated command acknowledgements. Normal Connect does not interrupt; explicit Start transfers bundled source into RAM via raw-paste/raw REPL. USB/Bluetooth routing, persisted mode, Thai/English UI, independent firmware stream buffers and bounded input bursts implemented. 111 targeted StackChan/localization tests pass, 27 firmware tests pass, and independent final review approves with no remaining code findings. No added application dependencies. After the owner's UIFlow re-burn, COM9 startup/hello/dashboard/face/RGB commands passed physical verification and the owner confirmed operation.
