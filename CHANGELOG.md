# Changelog

## Unreleased

- Check offline saves on a background thread at launcher start and after Install/Update installs the mod, opening Convert Save Data once per installation and installed mod data when any save may need conversion or a source-version choice; make the backup folder path in that window open the folder.
- Redesign the Launch view to anchor Start Game and installation/backup status beside the Mod Only, Mod + D2RLoader, and Ladder play mode cards, keeping mode cards horizontal down to Steam Deck widths.
- Make Loot Filter, Server Regions, Ladder Safeguards, Install Directory, and Installed Plugins/Patches collapsible section cards that remember their expanded/collapsed state across navigation and restarts, with verified installations defaulting to collapsed across all screens.
- Remember the last opened navigation page across restarts instead of opening to News & Announcements on startup, and restore the existing launcher window from the system tray when a second instance is started.
- Move the D2R Reimagined logo into the top version/account header, rename the JSON mod add-ons navigation and pages to Mod Add-ons, and add 110%, 120%, and 150% UI scaling options.
- Add searchable, filterable D2RLoader plugin and patch management on Launch with in-place enable/disable toggles outside Ladder and local DLL/ZIP installation with previews, backups, and rollback.
- Add a built-in plugin and patch config editor with line numbers, current-line highlighting, JSON validation, external-edit detection, and rolling backups.
- Add website account loot-filter selection and installation (`Apply Filter`) with compact status indicators, three rolling filter backups, and preservation of custom `filter.json` files across Ladder bundle updates and repairs.
- Simplify Ladder Server Regions with an `Automatic (balanced)` checkbox that selects the two fastest available regions by default and locks individual region checkboxes until unchecked.
- Move the full installation editor from Settings to Install/Update and add a two-step first-run setup guide that is skipped automatically for existing signed-in installations.
- Add a dedicated Patch Notes navigation page that renders release notes natively from the D2R Reimagined Wiki with version selection, installed-version badges, inline links, and offline caching.
- Make native Steam shortcuts select AppImage extraction automatically when Steam's runtime lacks FUSE while preserving normal mounting where available.
- Allow signed Ladder package installs while Explorer holds the mod folder open, retaining backups and rollback when a file is genuinely locked.
- Keep players' D2RLoader plugin configs and logs across Ladder bundle installs; configs the bundle ships (mod.toml and force-shipped rule configs, except the player-owned filter.json) still come from the signed bundle. Every other file under d2rloader/config (including per-character folders like Controller QOL's) is the player's: it carries over and no longer blocks Ladder launches as "outside the signed package".
- Keep only the three most recent Ladder bundle backups and optional-extension backups; older full mod copies are deleted after a successful install.
- Integrate official v0.16.7 and its approved Ladder runtime config-name matching fix.
- Add a dedicated native Linux Steam launcher session for Online, Offline and Ladder, preserving base-game authentication and vanilla D2R launching; keep the hidden game-session helper as an optional compatibility fallback.
- Add Desktop Mode Steam shortcut setup with account discovery, stable AppImage installation, backups, permission preservation, and migration to Diablo II: Reimagined shortcut names without duplicate entries.
- Generate a combined gamepad/trackpad-mouse layout with trackpad left-click, preserving normal triggers and buttons; allow custom layouts to be retained during setup.
- Make controller-template configuration optional for native Linux Steam shortcut setup; missing, incompatible or unwritable templates leave shortcut registration usable and saved layouts intact.
- Preserve the original inherited environment for normal launch processes when the launcher was opened through a dedicated Steam session; keep Battle.net, D2RMM, Lutris and Windows routing unchanged.
- Detect Proton from the initialized base-game prefix while retaining saved Proton selections.
- Preserve generated/custom launch arguments, including Force Desktop and Ladder map reset; retain native Windows launch previews and validate the official Steam installation/prefix.
- Keep launcher scrollbars expanded and add Exit Launcher to the main window.
- Document player setup and automated/live validation; retain the existing Battle.net/Lutris launch flow.
- Reserve Steam launches before preparing mod/session files and hold ownership through game-exit credential cleanup, preventing delayed-start overlapping launches from overwriting credentials.
- Resolve filesystem aliases before matching Wine game processes, while distinguishing separate copies of the executable.
- Select native/Flatpak Steam from the chosen installation and preserve Flatpak Offline routing when both clients coexist.
- Add an off-by-default game-only LSFG option for both dedicated and fallback sessions; allow it to be disabled before Steam shortcut setup.
- Hide the internal Launcher Game Session shortcut during setup, migrating existing visibility in place while retaining its launch ID and preserving unrelated shortcut data.
- Cache the registered Steam session identity instead of passing launcher-only environment markers, document Steam integration helpers, and group Linux Steam fallback/LSFG settings under Advanced.
