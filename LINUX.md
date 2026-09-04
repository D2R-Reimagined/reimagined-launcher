# Linux Build & Run

## Prerequisites

- For a release download, no .NET installation is required.
- For development, install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Steam users must enable Steam Play/Proton for Diablo II: Resurrected.
- Battle.net installations require `wine` to be available on `PATH`.
- Lutris users require `lutris` to be available on `PATH`, with Diablo II: Resurrected already installed as a Lutris game.

## Release download

Download `D2RReimagined.ReimaginedLauncher.AppImage` from the project's GitHub release, make it executable, and run it:

```bash
chmod +x D2RReimagined.ReimaginedLauncher.AppImage
./D2RReimagined.ReimaginedLauncher.AppImage
```

The launcher detects native and Flatpak Steam installations in their standard locations, including Steam libraries configured in `libraryfolders.vdf`. Custom locations can be selected from the Launch page.

## Lutris

Pick **Lutris** as the installation type on the Launch page (Linux only - the item is disabled on Windows) and select your Diablo II: Resurrected entry from the dropdown. The install directory and Wine prefix are read from Lutris, so there is nothing to browse for; the selected entry still goes through the same `D2R.exe` check as any other install directory. Nothing under `~/.local/share/lutris` is written to.

Set your launch parameters (`-mod Reimagined -txt`) in Lutris itself:

> Lutris → right-click the game → Configure → Game options → Arguments

The launcher's parameter field is disabled for Lutris profiles because Lutris' `lutris:rungameid/<id>` URI accepts no game arguments.

Whichever executable the Lutris entry points at is what runs - `D2R.exe` or `D2RLoader.exe`. Saves and backups resolve through the prefix recorded in the game's Lutris config.

## Steps

```bash
# Restore packages
dotnet restore ReimaginedLauncher.sln

# Build
dotnet build ReimaginedLauncher.sln

# Run the launcher
dotnet run --project ReimaginedLauncher/ReimaginedLauncher.csproj
```

## Publishing

To build a self-contained Linux binary, specify the Linux runtime and Production configuration:

```bash
dotnet publish ReimaginedLauncher/ReimaginedLauncher.csproj -c Production -r linux-x64 --self-contained
```

Output will be in `ReimaginedLauncher/bin/Production/net10.0/linux-x64/publish/`.

## Notes

- The D2RLoader Online experience is currently Windows-only. Linux profiles continue to use the Offline launch path until Loader startup through Proton/Wine has been validated end to end.
- Launcher self-updates are supported by the packaged AppImage.
- Steam launches use app ID `2536520` and pass the same Reimagined launch parameters as Windows.
- Battle.net installations are launched through Wine. When the selected game is inside a Wine prefix, the launcher derives and supplies `WINEPREFIX` automatically.
- Lutris launches are handed off to Lutris itself (`env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/<id>`), the same form Lutris writes into its own desktop shortcuts. Minimize to tray works across that handoff.
- Save backup discovery includes native Steam, custom Steam libraries, Flatpak Steam, Wine prefixes, and Lutris prefixes. A custom save directory can still be selected in Settings.
