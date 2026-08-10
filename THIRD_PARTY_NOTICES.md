# Third-party notices

OeXYZ Console Client is original C# application code released under the MIT
license. It interoperates with Minecraft Java Edition through an independently
implemented network protocol. It does not contain or redistribute Minecraft
client code, game assets, or server JAR files.

## Runtime dependencies

The release is self-contained and includes the applicable .NET runtime files.
Microsoft's .NET notices and licenses are included by the .NET publishing
toolchain. The application also uses these NuGet packages:

| Package | Version | Purpose | License/source |
|---|---:|---|---|
| CmlLib.Core.Auth.Microsoft | 3.3.1 | Microsoft/Xbox/Minecraft browser authentication | [MIT, source](https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft) |
| CmlLib.Core.Commons | 4.0.0 | Transitive authentication support | [MIT, source](https://github.com/CmlLib/CmlLib.Core) |
| XboxAuthNet | 3.0.4 | Transitive Xbox authentication support | [source](https://github.com/CmlLib/XboxAuthNet) |
| XboxAuthNet.Game | 1.4.1 | Transitive account-session support | [source](https://github.com/CmlLib/XboxAuthNet.Game) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.1 | Transitive dependency injection contracts | [MIT, source](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.1 | Transitive logging contracts | [MIT, source](https://github.com/dotnet/runtime) |
| Microsoft.Web.WebView2 | 1.0.1823.32 | Transitive authentication browser support | [package and license](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.1823.32) |

The exact resolved dependency graph is pinned by NuGet lock files and is also
recorded in each published `.deps.json` file.

## Build-time data

`minecraft-data` 3.113.0 from PrismarineJS is used only by the maintainer tool
that generates the committed packet-ID catalog. It is MIT-licensed. End users
do not install Node.js or this package.

- Source: <https://github.com/PrismarineJS/minecraft-data>
- Package: <https://www.npmjs.com/package/minecraft-data/v/3.113.0>

Minecraft is a trademark of Microsoft Corporation. OeXYZ is not affiliated
with, endorsed by, or approved by Microsoft, Mojang Studios, or any server
shown in project documentation.
