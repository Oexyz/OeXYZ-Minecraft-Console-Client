# Contributing

Thanks for helping improve OeXYZ Console Client.

## Development setup

Install the .NET 10 SDK. Node.js is needed only when regenerating the committed
protocol catalog; it is never required to build from an unchanged checkout or
to run a release.

```powershell
dotnet restore OeXYZ.ConsoleClient.slnx --locked-mode
dotnet build OeXYZ.ConsoleClient.slnx -c Release --no-restore
dotnet run --project tests/OeXYZ.Protocol.Tests -c Release --no-build
dotnet run --project tests/OeXYZ.ConsoleClient.Tests -c Release --no-build
```

When updating the pinned `minecraft-data` package:

```powershell
npm ci
npm run generate:protocol
dotnet run --project tests/OeXYZ.Protocol.Tests -c Release
```

Commit the updated `package-lock.json`, protocol catalog, and an explanation of
which real protocol versions were tested.

## Pull requests

- Keep the application renderer-free and usable without a terminal.
- Do not add game binaries, Minecraft assets, server JARs, credentials, account
  databases, logs, or personal server data.
- Keep warnings as errors and add a deterministic regression test for protocol
  or address-parsing fixes.
- Preserve honest client identification. Do not add anti-bot evasion, CAPTCHA
  bypasses, ban evasion, spam, or client-brand impersonation.
- Respect each public server's rules in manual tests. Never register test
  accounts or send messages without explicit permission.
- Update `CHANGELOG.md` and relevant documentation for user-visible changes.

By contributing, you agree that your contribution is licensed under the MIT
license in this repository.
