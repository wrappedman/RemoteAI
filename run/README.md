# RemoteDesk v0.2 — run & connect by ID

## Start

**Recommended:** extract `RemoteDesk-win-x64-portable.zip` and run `RemoteDesk.exe` inside the extracted folder. Keep all extracted files together; the .NET/WPF runtime is included.

Or run `RemoteDesk.exe` in the repository's run folder with its DLL/JSON companions and .NET 10 Desktop Runtime x64 installed.

Update **both** machines to v0.2. Protocol v2 intentionally does not connect to the older heartbeat-only v0.1 application.

## No IP entry on the same LAN

1. Run on both Windows x64 PCs on the same trusted Private LAN.
2. Each app shows a stable **12-digit RemoteDesk ID**; availability starts automatically while the app is visible.
3. Enter the destination's number and click **View remote screen**.
4. Confirm the intended Host and compare the displayed session code. The Host selects **Approve & share my screen**. No screen bytes are sent before approval.
5. The Controller shows **Viewing remote desktop** only after receiving and decoding an actual image.
6. Either side can disconnect; the Host also has **Stop sharing**. Closing the app stops its listener, discovery and screen producer.

ID discovery is **LAN-only**, not an Internet connection service. Different routed networks, isolated Wi-Fi clients and most VPNs cannot discover each other. Internet ID routing requires a deployed registry/signaling/relay service, which is not configured in this version.

## If a number is not found

- Keep the Host open and **Available for attended LAN connections** checked.
- Confirm both computers are on the same subnet / non-isolated Wi-Fi.
- Allow inbound **TCP 45831** (TLS) and **UDP 45832** (ID discovery) for this exact EXE on the **Private** profile / **LocalSubnet** only.
- Optional administrator-reviewed PowerShell script in the repository, also packaged with the portable build:

```powershell
.\Enable-LanFirewall.ps1 -ProgramPath 'C:\Path\To\RemoteDesk.exe'
```

The app itself does not elevate or silently change firewall rules. Never expose these ports on Public networks or forward them to the internet. Advanced manual setup remains available when LAN broadcast is blocked; it requires the Host IP and full fingerprint.

## What the screen feature does

Actual **primary display viewing**, GDI capture → JPEG → TLS → WPF image. Up to about 4 FPS, scaled to at most 1440×900. It is view-only: **no mouse/keyboard control**, files, clipboard or audio. It is not H.264/WebRTC yet. Protected/UAC/locked desktops and HDR may be black, unavailable or stale; no security-boundary bypass is attempted. A capture or decode error is displayed instead of treating “Connected” as proof of a working screen.

## Identity & consent

The numeric ID is derived from a user-scoped, non-exportable CNG key and remains stable across restarts for that Windows profile. It is an address, not a password or a globally unique server allocation. Discovery responses are signed, but self-signed device keys are not a central identity authority. For an untrusted network, compare full fingerprints through a trusted channel and use Advanced manual pinning. The short session code is a convenient correlation check, not a substitute for a full trusted fingerprint. After a successful connection the full resolved key is pinned in saved targets; a changed key is rejected.

Saved public targets: `%LOCALAPPDATA%\RemoteDesk\devices-v2.json`. Old v0.1 manual targets remain untouched in `devices.json`. No images are saved or logged by the running app. Builds are unsigned; SmartScreen may warn. Windows 11 x64 is the intended baseline, with two-PC/GPU/DPI acceptance still required. See repository docs and BUILD-INFO.md for exact checks/build provenance.
