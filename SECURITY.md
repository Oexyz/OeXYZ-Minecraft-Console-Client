# Security policy

## Supported versions

Security fixes are provided for the newest release. Update through the in-app
checker or the repository's Releases page and verify `SHA256SUMS`.

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
- Profiles and logs remain local. There is no telemetry or analytics endpoint.
- The updater only accepts an HTTPS `github.com` repository, downloads a named
  release asset, and verifies it against the release's SHA-256 manifest.
- An invalid or missing checksum stops the update; nothing is executed.
- Offline-mode profiles are explicitly labelled and should only be used where
  the server owner permits them.

See [Security and privacy](docs/SECURITY_AND_PRIVACY.md) for the data-flow
details and threat boundaries.
