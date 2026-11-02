# Next phase: Internet connectivity (not deployed / not yet implemented)

0.3.2 fixes the app-open password setup/login workflow. It does not create a public registry, relay or global numeric address service. LAN discovery remains UDP broadcast; it does not cross NAT/CGNAT/router boundaries. Do not expose a Windows Host port publicly as a shortcut.

## Recommended first Internet milestone

Self-hosted authenticated directory + outbound relay on a Linux VPS with a domain and valid HTTPS/WSS certificate. Both Windows endpoints initiate outbound connections (normally port 443), so neither PC requires inbound port forwarding and CGNAT can be supported. The VPS endpoint / deployment choice must be agreed before deployment; there is no Arena-hosted RemoteDesk service and no hosting cost or resource has been provisioned.

Proposed architecture:

1. Refactor SecureSession's TCP-specific ownership into a bounded duplex-stream transport while retaining the same pinned mutual TLS / approval / capability checks. Regression-test direct TCP unchanged.
2. Authenticate directory registration with device-key possession and fresh nonces, reject active ID collisions/key replacement, expire presence, bound registrations/requests/tunnels, and protect the deployment against unauthenticated resource exhaustion. A short numeric ID alone is never authentication.
3. Pair two outbound WSS streams at the relay. Run **end-to-end pinned mutual TLS inside that byte tunnel**; the relay transports ciphertext, never screen contents, keystrokes, files or automatic-access passwords. Use normal OS HTTPS certificate verification, not certificate-validation bypasses.
4. Persist prior full-key pins, keep manual first pairing and trusted-Controller password approval, expose Internet/LAN routing and reconnect errors in the UI. Never silently accept a key changed by the registry. First contact over an untrusted directory needs an independently verified full fingerprint (or a separately designed audited pairing protocol).
5. Ship a server container / deployment configuration, health checks, documented secrets generated outside chat, short-lived connection tickets, quotas/rate limits, renewal/restart recovery and operational instructions. No secret in repository, arguments, logs or screenshots. A public multi-tenant service needs separate account/MFA/abuse/revocation design; it is not just a demo relay opened to everyone.
6. Verify on **two independent Internet connections**, including CGNAT, refusal/wrong pin, password cooldown, loss/reconnect, two-way files/resume, multi-session resource usage and relay backpressure. Loopback/CI tunnel tests alone are not proof of Internet reachability.

Direct peer-to-peer ICE/STUN/TURN, WebRTC and hardware video/adaptive bandwidth are later optimizations; do not claim them implemented because a TCP relay exists. The first relay milestone prioritizes a correct reachable end-to-end authenticated channel.

## Deployment decision needed

- Is a Linux VPS available, and can a domain/subdomain point to it?
- Is this a private deployment for your own devices, or a public multi-user product?
- If using an existing private VPN for an interim test, manually pinned routable addresses can work, but UDP numeric LAN discovery will not magically become a global directory. This is not the product-level Internet relay milestone.

Do not send VPS passwords, SSH private keys, tokens or other credentials in chat. Deployment should use the platform's configured secure access or user-run instructions.
