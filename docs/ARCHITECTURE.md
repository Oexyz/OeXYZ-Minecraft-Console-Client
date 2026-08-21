# Architecture

OeXYZ implements the Minecraft Java protocol directly. It does not launch
Minecraft, a client JAR, LWJGL, a renderer, a mod loader, or game assets. GUI
and CLI are frontends over one session implementation:

```text
OeXYZ.ConsoleClient (Windows WinForms GUI: DPI, tray, notifications)
└── OeXYZ.Session ──────────────┐
                               ├── OeXYZ.Protocol
OeXYZ.Cli (Windows/Linux headless, dashboard, supervisor) ─┘
       │
       ├── OeXYZ.Authentication (browser/device auth + platform storage)
       ├── OeXYZ.Core (profiles, policies, migration, paths, redaction)
       └── OeXYZ.Updater (GitHub Releases, SHA-256, staging, rollback)
```

## Platform boundaries

`OeXYZ.Protocol`, `OeXYZ.Core`, and `OeXYZ.Session` target plain `net10.0` and
have no WinForms dependency. They own packet I/O, profiles, reconnect policy,
metrics, logging, diagnostics, Anti-AFK timing, startup commands, and the
complete session lifecycle. `OeXYZ.Cli` also targets plain `net10.0` and uses
async stdin/stdout, Ctrl+C/SIGTERM cancellation, an optional ANSI dashboard, a
loopback health server, and systemd readiness/watchdog notifications.
Self-contained single-file builds target `win-x64`, `win-arm64`, `linux-x64`,
and `linux-arm64`.

Only `OeXYZ.ConsoleClient` targets `net10.0-windows`; tray mode, balloon
notifications, Per-Monitor V2 DPI handling, dialogs, and UI controls stay
there. Linux intentionally has no WinForms or desktop dependency. The Windows
GUI's regular and italic Inter variable fonts are embedded resources, loaded
into a process-private font collection, and scaled through WinForms rather than
installed system-wide. `OeXYZ.Authentication` keeps platform storage separate:
Windows uses DPAPI `CurrentUser`; Linux uses the terminal device-code flow, an
AES-256-GCM account/session document, and no desktop or browser dependency.

## Protocol and untrusted data

`OeXYZ.Protocol` opens TCP, writes the Minecraft handshake, resolves DNS SRV,
negotiates compression and AES/CFB8 online-mode encryption, progresses through
login/configuration/play, and handles only the packets needed by a
renderer-free chat/AFK session. The committed generated catalogs contain packet
IDs and English translation strings—not executable code or game assets. The
translations resolve protocol keys such as `entity.minecraft.slime` locally.
JavaScript is used only at build time to regenerate both catalogs from the
pinned MIT-licensed `minecraft-data` package.

Network-controlled allocations are bounded: frames and decompressed packets are
limited to 2 MiB, strings and collections have contextual limits, NBT depth is
limited to 64, and invalid UTF-8 is rejected. Decompression stops the moment
output exceeds the declared size. Tests exercise oversized/negative/truncated
frames, five-byte continuation VarInts, one-byte TCP fragmentation, malformed
UTF-8/JSON/NBT/compression/encryption, unexpected states, duplicates, and
abrupt EOF.

The generated catalog also carries explicit Resource Pack request and response
layouts. The generator derives their field signatures from the pinned schema
and fails if any supported request cannot be classified. Runtime parsing never
guesses a version boundary: legacy URL/hash, forced/prompt, and UUID layouts are
decoded through the capability, bounded, declined through a typed status, and
never downloaded.

The opt-in protocol inspector receives immutable packet metadata only:
timestamp, direction, state, ID, known name, payload length, and wire length.
It does not expose raw payloads or decoded authentication fields. Unknown packet
counts stay local and can be included in a sanitized support package.

## Session lifecycle and concurrency

Each GUI tab or CLI target owns one `OeXYZ.Session.ConsoleSession`. It owns one
connection attempt at a time with `await using`, linked cancellation, a bounded
reconnect delay, a `PeriodicTimer` stale monitor, optional Anti-AFK timer, and
an asynchronous 8,192-entry bounded channel-backed log writer. Under an
extreme disk stall the oldest queued log event is discarded rather than
allowing unbounded RAM growth. No reconnect attempt retains a
socket or handler after disposal. Ctrl+C, GUI Disconnect, tab Close, and process
exit all converge on the same cancellation path.

Connection setup uses independent deadlines: TCP connect 15 seconds, login 45
seconds, Configuration 60 seconds, and a code-of-conduct decision 120 seconds.
The conduct prompt runs outside the receive loop, so Configuration keepalives
and pings continue while the UI decides. A pending finish-Configuration packet
is represented by one bounded flag rather than buffered packets.

All connection writes pass through one bounded `OutboundPacketDispatcher`.
Critical protocol controls and normal user traffic have separate 128-entry
queues; one writer builds payloads and writes them in wire order, with an
eight-packet critical burst limit to prevent starvation. Thus secure-chat
indexes are assigned at serialization time rather than by competing callers.

Protocol callbacks use a bounded event dispatcher. Subscriber invocation is
isolated per delegate and never runs on the receive/write path. Normal floods
are dropped with counters while critical state has a reserved queue. Metrics
are coalesced to one update every 200 ms, with immediate state/final snapshots.
Drop and subscriber-failure counters flow through session/runtime snapshots,
the health status, support package, CLI dashboard, and GUI inspector.

Both frontends apply age retention at startup and once per minute, followed by
a hard 300 MB aggregate cap. Active session logs rotate at 16 MiB and CLI log
files at 32 MiB. Selection is deterministic by last-write time; the oldest
closed parts are removed first and active session paths are protected.

Protocol callbacks update immutable snapshots. WinForms drains queued chat in
bounded batches and never performs network work on the UI thread. The CLI
writes the same session events to stdout/stderr and reads user commands from
stdin. Startup commands are opt-in, bounded to eight, delayed, non-repeating,
and reject authentication/registration/password commands.

## Authentication and secrets

`CmlLib.Core.Auth.Microsoft` performs the Windows browser flow. Linux uses a
bounded OeXYZ adapter for Microsoft's Live device-code endpoints and then joins
the same CmlLib/Xbox/Minecraft authentication pipeline.
OeXYZ never handles a Microsoft password. A device user code is sent only to a
terminal presenter and is deliberately excluded from ordinary session and file
logs. The resulting Minecraft token is used only for Mojang session join and
secure-chat certificate requests.

The first user-started connection permits interaction. Every automatic
Microsoft reconnect is `SilentOnly`: it refreshes the protected session and
secure-chat certificate under the existing authentication lock, swaps identity
state only after success, and never opens a browser or device-code flow. Offline
identity is constructed once per session lifecycle.

On Windows, refreshable sessions are one DPAPI `CurrentUser` payload in
`accounts.bin`. On Linux, a user-controlled passphrase/key is processed with
PBKDF2-SHA256 (600,000 iterations and a random salt) and AES-256-GCM
authenticated encryption. `accounts.bin` uses atomic replacement, bounded
payloads, and `0600` file permissions. Key material and decrypted buffers are
zeroed when no longer needed. The key itself is never accepted through a
general environment variable.

All logs, CLI errors, crash output, and support packages use the central
`SensitiveDataRedactor`. Support ZIPs are allowlist-built and never copy
`accounts.bin`, account-key files, tokens, passwords,
complete profile JSON, or full private chat logs.

## Profiles and migration

The format-3 profile document preserves unknown older fields through
`JsonExtensionData`. New fields receive conservative defaults; invalid/stale
session bookmarks are dropped. Saving is temporary-file then replace, with a
`.bak` copy of the previous profile. Profile input is capped at 2 MiB and JSON
depth 64. `--config` and `OEXYZ_CONFIG` override only the non-secret profile
path. Linux defaults follow XDG config/state roots; Docker maps separate
config, state, and key volumes.

Profile loading validates the primary and `.bak` independently. A corrupt or
missing primary with a valid backup produces a typed recovery state; it is never
restored silently. GUI confirmation or `oexyz profiles-recover` preserves the
original as a unique `.corrupt-*` file and atomically restores the validated
backup while holding the same interprocess lock.

## Service and container boundary

`supervise` owns multiple shared-core sessions but enforces a configurable
maximum (16 by default, 128 hard maximum). Profile schema 4 stores normalized
managed-session bindings as account/server UUID pairs. The guided setup can
therefore assign multiple accounts to one server or one account to multiple
servers without duplicating protocol or session state. An explicit account
option remains a deliberate override for legacy and scripted operation.
Runtime snapshots feed the terminal
dashboard and loopback-only `/health`, `/ready`, and `/status` endpoints; those
responses omit account IDs and server addresses. The systemd adapter uses
`NOTIFY_SOCKET` for `READY=1`, watchdog heartbeats, and `STOPPING=1`.

The Docker image cross-publishes the CLI for the target architecture, then
copies one binary into a pinned chiseled runtime-deps image. It runs as UID/GID
1654 with no shell, no capabilities, a read-only root, and writable named
volumes only at `/config`, `/state`, and `/keys`. The image contains no profile,
token, password, or account key.

## Local management, proxy, failover, and automation

`SessionControlManager` serializes management actions over stable opaque
account/server IDs. The hardened management listener keeps existing health
routes, adds Prometheus metrics and token-authenticated `/v1` actions, and
enforces bounded headers/bodies, timeouts, concurrency and write rate limits.

Connection creation is abstracted behind `IConnectionDialer`. Direct, SOCKS5,
and HTTP CONNECT implementations preserve the original handshake host while
applying explicit local/proxy DNS policy. Proxy credentials live only in the
separate `secrets.bin` DPAPI/AES-GCM boundary. Profile format 5 adds proxy
references, up to eight failover endpoints, 32 bounded automation rules,
configurable mention/PM patterns, and opt-in transfer policy.

Automation supports only enumerated Minecraft/session/local-notification
actions. It has per-rule cooldown/hour limits, a global action budget, bounded
inputs, and non-backtracking timed regex. Cookies remain connection-memory-only;
transfer chains are validated and limited before reconnect policy can follow
them.

## Update trust boundary

Update checks are explicit. The updater selects an asset matching the running
x64 or ARM64 process, downloads the ZIP and manifest over HTTPS, applies the
published SHA-256 in fixed time, rejects overlarge or path-traversal entries,
and stages both executables. After confirmation a temporary copy of the current
GUI waits for shutdown, backs up installed executables, replaces them through
temporary files, rolls back completed replacements on failure, and restarts.

Each installation attempt owns a unique backup directory and records whether
each frontend existed before the transaction. Rollback restores that exact
backup or deletes a newly introduced frontend, aggregates rollback failures,
removes transaction-scoped temporary files, and rejects reparse-point paths.

Updates are never silent. Releases are not Authenticode-signed. The GitHub
workflow separately creates provenance attestations; users verify those with
the GitHub CLI because the running app does not treat a local `gh` installation
as a required runtime dependency.
