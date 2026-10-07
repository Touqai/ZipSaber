# Zip Saber

A Mod That allows you to drag and drop .zip/map files into the beat saber window and Extracting and Importing to CustomWipLevels before refreshing song list.


### Mod Was Vibecoded So if you would like to recreate this go for it!

## Dependencies
BSIPA ^4.3.0
SongCore ^3.14.15
BSML ^1.12.0

## Mod Settings UI
Toggle to Delete WIP Maps that were dragged on Game Close
Toggle to add map prompt on whether it goes to WIP Levels or Custom Levels

## Mod Manager
Delete mods whilst In game Prompt to Delete and Restart or to delete on close
Install mods in game from beatmods.com with the choice to auto restart and install

## Enable / Disable Mods Without Restarting
Each mod in the Mod Manager has an On/Off button. Toggling applies immediately (the mod's OnDisable/OnEnable runs and BSIPA treats it as disabled/enabled), and the choice is saved to BSIPA's `UserData/Disabled Mods.json` so it sticks on the next launch.
- Disabling a mod other mods depend on asks first, then disables those too
- Enabling a mod also enables anything it needs that is turned off
- Mods disabled before launch are loaded live when enabled
- **Reload Menu** soft-reloads the menu (like pressing OK in game settings) so mods that add menus or Zenject bindings fully apply/unload — no game restart
- BSIPA, ZipSaber and its dependencies (BSML, SongCore) are shown as `Core` and can't be toggled

## Mod Manager extras
- **Settings cog** (top right of the Mod Manager): drop-prompt and delete-on-close toggles, a custom WIP map folder, and an accent colour knob with presets that recolours the Mod Manager UI.
- **Custom WIP folder**: point dropped WIP maps at any folder (full path). ZipSaber registers it with SongCore so the maps show up in the WIP pack. Hit *Default* to go back to `Beat Saber_Data/CustomWipLevels`.
- **Note Slots** (slot machine icon): a three-reel slot machine made of Beat Saber notes. Tick *I accept the risk*, pull the lever. Jackpot = three identical notes pointing the same way: 5 seconds of fireworks. Three dots = Beat Saber closes, three bombs = the menu glitches out for 30 seconds (gentle, no flashing). Hover the ? for every result and its odds. Uses the game's own UI/cut/bomb sounds where available, with synthesized slot sounds filling the gaps.

## Drag and Drop Sabers, Notes & More
Drop `.saber`/`.whacker` (CustomSabers), `.bloq`/`.note` (CustomNotes), `.plat` (CustomPlatforms), `.avatar` (CustomAvatars) or `.pixie`/`.box` (CustomWalls) files, or a zip of them, onto the game window. They're copied to the right folder and a popup tells you what was installed.

## Drag and Drop Mods
drag mods in as long as they have an embedded manifest, itll import and come up with a prompt asking if you would like to restart

## Drag and Drop Map Files
Drag maps to go into customwiplevels or customlevels

## What if I want to compile this myself?
I used .Net SDK 6.0.428
And Visual Studio Code

![437970791-79a7ef39-888c-4237-9a59-4f00df479504](https://github.com/user-attachments/assets/48d4879f-08e9-43c0-aaf2-76497bf13c4c)
