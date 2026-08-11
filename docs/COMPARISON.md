# OeXYZ, Mineflayer, and HeadlessMc

These projects overlap around headless Minecraft connections but target
different users. This document compares their stated architecture and scope; it
does not rank them. Upstream behavior can change after this document is
published.

| Area | OeXYZ Console Client | Mineflayer | HeadlessMc |
|---|---|---|---|
| Product type | Native Windows desktop application | JavaScript library and high-level bot API | Command-line Minecraft launcher |
| Main audience | People who want clickable chat, commands, reconnect, and AFK sessions | Developers building scripted bots and automation | Developers and testers who need the real client, mod loaders, or game behavior without a display |
| Connection model | Implements the required Minecraft Java protocol directly in C# | Connects through the PrismarineJS protocol stack without launching the game | Launches the Minecraft Java client in headless mode |
| Renderer | None | None by default; optional viewer projects exist | Game is launched without a screen; LWJGL/headless handling is part of the workflow |
| Runtime for normal use | One self-contained Windows x64 EXE | Node.js, npm dependencies, and user-written JavaScript | Java 8 or newer for the JAR path, or an upstream native launcher; Minecraft files are still involved |
| User interface | Native clickable WinForms GUI | Code/API; behavior is written by the user | Command-line interface |
| Authentication scope | Microsoft browser authentication, protected session refresh, and explicitly labelled offline-mode profiles | Microsoft or offline authentication configured by the bot developer | Validated Minecraft accounts for normal game launches; upstream documents a limited CI purpose for offline accounts |
| Automation depth | Deliberately narrow: chat, commands, respawn, reconnect, logging, and optional anti-AFK look changes | Broad programmable API with navigation, inventory, world interaction, and plugins | Full-client control, with additional commands and UI interaction through version-specific companion mods |
| Minecraft client mods | Not supported; OeXYZ does not launch the game | Not a Minecraft client mod loader | Designed to manage clients, servers, mods, and Fabric/Forge/NeoForge launches |
| Version statement | Committed mappings for Java 1.8 through 26.2, protocols 47 through 776 | Refer to the current upstream support table | Refer to the current launcher and mod-loader documentation |
| Best reason to choose it | A focused, renderer-free Windows AFK/chat client that requires no development environment | A rich automation platform for developers | Real Minecraft or mod behavior is required in CI or another headless environment |

## Important scope differences

OeXYZ intentionally does not provide pathfinding, combat, farming, plugin
execution, game rendering, mods, or a general bot scripting API. Mineflayer is
the stronger fit when those programmable bot capabilities are the objective.

OeXYZ also does not launch Minecraft. HeadlessMc is the stronger fit when tests
must execute the actual game, a mod loader, menus, or version-specific client
mods. That broader scope also means it is not the same kind of renderer-free
protocol client as OeXYZ.

## Primary sources

- [OeXYZ architecture](ARCHITECTURE.md), [testing evidence](TESTING.md), and
  [security boundary](SECURITY_AND_PRIVACY.md)
- [Mineflayer official repository](https://github.com/PrismarineJS/mineflayer)
  and [API documentation](https://github.com/PrismarineJS/mineflayer/blob/master/docs/api.md)
- [HeadlessMc official repository](https://github.com/headlesshq/headlessmc),
  [launch documentation](https://headlesshq.github.io/headlessmc/launch/), and
  [companion-mod documentation](https://headlesshq.github.io/headlessmc/specifics/)

The upstream sources were reviewed on 2026-08-11. OeXYZ is independent of all
projects listed here; the names belong to their respective maintainers.
