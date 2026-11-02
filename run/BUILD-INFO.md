# Verified Windows build

Source commit: 0671d49ef2fb342500df045bb9bce473b030b888
Workflow: https://github.com/wrappedman/RemoteAI/actions/runs/41398717137
Runner: windows-latest, Windows x64, .NET 10 SDK.
Checks: Release solution build, Core/Protocol/Security integration suites, Windows CNG persistence and non-exportability, WPF window startup.
Manual two-PC LAN, DPI and resource acceptance: pending (see docs/Acceptance.md).
Package is unsigned. No screen streaming or remote input in phase one.
