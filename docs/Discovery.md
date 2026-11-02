# Numeric ID discovery (workspace 0.3.1)

## Scope

Same-subnet IPv4 LAN only. This is not a deployed global device registry. A numeric ID is an address for discovery, never a password, authentication secret, account or guaranteed global allocation.

`ID = UInt64BigEndian(SHA256(SPKI)[0..8]) mod 10^12`, formatted as twelve decimal digits. The underlying CNG key persists per Windows user profile, so restarting the app does not change the number. A reset/replacement of the key changes it. Two profiles on one physical PC can have different IDs.

## Resolution

The Controller sends a versioned JSON query with a fresh random UUID nonce and target code to UDP 45832 (loopback, limited broadcast and active interfaces' directed broadcast addresses), four times during a three-second resolution window. It receives only bounded datagrams (≤4096 bytes). No application traffic is sent in plaintext except public discovery metadata.

A visible, available Host replies with its number, public certificate, name, TCP port, query nonce and RSA-SHA256 signature over the canonical tuple. The resolver verifies certificate validity/RSA strength, the numeric derivation, nonce and signature. It pins the full SPKI fingerprint for the subsequent mutual TLS connection to the datagram's source address. Forged/replayed responses are ignored. More than one distinct verified key claiming the number fails closed. Duplicate responses for one key are coalesced.

Replies are capped at eight per second per process. The listener has a single handshake/session slot. These are abuse bounds, not comprehensive DoS protection. Local network users can learn discoverable device IDs, names and public certificates. Disabling availability stops discovery and incoming TLS.

First-contact discovery is **self-asserted key discovery**, not a PKI. Twelve decimal digits provide a finite collision/search space (~40 bits), and an attacker can attempt offline key generation for a chosen ID. On an untrusted network use independently verified full fingerprints. After a successful attended connection the full key is stored locally; a later change for the same numeric ID is rejected. Saving an unconnected numeric target alone does not establish a trusted key.

## Network requirements

Private LAN inbound configured TCP port (default 45831) and UDP 45832 for the app, LocalSubnet scope. `scripts/Enable-LanFirewall.ps1` is an optional explicit admin action; the app never elevates or modifies rules. Firewalls can permit TCP while blocking UDP: that explains manual connectivity working while ID discovery fails. Broadcast cannot normally cross routers, VLANs, client-isolated Wi-Fi or VPN boundaries. There is no silent fallback to an insecure connection.

## Internet phase

Needs an actually deployed authenticated device registry, globally collision-free ID allocation/lookup, authenticated signaling, endpoint key binding and direct connectivity negotiation with an encrypted relay fallback. No service URL is hardcoded and no ID-to-public-IP promise is made in 0.3. Merely inventing a local number cannot make an endpoint reachable across NAT/firewalls.
