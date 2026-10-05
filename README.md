# Character Editor Deluxe

Version 1.0.7

An unofficial BepInEx plugin for editing employees and safe development options in Mad Games Tycoon 2.

## Features

- Edit employee stats and motivation on the game's 0–100 scale, with optional stat and motivation locks.
- Apply positive-only perks to one employee or in bulk. Neutral perks (CEO, Loyal, Nature Lover, Modest) and negative perks (Greedy, Unfocused, Untalented, Immunocompromised, Unlucky, Messy, Stress-Averse) remain manual-only.
- Bulk-edit employees and optionally auto-max new hires.
- Extend Design / Work Priority up to 200% per category for normal games, sequels, spinoffs, remasters, ports, contracts, Paid Addons, and MMO Addons.
- Set Game Update content contribution up to the safe 100% maximum.
- Protect modified values from invalid numeric input and overflow.

## Version 1.0.7 fixes

- Disabled Extended Design / Work Priority no longer clamps stored game priorities during load or persistence.
- Repeated stored-priority clamp warnings are limited to avoid per-game log spam.
- The prior nonexistent priority-menu lifecycle probe was removed to avoid a startup warning.

## Performance

The priority updater no longer performs recurring `FindObjectOfType` scene searches; it uses lifecycle hooks and cached menu references. Profiling of that updater measured a reduction from approximately 65 ms spikes to approximately 0.001–0.002 ms per steady-state call.

The mod also reduces unnecessary polling while the editor is closed, repeated refresh work, temporary allocations, and repetitive safety-cap logs. These are mod-specific measurements and code-path changes; they do not establish a full-game FPS improvement. Results vary with the game and other installed plugins.

## Requirements

- Mad Games Tycoon 2.
- BepInEx 5 installed for the game.
- For building: .NET SDK with .NET Standard 2.1 support and the game-managed assemblies from your own installation.

## Installation

1. Download `CharacterEditorDeluxe.dll` from the GitHub Release assets.
2. Create `BepInEx\plugins\CharacterEditorDeluxe\` in the game installation if it does not exist.
3. Copy `CharacterEditorDeluxe.dll` into that folder.
4. Start the game. BepInEx creates the configuration at `BepInEx\config\com.codex.mgt2.charactereditordeluxe.cfg`.

## Controls

Press **F8** to open or close the editor. Select an employee with the navigation arrows, stage changes, then apply them to the selected employee or all employees. The Employees tab includes per-employee motivation locks and the global stat-lock option; other tabs contain perk, priority, update-content, and safety settings.

## Safe limits

- Employee stats and motivation: 0–100.
- Extended Design / Work Priority: at most 200% per category.
- Game Update content: at most 100%.
- Non-finite and unsafe values are rejected or clamped before modified values reach game calculations.
- Disabling an optional feature restores vanilla calculations for future actions; values already saved remain in the save.
- **Reset cheats to vanilla** also clears employee lock records.

## Compatibility

The mod targets Mad Games Tycoon 2 with BepInEx 5. Extended priority hooks support the game and addon types listed above. Other plugins may patch the same game methods; compatibility with every mod combination is not guaranteed.

This repository does not include game files, Unity assemblies, BepInEx binaries, saves, logs, or third-party mods.

## Build

Set `MGT2_DIR` to your Mad Games Tycoon 2 installation, then build the Release configuration:

```powershell
$env:MGT2_DIR = "C:\Path\To\Mad Games Tycoon 2"
dotnet build .\CharacterEditorDeluxe.csproj -c Release
```

The build output is `bin\Release\netstandard2.1\CharacterEditorDeluxe.dll`. The synchronized release artifact is `Release\CharacterEditorDeluxe.dll`.

## Disclaimer

This is an unofficial, player-created mod and is not affiliated with or endorsed by Eggcode, the developer or publisher of Mad Games Tycoon 2, Unity Technologies, BepInEx, or Harmony. Use it at your own risk and keep backups of important saves.

The MIT License applies only to the original Character Editor Deluxe source in this repository. Game software, assets, names, trademarks, and third-party libraries remain the property of their respective owners.
