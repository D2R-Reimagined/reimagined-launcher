# Reimagined Launcher
This is a launcher built by the Reimagined team for D2R.

## Purpose
The reimagined launcher is intended to be used with the [Diablo 2 Resurrected - Reimagined Mod](https://www.nexusmods.com/diablo2resurrected/mods/503).

Source code for the mod can be found here: [https://github.com/D2R-Reimagined/d2r-reimagined-mod](https://github.com/D2R-Reimagined/d2r-reimagined-mod)


## Features
* Login with Nexus Mods
* 1-click Install for Premium NM Users (2 click install for non-premium NM users)
* Easy Launch Parameter Editing
* Per-installation Offline and Online (D2RLoader TCP/IP) experiences
* D2RLoader plugin and patch discovery for global and Reimagined-specific extensions
* Modify Skill Hard Point Caps
* Modify Resist Penalties for Difficulties
* Ability to modify Skills and Attributes awarded per level
* Modify the visuals of the game. Such as removing splash image, vignette, etc
* Modify sounds of the game. Such as removing aura sounds
* Launcher Auto-Update

## D2RLoader support

On Windows, place `D2RLoader.exe` beside `D2R.exe` and select **Online** on the Launch page. The launcher starts D2RLoader directly with `-mod Reimagined -txt`; it does not route the Online experience through Battle.net. Use D2RLoader's TCP/IP option in-game to host or join.

The Launch page inventories extensions from both supported scopes without loading plugin DLLs:

- `<game>/d2rloader/plugins` and `<game>/d2rloader/patches`
- `<game>/mods/Reimagined/d2rloader/plugins` and `<game>/mods/Reimagined/d2rloader/patches`

The mod source remains the canonical Reimagined content source. D2RLoader compatibility assets can be packaged beneath the mod-local `d2rloader` folder when they are required; a separate copy of the full mod is not required.

## Downloads

Windows x64 and Linux x64 downloads are published on the [GitHub Releases](https://github.com/D2R-Reimagined/reimagined-launcher/releases) page. Linux users should download the AppImage and follow the short setup instructions in [LINUX.md](LINUX.md).

## Contributing
This is a .NET project and utilizes our D2RReimagined.FileExtensions Nuget. Contributing can be done on either repo.
1) Fork the Repo
2) Submit a PR
3) Give us a shout in the Discord
