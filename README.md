# RemoteDesk

A modular Windows remote-support project. **The current milestone is phase 1: attended, authenticated, encrypted LAN sessions — not desktop streaming.**

The English WPF workspace follows the supplied [`01.jpg`](01.jpg): dark device rail, blue-accented workspace tab, command strip and session area. Unlike a mock remote desktop, it displays the real device identity, incoming approval, connection state and local audit events. Future screen/input controls are visibly disabled.

## Download / run

See [`run/README.md`](run/README.md). The Windows build produces:

- `run/RemoteDesk.exe` plus framework-dependent companion files (.NET 10 Desktop Runtime x64 required).
- `run/RemoteDesk-win-x64-portable.zip`: extract first; includes the runtime and runs without a separate .NET installation.
- Checksums and build provenance.

Build output is unsigned. Review the security model before exposing a listener. A successful CI startup/loopback test is **not** two-PC LAN acceptance or a security audit.

## Implemented

- Persistent RSA-3072 device identity; SHA-256 of DER SubjectPublicKeyInfo is the device ID.
- Non-exportable, user-scoped CNG private key and Windows Current User certificate store.
- TCP with TLS 1.2/1.3 and client certificates; exact Host fingerprint pinning, no insecure bypass.
- Local Host approval/rejection with a full Controller fingerprint; default deny, 45-second expiry.
- Versioned, size-bounded framed JSON; sequence, session and replay checks.
- One session at a time, cancellation, periodic heartbeat, disconnect detection and orderly teardown.
- Local saved targets/search, real network addresses, copyable public identity, capped in-memory activity log.
- Cross-platform core/protocol/TLS console test suites, Windows CNG tests and WPF startup smoke test.

## Deliberately not implemented

Desktop capture/streaming, mouse/keyboard injection, clipboard, files, unattended access, background Windows Service, Internet signaling/STUN/TURN/relay, backend accounts, automatic reconnect and update installation. Every reconnect requires fresh approval. There is no hidden input capability or UAC bypass.

## Build from source

Use a supported Windows 11 x64 machine, .NET 10 SDK (Visual Studio with the WPF workload is optional):

```powershell
dotnet build RemoteDesk.sln -c Release
dotnet run --project tests/RemoteDesk.Core.Tests -c Release
dotnet run --project tests/RemoteDesk.Protocol.Tests -c Release
dotnet run --project tests/RemoteDesk.Security.Tests -c Release
powershell -ExecutionPolicy Bypass -File scripts/Build.ps1
```

Tests are ordinary console executables with failing exit codes, intentionally requiring no third-party test packages. `dotnet test` is not their runner. The security suite establishes actual TLS connections on loopback; Windows additionally checks persisted non-exportable CNG keys. No UI/network behavior is claimed validated on Linux.

## Structure

| Project | Responsibility |
| --- | --- |
| Desktop | English WPF workspace and explicit user consent |
| Agent | Unprivileged incoming LAN listener and single-session admission |
| Core | Legal session transitions and default-deny capability model |
| Protocol | Wire envelopes, framing, validation, replay/order guard |
| Security | Device identity, key protection and certificate pin validation |
| Transport | Mutual TLS, protocol handshake, heartbeat and teardown |

See [Architecture](docs/Architecture.md), [Protocol](docs/Protocol.md), [Security](docs/Security.md), [Acceptance](docs/Acceptance.md), and [Roadmap](docs/Roadmap.md).

## Supported baseline

.NET 10 LTS; Windows 11 x64 24H2/25H2 with current security updates is the intended acceptance baseline, not yet a hardware-certified support matrix. Windows 10 (including ESU), ARM64, Server, Secure Desktop/UAC and multi-user scenarios require separate validation. No administrator privileges are requested. Incoming TCP 45831 is off until explicitly enabled; the app does not modify firewall rules.

## License

See the existing [LICENSE](LICENSE). This project has its own identity; the supplied reference is design inspiration, not a shipped third-party product interface or simulated remote session.
