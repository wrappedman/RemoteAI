# LAN protocol v2 — consent + primary display

**Both endpoints must update.** Version 1 (heartbeat only) is rejected. Framing remains 4-byte signed big-endian length followed by strict UTF-8 JSON, over mutually authenticated TLS 1.2/1.3. No plaintext fallback.

Envelope: required `version`, `messageId` UUID, `sessionId` UUID, positive per-direction `sequence`, string `type`, object `payload`. Case-sensitive fields, no unknown or duplicate JSON members, depth ≤8. Payload properties: optional `deviceId`, `displayName`, `viewScreen` boolean (default false), `jpeg` byte array encoded as base64.

Limits: total JSON frame 2–393,216 bytes; JPEG ≤262,144 bytes; JPEG encoded dimensions 1–1600 by 1–1000. Header validation precedes native decoding. Only 8-bit SOF0/SOF1/SOF2 JPEG dimensions are accepted. The capture implementation emits at most 1440×900. Truncated/invalid frames close the session. Strict sequence/session checks and unique message IDs are maintained for up to 100,000 messages per direction; reaching the bound closes the session and requires fresh approval. No unbounded replay cache.

## Handshake

1. Controller pins the full Host SPKI, obtained through signed local discovery or independently verified Advanced setup. Host requires a client certificate (proof of possession, not authorization).
2. Controller sends `ConnectionRequest` sequence 1 with its TLS-bound device ID, unverified display name, and `viewScreen` request. Client certificates and payload identities must match.
3. Host prompts its local user, explicitly showing the requested **view primary display** permission. No media producer is started before approval. Consent expires in 45 seconds, default deny.
4. Host sends `Approval` or `Rejection`, sequence 1. Approval echoes the granted view-screen flag; rejection has no capabilities and closes. Controller rejects unsolicited screen grants.
5. Only the approved Host may send `ScreenFrame` / `ScreenUnavailable`, and only the requesting Controller may receive them. A controller sending screen data, or any sender without a grant, is rejected. No input/file/clipboard message types exist.

A six-digit session reference derives from the fresh session UUID and both full device fingerprints. It supports human correlation, **not independent authentication**; its inputs are not TLS exporter secrets and a malicious peer controls its own session/identity. Do not replace trusted full-key verification with this short code on a hostile network.

## Connected messages

- `Ping` / `Pong`: empty payload, periodic 3-second heartbeat.
- `Disconnect`: empty payload, closes the session.
- `ScreenFrame`: only nonempty `jpeg` payload; no extra name, device ID or grant flags.
- `ScreenUnavailable`: empty payload; Controller clears its image and explains the failure.

Writes share one semaphore and a five-second deadline; frames cannot interleave. Screen capture **awaits the send** before taking another frame, then waits 250 ms. No unbounded producer queue. The renderer retains only the latest received JPEG and decodes on a 125-ms UI tick; excess intermediate frames are replaced. A frame-less UI shows waiting/unavailable/stale status instead of equating transport connectivity with working video. Disconnect clears the image and cancels/disposes the capture producer before releasing transport resources.

Read deadline 12 seconds, initial dial/TLS/request deadline 10 seconds, Controller approval wait 60 seconds. Media and heartbeat share TLS/TCP; head-of-line blocking and JPEG bandwidth are known baseline limitations. H.264/WebRTC needs a later media transport design, not a claim made by this protocol.
