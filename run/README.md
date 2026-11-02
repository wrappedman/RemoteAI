# RemoteDesk 0.3 — quick start

Extract `RemoteDesk-win-x64-portable.zip` and run the extracted `RemoteDesk.exe`; keep all files together. Runtime included. Or run the EXE in the repository run folder with its companion DLLs/JSON and .NET 10 Desktop Runtime x64 installed. **Close old instances and update BOTH endpoints to 0.3.**

## Connect and control

- Enter the destination's 12-digit ID on the SAME trusted Private LAN. IP entry is not required; manual full-pin setup remains in the advanced expander.
- Select Control / Files / Effects to request them. On the Host, approve screen viewing and separately check the permissions you want to grant. A grey control/file button means permission was not granted or Host policy/shared folder is not configured.
- A new full-size tab opens for each device. Up to 8 outgoing tabs, independently closable. One incoming Controller per Host.
- Target FPS: 5, 15, 20, 24, 30. Fast = 960px / JPEG 40; Balanced = 1440px / 55; Sharp = 1920px / 80. Actual receive FPS is in the status strip; 30 is not guaranteed. Inactive tabs reduce to 2 FPS.
- Display: Fit / Actual size (scrollable) / Stretch. F11 enters/exits full screen; Esc exits full screen. **Ctrl+Alt+F12 releases remote input.** Click the remote image to focus keyboard control. Focus/tab loss releases remote-held keys.

## Settings and permissions

Settings configures the incoming TCP port (1024–65535), startup availability, default FPS/width, permitted grants, shared folder and password policy. Saving restarts the incoming listener and disconnects its current Host session; outgoing tabs remain independent. UDP discovery stays on 45832.

**Shared folder / files:** choose a dedicated existing folder on the Host, enable file grants, then grant Files in the incoming approval. The Controller's Files panel lists/downloads that root and subfolders; Upload places files in the root. No SMB share or mapped drive is created. No symbolic links, path traversal, execution, overwrite or access outside the chosen root. Entire drives, settings/cache/profile ancestors are not suitable shared roots. Max 8 GiB/file; max 16 partial uploads / 16 GiB cache. Partial uploads are in `%LOCALAPPDATA%\RemoteDesk\Transfers`; use the Settings button to inspect/clean them locally. Reserved `.remotedesk-*` destination staging files are not exposed in the virtual browser.

**Resume:** pause or reconnect after interruption and choose the same source file / download destination. Acknowledged data is retained; upload prefixes and final files are hash-checked. Downloads use adjacent `.remotedesk.part` and `.json` sidecars and final SHA-256. Do not delete parts you wish to resume. A changed/corrupted partial is rejected, not silently trusted. Existing final files are never overwritten. Resume does not bypass a new session's permission prompt.

**Animations:** enable the Host policy and grant Effects for that session before selecting No animations. It changes Windows UI animation flags temporarily and restores them during normal cleanup. A crash can require restoring Windows animation preferences manually.

**Admin:** enable admin restart requests on the Host. The Controller requests, the local Host confirms, then Windows presents its ordinary UAC prompt. Current process sessions close; reconnect after approval. A different Windows account can have a different device ID. This is not a Secure Desktop/UAC bypass or a permanent elevated service.

**Physical input block:** enable its Host policy, grant Control, then request it in the session. Host must freshly confirm blocking BOTH physical mouse and keyboard. Maximum 30 seconds; local Ctrl+Alt+Del, expiry, disconnect/process exit releases it. Windows may refuse unless elevated. Do not enable this before testing recovery on your own hardware. It is not a hidden or indefinite mouse lock.

**Password-assisted access:** off by default. First pair manually and explicitly check Trust this full Controller key in the Host's approval. Set a unique 16+ character password in Settings; enable automatic approval for trusted keys and, separately, optional input/files grants. The app must stay open; no unattended service is installed. The Controller must already have a verified/pinned Host before sending a password by numeric ID. Password verifier is PBKDF2-SHA256 (600k), DPAPI-protected; plaintext passwords are not saved. Three failed attempts cause a 60-second cooldown. Forget trust / remove password in Settings to revoke it.

## Firewall and scope

Private LAN only. No Internet registry/relay is deployed. Allow the configured TCP port (default 45831) and UDP 45832 for the app, Private / LocalSubnet only. Optional explicit administrator-reviewed helper:

```powershell
.\Enable-LanFirewall.ps1 -ProgramPath 'C:\RemoteDesk\RemoteDesk.exe' -TcpPort 45831
```

The app never elevates or changes firewall rules silently. No Public port forwarding. Different VLANs, Wi-Fi isolation and most VPNs block broadcast discovery. Numeric IDs are public addresses, not passwords/global account IDs; on a hostile LAN verify full keys independently. Password access must use a previously pinned Host key.

## Remaining limits

Primary display, GDI/JPEG over TLS/TCP, not H.264/WebRTC. Protected/locked/UAC/HDR desktops can be black/stale or unavailable. Standard SendInput cannot cross Windows UIPI/UAC/session boundaries; some OS hotkeys stay local and remote Ctrl+Alt+Del is not supported. No clipboard sync, audio, multi-monitor selector, Internet relay, Windows-login control, background service, automatic reconnect/job scheduling or actual SMB drive mapping. Builds are unsigned. Physical two-PC FPS/latency, DPI/GPU, UAC and input-block recovery acceptance remain pending; see BUILD-INFO.md and repository docs.
