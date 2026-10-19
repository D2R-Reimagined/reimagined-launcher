# Linux Steam setup

This flow supports Diablo II: Resurrected installed through native Steam on Linux desktops and handhelds, including Steam Deck. It has no SteamOS or distribution-name requirement. Ubuntu automated checks and physical Steam Deck checks are recorded in the validation notes; live gameplay on other Linux distributions is not yet validated. Game launching does not require Decky, CEF debugging, Lutris, SSH, or terminal commands. Existing Battle.net, D2RMM, Lutris and Windows flows remain available. The branch includes the official v0.16.7 changes and integration with main at v0.16.11; release versioning remains owned by the release workflow.

## Prepare on the desktop

Use your normal desktop session. On handhelds or other systems with Gaming Mode, switch to Desktop Mode for setup, browser sign-in and downloads.

1. Install D2R through Steam. Download the launcher AppImage, make it executable in its file properties, and open it.
2. Under **Launch → Install directory**, select **Steam** and confirm the detected installation. Connect Nexus with **Login with Nexus Mods**. Connect your Reimagined account for Ladder.
3. Use **Install/Update → Download and Install** and complete the Nexus browser download. The launcher detects and extracts the completed archive; **Select Zip Manually** is the fallback. Complete browser sign-in and downloads on the desktop.
4. **Stay on the desktop.** Launch the official D2R entry from Steam, sign in, reach character select, then exit through the game menu. First launch can take time while Proton initializes. This establishes the official prefix and authentication.
5. Back in **Launch → Install directory**, check Proton detection. **Locate Proton** retries automatic detection; it does not open a file picker. If detection fails, confirm the official game launched successfully with its selected Proton tool before retrying.
6. Select **D2RLoader** for Online/Ladder and accept its installation prompt if needed. Enable **Force Desktop (`-forcedesktop`)** under **Settings → Launch Parameters** for multiplayer options.

During testing, Firefox redirected Reimagined sign-in to the profile page without completing launcher authorization. Reopening the launcher sign-in action completed it. This redirect issue remains unresolved.

## Register the launcher

1. Under **Launch → Install directory → Steam shortcuts**, select the Steam account.
2. Fully exit desktop Steam using **Steam → Exit**. Closing its window with **X is insufficient**. Keep the launcher open, or reopen the AppImage afterward.
3. When available, leave **Reset layout to Gamepad with Mouse Trackpad** checked for the supported layout. It preserves gamepad sticks, triggers and buttons, adds trackpad mouse movement, and maps trackpad press to left-click. R2 remains the normal game trigger. Uncheck reset to retain a custom controller layout. This option appears only when Steam provides the source template; other Linux devices can keep their existing layouts.
4. Click **Set up Steam shortcuts**. Setup backs up changed files, preserves unrelated shortcuts and preferences, and installs the AppImage at a stable location.
5. Close the desktop launcher and restart desktop Steam. If your system has Gaming Mode, switching back to it also restarts Steam.

Open **Diablo II: Reimagined (Native Launcher)**. Setup also retains a hidden **Diablo II: Reimagined (Launcher Game Session)** for the compatibility fallback. Do not enable Proton compatibility on either native entry.

The installed AppImage is `~/.local/share/ReimaginedLauncher/launcher/D2RReimagined.ReimaginedLauncher.AppImage`. Moving the Downloads copy does not break the registered entries. To install another AppImage manually, open it in Desktop Mode and rerun setup with Steam fully exited. Existing owned shortcut IDs are retained.

Controller configuration is optional. A missing, incompatible or unwritable template does not prevent shortcut registration. Setup reports that the layout was skipped; choose a layout in Steam Controller Settings if needed. Linux desktop users can restart desktop Steam and launch the registered entry without switching to Gaming Mode.

## Play and exit

Select Online, Offline or Ladder and press **Launch**. The launcher and game normally share the dedicated Steam session and controller layout. Exit through the game menu to return to the persistent launcher. **Exit Launcher** closes it and returns to its Steam page, even when minimize-to-tray is enabled.

If launcher clicking is unavailable, rerun setup with controller reset checked. Alternatively, edit the launcher layout in Steam Controller Settings: use **Gamepad with Mouse Trackpad**, then set **Right Trackpad → Click → Left Mouse Click**. The stock template binds trackpad press to right-stick click; selecting it alone does not provide mouse clicking.

The official D2R entry and its launch options are unchanged. Use that entry for vanilla play and authentication renewal when D2R requests sign-in. Avoid running vanilla and Reimagined together because both use the existing official Proton prefix.

## Optional LSFG

Under **Launch → Install directory → Advanced**, enable **Enable LSFG for the game** if an executable `~/lsfg` wrapper is already installed and configured. Decky LSFG-VK is one way to provide this wrapper on compatible devices. LSFG defaults off and affects only the game, not the launcher. No `%command%` launch-option edit is needed. The checkbox remains editable before shortcut setup; enabling LSFG without a supported Steam route or executable wrapper produces an actionable launch error.

The launcher does not install LSFG. Decky is optional for the launcher itself; it is one way to install/configure the frame-generation wrapper.

## Compatibility fallback

Enable **Use separate game session (compatibility fallback)** under **Launch → Install directory → Advanced** if the dedicated route has problems. The next launch uses the hidden helper, including LSFG when enabled. Close the game before switching routes. Uncheck fallback to return to the dedicated route.

After a fallback launch, Steam can retain the Game Session page in navigation history, including after switching back to the dedicated route. Press Back to leave it. The page alone does not mean the helper is still running.

Suspend/resume passed launch and input recovery on the test Deck, but temporary graphical glitches were observed until changing zones. Their cause is unconfirmed. Ladder requires an active connection and disconnected during sleep.

See [validation results and limits](steam-deck-validation.md).
