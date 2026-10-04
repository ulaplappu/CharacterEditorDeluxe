# Character Editor Deluxe

Version 1.0.6

An unofficial BepInEx plugin for editing employees and safe development options in Mad Games Tycoon 2.

## Features

- Edit employee skills and motivation on the game's 0–100 scale.
- Lock edited stats and motivation against vanilla changes.
- Apply positive-only perks to one employee or all employees.
- Keep neutral perks (CEO, Loyal, Nature Lover, Modest) and negative perks (Greedy, Unfocused, Untalented, Immunocompromised, Unlucky, Messy, Stress-Averse) manual-only.
- Bulk-edit current employees and optionally auto-max new hires.
- Extended Design / Work Priority up to 200% per category, including normal games, sequels, spinoffs, remasters, ports, contracts, Paid Addons, and MMO Addons.
- Set Game Update content contributions up to the safe 100% maximum.
- Apply numeric range and overflow protections before modified values reach game calculations.

## Performance

Version 1.0.6 removes recurring `FindObjectOfType` scene searches from the design-priority update loop and uses lifecycle hooks and cached menu references instead. This removes the measured priority-updater spikes of approximately 65 ms; steady-state updater time measured approximately 0.001–0.002 ms per call.

The release also reduces unnecessary closed-window polling, repeated tooltip/stat refresh work, temporary allocations, and repetitive safety-cap log messages. These are mod-specific measurements and code-path changes; they do not establish a full-game FPS improvement.

## Requirements

- Mad Games Tycoon 2.
- BepInEx 5 installed for the game.
- For building: .NET SDK with .NET Standard 2.1 support and the game-managed assemblies from your own installation.

## Installation

1. Download `CharacterEditorDeluxe.dll` from the GitHub Release assets.
2. Create this folder in the game installation if it does not exist:

   ```text
   BepInEx\plugins\CharacterEditorDeluxe\
   ```

3. Copy `CharacterEditorDeluxe.dll` into that folder.
4. Start the game. BepInEx creates the configuration file at:

   ```text
   BepInEx\config\com.codex.mgt2.charactereditordeluxe.cfg
   ```

## Controls

Press **F8** to open or close the editor. Use the employee arrows to select an employee, stage changes, and apply them to the selected employee or all employees. The editor tabs contain the perk, priority, update-content, and safety options.

## Safe Limits

- Employee skills and motivation: 0–100.
- Extended Design / Work Priority: at most 200% per category.
- Game Update content: at most 100%.
- Non-finite and out-of-range values are rejected or clamped before use.
- Disabling an optional feature restores vanilla calculations for future actions; values already saved remain in the save.

## Compatibility

The mod targets Mad Games Tycoon 2 with BepInEx 5. Its extended priority hooks support normal game development and the listed game/addon types. Other plugins may also patch game methods; compatibility with every mod combination is not guaranteed.

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
