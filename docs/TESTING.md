# Testing

Last verified: **2026-08-15** on Windows 11 x64 with .NET SDK 10.0.302, WSL2,
Docker AMD64, native Ubuntu 26.04 x64, and native Ubuntu 24.04 ARM64.

## Deterministic tests

Six dependency-free test executables validate profiles/reconnect/CLI policy,
protocol parsing and replay, support-package sanitization, authentication
storage, terminal dashboard layout/rendering, and the updater. No
normal test depends on an external Minecraft server:

```powershell
dotnet run --project tests/OeXYZ.Core.Tests -c Release
dotnet run --project tests/OeXYZ.Protocol.Tests -c Release
dotnet run --project tests/OeXYZ.ConsoleClient.Tests -c Release
dotnet run --project tests/OeXYZ.Session.Tests -c Release
dotnet run --project tests/OeXYZ.Authentication.Tests -c Release
dotnet run --project tests/OeXYZ.Cli.Tests -c Release
```

Expected results: `PASS: 22 core tests`, `PASS: 20 protocol tests`, `PASS: 10
updater tests` plus four GUI behavior tests, nine session checks, thirteen
authentication checks, and eleven CLI checks: **89 deterministic .NET checks**
total. The separate `tests/install-systemd.sh` regression adds **one shell
installer test** (**90 checks overall**).

The authentication suite specifically proves that the Minecraft Java public
client ID is sent to the Microsoft Live device/token endpoints rather than the
incompatible Entra/MSAL endpoint. It also covers pending and `slow_down`
polling, hostile verification URLs, oversized responses, authenticated account
storage, wrong keys, tampering, private key creation, one-time Linux store
initialization, cancellation-safe interprocess locking, and parallel first
login deduplication through durable profile-to-account bindings.

The dashboard regression suite verifies that terminal height controls the
visible history (more than ten rows at 120×30 and at least 35 at 156×47), the
lower frame remains closed after resizing, sensitive input stays redacted, and
steady-state refreshes overwrite only changed padded rows without a full-screen
or per-row clear. This prevents the previous ten-line cap and visible flicker.

The hardening suite covers invalid and overlong VarInts; negative, zero,
oversized, truncated, and fragmented frames; a transport returning one byte per
read; invalid UTF-8 and JSON; malformed/deep and allocation-amplifying NBT;
invalid UUIDs; malformed and over-expanding zlib data; invalid uncompressed-size
declarations; abrupt encrypted EOF; duplicate packets; unexpected packets and
invalid state calls.
The desired outcome is a bounded exception or readable placeholder—never an
unbounded allocation, hang, deadlock, or process crash.

Anonymized fixtures in `tests/fixtures` replay generated frame sequences for
1.8.8, 1.12.2, 1.16.5, 1.20.1, 1.21.5, and 26.2. They contain no token,
Microsoft ID, public server data, or personal chat. CI regenerates the protocol
catalog, produces a version/Protocol-ID/missing-mapping report, and fails when
critical mappings are absent from the newest protocol.

The v1.1 run additionally performed a real GUI restart test against the local
26.2 server: the initial connection reached Play, the server process was
stopped, OeXYZ classified the socket loss as transient, scheduled 5- and
10-second bounded backoff attempts, and reached Play again after the server
returned. Windows UI Automation separately invoked **Disconnect** and **Close**
and verified that the first stopped the session while the second removed its tab
without terminating the application.

For v1.2, the real `oexyz` CLI was run against the local official 26.2 server.
`status local` returned protocol 776; `run local` reached Play using the shared
session engine; a stdin chat line appeared in the Mojang server console;
`/respawn` produced a native `Respawn request sent` event instead of being
forwarded as a server command; and `/quit` shut down with exit code 0. This is
not a mock or a second protocol implementation.

The same 26.2 server was then switched to `online-mode=true` and tested with the
saved, DPAPI-protected Microsoft account. The Mojang session server verified the
account UUID, AES-CFB8 encryption and 256-byte compression were negotiated, and
the CLI reached Configuration, Play, and world-load acknowledgement in about
three seconds before `/quit` returned 0. This exposed and fixed a real issue in
which generic `CryptoStream` buffering delayed short encrypted Configuration
packets; a dedicated immediate-short-write CFB8 regression now covers it.

A final GUI run on the local 26.2 server changed real player health from 20 to
6.3, drove Food from 20 to 0, restored both values, summoned a slime, received
the translated `OeXYZTest was slain by Slime` death component, issued the
native respawn packet, and returned to HP 20/Food 20. The corresponding captures
are [health/hunger](images/v1.2-local-health-hunger.png) and
[death/respawn](images/v1.2-local-offline-dashboard.png); no dashboard value was
injected or mocked.

The final DPI/UI audit used the embedded Inter font and native Windows scaling.
Accounts, Servers, Settings, account/server editors, session actions, Player
List, protocol inspector, context menus, and Log Viewer were opened and resized.
No white trailing ListView area remained; dialog buttons stayed within their
rows; Disconnect stopped the live session; Close removed its tab; and the Log
Viewer successfully read the still-open session log with shared file access.

## Local end-to-end servers

The ignored `.integration` directory was populated with unmodified official
Mojang server JARs for the test session. These files are not part of the
repository or release.

| Server | Protocol | Verified behavior | Result |
|---|---:|---|---|
| Minecraft Java 1.8.8 | 47 | Offline login, compression, brand, position, chat send/receive, death, respawn | Pass |
| Minecraft Java 1.12.2 | 340 | Offline login, compression, brand, position, chat send/receive | Pass |
| Minecraft Java 1.16.5 | 754 | Offline login, compression, brand, position, chat send/receive | Pass |
| Minecraft Java 1.20.1 | 763 | Offline login, compression, brand, position, chat send/receive | Pass |
| Minecraft Java 1.21.5 | 770 | Login, configuration, compression, brand, position, player-loaded acknowledgement, chat | Pass |
| Minecraft Java 26.2 | 776 | Login, configuration, compression, brand, player-loaded acknowledgement, player list, metrics, chat, death, respawn, live reconnect | Pass |

The six local servers used non-default ports from `25566` through `25571` to
cover custom-port handling. Separate discovery checks verified the default port
and DNS SRV resolution paths.

## v1.3 Linux, service, and container qualification

The self-contained `linux-x64` binary was published locally, copied onto WSL2's
ext4 filesystem, and executed without a system .NET runtime. CLI profile
creation, JSON listing, `doctor`, XDG paths, a `0700` config directory, and a
`0600` profile file passed. Linux DNS SRV resolved `play.minehut.com` to the
advertised target and preserved the original handshake host.

The final x64 single-file artifact was also copied by SSH to native Ubuntu
26.04 x86_64 hardware. Its remote SHA-256 matched the local file. A real
Microsoft Live device-code login completed with exit code 0, created only the
AES-256-GCM `accounts.bin` account/session store at mode `0600`, and a second
login refreshed silently with no new device prompt. A passive
Microsoft-authenticated connection to `xenarchy.net` enabled Minecraft
online-mode encryption, reached Login/Play/headless-connected state, sent zero
chat messages and commands, reported no authentication/exception lines, and
exited 0 through SIGINT. Temporary captured output was deleted immediately.

The updated final binary was then run through SSH against `anarchy.ac`, whose
published rules permit clients while prohibiting attacks on server stability.
Status reported 15/100 players and 200 ms; the Play session verified the
Microsoft account, enabled encryption, stayed connected for more than one
minute, and received seven real public chat/join/leave events. The runtime ping
changed to the server's positive 88 ms TAB latency while HP/Food, coordinates,
traffic, and packet counters continued updating. The log contained zero sent
chat messages and zero sent server commands, local `/quit` returned 0, and a
new SSH check found no remaining OeXYZ process. Reviewed evidence is
[v1.3-linux-premium-ssh.png](images/v1.3-linux-premium-ssh.png), SHA-256
`E07D4539CFF22F2691803ED922D932FDDC184D7D08503A117C75F0076902C534`.

On that same machine, a uniquely named transient `systemd --user` unit started
the final `supervise --no-input` build. With user lingering already enabled, a
separate new SSH connection still observed the unit as `active/running` and
both loopback `/health` and `/ready` returned HTTP 200. The test unit was then
stopped and collected; its port and process disappeared, and the count of
pre-existing running user units returned unchanged. No existing service was
modified or stopped.

The systemd notifier was connected to a temporary Unix datagram socket. It sent
`READY=1`, repeated watchdog notifications, and `STOPPING=1`, then exited 0 on
SIGTERM. The supplied unit is separately checked with `systemd-analyze verify`.

The pinned Docker image built and ran as UID/GID `1654:1654` with a read-only
root and internal healthcheck. Its non-root process created a private key,
offline account, and custom-port server profile in three persistent named
volumes. Against the local official Minecraft 26.2 server, it reached Play,
received its own chat echo, reported HP 20/Food 20 and packet/byte metrics,
sent the native `/respawn`, answered health/status, and stopped through SIGTERM
with exit code 0. The ARM64 output is a native AArch64 ELF and the isolated
Buildx image reports `linux/arm64` with non-root UID/GID 1654. The final
single-file `linux-arm64` CLI was additionally copied to native Ubuntu 24.04
AArch64 hardware: its SHA-256 matched locally, `--help` ran, eight concurrent
profile updates were retained, profile/key/lock files stayed `0600`, and an
explicit port `0` returned exit 64. No service was inspected or changed. This
Docker host still lacks an ARM64 QEMU/binfmt handler, and Raspberry Pi 3
device-specific testing remains pending.

The guided Docker onboarding was also exercised from an empty, isolated
Compose project. One explicit source-path
`docker compose -f docker-compose.yml -f docker-compose.build.yml run --rm oexyz setup`
invocation created
two offline accounts, one custom-port server, and two managed account/server
bindings. A second one-off container read schema 4 from the supervisor's exact
`/config/profiles.json` path and reported `2` accounts, `1` server, and `2`
managed sessions. This caught and fixed an earlier XDG path mismatch. No
Minecraft connection was made during this deterministic setup test, and the
three project-scoped test volumes were verified by name before removal.

The Linux installer passes POSIX `sh -n` and ShellCheck 0.10.0. It detects
x64/ARM64, rejects 32-bit ARM, downloads only the official release path,
requires the published SHA-256, rejects absolute/parent TAR paths, installs
atomically without `sudo`, and optionally invokes GitHub attestation
verification.

The workflows pass actionlint 1.7.12 with only its currently unsupported
`concurrency.queue` schema diagnostic excluded. The exact `queue: max` setting
is covered by a repository regression and follows
[GitHub's documented workflow syntax](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency);
all remaining workflow diagnostics stay enabled.

## Public offline-mode/cracked compatibility

A real end-to-end test was performed against `play.minecraftanarchy.com`, whose
public site describes the server as no-rules and provides offline/TLauncher
joining guidance. A fresh random offline name was used. A one-time random
registration value was generated in process and submitted manually once. It
was never printed, persisted in the repository, or retained in command history;
the console and log showed `/register [REDACTED]`.

Observed result on protocol 776:

1. Status and version discovery succeeded.
2. TCP, compression, login, and configuration succeeded.
3. The honest client brand `OeXYZ` was announced.
4. Play state, position, and world-load acknowledgement succeeded.
5. The server's `/register` prompt was received and the server confirmed
   registration.
6. A preliminary compatibility run sent one benign line manually; after the
   request to remain passive, all screenshot/reconnect runs sent no chat.
7. Real public join, death, and chat messages from other players were received.
8. The final photographed run reported runtime ping 34 ms, HP 19, Food 20,
   position `-162.5 / 66.0 / 322.5`, about 1.6 MiB / 3,658 received packets,
   and 389 B / 33 sent protocol packets.
9. The 156×47 dashboard displayed more than 30 history rows with a complete
   frame. The incremental renderer did not clear unchanged rows.
10. The final log contained zero `Chat sent:`, `/register`, or `/login` lines,
    and the process shut down through Ctrl+C with exit code 0.

Reviewed v1.3 evidence is
[v1.3-linux-public-chat.png](images/v1.3-linux-public-chat.png). Its SHA-256 at
capture time was
`172FAAB760955714FC5A2A642C5DABF3814F631821F2A747A5F4876FA05EF091`.
The server is operated independently and is not affiliated with this project.

## Permitted public premium/hybrid checks

A passive Microsoft-authenticated test was performed against
[`anarchy.ac`](https://anarchy.ac/), whose published rules permit clients but
prohibit crashing, lagging, or attacking the service. The native Ubuntu SSH
run joined protocol 776 with the honest `OeXYZ` brand, received public chat,
reported live ping/health/food/position/traffic data, and sent no chat or server
command. Evidence is
[v1.3-linux-premium-ssh.png](images/v1.3-linux-premium-ssh.png).

A passive Microsoft-authenticated test was performed against `xenarchy.net`
after checking its current [published rules](https://www.xenarchy.net/). The
site states that mods and hacked clients are allowed as long as server stability
is not harmed. The test resolved its SRV target, verified the Microsoft session,
enabled encryption, reached Play with the honest `OeXYZ` brand, sent no chat,
performed no automation, and exited cleanly after seven seconds.

`mc.purityvanilla.com` was then tested with the saved Microsoft account through
its Velocity proxy. Encryption, compression, Configuration, Play, world-load,
health/hunger, TAB updates, public chat from other players, and automatic
respawn were observed. The first run exposed a deterministic disconnect after
roughly 60 seconds: the proxy used the modern play-state `ping` packet in
addition to keepalive, while OeXYZ did not yet answer it. After implementing the
required integer `pong`, the CLI remained connected for 82 seconds and exited
cleanly on `/quit`; the GUI capture remained connected beyond one minute with
live packet activity and public chat. No message or gameplay command was sent.
Evidence is [v1.2-premium-public-chat.png](images/v1.2-premium-public-chat.png).

Purity Vanilla's status endpoint remained reachable from the Ubuntu hardware
and reported a real 324 ms RTT with 81 players online, but its login proxy
classified that hosting address as a VPN and rejected Play login. The rejection
was respected: no proxying, rotation, or other bypass was attempted, and no SSH
screenshot is claimed for that failed login.

To separate proxy behavior from the base protocol, the same final CLI build was
also connected directly to `hardcoreanarchy.gay` (protocol 776, no advertised
proxy brand). Status, Microsoft session verification, AES-CFB8, compression,
Configuration, Play, and world-load acknowledgement succeeded; the session was
still live after 18 seconds and `/quit` returned 0. The status response reported
0 online players, so no public-chat screenshot was fabricated for that check.

These are narrow compatibility observations, not guarantees of future access
or permission for unattended play. Users must re-check each server's rules.

## v1.3.1 compatibility and reliability verification

The v1.3.1 hotfix was exercised against official, checksum-verified Vanilla
server JARs downloaded through Mojang's version manifest. Every server bound
only to `127.0.0.1`, used an isolated offline test identity, and was stopped
after the run. Microsoft OpenJDK 21/25 and Eclipse Temurin 8 were unpacked into
the temporary test root rather than installed system-wide.

| Host | Architecture | Mode | Server class | MC version | Protocol | Duration | Result |
|---|---|---|---|---:|---:|---:|---|
| Windows test host | win-x64 | Offline | local Vanilla server | 1.8.8 | 47 | 12 s | PASS |
| Windows test host | win-x64 | Offline | local Vanilla server | 1.12.2 | 340 | 12 s | PASS |
| Windows test host | win-x64 | Offline | local Vanilla server | 1.19.4 | 762 | 12 s | PASS |
| Windows test host | win-x64 | Offline | local Vanilla server | 1.20.2 | 764 | 12 s | PASS |
| Windows test host | win-x64 | Offline | local Vanilla server | 1.20.3 | 765 | 12 s | PASS |
| Windows test host | win-x64 | Offline | local Vanilla server | 26.2 | 776 | 12 s | PASS |
| `media-server` | linux-x64 (`x86_64`) | Offline | local Vanilla server | 26.2 | 776 | 65 s | PASS |
| `arm` | linux-arm64 (`aarch64`) | Offline | local Vanilla server | 26.2 | 776 | 65 s | PASS |

The Windows matrix reached Play, received position/player/health data, and
cleanly disconnected on every family. Real Resource Pack requests were also
enabled: 1.8.8 optional hash/status and 26.2 optional UUID/status declines
remained connected; a required 1.20.3 UUID pack was declined and the controlled
server disconnected the client as the warning predicted. No asset download was
attempted.

An actual stored-profile offline reconnect was performed locally and again on
both Linux hosts. The controlled server was stopped, refused bounded retry
attempts, restarted, and the same OeXYZ process reached Play again. Offline
identity was not recreated and Microsoft authentication was never invoked.
All three deliberate client stops used `/quit` or SIGTERM and returned exit 0.

The exact v1.3.1 `linux-x64` and `linux-arm64` single-file publishes were copied
to the SSH hosts. `file` reported native x86-64 and AArch64 ELF binaries,
respectively; transfer SHA-256 matched the local artifacts. Both passed
`--help`, isolated `list`, `doctor`, status, real Play, Resource Pack decline,
world-load acknowledgement, more than 60 seconds of runtime, and reconnect.

On `media-server`, an isolated systemd user unit passed `Type=notify`, READY,
loopback `/health`, `/ready`, `/status`, a 30-second watchdog for 68 seconds
without restart, and clean stop (`Result=success`, exit 0). The test unit was
then removed. The installer syntax and rendered-unit regression tests passed
on Ubuntu. `shellcheck` was not installed on either SSH host. Docker Compose
configuration passed locally, but the local Docker daemon was not running and
the SSH user lacked Docker-socket permission; passwordless sudo was unavailable,
so no real Docker run is claimed for this milestone.

No protected OeXYZ account store existed on either SSH host. Premium login,
silent refresh, and secure-chat certificate use are therefore recorded as
BLOCKED (stored account unavailable), not as passed. No interactive login was
started. Targeted scans of all generated OeXYZ and server `.log` files found no
Bearer/access/refresh/XSTS/password/control-token/proxy/account-key/device-code
patterns.

The deterministic suite additionally covers schema classification, bounded
Resource Pack parsing, all connection deadlines, nonblocking code-of-conduct
keepalive handling, DNS UDP-source/TCP fallback validation, compression edge
cases, silent-only reconnect selection, certificate replacement, bounded
unknown-packet overflow, and transaction-exact updater rollback.

## v1.4.0 transport and diagnostics verification

The v1.4.0 transport-complete `linux-x64` and `linux-arm64` single-file
publishes were run natively against the isolated Vanilla 26.2 offline servers.
Five independent x64 processes and one ARM64 process ran concurrently for 620 seconds. All six
reached Play, handled the optional UUID Resource Pack decline, received
position/health/player-list data, and sent periodic Anti-AFK position updates
through the ordered outbound dispatcher.

At roughly four minutes both controlled servers were stopped. Each client
recorded exactly one intentional disconnect, applied bounded backoff, and
reached Play again after restart. No rapid loop, authentication attempt,
subscriber failure, dropped event/log, outbound rejection, or unknown-packet
overflow was observed. A v1.4 supervisor additionally served real `/health`,
`/ready`, and `/status`; the status response exposed the new aggregate counters
with zero values under the real session load.

Initial RSS was approximately 60â€“75 MiB per process. After reconnect and JIT
activity, final observed RSS was approximately 87â€“93 MiB on x64 and 82 MiB on
ARM64; it did not exhibit a rapid monotonic queue-driven increase. Average CPU
declined after startup. All clients exited under the 620-second SIGTERM bound,
and targeted secret scans of every generated `.log` file returned no matches.

| Release | Host | Architecture | Mode | Server class | MC version | Protocol | Duration | Parallel sessions | Result |
|---|---|---|---|---|---:|---:|---:|---:|---|
| 1.4.0 | `media-server` | linux-x64 (`x86_64`) | Offline | local Vanilla server | 26.2 | 776 | 620 s | 5 | PASS |
| 1.4.0 | `arm` | linux-arm64 (`aarch64`) | Offline | local Vanilla server | 26.2 | 776 | 620 s | 1 | PASS |

The exact final code also passed deterministic concurrent outbound ordering,
critical-packet priority, bounded event saturation, slow/throwing subscriber
isolation, five-Hz metrics coalescing, status Ping/Pong fallback, capability
catalog validation, and explicit profile-recovery tests. Premium verification
remained blocked because neither host had a stored OeXYZ account; Docker
execution remained unavailable for the same permission/daemon reasons recorded
for v1.3.1.

After the final missing-primary profile-recovery edge fix, both artifacts were
republished, recopied, and SHA-256 matched locally/remotely. Those exact final
hashes each completed an additional 65-second Play/Anti-AFK/SIGTERM smoke with
exit 0; the recovery-only change did not alter the transport implementation
covered by the 620-second run.

## Manual release smoke test

Before publishing a tag:

1. Run the deterministic tests and the representative local integration matrix.
2. Publish self-contained GUI and CLI builds for `win-x64` and `win-arm64` and
   assert exactly one executable per frontend.
3. Launch the published x64 EXEs on a Windows account without a separately installed
   .NET runtime.
4. Add, edit, and remove profiles through the UI.
5. Verify automatic and custom-port connections.
6. Verify chat, `/respawn`, disconnect, log opening, and stable scroll behavior.
7. Confirm the branded update window appears immediately and shows installed
   and latest versions.
8. Confirm a release build selects its architecture, verifies SHA-256, rejects
   missing/incorrect hashes and unsafe ZIP paths, stages atomically, preserves a
   rollback backup, and never updates without confirmation.
9. Download the published release back from GitHub, verify `SHA256SUMS`, launch
   that exact EXE, connect it to the newest local server, and recheck updates.
