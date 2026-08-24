# Reimagined Launcher
This is a launcher built by the Reimagined team for D2R.

## Purpose
The reimagined launcher is intended to be used with the [Diablo 2 Resurrected - Reimagined Mod](https://www.nexusmods.com/diablo2resurrected/mods/503).

Source code for the mod can be found here: [https://github.com/D2R-Reimagined/d2r-reimagined-mod](https://github.com/D2R-Reimagined/d2r-reimagined-mod)


## Features
* Login with Nexus Mods
* 1-click Install for Premium NM Users (2 click install for non-premium NM users)
* Easy Launch Parameter Editing
* Per-installation Offline, Online (D2RLoader TCP/IP), and active Ladder experiences
* Active ladder schedules loaded from the D2R Reimagined API
* Ladder-safe clean-file restoration with launcher tweaks/plugins disabled and SHA-256-verified D2RLoader extension choices
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

Ladder launches also use D2RLoader, but restore the launcher-managed clean Reimagined files and skip all launcher tweaks and launcher plugins. D2RLoader plugins and patches must match the active ladder's API allowlist by kind, filename, and SHA-256. Approved extensions are unchecked by default; unapproved or unchecked files are moved into `ladder-disabled/plugins` or `ladder-disabled/patches` under their existing global or Reimagined D2RLoader root. The launcher restores those files before the next non-ladder launch.

## API configuration

Debug builds query the local Reimagined API at `http://localhost:5000/`. Other builds query `https://api.d2r-reimagined.com/`. Set `D2R_REIMAGINED_API_BASE_URL` to an absolute HTTP or HTTPS URL to override either default; for example, `$env:D2R_REIMAGINED_API_BASE_URL = "http://localhost:5000/"` in PowerShell before starting the launcher.

## Downloads

Windows x64 and Linux x64 downloads are published on the [GitHub Releases](https://github.com/D2R-Reimagined/reimagined-launcher/releases) page. Linux users should download the AppImage and follow the short setup instructions in [LINUX.md](LINUX.md).

## Contributing
This is a .NET project and utilizes our D2RReimagined.FileExtensions Nuget. Contributing can be done on either repo.
1) Fork the Repo
2) Submit a PR
3) Give us a shout in the Discord
