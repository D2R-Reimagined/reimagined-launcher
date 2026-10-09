# Steam Deck validation

Validated on a physical Steam Deck using native Steam, the Steam edition of Diablo II: Resurrected, and Proton Experimental. The integrated launcher includes official **v0.16.7** (`65f3532`), including its approved Ladder runtime config-name matching fix. That release's implementation and regression test are retained.

## Live validation

- Online and Offline launch, playable game, gamepad controls, automatic game focus, Steam-menu interaction, and return to a persistent launcher after normal game exit.
- Force Desktop (`-forcedesktop`) preservation and availability of multiplayer options.
- Ladder package download and launch with v0.16.7.
- Desktop Mode account sign-in, Steam install discovery, Nexus archive download detection/extraction, base-game authentication, automatic Proton detection, D2RLoader installation, and GUI shortcut setup.
- First-time setup followed by Online launch and launcher return.
- Launcher trackpad/R2 mouse input, gamepad input during play, and mouse input on return. Explicit Mouse Only reset through the setup button resolved an existing per-device Gamepad FPS override.
- Expanded scrollbars, Exit Launcher, and shortcut names beginning Diablo II:.
- Existing unrelated shortcuts preserved byte-for-byte. Linux atomic writes preserved both executable and non-executable file modes in a separate fixture.

The Mouse Only option is now checked by default and has been compiled; the underlying reset path is live-validated. A clean Steam Input profile with no historical launcher preference has not been independently tested. Offline was validated before the fresh-install reset. Multiplayer option availability does not establish a completed TCP/IP connection. Ladder validation covers package download and launch. Windows and Battle.net/Lutris paths received source/regression checks; no new live validation is claimed for those paths. Firefox's initial Reimagined sign-in redirected to the profile page and required reopening authorization.

## Automated validation

472 tests passed on Windows. Production Linux publish completed with zero warnings/errors. Coverage includes launch flags/overrides and Windows previews, official-prefix validation, Proton discovery, Steam account selection, shortcut parsing/collision rejection/idempotency/name migration, handoff state and Wine executable monitoring, and scoped controller preference migration/reset. These checks supplement the live results above.

## Runtime findings and contracts

- The official Steam app ID is `2536520`; its prefix is `steamapps/compatdata/2536520/pfx`. The launcher shares this initialized prefix for authentication and saves instead of creating a separate prefix or copying cookies.
- Authentication was observed at `drive_c/users/steamuser/AppData/Local/Blizzard Entertainment/ClientSdk/cookie.bin`; saves are under `drive_c/users/steamuser/Saved Games/Diablo II Resurrected`. Cookie contents were not inspected. Base-game sign-in is still required when authentication expires.
- Direct Proton children under the Steam-owned GUI reproduced launcher termination about ten seconds after game exit. An earlier trace recorded SIGINT/ SI_USER from native Steam, without a managed exception or core dump. Changing only compatibility or overlay IDs did not prevent it. Separate Steam game-session handoff kept the GUI alive.
- The production handoff uses launcher-owned shortcuts and request/status files under `~/.local/share/ReimaginedLauncher/steam-handoff`. It monitors the actual Wine executable, including prefix `dosdevices` mappings, rather than treating a Python Proton wrapper or Steam URI command as the game's lifetime.
- Final focus handling leaves game/overlay focus to Steam. Experimental Gamescope property writes were removed after producing focus/loading-screen issues. Repeated final launches ran immediately and returned correctly.
- Controller configuration was observed under `steamapps/common/Steam Controller Configs/<userdata-id>/config/configset_*.vdf`. Steam looked up the launcher as `diablo ii reimagined (native launcher)`, removing the display-name colon. A saved per-device selection overrode the generic Neptune default. Setup uses the observed normalized key and installed `controller_base/templates/controller_neptune_mouse.vdf`, with a scoped reset and backups. These are observed Steam internals, not a guaranteed public API; manual template selection remains available.
- A background NonSteamLaunchers scanner on the test Deck treated non-executable shortcut files as uninitialized. Preserving the original Linux file permissions during atomic replacement prevented its rewrite. The fix is generic and requires no NonSteamLaunchers installation.

Temporary tracing, Decky-dependent diagnostic shortcuts, machine-specific shortcut IDs, and manual Gamescope focus changes are not runtime dependencies. Flatpak Steam Online/Ladder and Gaming Mode shortcut registration remain unsupported. Follow [the setup guide](steam-deck-setup.md).

## 2026-10-09: integration with current main

Merged main at `46cd21a` (release CI version 0.16.11). Kept the project version exactly as main; future version changes remain owned by release CI. Combined profile-path Wine detection with main's D2R/D2R.exe fallback. The Steam handoff now retains main's token-ownership assignment and passes the installation directory and game token to the exit watcher. Confirmed handoff exit participates in main's session-secret cleanup; unconfirmed exit preserves the existing upstream policy. Non-handoff executable tracking retains main's implementation.

The Deck was unavailable and the tester requested skipping live validation. Earlier Deck results above apply to the earlier build; this new integration is covered by automated tests and Linux publishing, without a new live launch/return claim.

Final merged-build validation: all 512 tests passed, and self-contained Linux Production publish succeeded. No live Deck test was performed for this integration.
