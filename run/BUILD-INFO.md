# Verified Windows build

Source commit: ee17c4520b0d5190a59d31b43485afbaf52627fc
Workflow: https://github.com/wrappedman/RemoteAI/actions/runs/41399536343
Runner: windows-latest, Windows x64, .NET 10 SDK.
Checks: Release solution build, Core/Protocol/Security integration suites, Windows CNG persistence and non-exportability, WPF window startup and rendered workspace/default-off state checks.
Manual two-PC LAN, DPI and resource acceptance: pending (see docs/Acceptance.md).
Package is unsigned. No screen streaming or remote input in phase one.
