# Roadmap after workspace 0.3.1

Implemented in this branch: independent Controller session tabs (8 outgoing/1 incoming), compact connection center/settings, numeric LAN lookup, configured port/policy, binary paced JPEG/background rendering and profiles, consent-gated SendInput, virtual shared folder and resumable hashed transfers, trusted-key/password app-open approval, explicit UAC restart request and short local-confirmed physical-input-block request.

Native permission-sensitive behavior remains gated by physical acceptance (Acceptance.md); it is not considered production-audited because code exists or unit tests pass.

Next media phase: measured physical GPU/DPI/HDR/latency testing, cursor/multi-monitor support, Windows Graphics Capture/DXGI, hardware Media Foundation H.264 and low-latency/adaptive WebRTC transport. Current 30 FPS is a selectable target, not a certification.

Internet phase: actually deploy authenticated registry with globally unique ID allocation, signaling distinct from media, endpoint fingerprint/key binding, ICE/STUN/TURN and relay-blind encryption. Current numbers remain local addresses and cannot cross NAT alone.

Security/release phase: independent native/input/codec audit and fuzzing, rate limits, stronger first-contact pairing, central key lifecycle/revocation/MFA, durable audit, installer/code signing, authenticated updates/rollback protection and certified Windows matrix.

Future convenience: explicit clipboard/audio permissions, robust persistent reconnect/transfer job scheduler, file-management extensions, actual drive redirection only after a separate design. There is no hidden SMB or service installation in this build. A future service requires authenticated user-session IPC and must never turn screen consent into blanket administration.
