# Release and updater integrity

OeXYZ releases are intentionally not Authenticode-signed. Each release instead
publishes architecture-specific ZIPs, `SHA256SUMS`, and GitHub artifact
attestations. These establish file integrity and workflow provenance but cannot
create a Windows verified-publisher identity or override SmartScreen/Smart App
Control policy.

The user-triggered updater:

1. reads the latest GitHub Release over HTTPS;
2. selects `win-x64` or `win-arm64` from the running process architecture;
3. downloads the archive and checksum manifest with size limits;
4. compares SHA-256 using a fixed-time comparison;
5. extracts only bounded entries beneath a private staging folder;
6. validates that both single-file frontends are present;
7. asks before any download/install and never performs a silent update;
8. starts a temporary copy of the current GUI, waits for clean shutdown,
   preserves `.bak` rollback copies, replaces both executables, and restarts;
9. restores already-replaced files from backup if a later replacement fails.

GitHub attestations are verified independently with `gh attestation verify` as
shown in the README. The application does not require or silently invoke the
GitHub CLI on end-user machines.

The deterministic updater suite covers version normalization, successful and
rejected hashes, Windows temporary-file lifetime, ZIP traversal rejection, and
replacement with rollback copies.
