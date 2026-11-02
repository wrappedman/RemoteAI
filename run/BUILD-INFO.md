# Verified Windows build

Source commit: 5849678a6082756e0ff97ecdda9e7445e393402b
Workflow: https://github.com/wrappedman/RemoteAI/actions/runs/41402818911
Runner: windows-latest, Windows x64, .NET 10 SDK.
Checks: Release solution build, Core/Protocol/Security integration suites, Windows CNG persistence and non-exportability, WPF startup plus end-to-end numeric discovery, approved TLS, real GDI primary-desktop capture, JPEG transport/decoding, disconnect and close.
Manual two-PC LAN, DPI and resource acceptance: pending (see docs/Acceptance.md).
Package is unsigned. Primary display viewing is implemented. No remote input, files, unattended access or Internet ID routing.
