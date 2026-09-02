# Reimagined Launcher
This is a launcher built by the Reimagined team for D2R.

## Purpose
The reimagined launcher is intended to be used with the [Diablo 2 Resurrected - Reimagined Mod](https://www.nexusmods.com/diablo2resurrected/mods/503).

Source code for the mod can be found here: [https://github.com/D2R-Reimagined/d2r-reimagined-mod](https://github.com/D2R-Reimagined/d2r-reimagined-mod)


## Features
* Login with Nexus Mods
* Sign in to the D2R Reimagined API through the website
* 1-click Install for Premium NM Users (2 click install for non-premium NM users)
* Easy Launch Parameter Editing
* Per-installation Offline, Online (D2RLoader TCP/IP), and active Ladder experiences
* Active ladder schedules loaded from the D2R Reimagined API
* Ladder characters stored server-side, in their own save folder, with the bundled `server-saves` plugin installed automatically
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

## Ladder

A ladder launch is an Online (D2RLoader) launch with three extra guarantees: only the ladder's approved extensions are loaded, only clean Reimagined files are used, and **characters live on the Reimagined API rather than on the player's machine**.

It requires a signed-in D2R Reimagined website account. Without one the launch is blocked rather than silently falling back to local characters.

### Ladder characters have their own save folder

Ladder characters and a player's own Offline/Online characters are kept in physically separate directories. Before a ladder launch the launcher rewrites `savepath` in the mod's `modinfo.json`:

| Launch | Save folder under `Saved Games/Diablo II Resurrected/mods/` |
|---|---|
| Offline / Online | `ReimaginedThree/` |
| Ladder | `ReimaginedThree-<ladder-slug>-<ladder-id-prefix>/` |

For example, *Ben's Bitchin HC Ladder* becomes `ReimaginedThree-Bens-Bitchin-HC-Ladder-d630a3fc`. The id suffix keeps two ladders apart even if their names are similar, so a player can participate in several ladders at once without their characters colliding.

Because the folders are separate, **nothing is ever moved**. A player's own characters are never touched, hidden, or relocated, and switching back to Offline or Online just points the game at the original folder again. Removing the plugin, closing the launcher, or crashing mid-session cannot strand them.

Each ladder also gets its own shared stash, since `.d2i` lives in the save folder. Items cannot be moved between a ladder and offline play.

The launcher seeds a ladder folder with `Settings.json`, `lootfilter.json`, and any `*.fltr` loot filters from the normal Reimagined save folder when a destination file does not exist. Characters and the shared stash are deliberately not carried across. Existing files are never overwritten, so anything tuned inside a ladder is kept.

Every non-ladder launch restores the normal `savepath`, as does every ladder-launch failure path.

### The server-saves plugin

Players do not need to download this plugin. The launcher ships its own copy under `Assets/D2RLoaderPlugins/server-saves/` and installs it into `mods/Reimagined/d2rloader/plugins/` before every ladder launch, replacing it whenever the bundled build differs. A newer plugin shipped in a launcher update therefore rolls out automatically.

Installation happens *before* the ladder extension policy runs, so the policy still decides whether the plugin is actually allowed. Like any other extension it must match the active ladder's allowlist by kind, filename, and SHA-256, and it must be checked in the ladder extension list. If it is not approved or not checked, it is moved to `ladder-disabled/` and the launch proceeds on local characters — the same outcome as a ladder with no server saves configured.

> **Note for maintainers:** the allowlist matches on content hash, so a rebuilt `d2rl-server-saves.dll` is an unapproved DLL until the `LadderAllowedExtension` row's SHA-256 is updated for every ladder that approves it.

### Who talks to the API

The launcher and the plugin both call the API, but never the same endpoints, and the plugin does **not** route its traffic through the launcher.

| | Calls | Never touches |
|---|---|---|
| Launcher | `/auth/launcher/*`, `/ladders/active`, ladder extension policy | `/characters/saves*` |
| `server-saves` plugin | `/characters/saves*` | anything auth-related |

The only thing they share is the access token: the launcher obtains and refreshes it, writes it into `d2rloader/config/server-saves.toml`, and the plugin uses it as a bearer token on its own requests. Once the game starts, the plugin is self-sufficient and the launcher can be closed.

### Order of operations

```
Launcher, before the game starts
  1. D2RLoader extensions   install the bundled server-saves plugin, then apply the
                            ladder allowlist (unapproved/unchecked -> ladder-disabled/)
  2. Mod tweaks             restore clean Reimagined files, add the ladder banner
  3. Server saves           refresh the access token, write server-saves.toml,
                            redirect savepath and seed the ladder folder
  4. Backup                 if automatic backups are enabled
  5. Launch                 D2RLoader.exe -> D2R.exe

Plugin, inside the game process
  6. On load                pull the ladder's character manifest and reconcile the
                            folder against it, before the character screen is drawn
  7. During play            push each save, register new characters, forward deletes
  8. On exit                final push; park anything the server never accepted
```

Step 3 runs last on purpose. The save folder is only redirected once the plugin is confirmed installed, approved, and holding a valid token — otherwise a player would land in an empty folder where any character they created would never sync.

### Inside the ladder folder, the server is the authority

On each launch the plugin reconciles the ladder folder against the server's manifest. A character the server does not list is removed, and one whose bytes differ is replaced by the server's copy. If the API cannot be reached at all, the folder is emptied and the character screen is empty: no server, no characters.

Nothing is deleted outright. Anything displaced is copied into `.server-saves/backups/` first, and progress the server never accepted is parked in `.server-saves/pending-upload/` and re-offered on the next launch. Both live in a subfolder that D2R does not enumerate, so they are backups only and can never be loaded as characters.

## API configuration

Debug builds query the local Reimagined API at `http://localhost:5000/`. Other builds query `https://api.d2r-reimagined.com/`. Set `D2R_REIMAGINED_API_BASE_URL` to an absolute HTTP or HTTPS URL to override either default; for example, `$env:D2R_REIMAGINED_API_BASE_URL = "http://localhost:5000/"` in PowerShell before starting the launcher.

Launcher account sign-in opens the website in the system browser and returns through a random loopback port using a one-use PKCE authorization code. Debug builds open `http://localhost:9500/`; other builds open `https://www.d2r-reimagined.com/`. Set `D2R_REIMAGINED_WEBSITE_BASE_URL` to override the website origin during local integration testing.

Signed ladder packages also require the public half of the API's ECDSA signing key. Production builds can ship it as `Assets/LadderBundleSigningKeys/<key-id>.pem`. For local testing, point the launcher at the generated public PEM before starting it:

```powershell
$env:D2R_REIMAGINED_BUNDLE_PUBLIC_KEY_PATH = "C:\dev\d2r\reimagined-api\local-keys\local-development.pem"
```

The launcher rejects packages when the API descriptor, archive SHA-256, signed manifest, compatibility contract, declared file list, or installed file hashes differ. Schema-v2 packages manage the complete `mods/Reimagined` tree, including data JSON and TXT files; undeclared local files also block launch and are removed during repair. The private signing key must never be placed in this repository.

## Downloads

Windows x64 and Linux x64 downloads are published on the [GitHub Releases](https://github.com/D2R-Reimagined/reimagined-launcher/releases) page. Linux users should download the AppImage and follow the short setup instructions in [LINUX.md](LINUX.md).

## Contributing
This is a .NET project and utilizes our D2RReimagined.FileExtensions Nuget. Contributing can be done on either repo.
1) Fork the Repo
2) Submit a PR
3) Give us a shout in the Discord
