# RemoteDesk 0.3 — remote workspace

English Windows WPF client with numeric LAN discovery, per-device tabs, explicit permissions, mouse/keyboard control and resumable file transfer. Design follows the supplied `01.jpg`; **Connection center is separate from full-size session tabs**.

## Run

Extract `run/RemoteDesk-win-x64-portable.zip` and launch `RemoteDesk.exe`. Runtime included. Update **both endpoints** to 0.3 / protocol v3; older builds intentionally fail closed. Alternatively run the framework-dependent EXE with all companions and .NET 10 Desktop Runtime x64.

1. Open the app on both PCs on a trusted private LAN. Enter the Host's 12-digit ID in Connection center.
2. Choose requested Control / Files / Effects permissions. Host explicitly chooses which to grant; screen approval alone never grants input or files.
3. A new tab opens beside Connection center. Up to **8 outgoing devices**, independently disconnectable, plus **1 incoming Controller**. Host admission stays single-controller to avoid competing input.
4. Session toolbar: 5/15/20/24/30 target FPS; Fast/Balanced/Sharp resolution/quality; Fit/Actual size/Stretch; full screen (F11, Esc exits); input toggle; Files; admin/input-block requests. **Ctrl+Alt+F12 releases remote keyboard control.**
5. Use Settings for the TCP port, availability, Host permission policy, shared folder and opt-in password-assisted automatic approval.

Numeric discovery is **LAN-only**, not an Internet AnyDesk service. No registry/ICE/TURN/relay is deployed. UDP broadcast does not normally cross VLANs, routers, isolated Wi-Fi or most VPNs. Default TCP 45831 is configurable; UDP discovery is 45832. See [Discovery](docs/Discovery.md) and `run/README.md`.

## Implemented

- Persistent CNG RSA-3072 identity, signed nonce-bound ID discovery, pinned mutual TLS 1.2/1.3.
- Independent full-size remote session tabs and a compact connection/settings workspace.
- Primary-display GDI capture with **native downscaling before WIC**, binary JPEG frames (no base64 media), latest-frame-only background decoding, no old-frame backlog, frame pacing rather than a fixed 250-ms wait. Inactive tabs request 2 FPS and skip decoding. Actual receive FPS is displayed; target FPS is not a performance guarantee.
- Explicit mouse/keyboard grant, coordinate mapping for display modes, bounded dedicated Windows input thread and release of remote-held keys on focus loss/disconnect. Ordinary interactive desktop only; Windows UIPI/UAC restrictions remain.
- Files permission, sandboxed shared-folder browser, download/upload, acknowledged 32-KiB chunks, persistent cross-session resume, prefix validation for upload resume, final SHA-256, destination-volume staging and rename without overwrite. Max 8 GiB/file, 16 partial uploads / 16 GiB cache.
- Session animation-disable option only with its own Host grant; normal cleanup restores previous values.
- Admin restart request: disabled by default, explicit local Host confirmation and the **normal UAC prompt**, closes current process sessions and requires reconnect. No bypass, service install or background persistence.
- Physical-input blocking request: disabled by default, fresh local consent, **mouse AND keyboard**, dedicated injection thread, maximum 30 seconds; Ctrl+Alt+Del, expiry, disconnect or process/thread exit releases it. Windows may refuse without elevation. Native behavior requires manual validation.
- Password-assisted automatic approval only for **previously explicitly trusted Controller full keys**, opt-in and app-open only. 16+ character password, PBKDF2-SHA256 600,000 iterations, DPAPI-protected verifier, no saved plaintext/client password; 3 failed attempts cause a 60-second cooldown. First-contact numeric passwords are refused until a Host pin exists. Auto input/files grants are separate, off by default.

## Limits — do not confuse this with a finished AnyDesk replacement

JPEG/TCP is a much better-paced baseline, **not hardware H.264/WebRTC**. Smoothness depends on capture/encoding/GPU/network/other sessions; 30 FPS is a cap/target, not a measured physical-LAN promise. Primary display only; locked/protected/UAC/HDR desktops can be black/stale/unavailable. Some Windows-key combinations stay local; Ctrl+Alt+Del cannot be remotely injected. No clipboard sync, audio, multi-monitor selector, remote Windows login, automatic reconnect/job scheduler, actual SMB share/mapped drive, account backend, Internet relay or privileged service.

A shared folder is browsed **inside RemoteDesk**, not mounted as a Windows network drive. Resume means reconnect (with fresh consent/password policy), choose the same source or destination, and continue verified partial data; it does not silently authorize a new session. Files are never executed or overwritten by the transfer service.

## Build / verification

```powershell
dotnet build RemoteDesk.sln -c Release
foreach ($suite in @('Core','Protocol','Security','Desktop')) {
  dotnet run --project "tests/RemoteDesk.$suite.Tests" -c Release --no-build
}
powershell -ExecutionPolicy Bypass -File scripts/Build.ps1
```

Console suites, not `dotnet test` adapters. Tests cover actual TLS/binary frames, input permission, path sandbox, interrupted/resumed uploads and downloads, hashes, numeric discovery, DPAPI/PBKDF2 verifier and Windows UI multi-tab independent sessions with the **production Host runtime**. CI deliberately does **not** physically block a runner's keyboard or approve UAC; those are manual release gates. See `run/BUILD-INFO.md`, `run/Performance.txt` (when generated), [Acceptance](docs/Acceptance.md), [Security](docs/Security.md), [Files](docs/FileTransfer.md), [Performance](docs/Performance.md).

Modules: Core, Protocol, Security, Transport, Agent, Discovery, Capture, **Files**, **Windows**, Desktop. Intended baseline: updated Windows 11 x64, .NET 10 LTS. Physical OS/GPU/DPI/security certification pending. Unsigned development builds; existing [LICENSE](LICENSE) applies.
