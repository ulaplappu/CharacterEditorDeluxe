# Character Editor Deluxe

Version 1.1.0

An unofficial BepInEx plugin for editing employees and configuring gameplay options in Mad Games Tycoon 2.

## Features

- Edit employee motivation and eight skills; optionally lock edited values.
- Apply perks separately from employee stat edits, including positive-only perk application.
- Apply edits to one employee or in bulk, and optionally auto-max newly hired employees.
- Configure Design / Work Priority and Game Update content contributions within the safe limits below.

## Employee Stats

- The eight editable employee skills support values from 0 to 1000 (the configured skill cap defaults to 1000). Motivation remains limited to 0–100.
- Skills are written to the real `characterScript` `float` fields; the UI displays those live field values.
- Player employees use the configured extended cap for skill learning and training. NPC and rival employees retain vanilla skill caps.
- Stat locks keep the edited skill values stable and save their float snapshots in the mod's save sidecar. Values above 100 are representable in the sidecar.
- Auto-Max assigns the configured extended skill maximum to new employees.
- Stat-only edits do not alter perks; perk edits use a separate apply path.
- Skills above 100 exceed vanilla balance. Their supported range does not imply that every downstream gameplay outcome has been exhaustively tested.

## Perks

Perks can be edited independently of employee stats. Positive-only actions leave the existing negative and neutral classification unchanged; those perks can be managed manually.

## Design / Work Priority

Optional extended Design / Work Priority supports up to 200% per category. The normal limit is used when the option is disabled. The implementation covers normal games and the supported sequel, spinoff, remaster, port, contract, Paid Addon, and MMO Addon paths.

## Game Updates

Game Update content contribution can be configured up to 100%. Vanilla calculations remain active when this option is disabled.

## Performance

Employee-list polling is conditional on the editor being open or Auto-Max being enabled; priority-menu updates are handled only when needed. The editor does not force employee stats every frame. Performance depends on the game and other installed plugins; this project does not claim an exhaustive whole-game profiling result.

## Safe Limits

- Skills: 0–1000.
- Motivation: 0–100.
- Design / Work Priority: 0–200% per category.
- Game Update Content: 0–100%.

Non-finite or unsafe values are validated or clamped before the mod applies them. **Reset cheats to vanilla** disables optional cheats, returns employee skills to vanilla-compatible values, and clears employee lock records.

## Requirements

- Mad Games Tycoon 2.
- BepInEx 5 installed for the game.
- For building: .NET SDK with .NET Standard 2.1 support and game-managed assemblies from your own installation.

## Installation

1. Download `CharacterEditorDeluxe.dll` from the GitHub Release assets.
2. Create `BepInEx\plugins\CharacterEditorDeluxe\` in the game installation if it does not exist.
3. Copy `CharacterEditorDeluxe.dll` into that folder.
4. Start the game. BepInEx creates the configuration at `BepInEx\config\com.codex.mgt2.charactereditordeluxe.cfg`.

## Controls

Press **F8** to open or close the editor. Select an employee with the navigation arrows, stage changes, then apply them to the selected employee or all employees. The Employees tab contains stat and motivation locks; the other tabs contain perk, priority, update-content, and safety settings.

## Build

Set `MGT2_DIR` to your Mad Games Tycoon 2 installation, then build the Release configuration:

```powershell
$env:MGT2_DIR = "C:\Path\To\Mad Games Tycoon 2"
dotnet build .\CharacterEditorDeluxe.csproj -c Release
```

The build output is `bin\Release\netstandard2.1\CharacterEditorDeluxe.dll`. The synchronized release artifact is `Release\CharacterEditorDeluxe.dll`.

## Compatibility

The mod targets Mad Games Tycoon 2 with BepInEx 5. Other plugins may patch the same game methods; compatibility with every plugin combination is not guaranteed. This repository does not include game files, Unity assemblies, BepInEx binaries, saves, logs, or third-party mods.

## Disclaimer

This is an unofficial, player-created mod and is not affiliated with or endorsed by Eggcode, the developer or publisher of Mad Games Tycoon 2, Unity Technologies, BepInEx, or Harmony. Use it at your own risk and keep backups of important saves.

The MIT License applies only to the original Character Editor Deluxe source in this repository. Game software, assets, names, trademarks, and third-party libraries remain the property of their respective owners.
