# Security model and limitations

## Scope and assumptions

Phase one is for a trusted private LAN and an interactive Windows user. It is a development preview, not a production remote-control product. Local compromise of either endpoint or its user account is outside the protections provided here. Do not expose TCP 45831 to the public internet. Rate limits, central revocation, MFA, remote attestation and security review remain future work.

## Device identity and key custody

- RSA-3072 key named `RemoteDesk.DeviceIdentity.v1` in the current user's Microsoft Software Key Storage Provider.
- Key is marked non-exportable with signing usage. Private key is never serialized into app files or logs.
- Self-signed certificate, SHA-256/RSA PKCS#1 signature, digital-signature usage and server/client authentication EKUs.
- Certificate is stored in CurrentUser/My and renewed with the same CNG key near expiry (one-year certificate validity).
- Device ID = uppercase hex SHA-256 of DER SubjectPublicKeyInfo, not a friendly short number or password.
- TPM-backed storage is not implemented; non-exportable software keys do not prevent malware running as the same user from requesting signatures.
- No PFX import/export, no hardcoded shared secrets, no token store, and no account authentication yet.

## Mutual authentication and consent

Controller must obtain the Host's full fingerprint through an independent trusted channel and enter it explicitly. Certificate name/PKI-chain errors are deliberately replaced by exact SPKI pinning for self-signed endpoint certificates. Certificate presence, validity and minimum RSA strength are still checked. Arbitrary certificates are never accepted by the Controller.

The Host initially accepts a qualifying client certificate only for TLS key-possession proof and an **untrusted pending request**. Authorization requires its local user to compare the full displayed fingerprint with the Controller device's fingerprint, then explicitly approve. The peer's machine name and IP are not trusted identity. If a user blindly approves a fingerprint, the application cannot protect them from social engineering. No prior approval is persisted. Approval grants only the currently implemented protocol/heartbeat channel, not screen viewing or input access.

TLS 1.2/1.3 use the operating system's secure cipher policies. Renegotiation is disabled. No custom encryption, certificate bypass toggle or unauthenticated plaintext endpoint exists. Current TLS API behavior is verified by real loopback tests, not by application-only message mocks.

## Abuse bounds

16 KiB frame limit before allocation, strict JSON, bounded nesting, no unknown/duplicate fields, required envelope checks, per-session sequencing and duplicate ID rejection. The application caps frames per session, audit history and saved targets. Handshakes, consent, reads and writes have deadlines. One session/handshake at a time and listener default-off reduce exposure but **do not solve denial of service**; an attacker can occupy the single handshake/approval slot. Production needs per-source rate limits, consent spam suppression and network policy.

Audit is local, in-memory, timestamped and capped. It never records payload bodies, private keys, credentials, full fingerprints or desktop content. It is not a durable/tamper-evident enterprise audit service. Local saved targets contain addresses and public fingerprints only. Malicious edits to the user's saved target file can change a future destination; protect the Windows user profile and verify pins through trusted channels.

## Windows boundary

Application runs `asInvoker`; no elevation, service, Secure Desktop access, UAC bypass, session switching, hidden startup or unattended access. Windows input/capture security constraints will be assessed independently in later phases. No promise of control over every window or elevated application is made.

## Revocation / reset

Central revocation is not implemented. To distrust a remote device, remove its saved target and refuse future requests. If a key is suspected stolen, stop using that identity, close the app, remove matching RemoteDesk certificates and the named CNG key using reviewed Windows administrative tooling, then restart to generate a new identity and redistribute the new fingerprint out-of-band. Do not delete unrelated certificates/keys. No automated destructive reset button is shipped.

## Release blockers

Independent security review; two-device Windows acceptance; packet capture confirming encrypted wire data; resource measurements; anti-abuse policy; signing and secure update chain; supported OS matrix. Internet phase additionally needs authenticated signaling/key binding, ephemeral TURN credentials, endpoint-to-endpoint encryption and proof that the relay cannot decrypt media/data. TURN must never receive endpoint private keys or session plaintext.

Report suspected vulnerabilities privately to repository maintainers; do not include keys, live endpoints or personal desktop data in public issues.
