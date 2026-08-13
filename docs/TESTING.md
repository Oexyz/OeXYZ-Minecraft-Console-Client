# Testing

Last verified: **2026-08-14** on Windows 11 x64 with .NET SDK 10.0.302.

## Deterministic tests

Four dependency-free test executables validate profiles/reconnect/CLI policy,
protocol parsing and replay, support-package sanitization, and the updater. No
normal test depends on an external Minecraft server:

```powershell
dotnet run --project tests/OeXYZ.Core.Tests -c Release
dotnet run --project tests/OeXYZ.Protocol.Tests -c Release
dotnet run --project tests/OeXYZ.ConsoleClient.Tests -c Release
dotnet run --project tests/OeXYZ.Session.Tests -c Release
```

Expected results: `PASS: 12 core tests`, `PASS: 15 protocol tests`, `PASS: 5
updater tests`, and one sanitized-support-package pass: **33 deterministic
checks** total.

The hardening suite covers invalid and overlong VarInts; negative, zero,
oversized, truncated, and fragmented frames; a transport returning one byte per
read; invalid UTF-8 and JSON; malformed/deep NBT; invalid UUIDs; malformed and
over-expanding zlib data; invalid uncompressed-size declarations; abrupt
encrypted EOF; duplicate packets; unexpected packets and invalid state calls.
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

## Public offline-mode compatibility

A passive test was performed against `play.minecraftanarchy.com`, whose public
site describes the server as no-rules and provides offline/TLauncher joining
guidance. A fresh unregistered test name was used. No account was registered,
no password was supplied, and no chat or gameplay command was sent.

Observed result on protocol 776:

1. Status and version discovery succeeded.
2. TCP, compression, login, and configuration succeeded.
3. The honest client brand `OeXYZ` was announced.
4. Play state, position, and world-load acknowledgement succeeded.
5. The server's `/register` prompt was received repeatedly.
6. The session remained connected for the complete observation window.

The matching GUI evidence is [public-anarchy-connected.png](images/public-anarchy-connected.png).
The server is operated independently and is not affiliated with this project.

## Permitted public premium/hybrid checks

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

To separate proxy behavior from the base protocol, the same final CLI build was
also connected directly to `hardcoreanarchy.gay` (protocol 776, no advertised
proxy brand). Status, Microsoft session verification, AES-CFB8, compression,
Configuration, Play, and world-load acknowledgement succeeded; the session was
still live after 18 seconds and `/quit` returned 0. The status response reported
0 online players, so no public-chat screenshot was fabricated for that check.

These are narrow compatibility observations, not guarantees of future access
or permission for unattended play. Users must re-check each server's rules.

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
