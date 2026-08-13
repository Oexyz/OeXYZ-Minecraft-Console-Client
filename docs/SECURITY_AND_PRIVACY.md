# Security and privacy

## Local data

OeXYZ stores user data under:

```text
%LOCALAPPDATA%\OeXYZ\ConsoleClient\
├── profiles.json       server settings and non-secret display names
├── accounts.bin        Microsoft session data encrypted by Windows DPAPI
├── logs\               local session and crash logs
├── diagnostics\        user-created, sanitized support ZIPs
└── updates\            explicitly downloaded staging data and rollback files
```

`accounts.bin` can only be decrypted through Windows DPAPI by the same Windows
user on the same installation. It should still be treated as sensitive: do not
upload it, attach it to issues, or copy it between computers.

Logs contain server chat, server addresses, player names, status messages, and
errors. They do not intentionally contain Microsoft passwords or access tokens.
Review logs before sharing them. The selected 30-day, 90-day, or unlimited age
policy is additionally bounded by a 300 MB total log cap. OeXYZ checks this cap
at startup and every minute, deleting the oldest closed `.log` files first;
the log currently being written is never deleted underneath an active session.

The same `SensitiveDataRedactor` protects GUI logs, CLI file logs, crash logs,
and support packages. It removes bearer/JSON/key-value tokens and recognized
login/register/password command arguments. A support ZIP is built from an
allowlist of environment, sanitized server settings, DNS/SRV diagnosis, last
disconnect, at most 200 recent diagnostic lines, and optional unknown-packet
counts. It never copies `accounts.bin`, the complete profile file, raw packet
payloads, or full chat history. Review any diagnostic archive before sharing it.

## Network destinations

OeXYZ makes outbound requests only when needed for a user action:

| Destination | When | Data |
|---|---|---|
| Selected Minecraft server | Connect/status | Protocol packets, chosen profile name, chat entered by the user |
| DNS resolver | Automatic port discovery | `_minecraft._tcp` SRV query for the selected host |
| Microsoft/Xbox/Minecraft authentication services selected by the pinned authentication library | Microsoft sign-in or refresh | OAuth/session data handled by the authentication library |
| `sessionserver.mojang.com/session/minecraft/join` | Online-mode server login | Minecraft access token, profile UUID, and server join hash |
| `api.minecraftservices.com/player/certificates` | Secure-chat setup | Minecraft access token |
| `api.github.com`, `github.com`, and GitHub's HTTPS release CDN | User clicks Check for updates and explicitly confirms download/install | App version, process architecture, and standard HTTP metadata; no account token |

There is no telemetry, analytics, advertising, background update polling, or
project-operated cloud service.

Windows releases are not Authenticode-signed. Release ZIPs are instead hashed
with SHA-256 and attested by GitHub Actions. Those controls let users verify the
download against this repository's workflow, but they cannot make Windows show
a verified publisher and do not override SmartScreen or Smart App Control.

Microsoft session material exists in process memory while it is needed. On
disk, the account document is encrypted as one DPAPI `CurrentUser` payload.
DPAPI prevents another ordinary Windows profile from simply decrypting a copied
file; it cannot protect against software already executing as the same user.

## Threat boundaries

- A Minecraft server controls the content shown in chat. Treat links and
  commands from unknown servers as untrusted.
- Offline-mode servers do not prove ownership of a player name. Never reuse a
  valuable username or password merely because a server asks for `/register`.
- A server can disconnect clients that decline a required resource pack. OeXYZ
  declines packs because it does not render visual assets.
- Newer servers may present a code of conduct. OeXYZ displays it and requires a
  deliberate Yes click; it is never accepted silently.
- Minecraft click events such as `run_command` are never executed. HTTP(S) URLs
  open only after a user clicks and confirms them.
- Startup commands are opt-in, capped, delayed, non-repeating, and reject
  login/registration/password commands. Quick commands can still contain
  sensitive user text; the GUI warns before sending and the value remains in
  the local profile file, so do not store passwords there.
- Protocol inspection is disabled by default and records packet metadata only,
  never hexadecimal/raw payloads that could contain tokens or private chat.
- SHA-256 checks detect release corruption or mismatched assets but do not
  replace the trust placed in the configured GitHub repository and its owners.

## Removing data

Close OeXYZ and remove `%LOCALAPPDATA%\OeXYZ\ConsoleClient` to delete all saved
profiles, protected sessions, and logs. This signs the app out locally; it does
not revoke the Microsoft account session remotely.

`oexyz uninstall-path` removes the extracted release directory from the current
user's `PATH`. It does not delete the executable or local application data.
