# Verified Windows build

Source commit: 0eb5906e5a9e62e54cf72cc3818233771b1f19d5
Workflow: https://github.com/wrappedman/RemoteAI/actions/runs/41399822024
Runner: windows-latest, Windows x64, .NET 10 SDK.
Checks: Release solution build, Core/Protocol/Security integration suites, Windows CNG persistence and non-exportability, WPF window startup and rendered workspace/default-off state checks.
Manual two-PC LAN, DPI and resource acceptance: pending (see docs/Acceptance.md).
Package is unsigned. No screen streaming or remote input in phase one.
