# LAN protocol v1

TCP → TLS 1.2/1.3 with mutual certificates → framed UTF-8 JSON. There is no plaintext application fallback.

## Frame

4-byte signed big-endian payload length, then exactly that many bytes. Valid length: 2–16,384 inclusive. Reject length before allocation. Truncation/EOF closes the connection. JSON depth ≤ 8, case-sensitive names, no unknown members, no duplicate properties, string enums only. Invalid UTF-8/JSON, enum values or fields terminate the session.

```json
{
  "version": 1,
  "messageId": "7f870dc9-7e18-471f-829c-c54594b43d9a",
  "sessionId": "90bf57d8-2fc4-467d-a9c9-7c8d16a88a85",
  "sequence": 1,
  "type": "ConnectionRequest",
  "payload": {
    "deviceId": "64_UPPERCASE_HEX_CHARACTERS_OF_SHA256_SPKI",
    "displayName": "WORKSTATION"
  }
}
```

The sample `deviceId` is explanatory; real IDs are exactly 64 hex characters. Display name: 1–64 characters, non-whitespace, no control characters. It is **self-reported**, never a trust decision.

`payload` is always an object. Non-request messages have `deviceId`/`displayName` null or absent; other values are rejected. All UUIDs must be nonempty. The initiator generates a new session UUID on every connection. Sequences are independent per direction, start at 1, and must increment by exactly 1. A MessageGuard enforces the session UUID, next sequence and unique message ID. At most 10,000 IDs per direction are retained; reaching the cap terminates the session instead of growing memory indefinitely. TLS itself supplies transport record integrity and replay protection; application checks add state-machine safeguards, not a replacement for TLS.

## Handshake

1. Controller validates the Host certificate SPKI against the full pre-entered ID, including certificate validity and RSA strength.
2. Host requires a client certificate. TLS proves possession of its private key; it is NOT authorized yet.
3. Controller sends `ConnectionRequest` (sequence 1).
4. Host checks payload ID equals the actual client TLS SPKI fingerprint.
5. Host presents that full fingerprint, unverified name and address to its local user.
6. Only explicit approval sends `Approval` (Host sequence 1); rejection/expiry sends `Rejection` and closes. No unattended fallback exists.
7. Connected peers may exchange only `Ping`, `Pong` and `Disconnect`. A request or approval repeated in this state is a protocol violation.

`Ping` causes `Pong`; each peer also pings every 3 seconds. No arbitrary commands, screen content, file data or input events are supported. A valid frame must complete within 12 seconds of starting each connected read. A peer that stays silent, disconnects mid-frame or sends malformed data loses the session. Normal local disconnect sends `Disconnect`, then closes TLS/TCP. Reconnect creates fresh transport, UUID, sequences and approval.

## Future evolution

Reject unsupported version numbers, rather than silently downgrading. Add explicit capability negotiation before enabling future media/input messages. A future authenticated protocol must bind WebRTC fingerprints to pinned or centrally verified device keys. LAN v1 must never be presented as an Internet relay protocol.
