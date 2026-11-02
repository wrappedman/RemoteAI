# Security — v0.2 development preview

## Key custody and TLS

Persistent RSA-3072 key `RemoteDesk.DeviceIdentity.v1` in the current user's Microsoft Software Key Storage Provider, signing-only usage, non-exportable. Self-signed certificate (server/client EKUs, digital signature), one-year validity, stored in CurrentUser/My and renewed with the same key. Full identity = SHA-256 of DER SPKI. Private keys are never serialized to app files or logs. TPM and central revocation are not implemented. Same-user malware can still request signatures from a non-exportable software key; protect the Windows profile.

TLS 1.2/1.3 via SslStream; no custom encryption, plaintext mode or certificate-bypass switch. Controller uses an exact SPKI pin plus validity/RSA strength checks. Host initially accepts a client certificate only for key-possession proof and a pending **untrusted** request; the local user must authorize each session. Names and numeric IDs are not account authentication.

## Numeric discovery trust

Signed, nonce-bound UDP responses bind a local number to a public certificate, then TLS proves possession of that key. A signature from a self-signed key is **not a central identity authority**. The numeric address has a finite ~40-bit collision space, is public, and is not a password. First-contact trust depends on the trusted LAN and correct Host approval; for hostile networks exchange full fingerprints independently and use Advanced pinning. A changed saved full key for the same numeric ID fails closed. Multiple verified keys claiming one number within the lookup window are rejected.

The six-digit displayed session code is for human correlation only. It hashes public identities/session UUID, not shared TLS exporter material, and is not a PAKE or sufficient standalone MITM defense. It must not substitute for a full trusted fingerprint where adversarial LAN peers are a concern. Do not blindly approve a matching-looking name or incoming dialog.

Discovery exposes public IDs, public certificates and machine names on the LAN. Replies are rate-limited and datagrams bounded, but query floods/handshake-slot denial of service remain possible. Full anti-abuse controls require further hardening.

## Screen privacy and permissions

The Controller explicitly requests primary-screen viewing. The Host sees this permission and chooses **Approve & share my screen**; rejection/45-second expiry sends no screen frames. Only that Host may send JPEGs; only the requesting Controller may receive them. No mouse, keyboard, clipboard, file, audio or administrative commands exist. Disabling availability or disconnecting/closing stops the Host producer. A visible banner and Stop sharing action remain in the Host UI.

Frames stay in memory, are never logged/saved by the running app, and are protected by endpoint-to-endpoint TLS. The image is cleared on disconnect and capture failure is reported. A stale frame is labeled after five seconds without a new one. Full screen content can include personal data; close sensitive windows before approving. CI screenshots are test artifacts, not production recording functionality.

## Windows limitations

Runs `asInvoker` in the interactive user's desktop. No service, elevation, session switching, Secure Desktop/UAC bypass, hidden startup or unattended access. GDI capture may return black/stale images for locked/secure/protected/HDR desktops and cannot guarantee every GPU/application behaves identically. Stop sharing before locking or switching users. Only the primary monitor is supported in this baseline; cursor capture and multi-monitor selection are deferred.

## Bounds and release blockers

Strict required JSON fields, no unknown/duplicate properties, size/depth caps, per-direction session/sequence/replay checks, 100,000-message lifetime, five-second serialized writes, 12-second read deadlines. JPEG size and header dimensions are validated before native decoding; screen queues are bounded to a single latest frame. Keep Windows image codecs/security updates current. Framing limits do not replace native codec security review/fuzzing.

Availability now starts while the app is visible, unlike v0.1. Firewall access is never silently changed. Only allow TCP 45831 and UDP 45832 on trusted Private/LocalSubnet networks. Do not publish these ports or use v0.2 as an internet remote-control product. No registry/signaling/relay service is configured.

The audit is local, capped and in-memory, not durable/tamper-evident enterprise logging. Public saved pins are in `%LOCALAPPDATA%/RemoteDesk/devices-v2.json`; protect the profile against modification. Central revocation, MFA, code signing, authenticated updates, independent security review, physical two-PC capture/network acceptance, DPI/GPU/HDR/idle/stress measurements and a certified OS matrix remain release blockers.

To distrust a device, refuse incoming requests and remove its saved target. Suspected stolen local identity: stop the app and use reviewed Windows tooling to remove only its matching certificate/named CNG key, then redistribute the new full fingerprint out of band. Never delete unrelated keys. Report vulnerabilities privately without including personal screen data or keys.
