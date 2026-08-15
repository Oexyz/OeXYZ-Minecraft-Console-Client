## Summary

Describe the user-visible change and why it is needed.

## Verification

- [ ] `dotnet build OeXYZ.ConsoleClient.slnx -c Release`
- [ ] `dotnet run --project tests/OeXYZ.Protocol.Tests -c Release`
- [ ] `dotnet run --project tests/OeXYZ.Cli.Tests -c Release`
- [ ] Protocol catalog regenerated when its source changed
- [ ] No credentials, logs, server binaries, or personal data added
- [ ] Documentation and changelog updated where needed
- [ ] Server rules were respected in any public compatibility test
