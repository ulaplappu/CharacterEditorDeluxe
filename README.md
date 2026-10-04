# Character Editor Deluxe for Mad Games Tycoon 2

Character Editor Deluxe is a BepInEx 5 plugin for editing player employees and enabling optional player-only development cheats in Mad Games Tycoon 2.

## Features

- Edit nine employee attributes up to 9999.
- Lock edited skills against vanilla learning caps and resets.
- Lock motivation independently for each employee.
- Enable, disable, or clear employee perks.
- Apply edits to one employee or all current player employees.
- Optionally maximize newly hired employees after initialization.
- Raise Design Priority limits and allow totals above 100%.
- Set real Game Update content percentages with maximums of 100, 200, 400, 1000, or 9999%.
- Preserve edited values and lock metadata through normal save and load.

Game Update percentages affect update category points, quality, and workload. Update prices and player cash remain on the vanilla calculation.

## Requirements

- Mad Games Tycoon 2 for Windows.
- BepInEx 5 installed in the game directory.
- The game-managed assemblies from the user's own installation when compiling.
- .NET SDK capable of building .NET Standard 2.1 projects.

No game assemblies, Unity assemblies, BepInEx binaries, saves, or other mods are included in this repository.

## Install

1. Download `CharacterEditorDeluxe.dll` from the release package.
2. Create `BepInEx\plugins\CharacterEditorDeluxe` inside the game directory.
3. Copy the DLL into that folder.
4. Start the game. BepInEx creates the configuration file at:

   `BepInEx\config\com.codex.mgt2.charactereditordeluxe.cfg`

## Controls

Press **F8** to open or close Character Editor Deluxe.

- Use the employee arrows to select a character.
- Stage stat and perk changes, then apply them to the selected employee or all employees.
- **Lock Stats** protects the selected employee's edited skills.
- **Lock Motivation** protects the selected employee's current motivation.
- **Auto-max new employees** applies configured maximums to new hires.
- **Uncapped Design Priority** controls the priority maximum and total-limit override.
- **Game Update content percentages** enables real per-item update percentages while the Game Update menu is open.
- **Update % Max** selects 100, 200, 400, 1000, or 9999.
- Vanilla Game Update content is 2% per selected item.

Disabling an optional cheat restores vanilla calculations for future actions. Existing values already stored in a save remain part of that save.

## Build

The project resolves required references from your own game installation through the `MGT2_DIR` MSBuild property. Nothing from the game is copied into the repository.

PowerShell:

```powershell
$env:MGT2_DIR = "C:\Path\To\Mad Games Tycoon 2"
dotnet build .\CharacterEditorDeluxe.csproj -c Release
```

Alternatively, pass the property directly:

```powershell
dotnet build .\CharacterEditorDeluxe.csproj -c Release -p:MGT2_DIR="C:\Path\To\Mad Games Tycoon 2"
```

The output DLL is written to `bin\Release\netstandard2.1\CharacterEditorDeluxe.dll`.

## Disclaimer

This is an unofficial, player-created mod and is not affiliated with or endorsed by Eggcode, the developer or publisher of Mad Games Tycoon 2, Unity Technologies, BepInEx, or Harmony.

Use the mod at your own risk and keep backups of important saves. The MIT License applies only to the original Character Editor Deluxe source in this repository. Mad Games Tycoon 2, Unity, BepInEx, Harmony, and all related binaries, assets, names, and trademarks remain the property of their respective owners and are not distributed under this license.
