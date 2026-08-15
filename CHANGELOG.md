# Changelog

All notable changes follow a simplified form of Keep a Changelog. Version tags
use semantic versioning.

## [Unreleased]

## [1.3.0] - 2026-08-15

### Added

- Self-contained single-file `linux-x64` and `linux-arm64` headless builds with
  XDG paths, reversible `~/.local/bin` registration, profile/account/server
  creation commands, JSON output, and an ANSI live dashboard.
- Linux Microsoft Live device-code sign-in with one AES-256-GCM protected
  account/session file, PBKDF2-SHA256 key derivation, private permissions,
  explicit account-key generation, and no password handling.
- Multi-session `supervise`, loopback `/health`, `/ready`, and `/status`,
  SIGTERM shutdown, a local `doctor`, and systemd readiness/watchdog support.
- Guided `oexyz setup` for streamlined Docker onboarding, repeatable
  multi-account configuration, and persistent account/server session bindings
  that `supervise` starts without environment-variable plumbing.
- Pinned non-root AMD64/ARM64 Docker build, read-only Compose service, and
  separate persistent config/state/key named volumes.
- A per-user POSIX Linux installer with architecture detection, mandatory
  SHA-256 verification, safe TAR validation, atomic replacement, optional
  GitHub attestation verification, and optional systemd-unit installation.
- Portable Linux DNS SRV resolution with bounded parsing, resolver timeouts,
  compression-pointer loop protection, and deterministic fixtures.
- CycloneDX 1.6 SBOM generation plus Linux/ARM64/container release workflow
  artifacts and OCI provenance/SBOM publication.

### Changed

- Active session logs rotate at 16 MiB, CLI logs at 32 MiB, and all closed log
  parts remain subject to the existing deterministic 300 MiB aggregate cap.
- Profile documents are limited to 2 MiB/depth 64; offline names are validated
  before networking; service session counts default to 16 and are hard-capped
  at 128.
- Runtime snapshots now expose bounded CPU/RAM, connectivity, HP/Food, XYZ,
  ping, reconnect, packet, byte, and last-activity metrics without account IDs
  or server addresses.
- Runtime ping starts with the measured status-handshake RTT and switches to a
  positive server-supplied player latency when available, preventing proxies
  that publish a placeholder `0 ms` value from blanking a real measurement.
- The ANSI dashboard now uses the full terminal height (instead of a fixed
  ten-line history), keeps up to 500 recent local events, preserves complete
  borders across resize, and redraws only changed padded rows to avoid flicker.
- On Linux, `--config` now relocates only profiles/account storage while logs
  and diagnostics continue to honor `XDG_STATE_HOME`, so Docker's separate
  `/config` and `/state` volumes are used as documented.
- Linux device authentication now pairs the Minecraft Java Live client ID with
  Microsoft's Live device endpoint instead of the incompatible Entra/MSAL
  endpoint. This fixes `AADSTS700016` while retaining the Windows browser,
  Xbox, Minecraft-profile, and encrypted session pipeline.
- Native Ubuntu hardware over SSH passed Microsoft session refresh, encrypted
  online-mode Play, live public-chat receive, runtime metrics, and clean local
  shutdown on a populated protocol-776 server without sending chat or commands.
- Pull-only Compose now always refreshes the public GHCR `latest` image, while
  an explicit no-pull override retains direct repository builds without silent
  fallback. Release publication verifies the versioned manifest, pulls both
  AMD64 and ARM64 images anonymously, and uses a fully queued, serialized job
  to promote only the newest stable release to `latest` after its GitHub
  release exists.
- CI and Release multi-platform builds provision a commit-pinned Buildx action
  with an explicit `docker-container` driver instead of relying on the default
  Docker builder.

### Fixed

- Closed all nine P1 and nine P2 findings from the v1.3 repository audit. The
  complete fix/test/platform matrix is recorded in the
  [v1.3 bugfix report](docs/V1.3_BUGFIX_REPORT.md).
- Hardened concurrent profile and Microsoft account transactions against stale
  reloads, shared temporary names, duplicate first login, partial writes and
  cross-process lost updates.
- Bounded and sanitized server-controlled status, NBT chat, session/log,
  diagnostics and GUI queues; secrets and terminal controls are removed before
  persistent or interactive sinks.
- Made GUI save/auth/shutdown failure paths rollback-safe, corrected
  import-name and explicit-port boundaries, and made custom systemd installs
  and installer temporary files safe for literal paths.

### Security

- Linux account/key/profile files use `0600`, directories use `0700`, the
  account/session envelope uses an independent random salt and authenticated
  encryption, and decrypted/key buffers are cleared after use.
- Device codes bypass ordinary logs; Docker images contain no user profile,
  token, account file, or key and run as UID/GID 1654 without capabilities.
- Docker build contexts exclude ignored local account, session, profile, key,
  runtime, and log files before any source is sent to a local or remote builder.
- Linux account-store unlocking now completes before the dashboard starts its
  keyboard reader, preventing competing console readers during hidden
  passphrase entry; the temporary character buffer is explicitly cleared.

## [1.2.1] - 2026-08-14

### Security

- Official Release builds now pin update checks to
  `Oexyz/OeXYZ-Minecraft-Console-Client` and ignore the
  `OEXYZ_UPDATE_REPOSITORY` process environment variable. Repository overrides
  remain available only in Debug builds for maintainers testing forks.
- Added deterministic regression coverage proving a forged repository override
  cannot change the trusted Release update source.

## [1.2.0] - 2026-08-14

### Added

- Native `oexyz.exe` headless frontend with list/status/run/group commands,
  stdin chat, Ctrl+C shutdown, documented exit codes, optional sanitized file
  logging, explicit config paths, and reversible user-PATH setup.
- Shared platform-neutral session project used by both GUI and CLI, plus a
  separate authentication adapter and cross-platform-ready application paths.
- Session groups, opt-in session restore, profile quick commands, bounded and
  opt-in startup commands, searchable log viewer, retention policy, sanitized
  support packages, metadata-only packet inspector, and unknown-packet counts.
- Anonymized protocol replays for 1.8.8 through 26.2 and malformed-input tests
  for framing, UTF-8, NBT, compression, encrypted EOF, fragmentation,
  unexpected packets, duplicates, and invalid states.
- Native self-contained, single-file GUI and CLI release builds for Windows x64
  and Windows ARM64, with a generated protocol-compatibility CI report.
- Locally embedded Inter variable fonts for consistent GUI typography without
  installing fonts on the user's system.
- A deterministic 300 MB total log cap that deletes oldest closed logs while
  protecting files still owned by active sessions.
- Session log queues are bounded to prevent unbounded memory growth if storage
  stalls during an extreme message burst.

### Changed

- Profile format 3 keeps previous unknown fields while adding inspector and
  restore preferences; every migration/save preserves a backup.
- The updater chooses an architecture-matched release, verifies SHA-256, bounds
  extraction, rejects ZIP traversal, stages both executables, retains rollback
  copies, and restarts only after explicit confirmation.
- Strict UTF-8 decoding and bounded incremental decompression harden untrusted
  network input; packet tracing incurs no unknown-statistics work unless opted in.
- Minecraft AES-CFB8 now uses an immediate-write stateful stream instead of a
  block-buffering generic `CryptoStream`, preventing short encrypted login and
  Configuration packets from waiting until the server timeout.
- Modern NBT chat now supports Java modified UTF-8/CESU-8, proxy-generated
  unnamed text components, styled runs and literal translation patterns.
- The embedded English Minecraft catalog resolves entity, death, advancement
  and command keys instead of displaying raw values such as
  `entity.minecraft.slime`.
- Windows-native dark controls, menus, scrollbars, and branded message dialogs
  now use the embedded Inter family consistently at DPI-scaled sizes.

### Fixed

- Modern play-state `ping` packets receive the required integer `pong`, keeping
  Velocity sessions alive beyond their previous roughly 60-second timeout.
- Session actions remain fully visible after resizing and DPI scaling; real UI
  automation verifies Respawn, Disconnect, Log, More, and Close.
- The Log Viewer reads logs that an active session still has open and preserves
  its usable split layout at non-default DPI.
- Settings, profile editors, Accounts/Servers lists, context menus, Player List,
  and protocol-inspector layouts no longer expose white gaps or clipped rows.
- `/respawn` and `/disconnect` are handled as local session actions in the
  headless CLI instead of being forwarded as unknown Minecraft commands.

### Security

- Central redaction now covers bearer/JSON/key-value credentials and full
  login/register/password command lines in GUI logs, CLI logs, crash output,
  and support packages.
- Sensitive authentication commands are blocked from startup automation and
  are never included in command history.

## [1.1.1] - 2026-08-13

### Fixed

- Pinned GitHub Actions to the repository's .NET SDK 10.0.302 so locked
  single-file publish dependencies remain reproducible instead of rolling to a
  newer feature band during release packaging.

## [1.1.0] - 2026-08-13

### Added

- Compact session dashboard for version/protocol, health, hunger, XYZ, look
  direction, ping, uptime, reconnects, packet activity, bytes and packet counts.
- Live TAB player list with copy-name and prepare-message actions.
- Searchable/filterable Minecraft-formatted chat and per-session command history
  that excludes recognized login/register/password commands.
- Cached asynchronous server overview with status latency, protocol, player
  counts, MOTD, resolved endpoint and validated server icons.
- Explicit Windows tray mode and configurable local notifications.
- Versioned profile migration through a platform-neutral `OeXYZ.Core` project.

### Changed

- Reconnect now classifies user, permanent and transient disconnects, applies a
  bounded exponential backoff, exposes the next attempt, honors an optional
  attempt limit and resets after a stable connection.
- Anti-AFK interval and look angle are profile settings rather than hard-coded.
- Session logs are written asynchronously and include filter categories.
- The GUI declares Per-Monitor V2 DPI awareness.

### Fixed

- Stalled sockets are conservatively detected from real packet activity.
- Current 26.2 player-info action flags are decoded as the protocol's one-byte
  bit field, including hat/list-order updates.
- Session toolbar buttons remain visible at the supported minimum window size;
  automated UI tests confirm Disconnect stops a session and Close removes its
  tab without closing the app.
- Chat stays anchored only when already following the newest line, avoiding the
  full-buffer jump reported in earlier builds.

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
