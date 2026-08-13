# Architecture

OeXYZ implements the Minecraft Java protocol directly and does not launch
Minecraft. It is split into small components so the UI, protocol
implementation, and test tooling remain auditable.

```text
OeXYZ.ConsoleClient (WinForms UI)
├── profiles and encrypted account-session storage
├── session lifecycle, reconnect, anti-AFK and local logs
├── Microsoft browser-authentication adapter
├── OeXYZ.Core
│   ├── versioned profiles and backward-compatible migration
│   ├── reconnect classification and bounded backoff
│   └── non-sensitive command history
├── OeXYZ.Updater
│   └── GitHub release lookup, bounded download and SHA-256 verification
└── OeXYZ.Protocol
    ├── address parsing, DNS SRV and status discovery
    ├── packet framing, compression and AES/CFB8 encryption
    ├── login, configuration and play-state handlers
    ├── Microsoft session join and secure-chat signatures
    └── generated version-specific packet-ID catalog
```

## `OeXYZ.Protocol`

This library implements the network path independently in C#. It opens the TCP
connection, writes the Minecraft handshake, negotiates compression and online
mode encryption, progresses through configuration, and handles the small set of
play packets needed by a renderer-free session.

No Minecraft executable, client JAR, LWJGL renderer, game assets, mod loader, or
game installation is loaded by this path.

The committed `protocol-catalog.json` contains packet IDs, not executable code
or game assets. The maintainer script derives it from the pinned MIT-licensed
`minecraft-data` package. Runtime builds do not execute JavaScript.

## Authentication boundary

`CmlLib.Core.Auth.Microsoft` performs Microsoft's supported browser flow and
returns a Minecraft access session. OeXYZ never receives a Microsoft password.
The protocol library uses the resulting Minecraft token only for the official
session-server join request and secure-chat certificate request.

The library can also construct an offline-mode identity. This path performs no
authentication and must only be used on servers that intentionally support it.

## UI and concurrency

Each tab owns one `ConsoleSession`. Network callbacks enqueue immutable lines
in a concurrent queue. A 100 ms UI timer drains bounded batches into the chat
view with redraw suspended. If the user has scrolled up, the current selection
and first visible line are restored; otherwise the view follows new messages.
The view keeps 5,000 lines and trims 1,000 at a time.

Network work never runs on the UI thread. Protocol callbacks update immutable
session snapshots; the GUI samples those snapshots without blocking packet
processing. Disconnecting cancels connection, monitor, anti-AFK, retry, and
authentication operations through linked cancellation tokens. The reconnect
loop owns each connection with `await using`, so failed attempts do not retain
sockets, timers, or packet handlers.

`OeXYZ.Core` is UI-neutral. In v1.1 it owns profiles, migration, reconnect
policy, and command history. The remaining session orchestration is being moved
behind that boundary for the v1.2 CLI; `OeXYZ.Core` and `OeXYZ.Protocol` have no
WinForms dependency.

## Update trust boundary

The release workflow injects its own GitHub repository URL as assembly
metadata. The app queries only GitHub's HTTPS latest-release API. It looks for
the exact Windows archive name and `SHA256SUMS`, downloads to a temporary file,
compares hashes using a fixed-time comparison, closes the temporary archive,
and only then moves it to the user-selected path. It never launches the
downloaded program. The updater is a separate library with deterministic tests
for version comparison, successful verification, Windows file lifetime, and
checksum rejection.
