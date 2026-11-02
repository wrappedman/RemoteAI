# Virtual shared folder and resumable files

Host can select an existing dedicated directory in Settings, enable file policy and grant Files to a session. File exchange may also be granted without a root; no local data is exposed until a root is selected. The Controller browses it in Remote files. With Files granted, either endpoint can explicitly choose Share my folder in its session tab; the opposite endpoint opens Remote files / Refresh. A Controller’s configured root is never silently exposed back to a Host. Stop sharing revokes further requests. The panel identifies the actual remote root, not the local folder. This is not Windows SMB, a UNC share, a mapped drive or remote execution. Uploads target the root; downloads can browse subdirectories. No remote delete/rename/overwrite action is provided.

## Sandbox

Relative `/` paths only. Reject absolute paths, `..`, `.`, backslashes, ADS colons, Windows reserved device names, trailing spaces/dots, symlink/reparse ancestors and internal `.remotedesk-*` staging names. Listings filter links and staging files, max 256 entries. Settings reject entire drives and the RemoteDesk settings/cache area or profile ancestors; use a dedicated ordinary folder. Host same-user filesystem races remain outside the remote-only threat boundary; Windows profile protection is required.

## Upload

1. Client hashes the local source SHA-256 and offers plain name, total length ≤8 GiB and hash.
2. Host maps to a peer-key/content-hash scoped `.part` and metadata under `%LOCALAPPDATA%/RemoteDesk/Transfers`. It returns durable partial length and prefix SHA-256. Client compares its source prefix; mismatch requests a reset, never trusts an arbitrary offset.
3. One acknowledged 32-KiB chunk at a time, exact offset required. Host flushes before acknowledging. Max 16 partial files, actual partial bytes ≤16 GiB, checked again on writes. Disk exhaustion is an explicit failure, not silent success.
4. Complete checks full size and SHA-256. A verified file is staged on the destination volume under a reserved non-browsable temporary name, checked again and renamed without overwrite. A cancelled cross-volume copy does not expose the final filename as a complete file. Cache/metadata are deleted only after commit.
5. Already committed same-name/size/hash files are idempotent. Different existing content is never overwritten. Empty files are committed as real files.

After disconnection, reconnect with fresh authorization and choose the same source. Same Controller key and content hash recover the partial offset. A changed source/hash becomes a different transfer. Renaming a source mid-resume does not retarget an existing offer. There is no silent automatic reconnect or persistent background job scheduler.

## Download

Client requests remote size/hash, then keeps adjacent `<destination>.remotedesk.part` and `.json` metadata binding Host key, remote path, length/hash. On retry it appends at the durable length, verifies complete SHA-256 and only then renames to a new final destination. Changed metadata/content or corrupt final hash is rejected; remove/reset the partial locally if instructed. No overwrite or automatic file execution. The service does not claim protected at-rest encryption of user file contents; Windows user-profile and chosen-folder ACLs apply.

Pause stops further chunk scheduling; a received chunk/hash/commit may finish on the Host. Disconnect cancels the session worker. Partial cache and reserved destination staging can consume disk until resumed or manually cleaned; Settings opens the cache for explicit local cleanup. Do not delete active parts you need to resume.

Automated tests establish real TLS, interrupt after a chunk, reconnect, assert the preserved offset, finish/verify upload, test an empty/idempotent upload, resume a partial download, verify bytes/hashes, list a root and reject traversal. Manual gates include actual adapter loss, disk-full/cross-volume failure, large files, ACLs and repeated cancel/resume on physical PCs.
