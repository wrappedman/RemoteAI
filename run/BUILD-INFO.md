# Verified Windows build

Source commit: 872f0769c3002b93f0a7b90957f60f8dd93afe45
Workflow: https://github.com/wrappedman/RemoteAI/actions/runs/41406997546
Runner: windows-latest, Windows x64, .NET 10 SDK.
Checks: Release solution build, Core/Protocol/Security integration suites, Windows CNG persistence and non-exportability, WPF startup, multi-tab simultaneous sessions, real GDI capture/binary media/background decoding/fullscreen, DPAPI/PBKDF2 password verifier, input-worker cleanup; TLS input permission and resumable upload/download SHA-256/sandbox tests.
Manual two-PC LAN, DPI and resource acceptance: pending (see docs/Acceptance.md).
Package is unsigned. Primary-display viewing, consent-gated input, virtual shared folder and resumable transfers implemented. Admin/input-block/UAC hardware behavior and physical two-PC performance still require manual acceptance. No Internet relay or background unattended service.
