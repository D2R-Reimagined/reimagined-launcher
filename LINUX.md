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

Pick **Lutris** as the installation type on the Launch page (Linux only - the item is disabled on Windows) and select your Diablo II: Resurrected entry from the dropdown. The install directory and Wine prefix are read from Lutris, so there is nothing to browse for; the selected entry still goes through the same `D2R.exe` check as any other install directory.

Whichever executable the Lutris entry points at is what runs - `D2R.exe` or `D2RLoader.exe`. The dropdown names it after the game. Saves and backups resolve through the prefix recorded in the game's Lutris config.

### Launch options

Lutris' `lutris:rungameid/<id>` URI accepts no game arguments, so the launcher writes the Settings launch options into the selected game's Lutris arguments (Configure → Game options → Arguments) just before launching. Only its own options are touched - `-enablerespec`, `-resetofflinemaps`, `-players`, `-norumble`, `-forcedesktop`, `-nosound` and `-seed`; anything else in that field keeps its place. This is the only thing the launcher writes under `~/.local/share/lutris`.

Picking a game in the dropdown imports the options already in its arguments, so a flag you set in Lutris shows up ticked in Settings instead of being dropped. After that, Settings is the source of truth.

Selecting the mod is separate, and the launcher never touches it in the Lutris arguments. A `D2RLoader.exe` entry is already covered: the launcher writes `default_mod = "Reimagined"` into `d2rloader/config/d2rloader.toml` before every launch. Putting `-mod Reimagined` in the Lutris arguments works too - D2RLoader accepts it on the command line and merges it with its own `launch_arguments`. An entry that starts `D2R.exe` directly needs `-mod Reimagined -txt` in the Lutris arguments.

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

## D2RLoader Online / Ladder on Linux

The Online and Ladder experiences are supported on Linux through **Lutris**.

### Setup

1. Install **Battle.net via Lutris** using the standard Lutris installer.
2. Inside Battle.net, install **Diablo II: Resurrected**.
3. In the launcher, pick **Lutris** as the installation type and select your D2R entry.
4. Switch the experience to **Online** or **Ladder**.
5. The launcher will prompt to install **D2RLoader** if it is not present. Let it download and extract into the D2R folder.
6. In **Lutris**, edit the D2R game entry:
   - Change the **Executable** from `D2R.exe` to **`D2RLoader.exe`**.
   - Remove any `-mod Reimagined -txt` arguments if you added them manually — D2RLoader handles mod selection.
7. Launch from the launcher. Lutris will start `D2RLoader.exe` with Reimagined selected.

### Why Lutris is required for Online

D2RLoader needs to communicate with the Steam client for authentication. Running it through standalone Wine does not provide that integration. Lutris handles the Wine/Proton runtime setup that bridges D2RLoader to Steam.

## Notes

- Launcher self-updates are supported by the packaged AppImage.
- Steam launches use app ID `2536520` and pass the same Reimagined launch parameters as Windows.
- Battle.net installations are launched through Wine. When the selected game is inside a Wine prefix, the launcher derives and supplies `WINEPREFIX` automatically.
- Lutris launches are handed off to Lutris itself (`env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/<id>`), the same form Lutris writes into its own desktop shortcuts. Minimize to tray works across that handoff.
- Save backup discovery includes native Steam, custom Steam libraries, Flatpak Steam, Wine prefixes, and Lutris prefixes. A custom save directory can still be selected in Settings.
