# Testing

Last verified: **2026-08-11** on Windows 11 x64 with .NET SDK 10.0.302.

## Deterministic tests

The dependency-free protocol test executable validates catalog endpoints,
required packet mappings, server-address parsing, offline UUID generation, and
packet primitive round trips. A separate updater suite uses a loopback-only HTTP
server to verify semantic-version normalization, successful SHA-256 validation,
temporary-file closure on Windows, and rejection plus cleanup of a mismatched
checksum:

```powershell
dotnet run --project tests/OeXYZ.Protocol.Tests -c Release
dotnet run --project tests/OeXYZ.ConsoleClient.Tests -c Release
```

Expected results: `PASS: 5 protocol tests` and `PASS: 3 updater tests`.

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
| Minecraft Java 26.2 | 776 | Login, configuration, compression, brand, player-loaded acknowledgement, chat, death, respawn | Pass |

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

## PikaNetwork policy result

`play.pika-network.net` was also tested passively after confirming its official
address. The connection reached protocol 776 play state, position, and world
loading. The server then deliberately disconnected it with a message stating
that the client type is not allowed. OeXYZ does not spoof another brand or
attempt to bypass that policy, so PikaNetwork is not claimed as a compatible
AFK destination.

## Manual release smoke test

Before publishing a tag:

1. Run the deterministic tests and the representative local integration matrix.
2. Publish the self-contained `win-x64` build.
3. Launch the published EXE on a Windows account without a separately installed
   .NET runtime.
4. Add, edit, and remove profiles through the UI.
5. Verify automatic and custom-port connections.
6. Verify chat, `/respawn`, disconnect, log opening, and stable scroll behavior.
7. Confirm the branded update window appears immediately and shows installed
   and latest versions.
8. Confirm a release build reads its GitHub repository metadata, downloads to a
   temporary file, and rejects a missing or incorrect checksum.
9. Download the published release back from GitHub, verify `SHA256SUMS`, launch
   that exact EXE, connect it to the newest local server, and recheck updates.
