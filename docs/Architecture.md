# RemoteDesk architecture v0.2

```
Desktop (visible WPF app, current Windows user)
 ├─ Discovery → Security: signed numeric ID lookup / LAN UDP
 ├─ Agent → Transport → Protocol + Core + Security
 └─ Capture (Host only) → bounded JPEG frames → Transport/TLS → Controller renderer
```

Every app can initiate or receive one attended session. Availability starts while the visible app runs and can be disabled. There is no Windows Service, background unattended agent, backend account service or Internet relay. Desktop owns consent, visible lifecycle and rendering; Transport owns authenticated framing, capability checks, heartbeat, cancellation and media-producer lifetime. Neither Transport nor Core references WPF/capture.

New modules:

- **Discovery:** persistent twelve-digit IDs from device SPKI; nonce-bound RSA-signed UDP replies, three-second lookup, full-key pinning for TLS. See Discovery.md.
- **Capture:** Windows-only GDI BitBlt primary display capture in the current user session, cached DC/bitmap handles recreated on resolution changes, WPF JPEG encoding, bounded dimensions/size and deterministic cleanup. No privileged access or Secure Desktop workaround.

This is intentionally a compatibility-first **view-only GDI/JPEG baseline**. It is not the eventual DXGI/Windows Graphics Capture + H.264 + WebRTC low-latency engine. One capture loop per approved Host, awaiting each write, max about four frames/sec. UI keeps only the latest received image and distinguishes secure-channel-ready, waiting-for-screen, viewing, paused and unavailable.

The underlying legal lifecycle is unchanged: Disconnected → Connecting → Authenticating → WaitingForApproval → Connected → Disconnecting → Disconnected; cancellation can terminate any intermediate state. “Connected” in the core means transport readiness only. Displayed “Viewing remote desktop” requires a decoded frame.

Identity remains user-scoped, non-exportable RSA-3072 CNG, CurrentUser certificate store, full SHA-256 SPKI pin. Numeric IDs are discoverable addresses, not secrets/global allocations. Consent explicitly grants primary-screen viewing; keyboard/mouse/file/clipboard capabilities are absent. Saved v2 targets persist a public full-key pin after successful attended connection; old manual-target settings are not migrated or overwritten.

Session owns TCP/TLS, background receive/heartbeat tasks and any registered screen producer. Closing cancels the producer, closes sockets, waits for producer cleanup, then disposes synchronization primitives. Capture does not save images. The desktop renderer clears all images on disconnect. Logs exclude image bytes, full keys, session codes and private material, and remain capped at 100 in-memory entries.

Future Internet architecture still requires a deployed authenticated registry, signaling distinct from media relay, ICE/STUN/TURN, endpoint key/fingerprint binding, and relay-blind end-to-end encryption. A local number alone cannot traverse NAT. See Roadmap.md for remaining phases.
