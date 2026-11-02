# Security model — 0.3.1 development preview

## Identity, first contact and encryption

Persistent user-scoped non-exportable RSA-3072 CNG key, CurrentUser certificate store, full SHA-256 SPKI identity, standard pinned mutual TLS 1.2/1.3. No app PFX/private-key files, crypto bypass, plaintext traffic mode or TLS downgrade. Self-signed discovery signatures prove key possession, not a central identity authority. Numeric IDs are public ~40-bit addresses with collision/search limits, not passwords or globally allocated accounts. First contact assumes a trusted private LAN and intended local approval; on a hostile network independently compare full fingerprints. Saved Host key changes fail closed. The six-digit session reference hashes public identities/UUID and is only correlation, not PAKE/exporter-based authentication.

Discovery is signed/nonce-bound, ≤4096-byte datagrams, eight replies/sec, three-second lookup. These are abuse bounds, not full DoS protection. UDP exposes public IDs/certificates/names on the LAN. TCP port is configurable; UDP 45832 remains fixed. The app never changes firewall rules or runs as a service. Only Private/LocalSubnet rules should be enabled; no Internet relay is configured.

## Capabilities and visible ownership

Each connection asks separately for screen, input, files and temporary effects. Host chooses a subset; input/files/effects checkboxes start unchecked. Local policy can disable grants completely. Transport enforces direction and capability before dispatch, not just UI button state. A Controller cannot become a media sender; an ungranted input packet terminates an invalid session; ungranted RPC fails. Up to eight outgoing tabs and one incoming Controller avoid competing remote input owners on the Host.

Native input has a bounded queue and a dedicated injection thread. Only remote-owned held keys/buttons are released on focus loss/disconnect/cleanup; queued stale events are discarded at teardown. SendInput obeys UIPI/UAC and session/security boundaries. No Secure Attention Sequence, login bypass, stealth input hook, keylogging or arbitrary shell command exists. Authorized full desktop control can naturally perform actions at the Host user's existing privileges; choose that grant deliberately.

## Sensitive operations

**UAC restart**: disabled by default. Requires Control permission, enabled Host policy, a fresh explicit local Yes and the normal OS UAC prompt. Current process sessions close and reconnect is required. No unattended elevation, service installation, UAC bypass or attempt to type a consent/password into Secure Desktop. A different Windows account can have a different key/ID. Test this only with the published EXE on your own PCs; CI does not approve elevation.

**Physical input block**: disabled by default. Requires Control, enabled policy and a fresh local dialog explicitly saying **mouse AND keyboard**. The same dedicated thread calls BlockInput and injects approved remote events. Maximum 30 seconds, no silent extension. Local Ctrl+Alt+Del is an OS-provided escape; expiry, disconnect/thread/process exit releases it. The OS can refuse without elevation. Never advertise this as an invisible/permanent mouse lock. CI does not block its runner's physical input; manual recovery validation is a release blocker.

**Animations**: separate Effects grant and policy. Only normal UI animation flags change, no registry persistence flag. Previous values are restored during normal cleanup; a crash or external simultaneous settings change may require local restoration in Windows preferences. This is not an accessibility/security policy bypass.

## Password-assisted automatic approval

Off by default and **only while the app is open**, not an unattended Windows Service. Host first manually approves a Controller and explicitly trusts its **full TLS key**. Later opt-in password access checks this exact key and a unique 16+ character password. Automatic view, optional input and optional files are separate policies; effects/admin/blocking are not silently granted. Three failed password attempts trigger a 60-second global cooldown. An empty password still requires manual consent.

Verifier: PBKDF2-HMAC-SHA256, 600,000 iterations, random 32-byte salt, constant-time 32-byte comparison; verifier/salt wrapped with current-user DPAPI. No plaintext password saved, no client credential cache. Controller refuses sending a numeric first-contact password without a previous Host pin; changed discovery keys are rejected before a password is sent. Advanced full-pin setup is an explicit out-of-band verification path. DPAPI/non-exportable keys do not protect against malware already running as the same user. Protect the Windows profile and revoke both trusted keys/password if compromised. Forgetting keys disables password access; Remove password clears its verifier on Save.

Settings are current-user JSON with atomic replacement. Local malicious edits are outside this endpoint-compromise threat boundary. Root sharing refuses the RemoteDesk settings/cache area and profile/drive ancestors; do not expose application/configuration directories or broad sensitive trees. Shared-file sandbox and transfer integrity: FileTransfer.md. No automatic execution, delete or overwrite of received files.

## Bounds / privacy / release blockers

Protocol v4: required strict JSON, no duplicate/unknown fields, bounded nesting, validated input/ranges, binary JPEG size/dimension cap, session/sequence/ID replay checks capped at one million IDs/direction. RPC promises ≤16, commands ≤32, input queue ≤256, decode queue one latest frame. File hash/disk work is off the receive/heartbeat path. Native codecs still require Windows security updates and fuzzing; length limits are not a codec security audit.

Live screen bytes and passwords are never logged or saved by the running app. CI captures workspace screenshots only as test artifacts. Audit is capped in-memory, not durable enterprise logging. Partial file contents are stored on the chosen disk/Windows profile, not app-encrypted; protect ACLs and clean caches explicitly. After disconnect the image and input ownership are released; a stale frame is labeled after five seconds.

Independent audit, physical two-PC network/GPU/DPI/performance tests, real keyboard/layout/UIPI tests, input-block recovery, UAC acceptance/denial, partial/disk-full/cross-volume failures, long multi-session resource tests, code signing, authenticated updates and a certified Windows support matrix remain release blockers. Protected/locked/UAC/HDR desktops can be black/stale/unavailable; no bypass is attempted. No Internet registry/relay, Windows login access, background service, audio or clipboard synchronization is implemented.

Report vulnerabilities privately without passwords, private keys or personal desktop contents.
