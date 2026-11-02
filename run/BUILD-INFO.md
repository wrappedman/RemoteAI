# Verified Windows build

Source commit: 4177236d574fcd807a26ea4d3ed37ee67c4f95c7
Workflow: https://github.com/wrappedman/RemoteAI/actions/runs/41415191079
Runner: windows-latest, Windows x64, .NET 10 SDK.
Checks: Release solution build, Core/Protocol/Security integration suites, Windows CNG persistence and non-exportability, WPF startup, multi-tab simultaneous sessions, real GDI capture/binary media/background decoding/fullscreen, DPAPI/PBKDF2 password verifier, input-worker cleanup; real approval checkbox/policy hot-save, production two-way file exchange and share revocation, animation acknowledgement/rollback, persistent admin errors, UAC launch contract; Settings-to-TLS password setup before pairing, trusted auto-login without popup, DPAPI reload/draft retention, missing-folder recovery and cooldown; TLS input permission and resumable upload/download SHA-256/sandbox tests.
Manual two-PC LAN, DPI and resource acceptance: pending (see docs/Acceptance.md).
Package is unsigned. Workspace 0.3.2 / protocol v4: primary-display viewing, consent-gated input, explicit two-way virtual shared folders and resumable transfers implemented. Admin/input-block/UAC hardware behavior and physical two-PC performance still require manual acceptance. No Internet relay or background unattended service.
