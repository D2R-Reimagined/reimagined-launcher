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

The Online and Ladder experiences are supported on Linux through **Steam** and **Lutris**.

### Steam Setup
In order for **Steam** to work, the desired **Proton** executable must be selected.

Use the Proton tool selected for the base game in Steam's Compatibility settings. Launch the base game from Steam and sign in before the first modded launch, and repeat this when its authentication expires. The launcher reuses the existing `steamapps/compatdata/2536520/pfx` directory for saves and authentication; it does not copy authentication files into a new prefix. Configured launch options, including Force Desktop (`-forcedesktop`), are also passed to Proton.

Native Linux Steam supports one dedicated launcher session for Online, Offline and Ladder on desktops and handhelds, including Gaming Mode systems. A hidden helper remains available as a compatibility fallback. See the [Linux Steam setup guide](docs/steam-deck-setup.md) and [validation notes](docs/steam-deck-validation.md).

This is because **Steam** does not allow the **D2RLoader** executable to run in **D2R**'s game context directly. To circumvent this, **Proton** is run with all the arguments that **Steam** would have passed to it, plus the executable set to **D2RLoader.exe** instead of **D2R.exe**. 

> **Note:** Flatpak Steam is not supported for Online/Ladder. D2RLoader needs to
> reach the Steam client, and a Proton process started outside the Flatpak sandbox
> cannot do so. More investigation is needed to see how to run it inside the Flatpak sandbox. In the meantime use native Steam or Lutris instead.

### Lutris Setup

1. Install **Battle.net via Lutris** using the standard Lutris installer.
2. Inside Battle.net, install **Diablo II: Resurrected**.
3. In the launcher, pick **Lutris** as the installation type and select your D2R entry.
4. Switch the experience to **Online** or **Ladder**.
5. The launcher will prompt to install **D2RLoader** if it is not present. Let it download and extract into the D2R folder.
6. In **Lutris**, edit the D2R game entry:
   - Change the **Executable** from `D2R.exe` to **`D2RLoader.exe`**.
   - Remove any `-mod Reimagined -txt` arguments if you added them manually — D2RLoader handles mod selection.
7. Launch from the launcher. Lutris will start `D2RLoader.exe` with Reimagined selected.

### Why Lutris works for Online while Wine doesn't

D2RLoader needs to communicate with the Steam client for authentication. Running it through standalone Wine does not provide that integration. Lutris handles the Wine/Proton runtime setup that bridges D2RLoader to Steam.

## Notes

- Launcher self-updates are supported by the packaged AppImage.
- Steam launches use app ID `2536520` and pass the same Reimagined launch parameters as Windows.
- Battle.net installations are launched through Wine. When the selected game is inside a Wine prefix, the launcher derives and supplies `WINEPREFIX` automatically.
- Lutris launches are handed off to Lutris itself (`env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/<id>`), the same form Lutris writes into its own desktop shortcuts. Minimize to tray works across that handoff.
- Save backup discovery includes native Steam, custom Steam libraries, Flatpak Steam, Wine prefixes, and Lutris prefixes. A custom save directory can still be selected in Settings.


## Native Linux Steam integration

Follow the [Linux Steam setup guide](docs/steam-deck-setup.md). Setup is available under **Launch → Install directory → Steam shortcuts**, with Steam fully exited. On systems with Gaming Mode, perform setup in Desktop Mode. It installs the AppImage at a stable location, preserves owned shortcut IDs and unrelated entries, and backs up changed shortcut/controller files. No terminal, SSH, Lutris, Decky or CEF debugging is required.

Launch **Diablo II: Reimagined (Native Launcher)** through Steam. Before GUI startup it validates and caches the registered shortcut identity, then initializes matching launcher/game authentication identity and reuses the official D2R prefix. Online/Ladder start D2RLoader; Offline starts D2R.exe directly through the selected Proton tool. Game exit returns to the persistent GUI, and Exit Launcher returns to the dedicated Steam page. The official D2R entry and its launch options are untouched.

Controller-layout configuration is optional. When Steam provides the supported Neptune template, setup generates its own copy with trackpad mouse movement and press-to-left-click; R2 remains the game trigger. The reset checkbox is shown only when the source template exists. Uncheck controller reset to retain a custom layout. If the template is missing, incompatible or unwritable, shortcut registration still completes and setup reports that layout configuration was skipped. Other Linux desktops and handhelds can keep their existing controller layouts or select one in Steam Controller Settings. On compatible devices, the manual fallback is Gamepad with Mouse Trackpad with Right Trackpad Click bound to Left Mouse Click.

Battle.net and Lutris children inherit the original launcher environment even when the GUI was opened through the dedicated Steam shortcut. D2RMM retains its install-only path, and Windows does not initialize the Linux Steam session.

Under **Launch → Install directory → Advanced**, the optional **Use separate game session (compatibility fallback)** setting uses the retained hidden Launcher Game Session helper. It monitors the actual Wine executable and keeps separate game-session lifecycle handling. After using it, Steam can retain the helper page in navigation history. Close the game before switching routes.

Also under **Advanced**, optional LSFG wraps only the Proton game process with an existing configured executable `~/lsfg`. It defaults off; the launcher does not install or configure LSFG. The Steam session mechanism itself does not require Decky. Launch preparation for both routes retains the cross-instance reservation through credential cleanup.

For developer tooling, registration remains available with Steam fully exited:

```sh
/path/to/ReimaginedLauncher --install-steam-handoff "$HOME/.local/share/Steam" YOUR_STEAM_USERDATA_ID /path/to/ReimaginedLauncher
```

Use an existing Steam userdata ID and stable executable. Native shortcut registration and the dedicated route exclude Flatpak profiles; existing Flatpak Offline, Battle.net/Wine and Lutris routes retain their behavior. Release AppImage updating and release versioning retain the existing project workflow; manually packaged test AppImages do not contain release updater metadata.

To revert, close the launcher and Steam and restore the setup-created backups only if that would not discard newer library edits. Remove the launcher registration to disable its ownership metadata. The official game prefix and existing third-party shortcuts remain intact. See [validation](docs/steam-deck-validation.md) for tested paths and limits.
