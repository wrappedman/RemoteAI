# v0.2 acceptance

Both machines must use the new build; v1/v2 are deliberately incompatible.

## Automated Windows gates

- Core lifecycle and cancellation/default-deny model.
- Required/strict v2 envelopes, sequence/session/replay and message-count limits, malformed/oversized/truncated frames, screen payload/capability checks.
- Real mutual TLS, wrong pin, approval/rejection, directional screen grant, byte-for-byte encrypted JPEG transfer, matching session reference, heartbeat, cancellation, disconnect/restart and transport loss.
- Windows CNG persistence and non-exportability.
- Signed discovery verification, tampered signature, nonce replay and actual UDP numeric lookup.
- Real WPF application: numeric ID initialized, visible app availability on; a separate test Host advertises an ID, receives the UI's discovery/TLS request, approves only screen viewing, captures the actual Windows desktop and sends JPEGs. The UI must decode a frame and show Viewing remote desktop; then disconnect clears the image and closes cleanly. Test fixture auto-approval does not alter production consent.
- Real workspace/viewer screenshots and packaged EXE startup, publish and SHA-256 checksums.

Exact verified run/source commit lives in run/BUILD-INFO.md after CI. CI uses one Windows runner; **it does not prove cross-PC broadcasts or every physical GPU/desktop work**.

## Manual two-PC gate (pending)

Record both Windows build/update versions, network/firewall profiles, adapter/subnet, GPU, DPI, primary display/HDR/resolution and package/source commit.

1. Start both apps; their numbers are stable across restarts and differ between devices/profiles.
2. On the same Private subnet, enter only the Host number. No IP/full-key entry should be necessary. Host sees requester, requested screen permission and session code.
3. Reject/ignore: no image or screen bytes. Approve: actual moving Host primary desktop appears, not only Connected. Verify a visible window moving/changing on the Host is reflected on the Controller.
4. Disconnect/Stop sharing/close Host: traffic ceases, image clears, both states reset. Retry requires fresh approval.
5. Firewall TCP allowed but UDP blocked: ID lookup explains discovery failure; Advanced manual connection still works with a trusted full pin. TCP blocked: controlled connection error. Isolated Wi-Fi/VLAN: no false Internet-discovery claim.
6. Wrong number, own number, wrong full pin, changed saved pin, invalid/malformed/authenticated image: fail safely, no bypass or process crash.
7. Unplug adapter, kill/restart process, cancel during discovery/TLS/approval, simultaneous requests: bounded teardown and one active slot.
8. DPI 100/150/200%, 1080p/4K, display resolution change, sleep/lock/UAC/HDR: record supported behavior and black/stale/unavailable limitations. No Secure Desktop promise.
9. Measure idle/connected CPU, RAM, handles, bandwidth and screen latency over 10 minutes, one-hour run and repeated reconnects. Do not claim targets achieved before recording these measurements.

Not implemented/testable yet: input injection, advanced monitors, H.264/WebRTC, Internet registry/ICE/TURN/relay, files/clipboard, unattended access, elevation or updates.
