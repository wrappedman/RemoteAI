# Acceptance — 0.3.1

## Automated gates

- Core lifecycle/default-deny; protocol v4 required fields, strict JSON, replay/session/sequence/count limits, bounded media/input/file payloads.
- Real mutual pinned TLS; wrong pin, reject/cancel, heartbeat, restart, sudden loss; directional media/input/file capabilities.
- Windows CNG persistence/non-exportability; numeric ID signature/nonce forgery/replay rejection and actual UDP lookup.
- Real TLS input packet delivery to a test handler (CI does not type into arbitrary Windows apps).
- Shared-folder service over real TLS: begin/write a partial upload, disconnect, reconnect, assert offset, continue and verify bytes/SHA; empty/idempotent upload, resumed download, listing and traversal rejection.
- Windows UI: compact numeric connection center, two simultaneous real production-Host-runtime sessions in separate full-size tabs, repeated decoded frames, independently close one without dropping the other, fullscreen/restore, settings rendering, ID not clipped, clean shutdown.
- DPAPI/PBKDF2 correct/incorrect password verifier; dedicated input-worker release/cleanup without physical blocking or synthetic typing.
- Small native downscale/JPEG benchmark written to run/Performance.txt; workspace/session/settings screenshots, EXE startup and packaged runtime/hash checks.

CI uses one Windows runner and isolated test identities/temporary folders. Test auto-grants are explicit fixture policy, not a production consent bypass. Exact source/workflow is in run/BUILD-INFO.md. CI does **not** establish physical LAN latency, all GPU/OS behavior, real UAC/keyboard blocking or production security certification.

## Physical Windows release gates (pending)

Record OS builds/security patches, GPU/driver, DPI/HDR/primary resolution, adapter/subnet, firewall profile, source/package and number of concurrent tabs.

1. Connect by ID on two PCs, including custom TCP port and correct Private/LocalSubnet rules. Wrong ID/pin/changed saved key must fail.
2. Open multiple targets. Each has its own full-size viewport, profile/display modes/input/files state; closing one must not affect others. Hidden tabs reduce rate; input is released on tab/focus loss. Measure 1/4/8-tab CPU/RAM/GC/network/handles for one hour.
3. Verify actual moving desktop, target versus receive FPS for all profiles, physical capture/encode/transport/decode/input latency. Do not equate target 30 with achieved 30. Test 1080p/4K/portrait, resolution changes and DPI 100/150/200%; protected/UAC/HDR/lock limitations recorded honestly.
4. Grant/deny each capability separately; keyboard/mouse mapping with Fit/Actual/Stretch, left/right/middle buttons, dragging/wheel, layouts/Unicode/Alt/Ctrl keys, stuck-key recovery on disconnect. Windows OS shortcuts and Secure Attention restrictions remain.
5. Files: 0 bytes / 160 KiB / multi-GiB, pause, cable loss, reconnect/resume, mutation/corruption, wrong peer/hash, path/ADS/device-name/link attempts, directory limits, existing names, quotas, disk full, ACL denial, target-volume interrupted commit. No false success or overwrite.
6. Settings save: same-port policy/folder updates keep pending approvals and sessions alive; port changes restart incoming only. Outgoing tabs survive and startup-off works. No permission grant is silently broadened; root changes are explicit local actions.
7. Password opt-in: manual trusted-key pairing first; correct versus wrong/untrusted key/password, 3-failure cooldown, forgotten trust/removed verifier, changed Host pin rejection before sending a password, app closed means no access. No silent service or elevation.
8. Admin request disabled/enabled/declined; explicit Host confirmation; normal UAC accepted/denied; safe listener recovery on denial; reconnect and key/account differences. Never automate UAC bypass.
9. Physical input block on a test PC only: policy off/denial/non-admin refusal; local explicit confirmation; both mouse/keyboard behavior; **Ctrl+Alt+Del recovery**, 30-second timeout, disconnect, process/thread termination and repeated-request refusal while active. Do not enable before proving local recovery.
10. Animation grant/denial, normal restore, user changes/crash recovery. Document accessibility implications; no persisted registry changes promised.

Not done: H.264/WebRTC, Internet registry/ICE/TURN/relay, advanced monitor/cursor pipeline, audio, clipboard sync, background unattended service, automatic reconnect/job queue, actual SMB mapping, central audit/MFA/revocation, signed installer/secure updates.
