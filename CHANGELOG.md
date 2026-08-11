# Changelog

All notable changes follow a simplified form of Keep a Changelog. Version tags
use semantic versioning.

## [Unreleased]

### Added

- Search-oriented README introduction, CI/release/protocol badges, and a
  reproducible GitHub artifact-attestation verification guide.
- Objective scope comparison with Mineflayer and HeadlessMc based on their
  official project documentation.
- Explicit Microsoft-token storage and outbound-network documentation.

### Changed

- Architecture documentation now states prominently that OeXYZ implements the
  protocol directly and never launches Minecraft.

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
