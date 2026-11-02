# Architecture — v1.0 / phase-one implementation

## Product boundaries

RemoteDesk is a Windows, attended remote-support foundation. Each endpoint can be Controller or Host, but never both concurrently in this milestone. The UI and transport have separate assemblies. There is no backend, media path or privileged service yet.

```
Desktop (WPF / current interactive user)
  ├─ Agent → Transport → Protocol
  ├─ Transport → Core + Security
  └─ Security → Windows CNG + CurrentUser/My

Controller ─── TCP / mutually authenticated TLS ─── Host
               encrypted framed JSON only
```

Agent is a library hosted in the desktop process. Treating it as a Windows Service before solving session isolation and an authenticated consent IPC boundary would be unsafe. Closing the UI closes the listener and session. The input/capture engines are future modules, not empty success-returning stubs.

## Lifecycle

`Disconnected → Connecting → Authenticating → WaitingForApproval → Connected → Disconnecting → Disconnected`.

Any intermediate active state can move to Disconnecting, then Disconnected. Core rejects illegal jumps. Transport reports handshake stages, while Desktop owns the visible state machine. Incoming reservation is serialized on the UI dispatcher to prevent simultaneous incoming/outgoing sessions. Agent allows one active handshake/session and immediately closes excess clients.

- Listener: IPv4, configurable in library, UI port 45831. Explicit opt-in; no firewall modification.
- Dial/TLS/first request: 10-second deadline.
- Host local consent: 45 seconds; default reject.
- Controller approval wait: 60 seconds (includes request and network overhead).
- Heartbeat: every 3 seconds; each next complete frame must arrive within 12 seconds.
- Writes: serialized; 5-second deadline, bounded message size.
- After 10,000 outgoing frames or accepted incoming frames, end the session; fresh approval required.
- No automatic reconnect or trust-on-first-use cache. Saved pins are public local configuration, not authorization grants.

## Resource ownership

Identity lives for the desktop lifetime. A transport session owns its TcpClient, SslStream and linked worker cancellation source. Controller/UI owns outgoing sessions; Agent owns incoming sessions. Both receive and heartbeat workers terminate before synchronization primitives are disposed. Agent cancellation tears down an active session and stops its listener. Invalid input is handled as a failed session, not a process-level error. UI audit is capped at 100 entries; frame allocation is capped at 16 KiB; replay sets are bounded by the message limit.

## Planned Internet architecture

Signaling coordinates endpoints; it does not carry desktop video. ICE selects a direct candidate when possible and TURN relays encrypted endpoint traffic when direct connectivity fails. DTLS-SRTP is suitable for WebRTC media and DTLS/SCTP for data channels. TURN must not terminate endpoint DTLS. Signaling must bind the authenticated endpoint key to the negotiated DTLS fingerprint, otherwise a compromised signaling service can substitute keys. That binding is a mandatory future security design, not something the current LAN TLS pin automatically solves.

Backend candidates: ASP.NET Core, PostgreSQL, Redis, WebSocket signaling, audited identity/session/authorization services. Introduce only when their phase is reached. UI must not know the eventual WebRTC implementation.

## Technology decisions

1. .NET 10 LTS, C#, WPF. No package dependencies beyond framework/runtime packs.
2. Standard TLS via SslStream, no custom encryption. RSA SPKI identity pinned out-of-band.
3. CNG non-exportable keys; TPM not yet required/supported as a provider option.
4. Strict framed JSON v1 before media. All future permissions require independent checks on Host.
5. No image content or private key material in audit records.
6. Framework-dependent EXE and self-contained x64 ZIP produced by Windows CI.
