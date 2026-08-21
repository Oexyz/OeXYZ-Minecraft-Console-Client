# Linux, Docker, systemd, and Raspberry Pi

v1.3 adds a self-contained headless frontend for `linux-x64` and
`linux-arm64`. It does not install Java, Minecraft, Node.js, or a .NET runtime,
and it does not require a desktop. The Windows WinForms GUI is not included in
Linux archives.

## Native Linux

The normal installer is a single per-user command and never invokes `sudo`:

```bash
curl -fsSL https://raw.githubusercontent.com/Oexyz/OeXYZ-Minecraft-Console-Client/main/install.sh | sh
```

It detects the CPU architecture, selects the latest Linux release, verifies
SHA-256, and atomically installs `~/.local/bin/oexyz`. For a pinned and manually
reviewed installation, use the release tag and optional GitHub attestation:

```bash
curl -fsSL https://raw.githubusercontent.com/Oexyz/OeXYZ-Minecraft-Console-Client/v1.5.0/install.sh | sh -s -- --version 1.5.0 --verify-attestation
```

`--verify-attestation` requires a configured GitHub CLI. SHA-256 verification
is always performed. Never change the command to `sudo sh`; the default
destination is `~/.local/bin`, and `--with-systemd` only copies the user unit—it
never enables or starts it without a separate explicit command.

Extract the archive matching the machine:

| Platform | Archive |
|---|---|
| Intel/AMD 64-bit Linux | `OeXYZ-Minecraft-Console-Client-v1.5.0-linux-x64.tar.gz` |
| ARM64 Linux / Raspberry Pi OS 64-bit | `OeXYZ-Minecraft-Console-Client-v1.5.0-linux-arm64.tar.gz` |

```bash
tar -xzf OeXYZ-Minecraft-Console-Client-v1.5.0-linux-x64.tar.gz
chmod 755 oexyz
./oexyz --help
./oexyz install-path
```

Default locations follow the XDG base-directory convention:

```text
~/.config/oexyz/profiles.json
~/.config/oexyz/accounts.bin
~/.local/state/oexyz/logs/
~/.local/state/oexyz/diagnostics/
```

`XDG_CONFIG_HOME` and `XDG_STATE_HOME` are honored. `--config` or the
non-secret `OEXYZ_CONFIG` variable may select a different profile file.

Run `oexyz doctor --json` for a sanitized check of architecture, filesystem
permissions, profile integrity, log use, DNS/SRV, and an optional server. The
report never includes tokens, passwords, the key, or account files.

### Foreground terminal versus 24/7 service

`oexyz run survival` streams messages in the current terminal and accepts chat
or commands on stdin. Add `--dashboard` for the full-screen status/chat view.
Both are foreground modes: the same shell cannot accept unrelated commands,
and closing its SSH connection stops the process cleanly.

For an SSH-independent session, use the systemd user service below with
`supervise --no-input`, then read output in another shell with:

```bash
journalctl --user -u oexyz.service -f
```

The service survives a terminal disconnect. Surviving a complete user logout
also requires an administrator to opt that account into lingering, as described
below. Docker Compose is the alternative when container-managed persistence is
preferred. OeXYZ never silently daemonizes a foreground command.

## systemd user service

Set up profiles and a private key before enabling unattended Microsoft
sessions:

```bash
oexyz account-key-generate ~/.config/oexyz/account.key
oexyz account-add-microsoft main
oexyz account-login main --account-key-file ~/.config/oexyz/account.key
oexyz server-add survival --address play.example.net --group AFK
```

Install the supplied user unit:

```bash
mkdir -p ~/.config/systemd/user
cp share/systemd/user/oexyz.service ~/.config/systemd/user/oexyz.service
mkdir -p ~/.config/oexyz
printf 'OEXYZ_ACCOUNT=main\nOEXYZ_GROUP=AFK\n' > ~/.config/oexyz/service.env
chmod 600 ~/.config/oexyz/service.env ~/.config/oexyz/account.key
systemctl --user daemon-reload
systemctl --user enable --now oexyz.service
systemctl --user status oexyz.service
```

`service.env` contains profile display names, not credentials. The service uses
`Type=notify`, watchdog heartbeats, bounded tasks/file descriptors, a 30-second
graceful stop, restart-on-failure, `UMask=0077`, no capabilities, a private
temporary directory, and kernel/filesystem hardening. Its health endpoint is
reachable only on `127.0.0.1:8765`.

To keep the user service running after logout, an administrator may explicitly
enable lingering for that user with `loginctl enable-linger <user>`. This is a
host-policy choice and is never changed by OeXYZ.

## Docker Compose

The image is a pinned .NET chiseled runtime, contains one OeXYZ binary, and
runs as non-root UID/GID `1654:1654`. The root filesystem is read-only, all
Linux capabilities are dropped, and `no-new-privileges` is enabled. The same
pull-only Compose service uses a public prebuilt image; an explicit override
adds the direct source build.

To pull the public AMD64/ARM64 `latest` image, run the guided setup once, and
start it:

```bash
docker compose run --rm oexyz setup
docker compose up -d
```

To build directly from the checked-out repository instead:

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml build --pull
docker compose -f docker-compose.yml -f docker-compose.build.yml run --rm oexyz setup
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --no-build
```

Compose defaults to
`ghcr.io/oexyz/oexyz-minecraft-console-client:latest` with
`pull_policy: always`, so normal setup and startup always check GHCR for the
current stable image. Set `OEXYZ_IMAGE` to an explicit mirror or pinned version
tag if another image is required. The build override changes the pull policy to
`never`, ensuring that GHCR cannot replace the locally built image. Because the
base file contains no `build` section, a failed public-image pull cannot
silently fall back to compiling source.

The wizard creates the persistent volumes, asks for Offline or Microsoft login,
accepts `host` or `host:port`, and records the account/server pair as a managed
session. For Microsoft login it creates `/keys/account.key` exactly once and
shows the normal device-code prompt. It refuses to replace a missing key for an
existing encrypted account store.

Multiple accounts are supported in the same wizard. A server can be assigned
to more than one account, and one account can be assigned to multiple servers.
For example, `Main -> Survival`, `Alt -> Survival`, and `Alt -> Test` become
three independent sessions in the same supervisor. Running `setup` again adds
accounts or assignments without clearing existing ones.

Monitor or stop the background service with:

```bash
docker compose ps
docker compose logs --follow oexyz
docker compose stop
```

`supervise` uses the saved managed-session assignments automatically. The
optional non-secret environment variables remain available as deliberate
overrides: `OEXYZ_GROUP` limits the saved assignments to one group, while
`OEXYZ_ACCOUNT` selects one account for every matching server instead of using
the saved assignments.

The lower-level commands below use the public `latest` path and remain
available for automation or manual setup:

```bash
docker compose run --rm oexyz account-key-generate /keys/account.key
docker compose run --rm oexyz account-add-offline DockerPlayer --config /config/profiles.json
docker compose run --rm oexyz account-add-microsoft main --config /config/profiles.json
docker compose run --rm oexyz account-login main --config /config/profiles.json --account-key-file /keys/account.key
docker compose run --rm oexyz server-add survival --address play.example.net --group AFK --config /config/profiles.json
```

For the direct source-build path, replace the leading `docker compose` in each
command above with
`docker compose -f docker-compose.yml -f docker-compose.build.yml`. This keeps
the build override and its `pull_policy: never` in effect.

Compose persists `/config`, `/state`, and `/keys` in separate named volumes.
The key and encrypted session files are never copied into the image. Anyone
with administrative access to the Docker engine can read mounted volumes and
must therefore be considered trusted. `docker compose down -v` permanently
removes all three volumes and their local account/session data.

The container healthcheck calls the internal loopback endpoint. No port is
published to the host by default. `/health` reports liveness, `/ready` reports
whether sessions are ready, and `/status` exposes bounded local metrics without
account identifiers or remote server addresses.

v1.5 extends that same listener with `/metrics` and authenticated `/v1`
management actions. Compose still publishes no port by default. Create a token
inside `/keys` (for example with `control-token-create`) and pass its file path
explicitly; never place the token in Compose environment variables. Remote
control requires `--allow-remote-control` and should be exposed only through a
VPN or authenticated TLS reverse proxy.

### GHCR publication requirement

The Release workflow publishes the version tag for `linux/amd64` and
`linux/arm64`, then verifies the versioned manifest and pulls both platform
images through an empty Docker credential store. The release fails if an
anonymous client cannot download either image. After the GitHub release has
been created, fully queued and serialized promotion jobs update `latest` only
when that version is still the highest stable SemVer release; older or
restarted release runs cannot move `latest` backwards.

GitHub creates the first GHCR package as private by default. Before the first
container release can pass, a package administrator must open the
`oexyz-minecraft-console-client` package settings and change its visibility to
**Public**. This is a one-time GitHub setting; public GHCR packages support
anonymous pulls, and GitHub does not allow a public package to be made private
again. See [GitHub's package visibility documentation](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility).

## Raspberry Pi

Use a **64-bit ARM operating system** and the `linux-arm64` archive or ARM64
container. Raspberry Pi 4/5 are the primary targets. A Pi 3 can run ARM64 only
with a 64-bit OS and has much tighter memory headroom; begin with one session
and raise `--max-sessions` only after observing `oexyz doctor` and the health
metrics.

The ARM64 binary and container are compiled as native ARM64; local qualification
checks their ELF architecture and ARM64 image manifest. The current Docker host
has no ARM64 QEMU/binfmt handler, so it does not claim emulated execution.
Physical Pi 3 qualification must not be claimed until it has been run on the
user's prepared device.

OeXYZ uses async sockets, cancellation tokens, bounded channels, periodic
timers, capped logs, and progressive reconnect delays. It performs no renderer
loop, pathfinding, mining, combat, or busy-waiting.
