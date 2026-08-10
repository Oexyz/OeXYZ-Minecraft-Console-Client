# Security and privacy

## Local data

OeXYZ stores user data under:

```text
%LOCALAPPDATA%\OeXYZ\ConsoleClient\
├── profiles.json       server settings and non-secret display names
├── accounts.bin        Microsoft session data encrypted by Windows DPAPI
└── logs\               local session and crash logs
```

`accounts.bin` can only be decrypted through Windows DPAPI by the same Windows
user on the same installation. It should still be treated as sensitive: do not
upload it, attach it to issues, or copy it between computers.

Logs contain server chat, server addresses, player names, status messages, and
errors. They do not intentionally contain Microsoft passwords or access tokens.
Review logs before sharing them.

## Network destinations

OeXYZ makes outbound requests only when needed for a user action:

| Destination | When | Data |
|---|---|---|
| Selected Minecraft server | Connect/status | Protocol packets, chosen profile name, chat entered by the user |
| DNS resolver | Automatic port discovery | `_minecraft._tcp` SRV query for the selected host |
| Microsoft/Xbox/Minecraft authentication services | Microsoft sign-in or refresh | OAuth/session data handled by the authentication library |
| Minecraft session and certificate services | Online-mode login | Minecraft access token and server join hash |
| GitHub API and release assets | User clicks Check for updates and accepts download | App version, standard HTTP metadata |

There is no telemetry, analytics, advertising, background update polling, or
project-operated cloud service.

## Threat boundaries

- A Minecraft server controls the content shown in chat. Treat links and
  commands from unknown servers as untrusted.
- Offline-mode servers do not prove ownership of a player name. Never reuse a
  valuable username or password merely because a server asks for `/register`.
- A server can disconnect clients that decline a required resource pack. OeXYZ
  declines packs because it does not render visual assets.
- Newer servers may present a code of conduct. OeXYZ displays it and requires a
  deliberate Yes click; it is never accepted silently.
- SHA-256 checks detect release corruption or mismatched assets but do not
  replace the trust placed in the configured GitHub repository and its owners.

## Removing data

Close OeXYZ and remove `%LOCALAPPDATA%\OeXYZ\ConsoleClient` to delete all saved
profiles, protected sessions, and logs. This signs the app out locally; it does
not revoke the Microsoft account session remotely.
