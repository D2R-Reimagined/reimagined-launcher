# Local plugin and account filter installation

Based on upstream main `fd5f721536ad800196a951795cdd321151a50b5f` (v0.17.4), checked on 2026-10-19. Upstream exposed no next branch. The original launcher checkout and installed game were not modified during development.

## Launch UI

- Installed Plugins/Patches replaces D2RLoader Extensions; the navigation Plugins name stays unchanged and User Plugins becomes Community Plugins.
- Install Plugin/Zip accepts a local DLL or ZIP. A scrollable, controller-focusable preview shows destinations and replacement actions; Cancel initially receives focus. Installer actions target the selected game's normal `mods/Reimagined/d2rloader/`, never global folders. Offline and D2RLoader modes share that directory. Ladder's install button is disabled and the write path independently refuses Ladder.
- DLLs and accompanying MPQs go to plugins/. Config trees under config/ retain relative paths. Unstructured TOML/INI/CFG files map to config/; ambiguous JSON stops the operation. JSON patches are not imported by this button. Other files are listed as skipped.
- Existing configs are kept unless replacement is explicitly selected. Every replacement is backed up. ZIP traversal, links, reserved names, duplicate destinations and excessive expanded size are rejected. The game and loader must be closed. Files are staged before replacement; caught failures roll back completed replacements, retaining backups if recovery requires manual action. A multi-file installation is not crash-atomic.

Loot Filter is placed above Ladder Safeguards. Server Regions is a separate sibling section between them, visible only for Ladder with loaded region choices. Logical child order matches visual order for keyboard/controller navigation.

## Windows signed-package folder locks

The reported log failed at Directory.Move while backing up ReimaginedLadder. A Windows integration test reproduces the same installer failure by opening the existing directory without FILE_SHARE_DELETE. The test failed before the fix and passed afterward.

The normal directory-swap path remains the first choice. On Windows access-denied/sharing/lock errors, with the old directory still present and no backup rename completed, a fallback copies all previous files into the existing transaction backup before replacing the verified staged contents in place. It leaves the root and directory skeleton intact, replaces only changed files and removes stale files. It refuses path links/collisions and detects destinations changed during installation. A genuinely locked DLL still causes failure and rollback of earlier replacements; the installed-state record is restored too. Recovery failures retain the backup location in the error. Permissions and file-sharing restrictions are not bypassed, and Explorer is not terminated. This fallback applies to signed Ladder packages, not every legacy normal-mod installation path.

Coverage includes an open root folder, an open obsolete subfolder, MPQ-directory translation and a locked DLL after an earlier config replacement. These tests use temporary Windows handles and signed fixture packages, not a deployment into the installed game. The fallback is exception-safe across files, not crash-atomic.

References: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefile and https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew

## Account filters

The existing account access token authorizes GET /loot-filters and GET /loot-filters/{id}. No filter upload, update, delete or mark-used request is sent. Sign-in/token refresh use the launcher's existing authentication flow.

The dropdown caches the current account's list across navigation. Refresh is limited to five list requests per rolling minute; server Retry-After is honored for list and apply operations. Apply fetches the latest document, validates it and atomically installs only its JSON document as config/filter.json. The list-refresh limit does not stop applying an already listed filter. In-flight results are discarded if the account, installation, profile, mode or selected ladder changes.

Account-saved copies are visible; browser-only drafts must first be saved to the website account. Signed-out/empty states link to sign-in and the website editor. UnHoarder must already be available for the selected mode. Global UnHoarder installations do not count as available for isolated Ladder play. The feature never installs or enables UnHoarder. Applying while the game runs is supported; in-game activation is not confirmed by the launcher.

Last-installed name and ID are stored in existing settings.json, keyed by canonical mod D2RLoader root. This describes the last launcher installation, without checksums or modification detection. If installation succeeds but settings persistence fails, the UI reports those outcomes separately.

Backups live outside mod packages under `<game>/.reimagined-launcher/launcher-filter-backups/<mod>/` and `launcher-backups/<mod>/`. Keep the last three filter backups. Plugin replacement backups remain available for manual recovery. No metadata sidecar is added to filter.json.

## Ladder filter exception

Only the exact mod-scoped d2rloader/config/filter.json is exempt from installed-file size/hash enforcement, even if the signed package provides a default. A package-declared file must still exist. The signed archive payload, artifact, manifest, descriptors, signatures and all other signed files retain their checks. Repair/update copies the player's existing filter over a newly staged default; an existing full-package backup remains available. No DLL, MPQ or other config exception is introduced.

## Source contracts

- https://github.com/D2R-Reimagined/d2r-reimagined-website/blob/master/src/lib/loot-filter/api.ts
- https://github.com/D2R-Reimagined/d2r-reimagined-api/blob/main/src/Services/LootFilterValidation.cs

LootFilterValidation.cs adapts the project's API validator to local InvalidDataException errors. It follows the current website-supported v3 schema rather than accepting arbitrary future UnHoarder extensions.

## Validation boundaries

On Windows, the final Release suite passed 605 tests with two Linux-only skips (607 total). The Release build and self-contained win-x64 publish succeeded.

The automated suite covers extraction, config preservation, backups, rollback after partial replacement, changed destinations, exact Ladder filter exemption, repair/update preservation, signed download rejection, GET-only account requests, cache/rate limiting and filter validation/retention. A separate UI harness renders the actual LaunchView and package preview with fixture files and HTTP responses.

Production account listing/download, real plugin installation, live-game reload, Linux runtime behavior and physical Steam Deck controller interaction require manual validation. No deployment, GitHub push, PR or release was performed. Ladder-to-offline conversion is out of scope.

## UI-only comparison build

The fixes-only variant retains all previously agreed features and the folder-lock fallback. Its only new changes are the explicit non-Ladder UnHoarder/D2RLoader requirement and top-right header actions on wide windows. Compact windows retain wrapping beneath the heading. Standard Offline still launches D2R directly; D2RLoader single-player is available through the D2RLoader mode.

## Optional plugin checkbox build

The second comparison build includes the UI fixes and individual plugin/patch checkboxes for non-Ladder modes. The launcher lists both active plugins/*.dll and patches/*.json and their parked counterparts under d2rloader/launcher-disabled/plugins and launcher-disabled/patches. D2RLoader continues using only its documented active directories. The launcher derives checkbox state from file locations, without another settings file.

Unchecking moves only the selected DLL or patch JSON into the parked directory in the same global or mod scope. Checking moves it back. Configs and companion MPQs remain intact. Global rows explicitly warn that they affect other mods. Scope-wide loader settings still apply, and another copy of a plugin can remain enabled in a different scope. Changes affect subsequent D2RLoader launches. Manual toggles are blocked on Ladder, while the game or loader is running, or while installation/launch preparation is pending.

Active/parked duplicates refuse toggling rather than overwriting. Reinstalling a disabled mod plugin updates the parked DLL with a replacement backup and retains its disabled state. No dependency inference or automatic enabling is performed.

The optional build passed 612 Release tests with two Linux-only skips. Seven added cases cover both-folder discovery, disable/re-enable, scope separation, conflicts, Ladder refusal, running-game refusal, preserved configs/MPQs, disabled-plugin replacement backups and uppercase extensions. The actual LaunchView was rendered with active/parked fixture files. Real D2RLoader loading behavior and physical Steam Deck input still require manual testing.

Sources: https://d2rloader.net/docs.html documents loader plugin/patch directories and scope settings; https://github.com/D2RLoader/PluginSDK/blob/master/README.md documents companion MPQ lookup beside loaded plugins.

## Revised comparison layout

Non-Ladder keeps a full-width filter card with a picker/action group capped at 620 pixels. Ladder places Loot Filter and Server Regions in adjacent equal-width cards at view widths of 960 pixels or more; narrower views stack them. Safeguards remains separate below. Plugin actions form one top-right row on wide views and a deliberate two-by-two group below the heading on compact views. Both comparison variants use this layout.


The next layout revision pairs Ladder Safeguards with Loot Filter instead, leaving Server Regions in its own full-width row below. Compact views stack the cards. The Active Ladder selector and approved extension details fit the narrower safeguards card. Both variants retain identical layout.


The revised Launch layout places Loot Filter / Launch in row one, Install Directory / Ladder Safeguards in row two, and Server Regions / an intentionally empty grey card in row three, beneath Play Mode. Installed Plugins/Patches spans the width below. Compact views follow that reading order vertically. Ladder-only cards and the placeholder hide outside Ladder. Filter list fetching remains unchanged: first signed-in need without cache, explicit refresh, and a fresh cache on account change or process restart; navigating uses the shared cached list. Apply fetches current filter details.

## Per-entry Edit Config

The checkbox variant offers a compact row for each plugin/patch: checkbox beside three lines: name, filename/version, then installation/author text; Edit Config occupies the right column of the filename row. Disabled status appears only for disabled files; duplicate warnings remain visible. Author and long metadata wrap within their assigned line. Filename and version are joined by a middle dot in one muted line. The third line reads "Installed in mod · by Author" (or "Installed globally"), omits missing authors, and uses smaller, quieter text. Slightly larger text and padding improve readability while keeping the action aligned to the filename.

Edit Config is disabled when no unique existing configuration is found or in Ladder. Refresh rechecks availability. Opening revalidates the file and scope, so removed files cannot open using stale UI state. The resolver matches a single top-level text config by normalized DLL filename (ignoring case, separators and an optional d2rl- prefix). This is a naming convention, not a plugin metadata contract. Unknown or ambiguous mappings remain disabled; no arbitrary fallback file picker is offered. Disabled DLLs use their original config folder; patches open their actual JSON, including parked JSON for disabled entries.

Existing scope, game/installer/launch guards apply. Windows opens Notepad with a separate filename argument. Linux uses the desktop file association and reports missing associations. The launcher does not create, overwrite or save configs; editing/reloading happens through the external editor. No new config metadata is stored.


## In-place toggle updates

The shared install semaphore and MainWindow install reservation remain held through the file move. A successful toggle updates the existing observable entry (path, enabled state, metadata and config availability) and the enabled/disabled counts. It does not rediscover inventory or reassign ItemsSource. UnHoarder availability is recalculated from that same inventory. Failure restores the checkbox from the unchanged model without rebuilding either list. Pointer actions are blocked during the move without disabling/recreating the controls; keyboard/reentrant handlers retain their guards. Manual refresh and mode changes are blocked during the operation. Ladder/game/launch/context and collision guards remain in effect.


## Unified Ladder extension section and stable status cards

The main header removes the Sanctum Status heading and descriptive copy, retaining version/update status, session log and account controls in a smaller toolbar. Ladder now displays the existing approved extension choices in the single Installed Plugins/Patches section instead of the duplicate Extension policy details expander plus raw inventory. Required entries retain their REQUIRED badge and cannot be unchecked; optional selection keeps its existing signed-policy handler and verification. Availability labels distinguish installed files, supplied files, missing/hash-mismatched files and disabled files. Manual DLL/ZIP installation and Edit Config remain unavailable on Ladder. Unapproved-file warnings remain accessible in the unified section; Safeguards retains its package readiness/progress summary. Non-Ladder retains its in-place manual toggle and config controls.

The filter/launch cards reserve 250 pixels, installation/safeguards cards 300 pixels, and server regions/placeholder 250 pixels. Each content card scrolls internally when long warnings or expanded details exceed that space, preserving access without resizing the row. Compact views keep the same card sizes while stacking columns. These limits prevent check/progress/warning transitions from moving the sections below; mode changes can still intentionally show or hide Ladder sections.


The smaller-display revision reduces these reservations and padding by roughly one third. Mode choices stay horizontal from 760 logical pixels of Launch-view width, paired cards from 820 pixels, while plugin columns/toolbars retain the existing 960-pixel compact behavior. Launch title/actions sit outside its scrolling warning/details area so the Start button stays visible. The actual LaunchView is checked in a simulated 1280-by-800 shell allocation with sidebar and toolbar space reserved, plus a narrower stacked viewport. Physical Steam Deck interaction remains untested.


Installation warnings now occupy a persistent 64-pixel slot, with long messages scrollable within that slot. Hidden platform-specific form rows no longer leave grid gaps. Its outer form scrollbar always reserves its width, so showing/hiding policy warnings changes neither field positions nor field widths. Server Regions and its placeholder reserve 250 pixels, sufficient for five normal region rows; unusually long lists/admin controls can still scroll. The UI fixture checks absent, short and long warnings against the actual form controls, not just outer card bounds.


The latest layout supersedes fixed card heights and nested scrolling. At 900 logical pixels of Launch-view width or above, Ladder has Loot Filter / Server Regions / Launch in a single row, with Install Directory / Ladder Safeguards beneath. Non-Ladder retains filter/launch side by side. Smaller widths stack the cards. All cards flow naturally inside the single page scrollbar; installation warnings use a minimum-height area below the form, so normal policy warning transitions do not move fields. The grey placeholder is removed. Three-column filters place the picker above Refresh/Apply to avoid horizontal clipping.


The latest UI refinement labels the existence-only badge "D2RLoader detected"; it does not certify plugin readiness. Overlay measurement text reserves the normal wrapped Launch description, signed-bundle status and extension-policy summary footprints across checking/ready transitions without fixed card heights or nested scrolling. Re-test/Testing uses a fixed-width button. Ladder entries retain a visible disabled Edit Config action with no edit handler. Mode titles use the requested local-build jokes (Lame Peeps, D420RLoaderBlazeIt, The Chads), with TCP/IP called out explicitly; enum routing and Ladder restrictions are unchanged.

Hero.21 filter feedback: the saved InstalledLootFilters metadata in settings.json preselects the matching account filter. A fixed-width inline indicator shows an empty circle for no file or an unapplied selection, a refresh symbol during application and a green check for an installed file. There is no separate Last installed or success-status row. Sign-in, empty-list and error guidance remain visible when needed. File presence is checked without adding checksum tracking or uploading data; deleted files do not retain a green check solely because metadata exists.
