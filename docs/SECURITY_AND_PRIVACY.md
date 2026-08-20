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

Linux follows XDG paths instead:

```text
~/.config/oexyz/
├── profiles.json       non-secret profiles and account/server bindings (0600)
├── accounts.bin        encrypted Microsoft/Xbox/Minecraft session (0600)
└── account.key         optional service key created explicitly by the user (0600)
~/.local/state/oexyz/
├── logs/
└── diagnostics/
```

`accounts.bin` can only be decrypted through Windows DPAPI by the same Windows
user on the same installation. It should still be treated as sensitive: do not
upload it, attach it to issues, or copy it between computers.

On Linux, `accounts.bin` is a bounded AES-256-GCM envelope. It derives its key
using PBKDF2-SHA256 with 600,000 iterations and a random 128-bit salt from the
hidden passphrase or explicit key file.
Authentication tags reject a wrong key or modified ciphertext before account
data is used. Payloads are capped at 8 MiB and 4 MiB respectively. Directories
are `0700`; files and backups are `0600`. OeXYZ does not accept this key through
a general environment variable or place it in support packages.

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
counts. It never copies `accounts.bin`, account-key files, the
complete profile file, raw packet payloads, or full chat history. Review any
diagnostic archive before sharing it.

## Network destinations

OeXYZ makes outbound requests only when needed for a user action:

| Destination | When | Data |
|---|---|---|
| Selected Minecraft server | Connect/status | Protocol packets, chosen profile name, chat entered by the user |
| DNS resolver | Automatic port discovery | `_minecraft._tcp` SRV query for the selected host |
| Microsoft/Xbox/Minecraft authentication services selected by the pinned authentication libraries | Browser/device sign-in or refresh | OAuth/session data handled by the authentication libraries |
| `sessionserver.mojang.com/session/minecraft/join` | Online-mode server login | Minecraft access token, profile UUID, and server join hash |
| `api.minecraftservices.com/player/certificates` | Secure-chat setup | Minecraft access token |
| `api.github.com`, `github.com`, and GitHub's HTTPS release CDN | User triggers the updater or Linux installer | App version, process architecture, and standard HTTP metadata; no account token |

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

On Linux, Microsoft sign-in uses the standard device-code flow. Only the
verification URL and short-lived user code are shown. The user code bypasses
ordinary logging and disappears from the terminal dashboard when the flow
completes. Microsoft passwords are entered only on Microsoft's site. The
refreshable Live/Xbox/Minecraft session is held inside encrypted `accounts.bin`
so later service starts can authenticate silently without storing a password.

## Threat boundaries

- A Minecraft server controls the content shown in chat. Treat links and
  commands from unknown servers as untrusted.
- Offline-mode servers do not prove ownership of a player name. Never reuse a
  valuable username or password merely because a server asks for `/register`.
- A server can disconnect clients that decline a required resource pack. OeXYZ
  declines packs because it does not render visual assets.
- Newer servers may present a code of conduct. OeXYZ displays it and requires a
  deliberate Yes click; it is never accepted silently.
- The code-of-conduct text is length-bounded and terminal-safe before display.
  Its 120-second decision runs outside the network receive loop, allowing
  mandatory Configuration keepalive and ping responses to continue.
- Minecraft click events such as `run_command` are never executed. HTTP(S) URLs
  open only after a user clicks and confirms them.
- Startup commands are opt-in, capped, delayed, non-repeating, and reject
  login/registration/password commands. Quick commands can still contain
  sensitive user text; the GUI warns before sending and the value remains in
  the local profile file, so do not store passwords there.
- Protocol inspection is disabled by default and records packet metadata only,
  never hexadecimal/raw payloads that could contain tokens or private chat.
- Resource Pack request fields are schema-classified and bounded. URLs are
  never opened, and URLs, hashes, prompts, and raw packets are not logged.
  Optional and required packs are declined because OeXYZ renders no assets.
- Automatic reconnect performs Microsoft refresh in silent-only mode. Only
  recognized OAuth interaction states can permit an interactive flow during an
  explicitly user-started connection; filesystem, decryption, and integrity
  failures remain operational errors.
- SHA-256 checks detect release corruption or mismatched assets but do not
  replace trust in the official GitHub repository and its owners. Release
  builds pin that repository and ignore repository overrides supplied through
  the process environment.
- Docker images contain no profiles, keys, or sessions. Compose uses separate
  persistent named volumes for `/config`, `/state`, and `/keys`; Docker-engine
  administrators can read mounted volumes and are inside the trust boundary.
- `oexyz setup` may create `/keys/account.key` only when no key and no existing
  encrypted account store exist. It never replaces a missing key for an
  existing store, never prints key material, and stores managed-session UUID
  pairs—not tokens or passwords—in `profiles.json`.

## Removing data

Close OeXYZ and remove `%LOCALAPPDATA%\OeXYZ\ConsoleClient` to delete all saved
profiles, protected sessions, and logs. This signs the app out locally; it does
not revoke the Microsoft account session remotely.

`oexyz uninstall-path` removes the extracted release directory from the current
user's `PATH`. It does not delete the executable or local application data.

On Linux, remove `~/.config/oexyz` and `~/.local/state/oexyz` after stopping all
sessions. For the supplied Compose project, `docker compose down -v` deletes
the three persistent volumes and is intentionally destructive. Local deletion
does not revoke Microsoft authorization remotely.
