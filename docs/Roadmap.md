# Roadmap after v0.2

- Phase 0/1 foundation: modular endpoint, TLS identity/consent, protocol/state machine and Windows builds are implemented; final physical two-PC/security acceptance remains required.
- v0.2 responds to the requested usability change: local numeric IDs and initial **view-only GDI/JPEG** desktop capture/transport/rendering. This is a basic capture/media vertical slice, not completion of the eventual performance/media architecture.
- Next capture/media work: physical GPU/DPI/HDR tests, Windows Graphics Capture/DXGI, cursor/multi-monitor handling, bounded H.264 pipeline, measured latency/bandwidth and frame pacing.
- Input phase: explicitly requested per-action mouse/keyboard permissions and Windows security/session/UAC boundary review. No input capability is implicitly granted by current screen consent.
- Internet Hybrid phase: deploy authenticated registry + collision-free IDs, signaling with endpoint key binding, ICE/STUN/TURN and encrypted relay fallback. Current numbers only resolve on the LAN; no server is deployed/configured.
- Security phase: rate limits, pairing protocol review for hostile networks, central key lifecycle/revocation/MFA, codec fuzzing, independent security audit.
- Later: separate opt-in files/clipboard/device management; authenticated IPC/service design only if justified. Never treat a screen approval as blanket administration.
- Release: code signing, installer, authenticated updates/rollback protection, measured/certified Windows support matrix. All current packages are unsigned development previews.
