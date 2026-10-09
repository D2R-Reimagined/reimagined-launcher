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

Steam Deck Gaming Mode uses the launcher-owned game-session handoff described below. Online/Offline play, controls, focus, and persistent launcher return were validated, along with Ladder package download and launch on v0.16.7. See the [setup guide](docs/steam-deck-setup.md) and [validation notes](docs/steam-deck-validation.md).

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


## Native Steam Gaming Mode handoff

The native launcher can register its own Steam shortcuts; Decky and Lutris are not required. This first pass supports the Steam edition of D2R with an initialized official `2536520` prefix and native Steam. Keep using the same Proton tool as the base game. Start the base game and sign in when authentication renewal is required.

For one-time setup, stay in Desktop Mode and open the launcher AppImage. Under **Launch → Install directory → Steam shortcuts**, check the selected Steam account. In desktop Steam, choose **Steam → Exit**, then return to the launcher and click **Set up Steam shortcuts**. Steam must be fully exited; closing its window is insufficient. The button copies the AppImage into a stable launcher-owned location, backs up existing shortcuts, and adds the launcher and game-session entries. Close the desktop launcher, restart Steam, and open **Diablo II: Reimagined (Native Launcher)** in Gaming Mode. Leave Reset launcher layout to Mouse Only checked for trackpad input, or uncheck it to retain a custom layout. No SSH or terminal command is required.

The AppImage installed by setup lives at `~/.local/share/ReimaginedLauncher/launcher/D2RReimagined.ReimaginedLauncher.AppImage`; moving or removing the Downloads copy afterward does not invalidate these shortcuts. Existing manual Proton selections and game settings are preserved. If several Steam accounts exist, select the intended account; the most recent account is preselected when Steam's login metadata identifies it unambiguously. Flatpak Steam and Gaming Mode registration are rejected with instructions.

For developer tooling, the original explicit command remains available with Steam fully exited:

```sh
/path/to/ReimaginedLauncher --install-steam-handoff "$HOME/.local/share/Steam" YOUR_STEAM_USERDATA_ID /path/to/ReimaginedLauncher
```

Use your existing numeric directory under Steam's `userdata`, and a stable executable path that accepts command-line arguments (the real AppImage can be used). Setup adds **Diablo II: Reimagined (Native Launcher)** and **Diablo II: Reimagined (Launcher Game Session)**, backs up `shortcuts.vdf`, preserves existing binary entries, and refuses unknown/corrupt formats or conflicting IDs. Restart Steam, open **Native Launcher**, select the Steam installation and real Proton executable, then launch the selected experience. Do not force Proton on either native launcher shortcut. The game-session entry is a helper: start games through the GUI so it receives a current request.

The GUI hands off through its own game-session shortcut. That helper launches D2RLoader for Online/Ladder and D2R.exe for Offline, retaining generated/custom arguments and the official prefix. It monitors the actual Wine executable rather than a Steam command or Python wrapper. Gaming Mode hides the GUI during play regardless of the desktop minimize preference, leaves game/overlay focus to Steam’s separate session, and restores the GUI on exit or failure. No authentication cookie is copied.

Requests/status are stored under `~/.local/share/ReimaginedLauncher/steam-handoff/` (the .NET local application-data directory). Concurrent launches are rejected. A missing registration or startup timeout returns an actionable failure; the launcher does not retry by silently launching in the GUI's shared Steam session. This handoff does not support Flatpak Steam or Battle.net prefixes yet. Overlay, Ladder/account workflows, fresh-install setup UX, and live regression results are tracked in `docs/steam-deck-validation.md`.

To revert, close the launcher and Steam, restore the setup-created shortcut backup (only if no newer library edits would be lost), and remove the handoff registration file. Existing Decky shortcuts and the official game prefix are untouched. Keep the ordinary desktop launch route for machines without handoff registration.


### Launcher controls on Steam Deck

Native Launcher is a mouse/keyboard Avalonia GUI; it currently has no native gamepad navigation. The setup reset option is checked by default and applies Steam's installed Mouse Only template to the owned launcher preference. Uncheck it to preserve an existing custom launcher layout. Check the trackpad and mouse clicking after restarting Steam. If needed, set the **Native Launcher** entry's controller layout to **Templates → Mouse Only** and apply it. Keep **Launcher Game Session** and D2R on their gamepad layouts. These separate entries allow Steam to switch between GUI mouse input and game controls automatically on focus changes.

The setup button changes only owned launcher preferences in backed-up controller configuration files while Steam is fully exited. It preserves unrelated preferences and the Deck-wide desktop/chord layout; unchecked reset preserves custom launcher choices. Mouse Only reset is live-validated, and the checked-by-default UI uses that same path. Manual template selection remains the fallback.
