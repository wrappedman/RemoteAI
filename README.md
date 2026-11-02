# RemoteDesk v0.2 — numeric LAN IDs + live desktop viewing

English WPF workspace inspired by `01.jpg`, persistent device keys, attended consent, encrypted LAN desktop viewing.

## Run

Extract **`run/RemoteDesk-win-x64-portable.zip`** to a folder and run `RemoteDesk.exe` inside it. Runtime included. Alternatively use `run/RemoteDesk.exe` with all its companion files and .NET 10 Desktop Runtime x64 installed. Both endpoints must use v0.2; the old v1 protocol is deliberately rejected.

1. Open RemoteDesk on both Windows devices on the **same private LAN**. Availability is on while the visible app is running.
2. Share the Host's **12-digit RemoteDesk ID**, e.g. `123 456 789 012`.
3. Enter that ID on the Controller and choose **View remote screen**. No IP or fingerprint entry is required in the default flow.
4. Compare the displayed session code and confirm the intended requester. The Host explicitly selects **Approve & share my screen**.
5. The Controller displays the actual primary desktop. **Viewing remote desktop** is shown only after an image has decoded, never merely because TLS connected.
6. **Disconnect**, **Stop sharing**, disabling Host availability, or closing the Host app ends transmission.

Numeric IDs resolve through signed UDP discovery **on the LAN only**. They are not Internet-routable AnyDesk accounts. There is no deployed registry/signaling/relay server. Broadcast discovery cannot cross routed subnets, isolated Wi-Fi clients, or most VPNs. See [network setup](docs/Discovery.md).

## What changed from v0.1

- Persistent, formatted numeric IDs derived from device public keys; signed, nonce-bound LAN discovery resolves address and full TLS pin automatically.
- Advanced manual IP/full-fingerprint connection retained for networks that block discovery.
- Actual GDI primary-display capture, JPEG encoding, bounded encrypted transport and WPF rendering.
- Explicit screen consent and directional capability enforcement: only the approved Host can send frames.
- Backpressure, latest-frame rendering, paused/unavailable states and immediate image clearing on disconnect.
- End-to-end Windows UI test drives numeric discovery → TLS approval → actual desktop capture → JPEG transfer → visible decoded image → disconnect.

## Current limits

View only: no mouse/keyboard control, clipboard, files, unattended access, UAC/Secure Desktop support, sound, multi-monitor switching or Internet relay. Capture is a compatibility-first GDI/JPEG baseline, **not H.264/WebRTC**: primary monitor, at most 1440×900, quality adjusted to stay under 256 KiB, at most about 4 FPS (slower on slow hosts/links). Protected content, locked desktops and HDR can appear black or stale; no bypass is attempted. This is not yet a low-latency video/remote-control release.

IDs are **addresses, not passwords or globally allocated unique numbers**. First-contact discovery is not a central trust authority. For hostile networks, verify the full fingerprint using Advanced setup and a trusted channel. Saved verified keys are pinned on subsequent ID connections. See [Security](docs/Security.md).

## Build / test

.NET 10 LTS SDK, Windows x64:

```powershell
dotnet build RemoteDesk.sln -c Release
dotnet run --project tests/RemoteDesk.Core.Tests -c Release --no-build
dotnet run --project tests/RemoteDesk.Protocol.Tests -c Release --no-build
dotnet run --project tests/RemoteDesk.Security.Tests -c Release --no-build
dotnet run --project tests/RemoteDesk.Desktop.Tests -c Release --no-build -- artifacts/workspace.png
powershell -ExecutionPolicy Bypass -File scripts/Build.ps1
```

Tests are console executables, not `dotnet test` adapters. Security tests use real TLS and UDP. Desktop tests use real Windows capture/encoding and an isolated auto-approving **test Host**; production approval is not bypassed. A CI end-to-end test is not a substitute for acceptance on two physical Windows PCs.

Projects: Desktop, Agent, Core, Protocol, Security, Transport, **Discovery**, **Capture**. Core/transport remain independent of WPF/capture. The capture library is Windows-specific.

Build provenance and pending manual gates: `run/BUILD-INFO.md`, [Acceptance](docs/Acceptance.md). Target baseline: updated Windows 11 x64 24H2/25H2; physical OS/GPU/DPI certification pending. Windows 10/ESU, Server and ARM64 require independent validation. Builds are unsigned. Existing [LICENSE](LICENSE) applies.
