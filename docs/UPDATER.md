# Release and updater integrity

OeXYZ releases are intentionally not Authenticode-signed. Each release instead
publishes architecture-specific ZIPs, `SHA256SUMS`, and GitHub artifact
attestations. These establish file integrity and workflow provenance but cannot
create a Windows verified-publisher identity or override SmartScreen/Smart App
Control policy.

The user-triggered updater:

1. reads the latest GitHub Release over HTTPS from the pinned official
   `Oexyz/OeXYZ-Minecraft-Console-Client` repository;
2. selects `win-x64` or `win-arm64` from the running process architecture;
3. downloads the archive and checksum manifest with size limits;
4. compares SHA-256 using a fixed-time comparison;
5. extracts only bounded entries beneath a private staging folder;
6. validates that both single-file frontends are present;
7. asks before any download/install and never performs a silent update;
8. starts a temporary copy of the current GUI, waits for clean shutdown, and
   creates a unique transaction backup that records whether each frontend
   existed before replacement;
9. replaces both executables through transaction-scoped temporary files and
   restarts only after both succeed;
10. restores an existing frontend from that transaction's exact backup or
    deletes a frontend that did not exist before the update when a later step
    fails;
11. aggregates and reports rollback/cleanup failures and rejects symbolic-link
    or reparse-point update paths.

GitHub attestations are verified independently with `gh attestation verify` as
shown in the README. The application does not require or silently invoke the
GitHub CLI on end-user machines.

Official Release builds ignore `OEXYZ_UPDATE_REPOSITORY`. This prevents a
locally injected process environment from redirecting the updater to a
different GitHub owner whose archive and checksum agree with each other. The
override remains available only in Debug builds for maintainers testing a fork.

The deterministic updater suite covers version normalization, release-source
pinning, Debug override validation, successful and rejected hashes, Windows
temporary-file lifetime, ZIP traversal rejection, every prior frontend
existence combination, first/second replacement failures, partial rollback
failure reporting, stale backups, repeated updates, and successful exact
rollback.
