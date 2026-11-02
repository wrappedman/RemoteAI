# Phase-one acceptance checklist

Automated tests passing is necessary but not sufficient. **Do not start phase two until the two-device checks below are recorded on real Windows machines.**

## Automated gates

- Core: complete lifecycle, illegal transitions, disconnect/cancel from each intermediate state, default-deny capabilities.
- Protocol: frame roundtrip, duplicate ID, sequence gap, mismatched session/version, invalid payload, negative/oversized/truncated/malformed frames, cancellation.
- Security/transport: distinct keys, pins and mismatches, certificate requirement, real two-way TLS, TLS/request identity binding, explicit approve/reject, heartbeat, orderly disconnect, cancellation during approval, Agent restart, transport loss.
- Windows-only: stable CNG identity across reloads, private-key export fails.
- WPF process stays alive and creates a real main window after publish.

Console test exit code is the gate. No third-party test adapter is required. Exact build/test outcome and artifact run are in `run/BUILD-INFO.md` when generated.

## Manual two-PC Windows gate (pending)

Record Windows build number, security update status, CPU/RAM, adapter, firewall profile and .NET runtime/portable package for both endpoints.

| Check | Expected | Status |
| --- | --- | --- |
| Host opt-in + trusted Private firewall rule | Port reachable only as intended | Pending |
| Full Host pin and approved Controller fingerprint | Connected on both machines | Pending |
| Wrong Host fingerprint | TLS fails, no session | Pending |
| Host rejects or ignores for 45 seconds | No authorization; returns Disconnected | Pending |
| Stop Controller while consent pending | Host expires/cancels; no automatic approval | Pending |
| Disconnect on either side | Both return Disconnected | Pending |
| Pull network cable / disable adapter | Session closes after heartbeat deadline | Pending |
| Close/restart either process | Old session cannot resume; fresh approval required | Pending |
| Simultaneous incoming/outgoing request | At most one active slot | Pending |
| Malformed/large/replayed authenticated message | Session closed without app crash | Pending |
| Packet inspection | No JSON/device data outside TLS | Pending |
| DPI 100/150/200%, keyboard navigation | Readable cards, usable consent, no overflow | Pending |
| Idle resource measurement, 10 minutes | Record CPU, working set, handles and threads | Pending |
| Sustained connected test, 1 hour | Bounded resource use; fresh reconnection on drop | Pending |
| Public network/firewall denied | Predictable error, no firewall bypass | Pending |

Initial targets (not measured promises): detection within about 12 seconds plus OS scheduling; very low idle CPU; bounded RAM/handles. Compare before/after repeated reconnects and record results. No video latency or capture performance claims apply to phase one.
