# Changelog

All notable changes follow a simplified form of Keep a Changelog. Version tags
use semantic versioning.

## [Unreleased]

## [1.0.3] - 2026-08-11

### Added

- A deterministic loopback regression test proving that an established
  Minecraft connection can be disconnected promptly and repeatedly.
- A reproducible 4K social-preview master and a GitHub-ready 1280x640 export.
- Search-oriented README introduction, CI/release/protocol badges, and a
  reproducible GitHub artifact-attestation verification guide.
- Objective scope comparison with Mineflayer and HeadlessMc based on their
  official project documentation.
- Explicit Microsoft-token storage and outbound-network documentation.

### Changed

- Renamed the GitHub repository to `OeXYZ-Minecraft-Console-Client` and
  migrated badges, release links, updater metadata, and project URLs.
- Architecture documentation now states prominently that OeXYZ implements the
  protocol directly and never launches Minecraft.

### Fixed

- Disconnect now terminates the active Minecraft socket instead of only
  cancelling the outer session, so connected sessions shut down immediately.
- Close no longer waits indefinitely for a connected receive loop.
- Session action buttons now use a DPI-aware, auto-sized toolbar so Disconnect
  and Close remain fully visible and clickable.
- Repeated or concurrent disconnect and disposal requests are handled safely
  without reporting an intentional socket shutdown as a connection failure.

## [1.0.1] - 2026-08-11

### Fixed

- Replaced the update message box with an immediately visible branded window.
- Closed the verified temporary archive before its final move on Windows.
- Normalized release and assembly versions so `1.0.0` equals `1.0.0.0`.

### Added

- Deterministic updater tests for successful downloads, Windows file lifetime,
  version comparison, checksum rejection, and temporary-file cleanup.

## [1.0.0] - 2026-08-11

### Added

- Native C# Windows interface with OeXYZ branding.
- Independent Minecraft Java protocol support from 1.8 through 26.2.
- Automatic SRV lookup, explicit custom ports, and version detection.
- Microsoft browser authentication and Windows DPAPI session protection.
- Offline-mode profiles for servers that explicitly permit them.
- Live chat, commands, `/respawn`, automatic respawn, keepalive, reconnect, and
  optional anti-AFK look movement.
- Stable queued chat rendering with a 5,000-line memory bound.
- Honest `OeXYZ` client-brand announcement and 26.x code-of-conduct prompt.
- SHA-256-verified GitHub release downloads.
- Local integration coverage using real servers across six representative
  versions from 1.8.8 through 26.2.
