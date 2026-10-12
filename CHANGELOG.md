# Changelog

## Unreleased

- Integrate official v0.16.7 and its approved Ladder runtime config-name matching fix.
- Add a dedicated native Steam Deck launcher session for Online, Offline and Ladder, preserving base-game authentication and vanilla D2R launching; keep the hidden game-session helper as an optional compatibility fallback.
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
