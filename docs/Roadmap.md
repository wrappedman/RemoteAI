# Execution roadmap

| Phase | Deliverable | Gate |
| --- | --- | --- |
| 0 | Modules, identity decisions, v1 wire contract, threat model | Implemented in this branch; review pending |
| 1 | Attended authenticated LAN channel, English workspace, tests, Windows build | Automated gates + real two-PC acceptance; manual gate pending |
| 2 | Capture interface + Windows Graphics Capture / DXGI fallback | Supported GPU/OS/DPI tests; no network assumptions |
| 3 | H.264 media pipeline with bounded queues and adaptive quality | Real latency, CPU, bandwidth measurements |
| 4 | Mouse/keyboard data path and per-action permission checks | UAC/session boundary review; local user can revoke immediately |
| 5 | ASP.NET Core identity/signaling, ICE/STUN/TURN Hybrid connectivity | Endpoint key binding; direct and relay network matrix; relay cannot decrypt |
| 6 | Security hardening | Key lifecycle, revocation, MFA, rate limits, fuzzing, independent audit |
| 7 | Files, clipboard, managed devices | Separate opt-in permissions and data handling policies |
| 8 | Release | Signed installer/binaries, authenticated updates/rollback protection, measured support matrix |

File transfer, unattended access, multi-session, privileged service and advanced management are not MVP shortcuts. A future service needs authenticated IPC to the active user session and must never turn transport consent into blanket administrative authority.

A beautiful workspace is not the phase-one success criterion. The current UI intentionally has no fake remote desktop: connectivity, identity, consent, bounded framing and teardown must be accepted before capture work begins.
