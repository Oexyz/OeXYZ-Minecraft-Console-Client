# Security policy

## Supported versions

Security fixes are provided for the newest release. Update through the in-app
checker or the repository's Releases page and verify `SHA256SUMS`.

Releases are not Authenticode-signed. The project does not create or require a
signing certificate. GitHub artifact attestations and the published SHA-256
manifest provide workflow provenance and integrity checks, but do not create a
Windows publisher identity; SmartScreen or Smart App Control can therefore warn
or block according to local Windows policy.

## Reporting a vulnerability

Please use GitHub's private security-advisory feature for this repository. Do
not post access tokens, account files, server passwords, crash logs containing
personal data, or proof-of-concept exploits in a public issue.

Include the affected OeXYZ version, Windows version, reproduction steps, and
the security impact. Remove usernames, IP addresses, chat content, and tokens
unless they are strictly necessary to reproduce the problem.

## Security design

- Microsoft passwords are never requested or stored by OeXYZ.
- Browser authentication is handled by the Microsoft authentication library.
- Refreshable account sessions are encrypted with Windows DPAPI for the
  current Windows user in `%LOCALAPPDATA%\OeXYZ\ConsoleClient\accounts.bin`.
- Linux device-code sessions use one AES-256-GCM account/session file,
  PBKDF2-SHA256 with a random salt, private `0700`/`0600` permissions, bounded
  payloads, and a hidden passphrase or explicit key file.
  Device user codes are displayed directly and excluded from ordinary logs.
- Profiles and logs remain local. There is no telemetry or analytics endpoint.
- Official Release builds pin updates to
  `Oexyz/OeXYZ-Minecraft-Console-Client`, select the running x64/ARM64
  architecture, and verify the release ZIP against its SHA-256 manifest before
  bounded extraction. Repository overrides are available only in Debug builds.
- An invalid/missing checksum, oversized archive, unsafe ZIP path, or missing
  frontend stops the update. Installation requires confirmation, retains a
  rollback backup, and attempts rollback if replacement fails.
- Central redaction protects GUI/CLI logs, crash reports, and allowlist-built
  support packages. Raw packet payloads, `accounts.bin`, account keys,
  passwords, and tokens are excluded.
- Offline-mode profiles are explicitly labelled and should only be used where
  the server owner permits them.

## Microsoft session handling

OeXYZ never asks for, receives, or stores a Microsoft password. Windows
interactive sign-in happens in the system browser through the pinned
[`CmlLib.Core.Auth.Microsoft`](https://www.nuget.org/packages/CmlLib.Core.Auth.Microsoft/3.3.1)
library. Linux uses OeXYZ's bounded Microsoft Live device-code adapter with the
Minecraft Java public client ID; the temporary user code is displayed directly
without ordinary file logging.
OAuth and Minecraft session values exist in process memory while needed for
authentication and server login.

On Windows, refreshable account-session data is serialized only to:

```text
%LOCALAPPDATA%\OeXYZ\ConsoleClient\accounts.bin
```

Before it reaches disk, the complete account document is encrypted with Windows
Data Protection API using `DataProtectionScope.CurrentUser`. Windows therefore
binds decryption to the same Windows user profile on the same installation.
This protects against casual file copying, but not against malware already
running as that user. Treat `accounts.bin` as sensitive and never attach it to
an issue.

Deleting the local application folder signs OeXYZ out locally. It does not
remotely revoke Microsoft authorization; use the Microsoft account security
pages when remote revocation is required.

On Linux, encrypted refreshable sessions are stored in `accounts.bin` under
`~/.config/oexyz` (or the selected XDG/config path), while the key file is
user-selected or stored with private permissions. Windows DPAPI files and Linux
encrypted files are deliberately not interchangeable. A Docker administrator
or malware running as the same user remains capable of reading the configured
key and is therefore inside the local trust boundary.

## Outbound network targets

OeXYZ has no telemetry or project-operated backend. Depending on the action,
it can contact:

| Target | Purpose | Credentials sent |
|---|---|---|
| User-selected Minecraft host and its DNS SRV target | Status, login, keepalive, chat, and commands | Minecraft protocol identity and user-entered chat |
| System DNS resolver | `_minecraft._tcp` SRV discovery | Requested server hostname only |
| Microsoft, Xbox, and Minecraft authentication endpoints selected by the pinned authentication libraries | Browser/device sign-in and session refresh | OAuth/Xbox/Minecraft authentication data |
| `sessionserver.mojang.com/session/minecraft/join` | Prove account ownership to an online-mode server | Minecraft access token, profile UUID, and server hash |
| `api.minecraftservices.com/player/certificates` | Obtain the current secure-chat certificate | Minecraft access token in the HTTPS authorization header |
| `api.github.com`, `github.com`, and GitHub's HTTPS release CDN | User-triggered update/installer download and verification | No Microsoft or Minecraft token |

Authentication providers and GitHub may use HTTPS redirects to infrastructure
they control. The directly implemented Minecraft service endpoints are visible
in [`MinecraftServicesClient.cs`](src/OeXYZ.Protocol/MinecraftServicesClient.cs),
and updater requests are visible in
[`GitHubUpdateService.cs`](src/OeXYZ.Updater/GitHubUpdateService.cs).

See [Security and privacy](docs/SECURITY_AND_PRIVACY.md) for the data-flow
details and threat boundaries.
