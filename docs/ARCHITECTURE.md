# Architecture

OeXYZ implements the Minecraft Java protocol directly. It does not launch
Minecraft, a client JAR, LWJGL, a renderer, a mod loader, or game assets. GUI
and CLI are frontends over one session implementation:

```text
OeXYZ.ConsoleClient (Windows WinForms GUI: DPI, tray, notifications)
└── OeXYZ.Session ──────────────┐
                               ├── OeXYZ.Protocol
OeXYZ.Cli (headless stdin/out) ─┘
       │
       ├── OeXYZ.Authentication (Microsoft browser auth + Windows DPAPI adapter)
       ├── OeXYZ.Core (profiles, policies, migration, paths, redaction)
       └── OeXYZ.Updater (GitHub Releases, SHA-256, staging, rollback)
```

## Platform boundaries

`OeXYZ.Protocol`, `OeXYZ.Core`, and `OeXYZ.Session` target plain `net10.0` and
have no WinForms dependency. They own packet I/O, profiles, reconnect policy,
metrics, logging, diagnostics, Anti-AFK timing, startup commands, and the
complete session lifecycle. `OeXYZ.Cli` also targets plain `net10.0` and uses
async stdin/stdout plus Ctrl+C cancellation.

Only `OeXYZ.ConsoleClient` targets `net10.0-windows`; tray mode, balloon
notifications, Per-Monitor V2 DPI handling, dialogs, and UI controls stay
there. Its regular and italic Inter variable fonts are embedded resources,
loaded into a process-private font collection, and scaled through WinForms
rather than installed system-wide. `OeXYZ.Authentication` keeps the current DPAPI account-storage adapter
separate. Offline profiles are already platform-neutral; secure Microsoft
device-code storage on Linux is explicitly future v1.3 work.

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

The GUI applies age retention at startup and once per minute, followed by a
hard 300 MB aggregate cap. Selection is deterministic by last-write time; the
oldest closed logs are removed first and active session paths are protected.

Protocol callbacks update immutable snapshots. WinForms drains queued chat in
bounded batches and never performs network work on the UI thread. The CLI
writes the same session events to stdout/stderr and reads user commands from
stdin. Startup commands are opt-in, bounded to eight, delayed, non-repeating,
and reject authentication/registration/password commands.

## Authentication and secrets

`CmlLib.Core.Auth.Microsoft` performs the supported browser flow. OeXYZ never
handles a Microsoft password. The resulting Minecraft token is used only for
the Mojang session join and secure-chat certificate requests. Refreshable
account sessions are one Windows DPAPI `CurrentUser` payload in `accounts.bin`.

All logs, CLI errors, crash output, and support packages use the central
`SensitiveDataRedactor`. Support ZIPs are allowlist-built and never copy
`accounts.bin`, tokens, passwords, complete profile JSON, or full private chat
logs.

## Profiles and migration

The format-3 profile document preserves unknown older fields through
`JsonExtensionData`. New fields receive conservative defaults; invalid/stale
session bookmarks are dropped. Saving is temporary-file then replace, with a
`.bak` copy of the previous profile. `--config` and `OEXYZ_CONFIG` override only
the profile path, which prepares non-secret configuration mounting for v1.3.

## Update trust boundary

Update checks are explicit. The updater selects an asset matching the running
x64 or ARM64 process, downloads the ZIP and manifest over HTTPS, applies the
published SHA-256 in fixed time, rejects overlarge or path-traversal entries,
and stages both executables. After confirmation a temporary copy of the current
GUI waits for shutdown, backs up installed executables, replaces them through
temporary files, rolls back completed replacements on failure, and restarts.

Updates are never silent. Releases are not Authenticode-signed. The GitHub
workflow separately creates provenance attestations; users verify those with
the GitHub CLI because the running app does not treat a local `gh` installation
as a required runtime dependency.
