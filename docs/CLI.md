# Headless CLI

`oexyz` is the renderer-free terminal frontend. Windows releases name it
`oexyz.exe`; Linux releases use `oexyz`. Both frontends share the same profile,
protocol, reconnect, Anti-AFK, metrics, logging, redaction, and session code.
The Linux build has no desktop GUI dependency.

## Commands

```text
oexyz list [--json]
oexyz profiles [--json]
oexyz setup
oexyz status <profile> [--json]
oexyz doctor [profile] [--json]
oexyz account-add-offline <player-name>
oexyz account-add-microsoft <profile-name> [--login-hint <email>]
oexyz account-key-generate <path>
oexyz account-login <profile-name> [--account-key-file <path>]
oexyz server-add <profile-name> --address <host[:port]>
oexyz run <profile> [--account <name>]
oexyz connect <profile> [--account <name>]
oexyz run-address <host[:port]> [--account <name>]
oexyz connect-all [--account <name>]
oexyz connect-group <group> [--account <name>]
oexyz supervise [group] [--account <name>] [--no-input]
oexyz healthcheck [http://127.0.0.1:8765/health]
oexyz export-profiles <portable.json>
oexyz import-profiles <portable.json>
oexyz profiles-recover [--json]
oexyz install-path
oexyz uninstall-path
```

`connect` and `run` are equivalent foreground commands. While connected, chat
and slash commands are read from stdin. `/respawn` sends the native respawn
packet, `/disconnect` stops the selected session, and `/quit`, EOF, Ctrl+C, or
SIGTERM stops all owned sessions and awaits deterministic disposal. In a
multi-session run, input is sent only to the first connected session; run
separate interactive processes when explicit input routing matters.

## First-time profile setup

The easiest interactive setup, including Docker and multiple accounts, is:

```bash
oexyz setup
```

It can add multiple Offline or Microsoft accounts and binds each desired
account/server pair as a managed session. `supervise` starts those bindings
automatically, so two accounts may use the same server without duplicating the
server profile. Running `setup` again only adds data; it does not reset existing
profiles.

The equivalent lower-level commands remain available for scripts:

```bash
oexyz account-add-offline TestPlayer
oexyz server-add survival --address play.example.net --group AFK
oexyz run survival --account TestPlayer
```

Offline identities work only on servers that deliberately permit offline mode.
Names are validated before any network connection. OeXYZ never automatically
runs `/register` or `/login`.

For Microsoft authentication on Linux, create a private machine/account-store
key once and complete the standard Microsoft device flow:

```bash
oexyz account-key-generate ~/.config/oexyz/account.key
oexyz account-add-microsoft main
oexyz account-login main --account-key-file ~/.config/oexyz/account.key
```

The terminal shows Microsoft's verification URL and temporary user code. The
code is displayed on screen only and is not sent to OeXYZ logs. OeXYZ never
asks for the Microsoft password. The saved account document and refreshable
session are one AES-256-GCM file derived from the key with PBKDF2-SHA256; the
profile and key files use `0600` and their directories use `0700`.

For an interactive foreground connection, `--account-key-file` may be omitted;
OeXYZ then asks for a hidden passphrase. A service, Docker container, or
redirected stdin must use a private key file. Never commit that file.

Windows continues to use the system browser and DPAPI `CurrentUser` storage.
Windows and Linux account stores are intentionally not portable between
operating systems.

## Options

| Option | Purpose |
|---|---|
| `--account <name>` | Select one account when multiple accounts exist |
| `--address <host[:port]>` | Address used by `server-add` |
| `--port <1-65535>` | Explicit custom port used by `server-add` |
| `--minecraft-version <version>` | `auto` or an embedded supported version |
| `--group <name>` | Session group used by `server-add` |
| `--config <path>` | Read an explicit profiles JSON file |
| `--account-key-file <path>` | Unlock the encrypted Linux account stores |
| `--log-file <path>` | Also write sanitized CLI events to a rotating file |
| `--log-level <level>` | `trace`, `debug`, `information`, `warning`, or `error` |
| `--inspect-packets` | Print metadata-only packet traces and count unknown IDs |
| `--dashboard` | Show the live ANSI terminal dashboard |
| `--no-input` | Disable stdin for a service/container |
| `--health-port <port>` | Start loopback-only `/health`, `/ready`, and `/status` |
| `--max-sessions <1-128>` | Bound concurrent sessions; default `16` |
| `--json` | Machine-readable output where supported |

`OEXYZ_CONFIG` can provide the profile-file path when `--config` is absent.
For `supervise`, saved managed-session bindings are used when no account is
specified. `OEXYZ_GROUP` can limit them to one group. `OEXYZ_ACCOUNT` is a
deliberate override that runs every matching server under that one account.
These variables contain only non-secret display names; tokens, passwords, and
the account-store key are not accepted as general environment variables.

Active session logs rotate at 16 MiB per part. CLI files rotate at 32 MiB, and
the complete log tree is capped at 300 MiB by deleting the oldest closed log
parts. The current live log is protected.

If `profiles.json` is corrupt and `profiles.json.bak` validates independently,
normal commands report that recovery is available. `profiles-recover` is the
only CLI action that restores it. The corrupt primary is retained under a
unique `.corrupt-*` name, the restore is atomic and locked, and `--json`
reports only recovery metadata, never profile or account contents.

The dashboard and `/status` include aggregate dropped-event, dropped-log,
subscriber-failure, outbound-rejection, and unknown-packet-overflow counters.

## PATH installation

On Windows, run this in the extracted release directory and open a new shell:

```powershell
.\oexyz.exe install-path
oexyz --help
```

On Linux, place the release binary anywhere you keep applications, then run:

```bash
./oexyz install-path
export PATH="$HOME/.local/bin:$PATH"
```

Linux creates only `~/.local/bin/oexyz` as a symbolic link. Windows adds only
the current executable directory to the current user's `PATH`. The inverse
`uninstall-path` operation refuses to remove a different file or link.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success or deliberate shutdown |
| 2 | Profile/config target not found |
| 3 | Microsoft/Xbox/Minecraft authentication error |
| 4 | Unsupported protocol |
| 5 | Transient/network connection failure |
| 6 | Permanent server rejection |
| 7 | Diagnosis could not be created |
| 64 | Invalid command line or bounded local input rejected |
| 70 | Unexpected internal error |

Connection errors identify whether TCP connect, login, Configuration, or the
code-of-conduct decision exceeded its deadline. These remain distinct from
Ctrl+C, `/quit`, and other deliberate shutdowns. If a Microsoft reconnect can
no longer refresh silently, automatic retries stop and the CLI asks the user to
run an explicit login again; reconnect never starts a browser or device-code
flow on its own.

See [Linux, Docker, systemd, and Raspberry Pi](DEPLOYMENT.md) for unattended
operation. v1.3 provides a terminal dashboard, not a Linux desktop GUI; the
clickable WinForms frontend remains Windows-only.
