# Headless CLI

`oexyz.exe` is the non-GUI frontend included in every v1.2 Windows release. It
uses the same profile file, DPAPI Microsoft sessions, protocol implementation,
reconnect policy, Anti-AFK loop, startup commands, metrics, redaction, and
session lifecycle as the GUI.

```text
oexyz list
oexyz profiles
oexyz status <profile>
oexyz run <profile> [--account <name>]
oexyz connect <profile> [--account <name>]
oexyz run-address <host[:port]> [--account <name>]
oexyz connect-all [--account <name>]
oexyz connect-group <group> [--account <name>]
oexyz install-path
oexyz uninstall-path
```

`connect` and `run` are equivalent foreground commands. While connected, chat
and slash commands are read from stdin. `/respawn` sends the native respawn
packet, `/disconnect` stops the selected session, and `/quit`, EOF, or Ctrl+C
stops all owned sessions and waits for deterministic disposal. In a multi-session run, user
input is sent only to the first currently connected session; use separate
processes when interactive routing matters.

Options:

| Option | Purpose |
|---|---|
| `--account <name>` | Select one account when multiple accounts exist |
| `--config <path>` | Read an explicit profiles JSON file |
| `--log-file <path>` | Also write sanitized CLI events to this file |
| `--log-level <level>` | `trace`, `debug`, `information`, `warning`, or `error` |
| `--inspect-packets` | Print metadata-only packet trace and collect unknown counts |

`OEXYZ_CONFIG` can provide the profile-file path when `--config` is absent. It
is intended for non-secret path selection, not tokens. Secrets are not accepted
as command-line arguments or general environment variables.

## PATH installation

From the extracted release directory:

```powershell
.\oexyz.exe install-path
```

Open a new PowerShell/CMD/Terminal instance, then run `oexyz --help`. The helper
adds that exact directory once to the current Windows user's `PATH`. It does not
copy files, install a service, create shell aliases, or touch the machine-wide
PATH. `oexyz uninstall-path` removes only that normalized directory entry.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success or deliberate `/quit`/Ctrl+C shutdown |
| 2 | Profile/config target not found |
| 3 | Authentication error |
| 4 | Unsupported protocol |
| 5 | Transient/network connection failure |
| 6 | Permanent server rejection |
| 64 | Invalid command line |
| 70 | Unexpected internal error |

Microsoft browser sign-in and token persistence currently require Windows
DPAPI. A remote Linux device-code flow and portable secure token store are
planned for v1.3; Linux/Raspberry Pi/Docker are not claimed as supported in
v1.2.
