<p align="center">
  <img src="assets/oexyz-logo.svg" width="112" alt="OeXYZ logo">
</p>

<h1 align="center">OeXYZ Minecraft Console Client</h1>

<p align="center">
  Stay connected. Render nothing.<br>
  A native Windows client for Minecraft Java chat and reliable AFK sessions.
</p>

OeXYZ Console Client is a lightweight **headless Minecraft Java Edition
client**, **Minecraft console client**, and focused **AFK client** for Windows.
It implements the Minecraft protocol directly, uses **no renderer**, supports
**Microsoft authentication**, and connects to servers from Minecraft Java
**1.8 through 26.2** without launching the Minecraft game.

<p align="center">
  <a href="https://github.com/Oexyz/OeXYZ-Minecraft-Console-Client/actions/workflows/ci.yml"><img alt="CI status" src="https://github.com/Oexyz/OeXYZ-Minecraft-Console-Client/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/Oexyz/OeXYZ-Minecraft-Console-Client/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Oexyz/OeXYZ-Minecraft-Console-Client?display_name=tag&sort=semver"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-1389FD?logo=windows">
  <img alt="Minecraft Java 1.8 through 26.2" src="https://img.shields.io/badge/Minecraft%20Java-1.8%E2%80%9326.2-35c46a">
  <img alt="Protocols 47 through 776" src="https://img.shields.io/badge/protocols-47%E2%80%93776-8b5cf6">
  <img alt="Thirty-three deterministic tests" src="https://img.shields.io/badge/tests-33%20deterministic-35c46a">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-2ea44f"></a>
</p>

![OeXYZ connected to a premium Minecraft 26.2 proxy with live public chat](docs/images/v1.2-premium-public-chat.png)

OeXYZ keeps a real Minecraft Java session connected for chat, commands,
reconnect, and AFK use. Everything an end user needs is in two self-contained,
single-file Windows executables: the clickable GUI and optional `oexyz.exe`
headless CLI. No Node.js, Java runtime, Minecraft installation, or separate
.NET installation is needed.

## Download

Download the archive matching your Windows computer from the
[latest GitHub release](https://github.com/Oexyz/OeXYZ-Minecraft-Console-Client/releases/latest), extract it, and open
`OeXYZ Console Client.exe`.

| System | Release asset |
|---|---|
| Most Intel/AMD Windows 10/11 PCs | `OeXYZ-Minecraft-Console-Client-v1.2.0-win-x64.zip` |
| Native Windows on ARM64 | `OeXYZ-Minecraft-Console-Client-v1.2.0-win-arm64.zip` |

Each archive contains exactly `OeXYZ Console Client.exe` for the GUI and
`oexyz.exe` for terminal/headless use, plus documentation. The executables do
not silently fall back to another CPU architecture.

The release also contains `SHA256SUMS`. The in-app updater checks the same
manifest before accepting a downloaded archive. Releases are deliberately
**not Authenticode-signed**. SmartScreen may warn, and Windows 11 with Smart App
Control enabled can block an unsigned build. SHA-256 and GitHub's build
attestation verify archive integrity and workflow provenance, but neither
replaces an Authenticode publisher identity.

### Verify a release

Every release archive receives a GitHub artifact attestation in the pinned
[release workflow](.github/workflows/release.yml). With the
[GitHub CLI](https://cli.github.com/) installed, download and verify the latest
archive without trusting a copied checksum:

```powershell
gh release download --repo Oexyz/OeXYZ-Minecraft-Console-Client `
  --pattern 'OeXYZ-Minecraft-Console-Client-*-win-x64.zip' `
  --pattern 'SHA256SUMS'

$archive = Get-ChildItem 'OeXYZ-Minecraft-Console-Client-*-win-x64.zip' | Select-Object -First 1
$line = Get-Content SHA256SUMS | Where-Object { $_ -like "*$($archive.Name)" }
$expected = ($line -split '\s+')[0]
$actual = (Get-FileHash $archive -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'Release checksum mismatch.' }

gh attestation verify $archive `
  --repo Oexyz/OeXYZ-Minecraft-Console-Client
```

The attestation proves which GitHub repository and workflow produced the ZIP.
The checksum detects corruption or substitution between the manifest and
archive. Neither mechanism is a substitute for reviewing the repository owner
and source code.

## What it can do

- Connect through an independent C# implementation of Minecraft Java protocols
  from **1.8 through 26.2**.
- Detect the server version automatically or use a manually selected version.
- Resolve standard Minecraft DNS SRV records while preserving the original
  handshake host.
- Respect an explicit port in the address and support a separate custom-port
  override for any host, provider, proxy, or self-hosted server.
- Sign in with Microsoft in the default browser, refresh saved sessions, and
  join online-mode servers with encrypted protocol traffic.
- Use a clearly labelled offline-mode name on servers that intentionally allow
  that authentication mode.
- Read live server messages, send chat and commands, and keep a local log.
- See a compact live dashboard with health, hunger, XYZ/look direction, real
  server-reported player ping, uptime, reconnect count, last packet, traffic,
  and packet counters.
- Search and filter chat without losing the stable scroll position; render
  Minecraft colors, bold, italic, underline, and strikethrough; copy or clear
  the view; and recall non-sensitive commands with Up/Down.
- Inspect the live TAB/player list, copy a player name, or prepare (but never
  automatically send) `/msg <player>`.
- See cached, non-blocking online status, latency, version, protocol, player
  count, MOTD, resolved endpoint, and server icon in the profile list.
- Handle keepalive, position, teleport confirmation, player-loaded state,
  compression, secure chat sessions, and the 26.x configuration phase.
- Send `/respawn` through the correct native packet, automatically respawn after
  death, classify disconnects before retrying, reconnect with bounded
  exponential backoff, detect stalled sockets conservatively, and optionally
  send a configurable small look change.
- Continue sessions explicitly in the Windows tray, connect or disconnect all
  profiles from its menu, and opt into local disconnect/reconnect/death/mention
  and private-message notifications.
- Keep the chat view stable under heavy output: incoming lines are queued,
  appended in batches, and do not steal the scroll position while reading
  older messages.
- Show newer server codes of conduct and require a deliberate approval click.
- Organize profiles into session groups, connect/disconnect a group, and opt in
  to restoring sessions that were connected at the previous clean shutdown.
- Add safe one-click quick commands and a bounded, opt-in startup-command list;
  login, registration, and password commands are blocked from automatic use.
- Browse, search, export, and explicitly delete local logs; apply 30/90-day or
  unlimited age retention with a hard 300 MB total cap that removes the oldest
  closed logs; and create a centrally redacted support ZIP.
- Inspect packet timestamp/direction/ID/name/size and local unknown-packet
  counts in an opt-in developer view. Payload/secret dumps are not produced.

## Quick start

### Clickable Windows GUI

1. Extract the release for your architecture and open
   `OeXYZ Console Client.exe`.
2. Add an account, then add a server profile. A host, `host:port`, separate
   custom port, or DNS SRV hostname is accepted.
3. Select both profiles and click **Connect**.

### Headless CLI

The CLI uses the exact same profiles, Microsoft-session store, protocol code,
and session lifecycle as the GUI:

```powershell
.\oexyz.exe list
.\oexyz.exe status survival
.\oexyz.exe run survival
```

To make the short command available in newly opened terminals, run this once
from the extracted release folder:

```powershell
.\oexyz.exe install-path
oexyz run survival
```

`oexyz uninstall-path` reverses only that directory entry. The helper changes
the current user's `PATH`; it installs no service and creates no shell alias.

Available commands are `list`, `profiles`, `status <profile>`,
`run|connect <profile>`, `run-address <host[:port]>`, `connect-all`, and `connect-group <group>`. Use
`--account <name>` when more than one account exists, `--config <path>` for an
explicit profile file, `--log-file <path>` plus `--log-level`, and
`--inspect-packets` for safe metadata-only tracing. Chat and commands come from
stdin; `/quit` or Ctrl+C performs a graceful shutdown.

| Exit code | Meaning |
|---:|---|
| 0 | Success or deliberate shutdown |
| 2 | Profile/config target not found |
| 3 | Authentication failed |
| 4 | Protocol unsupported |
| 5 | Connection failed |
| 6 | Permanent server rejection |
| 64 | Invalid arguments |
| 70 | Internal error |

Microsoft login currently uses Windows DPAPI and the browser flow shared with
the GUI. Remote Linux device-code UX is planned for v1.3 and is not claimed as
available in v1.2.

## Choosing the right headless client

OeXYZ, Mineflayer, and HeadlessMc solve different problems. This is a scope
comparison, not a claim that one project is universally better:

| | OeXYZ | Mineflayer | HeadlessMc |
|---|---|---|---|
| Primary role | Clickable Windows chat and AFK client | Programmable JavaScript bot API | Command-line launcher for the full Minecraft client |
| Runs Minecraft game code | No; implements the protocol directly | No; connects as a bot | Yes; launches Minecraft headlessly |
| End-user runtime | Self-contained Windows EXE | Node.js and a bot project | Java launcher or provided native build, plus game files |
| Automation | Focused chat, commands, reconnect, respawn, anti-AFK | Broad scripting and bot ecosystem | Full-client and mod-driven control |
| Mods | No | Not Minecraft client mods | Fabric, Forge, and NeoForge workflows |
| Best fit | Non-developers who want a lightweight GUI for chat/AFK | Developers building automated agents | CI, mod testing, or full-client behavior without a screen |

See the [full comparison with scope notes and official project
sources](docs/COMPARISON.md). Feature sets change, so verify upstream
documentation before choosing a tool.

## Public offline-mode proof

![OeXYZ connected to Minecraft Anarchy](docs/images/public-anarchy-connected.png)

The screenshot above is a real passive connection to
`play.minecraftanarchy.com` on protocol 776. The server accepted the honest
`OeXYZ` brand, reached play state, supplied a position, acknowledged world
loading, and delivered its registration prompt. The test did not register an
account, send a password, or send chat. The server is independently operated
and is not affiliated with or endorsed by OeXYZ. Details and limitations are in
[the test report](docs/TESTING.md).

## Premium online-mode and proxy proof

![OeXYZ receiving real public chat through a Velocity proxy](docs/images/v1.2-premium-public-chat.png)

This is a real Microsoft-authenticated protocol-776 session on
`mc.purityvanilla.com`. It shows public messages from other players, health,
hunger, position, live TAB count, ping, traffic, packet counters, and an uptime
beyond the proxy's former 60-second timeout. OeXYZ sent no chat or gameplay
automation during the capture. The compatibility fix responds to modern
play-state ping packets with the required pong while keeping the honest `OeXYZ`
brand. The independently operated server is not affiliated with this project.

## Using the app

1. Click **Add** under Accounts.
2. Choose **Microsoft account** for normal authenticated servers. OeXYZ opens
   Microsoft's browser flow when you first connect. Choose **Offline-mode name**
   only when the server owner explicitly supports it.
3. Click **Add** under Servers and enter a hostname or IP address.
4. Leave **Custom port** at `0` for SRV lookup followed by port `25565`, or enter
   the exact custom port. `host:port` is also accepted in the address field.
5. Leave the version on **auto** unless a proxy hides or misreports it.
6. Select an account and server, then click **Connect**.
7. Type chat, `/commands`, or `/respawn` in the field at the bottom. All other
   controls are clickable; a command window is never required.

Useful session controls:

- `Ctrl+F` focuses search; `Ctrl+L` clears the visible chat.
- Up/Down navigates the per-session command history. Login/register/password
  commands are deliberately excluded from that history.
- **Players** toggles the live TAB list. Double-clicking a player only prepares
  `/msg`; it does not send anything.
- **Settings** controls tray behavior and local notifications. Closing continues
  in the tray only when that option is explicitly enabled; tray **Exit** always
  shuts sessions down cleanly.
- Set an optional **Session group** in each server profile. Use **Groups**, the
  server context menu, the tray menu, or `oexyz connect-group <group>`.
- **Restore previous sessions** is a manual button by default. Automatic restore
  remains disabled until it is explicitly enabled in Settings.
- Configure up to 12 quick commands or 8 delayed startup commands per profile.
  Startup execution is disabled by default and sensitive authentication
  commands are never permitted to run automatically.
- **Logs** opens the searchable viewer. **More** inside a session exposes the
  sanitized support-package export and opt-in protocol inspector.

<details>
<summary>Welcome screen</summary>

![OeXYZ welcome screen](docs/images/client-welcome.png)

</details>

## Why the project is auditable

The runtime is intentionally small and transparent:

| Component | Used for | Runs on an end-user machine |
|---|---|---|
| OeXYZ C# source | UI, protocol, networking, sessions, updater | Yes |
| .NET 10 Windows runtime | Self-contained native Windows application host | Yes, bundled in the release |
| CmlLib.Core.Auth.Microsoft 3.3.1 | Microsoft/Xbox/Minecraft browser authentication | Yes |
| Windows DPAPI | Encrypting saved account sessions for the current user | Yes, built into Windows |
| Embedded Inter variable fonts | Consistent DPI-aware GUI typography | Yes, loaded locally under the SIL Open Font License |
| PrismarineJS `minecraft-data` 3.113.0 | Generating the committed packet-ID and English translation catalogs | No, build time only |

OeXYZ contains no Minecraft game executable, game assets, server JAR, hidden
launcher, advertising SDK, telemetry SDK, or remotely loaded plugin system.
The protocol code is in [`src/OeXYZ.Protocol`](src/OeXYZ.Protocol), shared
session lifecycle in [`src/OeXYZ.Session`](src/OeXYZ.Session), profiles and
policies in [`src/OeXYZ.Core`](src/OeXYZ.Core), authentication in
[`src/OeXYZ.Authentication`](src/OeXYZ.Authentication), the CLI in
[`src/OeXYZ.Cli`](src/OeXYZ.Cli), verified updating in
[`src/OeXYZ.Updater`](src/OeXYZ.Updater), and the Windows UI in
[`src/OeXYZ.ConsoleClient`](src/OeXYZ.ConsoleClient).

See also:

- [Architecture](docs/ARCHITECTURE.md)
- [Headless CLI reference](docs/CLI.md)
- [Release and updater integrity](docs/UPDATER.md)
- [Objective comparison](docs/COMPARISON.md)
- [Security and privacy](docs/SECURITY_AND_PRIVACY.md)
- [Testing evidence](docs/TESTING.md)
- [Screenshots](docs/SCREENSHOTS.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [Security policy](SECURITY.md)

## Privacy and accounts

Microsoft passwords never pass through OeXYZ. Refreshable account-session data
is written to `%LOCALAPPDATA%\OeXYZ\ConsoleClient\accounts.bin` after encryption
with Windows DPAPI for the current Windows user. Profiles and logs remain in the
same local application folder. There is no telemetry, analytics, advertising,
or background update polling.

The only network destinations are the selected Minecraft server, DNS for SRV
discovery, Microsoft/Xbox/Minecraft authentication endpoints when signing in,
and GitHub when the user clicks **Check for updates**.

## Build from source

End users should use the self-contained release. Contributors need the .NET 10
SDK. Node.js is only needed to regenerate the packet catalog after changing the
pinned `minecraft-data` version.

```powershell
dotnet restore OeXYZ.ConsoleClient.slnx --locked-mode
dotnet build OeXYZ.ConsoleClient.slnx -c Release --no-restore
dotnet run --project tests/OeXYZ.Core.Tests -c Release --no-build
dotnet run --project tests/OeXYZ.Protocol.Tests -c Release --no-build
dotnet run --project tests/OeXYZ.ConsoleClient.Tests -c Release --no-build
dotnet run --project tests/OeXYZ.Session.Tests -c Release --no-build
dotnet publish src/OeXYZ.ConsoleClient -c Release -r win-x64 --self-contained true
dotnet publish src/OeXYZ.Cli -c Release -r win-x64 --self-contained true
dotnet publish src/OeXYZ.ConsoleClient -c Release -r win-arm64 --self-contained true
dotnet publish src/OeXYZ.Cli -c Release -r win-arm64 --self-contained true
```

To regenerate protocol mappings:

```powershell
npm ci
npm run generate:protocol
```

The generated catalogs currently contain 74 release mappings from protocol 47
through protocol 776 and 7,886 English translation entries. Release automation
verifies that regenerating either file produces no uncommitted difference.

## Roadmap

### v1.1 — Session experience ✅ complete

Live dashboard and metrics, intelligent reconnect, stale-connection monitoring,
tray mode, local notifications, searchable/formatted chat, command history,
live player list, cached server overview, custom-port stability, and Per-Monitor
V2 DPI scaling.

### v1.2 — Headless and reliability ✅ complete

Shared GUI/CLI session core, `oexyz` headless commands and reversible PATH
setup, session groups/restore, configurable Anti-AFK, quick/startup commands,
log viewer, redacted diagnostics, protocol inspector, fuzz/replay coverage,
Windows x64/ARM64 releases, and an architecture-aware rollback updater.

### v1.3 — Linux, Docker, and Raspberry Pi 📋 planned

Planned—not currently claimed as supported:

- Linux x64 and Linux ARM64 headless builds
- Raspberry Pi 4/5, Raspberry Pi OS 64-bit, Debian, and Ubuntu ARM64
- `systemd` service and graceful SIGTERM shutdown
- AMD64/ARM64 minimal Docker image running as a non-root user
- Docker Compose for resource-conscious multi-session 24/7 AFK use
- persistent config and log volumes, log rotation, restart guidance, and a
  local health check
- no credentials baked into images; a deliberate remote device-code/OAuth UX

The protocol, profile, reconnect, diagnostics, and session projects are already
free of WinForms. The CLI remains `net10.0`, avoids busy waiting, uses
`CancellationToken` and async I/O, and reads an explicit `--config` file or the
non-secret `OEXYZ_CONFIG` path override. v1.3 still needs a secure cross-platform
Microsoft token store and release qualification before Linux artifacts can be
advertised.

## Responsible use

Server owners decide whether AFK sessions, automated movement, alternate
clients, or offline-mode accounts are permitted. Check the server's current
rules and disable features it does not allow. OeXYZ does not implement CAPTCHA
bypasses, anti-bot evasion, ban evasion, brand impersonation, spam, or automatic
account registration.

The final v1.2 passive compatibility check remained connected through a modern
Velocity proxy beyond its keepalive timeout with the honest `OeXYZ` brand and
without sending chat or gameplay automation. That compatibility result is not
a promise that every server permits AFK clients: rules and enforcement can
change, and OeXYZ will never disguise itself to bypass them.

Minecraft is a trademark of Microsoft Corporation. This project is independent
and is not affiliated with, endorsed by, or approved by Microsoft or Mojang
Studios.

## License

OeXYZ source code is available under the [MIT License](LICENSE). Third-party
components retain their own licenses as listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
