# Architecture — workspace 0.3

```
MainWindow: connection center + settings + independent session tabs (max 8 outgoing / 1 incoming)
  SessionView (Controller): viewport/input routing + profile toolbar + virtual file browser
  HostSessionRuntime: granted input worker + capture producer + shared-folder handler + effects lease
  Settings: current-user policy, trusted keys and DPAPI-protected password verifier
  Discovery → Security: nonce-signed public LAN ID lookup, full-key pin for TLS
  Agent → Transport → Protocol + Core + Security
  Capture (Windows): native GDI scaling → JPEG → binary TLS media
  Files: sandbox, bounded chunks, partial cache, resume/hash/verified commit
  Windows: bounded dedicated SendInput/BlockInput thread, animation flags, DPAPI verifier
```

Core/Transport/Files do not depend on WPF. Discovery is still LAN-only and advertises the selected TCP port. UDP stays 45832. Host remains one active incoming Controller, but can simultaneously have independent outgoing sessions. MainWindow no longer has a single global active session/image. Each session owns its viewport, pending RPCs, decoder queue, input focus, transfer cancellation and lifetime. The core Disconnected→Connecting→Authenticating→WaitingForApproval→Connected lifecycle describes channel setup; live video status requires decoded frames.

Protocol v3 negotiates a subset of requested ViewScreen, ControlInput, FileAccess and DesktopEffects permissions. No implicit input grant from screen consent. The Host command worker is bounded (32), correlation requests are bounded (16), input queue is bounded (256), media decoder queue is one latest frame. Receive/heartbeat are independent of potentially slow file hashing. Files/admin/input-block requests are authorized by capability before dispatch. Native input/BlockInput share one dedicated thread; remote-owned keys are released on focus loss, session end and thread cleanup.

Media has a distinct binary envelope inside TLS, not base64 JSON. Native scaling occurs before WPF/WIC to avoid cloning full 4K bitmaps. Frame pacing subtracts capture/encode/send time from the selected period; busy media writes are dropped instead of queueing behind control/files. Decoding is off the UI thread, bitmaps are frozen, and a selected tab renders the latest frame; hidden tabs request 2 FPS and skip decoding. This is a lower-latency JPEG baseline, not the future GPU/H.264/WebRTC architecture.

Settings are current-user JSON with atomic replace. Password verifier is salted PBKDF2-SHA256 600k wrapped by current-user Windows DPAPI; no plaintext credential persistence. Automatic approval is opt-in, current visible app only, restricted to explicitly trusted Controller full keys; optional auto-input/files policies remain separate. Controller refuses first-contact numeric password transmission without a prior pinned Host. Failed password attempts have a global cooldown.

UAC requests require explicit Host confirmation, normal Windows elevation and process restart/reconnect. No service, security-boundary bypass or silent elevation. Physical blocking is an additional, fresh Host action, at most 30 seconds, both mouse/keyboard, local Ctrl+Alt+Del recovery. Desktop animation changes have separate consent and a normal-cleanup restoration lease. See Security/Acceptance for manual native-feature release gates.

SharedFolderService exposes a virtual file root, not SMB. It rejects traversal/absolute paths, links/reparse points, reserved Windows names and internal staging paths. Upload parts are content/peer-key scoped outside the shared root; full verification precedes destination-volume staging + rename without overwrite. No command executes transferred files. Resume across a fresh TLS session requires that session's authorization.

Internet still requires a real authenticated registry/ID allocation, signaling, key binding, ICE/STUN/TURN and encrypted relay deployment. A local numeric address alone does not cross NAT.
