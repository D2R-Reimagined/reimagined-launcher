# Steam Deck validation

Validated on a physical Steam Deck with native Steam, the Steam edition of D2R and Proton Experimental. Source includes official v0.16.7 and its Ladder runtime config-name matching fix, plus integration with main at v0.16.11. Release versioning remains unchanged.

## Live checks completed

- Hero preview follow-up: the native shortcut failed before application startup
  under the newer `steamrt64` client container because no suitable fusermount
  helper was visible there. The same installed AppImage launched from Desktop
  Mode. A FUSE-free Steam launch opened the native Hero window with the existing
  Steam GameId. The automatic bootstrap is additionally checked with FUSE
  present, helpers absent and an explicit helper path; quoted arguments are
  preserved. See the local deployment receipt for the final automatic-launch
  result. No new game-session/controller validation is implied by this GUI test.

- Dedicated Online, Offline and Ladder launch, gamepad controls, persistent launcher return, and exit to the Native Launcher page without the helper page when fallback has not been used.
- Repeated Online → Offline → Ladder launches from one open launcher, with LSFG enabled, gamepad input during play and trackpad movement/clicking on return.
- Ladder package download and launch. Process inspection confirmed the integrated Ladder game uses the registered Native Launcher SteamGameId and compatibility app ID, rather than the helper identity.
- LSFG behavior reported working for Online, Offline and Ladder. D2R process inspection confirmed wrapper variables and the mapped liblsfg-vk.so. No frame-rate benchmark is claimed.
- Helper fallback with controls/LSFG/persistent return, then switching back to the dedicated route. Fallback left a Game Session page in Steam history; the page alone did not establish a surviving process.
- Steam's Exit game closed both game and launcher; reopening and launching Online succeeded.
- Suspend/resume restored gameplay, controls, LSFG, launcher mouse input and relaunch. Temporary graphical artifacts cleared after changing zones; their component/cause is unconfirmed. Ladder disconnected during sleep.
- Official D2R launched vanilla directly, signed in normally, and did not open the launcher or mod.
- Fresh shortcut-only setup recreated visible Native Launcher and hidden helper, with matching installed/Downloads AppImage hashes. All 136 unrelated shortcut bodies remained byte-identical. With controller reset unchecked, saved controller configs/preferences and Steam localconfig stayed hash-identical. Post-restart Online, controller input, launcher return and clean exit navigation passed.
- The generated combined controller layout supplies trackpad movement and press-to-left-click while preserving normal gamepad controls. Reset through setup and preserving an existing layout were both tested.
- Earlier Desktop Mode sign-in, installation discovery, Nexus archive detection/extraction, initial base-game authentication, Proton detection, D2RLoader installation, expanded scrollbars and Exit Launcher were tested. Force Desktop preservation and multiplayer option availability were verified; that does not establish a completed TCP/IP connection.

Dedicated-session tests were performed with Decky stopped, the original CEF marker backed up, and no listener on the launcher's former debugging port 8080. Decky and the original marker were restored afterward. No debugging connection is used by the final implementation.

This was one Deck. The fresh test retained game files, saves, accounts and authentication; it was not a completely new-account installation. Firefox's first Reimagined sign-in redirected to the profile page and required reopening authorization. New live Windows, Battle.net/Lutris and Flatpak checks are not claimed.

## Automated checks

Initial dedicated-session cleanup: 550 tests passed on Windows; the solution build completed with zero warnings/errors and self-contained Production Linux publishing succeeded. Coverage includes launch arguments, LSFG wrapping and missing-wrapper failures, dedicated identity/installation gates across all experiences, fallback persistence, launch reservations and credential preservation, Wine executable path aliases, selected native/Flatpak Steam ownership, shortcut migration/collision rejection, controller migration and the scoped trackpad-click change. Production Linux publishing passed. The official-entry experimental mode and all navigation/debugging code were removed during cleanup; remaining UI advice and command previews were updated.

## Runtime contracts and findings

Optional-layout regressions exercise missing, malformed, incompatible and unwritable templates, preservation of custom/non-Neptune mappings, repeat setup, and real Linux shortcut registration in disposable Steam directories. Launch compatibility tests cover non-Steam routing and restoration of the original inherited environment. The Launcher Tests workflow runs the solution build and complete test suite on Ubuntu and Windows. These automated fixtures do not validate desktop Steam gameplay or focus behavior.

- Official Steam app ID is `2536520`; shared prefix is `steamapps/compatdata/2536520/pfx`. Authentication was observed under `drive_c/users/steamuser/AppData/Local/Blizzard Entertainment/ClientSdk/cookie.bin`; saves are under `drive_c/users/steamuser/Saved Games/Diablo II Resurrected`. Cookie contents were not inspected or copied. Normal base-game sign-in remains necessary when authentication expires.
- Native GUI initialization requires its registered shortcut app/game IDs and stable installed executable path. Before GUI startup it sets D2R's authentication identity while retaining its dedicated shortcut session and compatibility identity. Official D2R launch options are never edited. Machine-specific shortcut IDs are derived, not hardcoded.
- The owned shortcut identity is cached only after startup registration validation. Dedicated launch selection uses that cache and the retained SteamGameId; launcher-only environment markers are unnecessary. Startup also recognizes an inherited D2R authentication app ID on a launcher restart only when the owned executable and dedicated SteamGameId still match. Normal child processes recover the original shortcut SteamAppId.
- Earlier direct-child tests changed only the game's identity and reproduced Steam-origin SIGINT about ten seconds after game exit. The validated dedicated route initializes matching GUI/child authentication identity from startup. It does not suppress exit signals. The separate helper remains a compatibility fallback.
- Launch preparation takes a cross-instance reservation before authentication or mod/session-file changes and retains it through game-exit credential cleanup, for dedicated and helper routes. Startup maintenance uses the same reservation. Unconfirmed game exit retains upstream credential-cleanup policy.
- Wine executable matching resolves parent-directory aliases and dosdevices drive mappings while rejecting unrelated executable copies. Native/Flatpak client selection follows the selected installation and library ownership. Flatpak Offline remains on its existing route; native setup and the dedicated route do not capture Flatpak profiles.
- Controller preferences live under `steamapps/common/Steam Controller Configs/<userdata-id>/config/configset_*.vdf`; Steam's launcher key removes the display-name colon and lowercases it. Per-device preferences can override the generic Neptune selection.
- Steam's installed controller_neptune_gamepad+mouse.vdf binds absolute_mouse trackpad press to xinput_button JOYSTICK_RIGHT. Setup creates controller_neptune_reimagined.vdf from that installed template, changing only the unique mouse-group binding to mouse_button LEFT. The stock template is untouched; ambiguous/changed source layouts are rejected and existing generated files are backed up. No Steam-owned template asset is committed.
- Atomic shortcut replacement preserves file modes. This prevented a background NonSteamLaunchers scanner on the test Deck from treating the shortcut file as uninitialized. The launcher does not require that scanner.
- Focus is left to Steam. Gamescope property writes, official-entry wrappers, silent-launch experiments, CEF navigation, diagnostic shortcuts and temporary scripts are absent from the final setup flow.

Steam identity and controller preferences are observed client contracts, not a guaranteed public API. The explicit helper fallback and manual controller editor remain available. Existing Windows and Battle.net/Lutris paths retain their launch implementation.

Hero preview follow-up: host shell probes found `/dev/fuse` and both helpers, while Steam-origin AppImage startup still reported no suitable helper. Host presence alone therefore cannot establish FUSE availability in the effective launch environment. Steam startup uses the explicit `--appimage-extract-and-run` flag; launches outside Steam retain normal FUSE startup when available. Forty-eight focused tests passed. Final physical startup evidence is recorded in the local deployment receipt.

Hero.18 physical GUI result: native Steam shortcut opened the installed stable AppImage, PID 410319, with original Steam GameId 15660535476714995712. Four shell branches passed (host FUSE, missing helper, explicit helper, Steam context). Existing shortcut registration retained owned IDs, 136 unrelated entries and controller preferences; repeated compiled setup was idempotent. Native game launch was not repeated in this follow-up.
