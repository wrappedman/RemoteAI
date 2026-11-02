# Verified Windows build

Source commit: bc315fa7fc6c13b0f678c4cce3d5aaf3b514a10f
Workflow: https://github.com/wrappedman/RemoteAI/actions/runs/41410389054
Runner: windows-latest, Windows x64, .NET 10 SDK.
Checks: Release solution build, Core/Protocol/Security integration suites, Windows CNG persistence and non-exportability, WPF startup, multi-tab simultaneous sessions, real GDI capture/binary media/background decoding/fullscreen, DPAPI/PBKDF2 password verifier, input-worker cleanup; real approval checkbox/policy hot-save, production two-way file exchange and share revocation, animation acknowledgement/rollback, persistent admin errors, UAC launch contract; TLS input permission and resumable upload/download SHA-256/sandbox tests.
Manual two-PC LAN, DPI and resource acceptance: pending (see docs/Acceptance.md).
Package is unsigned. Workspace 0.3.1 / protocol v4: primary-display viewing, consent-gated input, explicit two-way virtual shared folders and resumable transfers implemented. Admin/input-block/UAC hardware behavior and physical two-PC performance still require manual acceptance. No Internet relay or background unattended service.
