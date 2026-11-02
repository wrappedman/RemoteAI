# Run RemoteDesk (Windows x64)

- **No .NET installed?** Extract `RemoteDesk-win-x64-portable.zip` to a new folder and run `RemoteDesk.exe` inside it. Keep all extracted files together. This package includes the runtime.
- **.NET 10 Desktop Runtime (x64) installed?** Run `RemoteDesk.exe` directly in this folder. Keep its DLLs, JSON files and EXE together.

The Windows CI workflow creates these files after the solution and tests pass. Do not mistake source files or scripts for a compiled application. See `BUILD-INFO.md` for the actual build status/provenance when available.

Target: Windows 11 x64 24H2/25H2 with current security updates. No administrator privileges required. Windows 10 is not certified; use only a supported/ESU installation after independent testing. The executable is not code-signed; Windows SmartScreen may warn.

## Two-device LAN test

1. Run on two Windows machines on a trusted private LAN.
2. On the Host, enable **Allow attended requests on port 45831**. If required, create an inbound Windows Firewall Private-profile rule for this application/TCP port. Do not expose it to the internet.
3. Share the Host IPv4 address and **full 64-character device ID** through a trusted channel.
4. On the Controller, enter the Host address, port and fingerprint, then click **Connect securely**.
5. On the Host, verify the Controller's full fingerprint against its own app, then approve. A self-reported machine name is NOT verified identity.
6. Both sides show **Connected**. Heartbeats run every 3 seconds. Either side can disconnect. Each reconnect requires fresh local approval.

**Phase one only:** authenticated encrypted protocol messages. Screen viewing, remote mouse/keyboard, file transfer, clipboard, relay and unattended access are not implemented. Disabled toolbar buttons are intentional.

Device keys are non-exportable, user-scoped Windows CNG keys; public certificates are stored under Current User / Personal. Saved device addresses and public fingerprints are local to `%LOCALAPPDATA%\RemoteDesk\devices.json`. No private keys, tokens or desktop content are logged. See `docs/Security.md` in the repository before deployment.
