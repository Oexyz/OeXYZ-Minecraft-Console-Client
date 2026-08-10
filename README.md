<p align="center">
  <img src="assets/oexyz-logo.svg" width="112" alt="OeXYZ logo">
</p>

<h1 align="center">OeXYZ Console Client</h1>

<p align="center">
  Stay connected. Render nothing.<br>
  A native Windows client for Minecraft Java chat and reliable AFK sessions.
</p>

<p align="center">
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-1389FD?logo=windows">
  <img alt="Minecraft Java 1.8 through 26.2" src="https://img.shields.io/badge/Minecraft%20Java-1.8%E2%80%9326.2-35c46a">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet">
  <img alt="MIT license" src="https://img.shields.io/badge/license-MIT-2ea44f">
</p>

![OeXYZ connected to a local Minecraft 26.2 server](docs/images/client-connected.png)

OeXYZ keeps a real Minecraft Java session connected without starting the game
renderer. Everything an end user needs is in one self-contained Windows
release: no terminal, Node.js, Java runtime, or separate .NET installation.

## Download

Download `OeXYZ-Console-Client-win-x64.zip` from the
[latest GitHub release](../../releases/latest), extract it, and open
`OeXYZ Console Client.exe`.

The release also contains `SHA256SUMS`. The in-app updater checks the same
manifest before accepting a downloaded archive. Windows may show a SmartScreen
prompt for a new unsigned open-source publisher; verify the release source and
checksum before choosing to run it.

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
- Handle keepalive, position, teleport confirmation, player-loaded state,
  compression, secure chat sessions, and the 26.x configuration phase.
- Send `/respawn` through the correct native packet, automatically respawn after
  death, reconnect with bounded backoff, and optionally send a small look change
  every 45 seconds.
- Keep the chat view stable under heavy output: incoming lines are queued,
  appended in batches, and do not steal the scroll position while reading
  older messages.
- Show newer server codes of conduct and require a deliberate approval click.

## Public offline-mode proof

![OeXYZ connected to Minecraft Anarchy](docs/images/public-anarchy-connected.png)

The screenshot above is a real passive connection to
`play.minecraftanarchy.com` on protocol 776. The server accepted the honest
`OeXYZ` brand, reached play state, supplied a position, acknowledged world
loading, and delivered its registration prompt. The test did not register an
account, send a password, or send chat. The server is independently operated
and is not affiliated with or endorsed by OeXYZ. Details and limitations are in
[the test report](docs/TESTING.md).

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
| PrismarineJS `minecraft-data` 3.113.0 | Generating the committed packet-ID table | No, build time only |

OeXYZ contains no Minecraft game executable, game assets, server JAR, hidden
launcher, advertising SDK, telemetry SDK, or remotely loaded plugin system.
The protocol code is in [`src/OeXYZ.Protocol`](src/OeXYZ.Protocol), the desktop
application is in [`src/OeXYZ.ConsoleClient`](src/OeXYZ.ConsoleClient), and the
generator is in [`tools/protocol-catalog`](tools/protocol-catalog).

See also:

- [Architecture](docs/ARCHITECTURE.md)
- [Security and privacy](docs/SECURITY_AND_PRIVACY.md)
- [Testing evidence](docs/TESTING.md)
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
dotnet run --project tests/OeXYZ.Protocol.Tests -c Release --no-build
dotnet publish src/OeXYZ.ConsoleClient -c Release -r win-x64 --self-contained true
```

To regenerate protocol mappings:

```powershell
npm ci
npm run generate:protocol
```

The generated catalog currently contains 74 release mappings from protocol 47
through protocol 776. Release automation verifies that regenerating it produces
no uncommitted difference.

## Responsible use

Server owners decide whether AFK sessions, automated movement, alternate
clients, or offline-mode accounts are permitted. Check the server's current
rules and disable features it does not allow. OeXYZ does not implement CAPTCHA
bypasses, anti-bot evasion, ban evasion, brand impersonation, spam, or automatic
account registration.

PikaNetwork, for example, accepted the protocol through active play state during
testing and then explicitly rejected this client type. That server is therefore
not presented as a supported AFK destination, and OeXYZ will not disguise itself
to bypass the restriction.

Minecraft is a trademark of Microsoft Corporation. This project is independent
and is not affiliated with, endorsed by, or approved by Microsoft or Mojang
Studios.

## License

OeXYZ source code is available under the [MIT License](LICENSE). Third-party
components retain their own licenses as listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
