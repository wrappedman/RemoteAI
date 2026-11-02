# LAN protocol v3

Both peers must update. Version 1/2 deliberately fail. Mutual pinned TLS 1.2/1.3 remains mandatory.

## Framing

Four-byte signed big-endian payload length, 2–786432 bytes, validated before allocation. Two bodies:

- Control: strict UTF-8 JSON envelope; required version=3, messageId UUID, sessionId UUID, positive per-direction sequence, string type, object payload. Case-sensitive, no unknown/duplicate members, depth ≤8.
- Screen: `RDV3` magic (4), session UUID bytes (16), big-endian Int64 sequence (8), message UUID bytes (16), then raw JPEG. Total header 44 bytes. No base64 conversion/JSON media expansion.

Screen size ≤512 KiB; encoded dimensions 1–1920 × 1–1200, 8-bit JPEG headers validated before decoding. Strict sequence/session/unique ID checks apply to both body formats. Bound: 1,000,000 accepted IDs per direction, then fresh authorization required. The replay set is bounded but long multi-session runs still require measured memory acceptance.

## Consent

ConnectionRequest sends TLS-bound device ID, unverified name and requested view/input/files/effects flags. Optional password ≤256 chars stays inside pinned TLS and is never logged. Host requires a client key, then returns a permitted subset in Approval or sends Rejection. Password-based authorization is an application policy, restricted to known trusted Controller keys; it is not a protocol bypass or new cryptography. A short session reference remains a public-identity/UUID correlation hint, not a PAKE/MITM substitute.

Connected types: Ping/Pong, Disconnect, ScreenFrame/ScreenUnavailable, Input, Command, Reply. Only the granted Host sends media; only the granted Controller sends Input or initiates RPC. No remote shell, arbitrary execution, delete, clipboard or service-install messages.

Input: validated kind Move/Button/Wheel/Key/ReleaseAll, normalized primary-display x/y 0..65535, VK 1..254, mouse button 1..5, wheel bounded ±1200. Host injects via one bounded Windows thread; it does not spoof Secure Attention Sequence.

Command/Reply: unique request UUID, operation, bounded path/data/range/hash/entries. File chunks ≤32768 bytes, file ranges ≤8 GiB, directory listing ≤256 entries; names cannot contain control characters/path separators/ADS colon. Up to 16 in-flight RPC promises and 32 queued commands. Unknown permission/operation fails safely, not elevated. Reply IDs/opcodes are matched.

Operations:
- SetStream: 1..30 FPS, width 640..1920, JPEG quality 20..85; disabling animations additionally needs Effects.
- ListFiles, ReadInfo, ReadChunk, WriteBegin, WriteChunk, WriteComplete: need Files; paths are independently sandboxed by Host.
- RequestAdmin, RequestInputLock: need Control, enabled Host policy, and fresh local confirmation. They cannot remotely approve UAC or persist an input block.

Serialized writes have 5-second deadlines. Media skips a busy writer rather than backlog. Read deadline 12 seconds, heartbeat every 3 seconds, dial/TLS 10 seconds, initial approval 45 seconds (Controller wait 60). RPC timeout normally 120 seconds; large hashing/commit operations up to 600 seconds, without blocking heartbeat/media receive. UI transfer pause stops scheduling chunks; it does not revoke an already received disk operation. Disconnect cancels all workers, pending requests and input ownership.

Resume wire contract and failure handling: see FileTransfer.md. This remains TLS/TCP with head-of-line blocking, not a claim of hardware video transport.
