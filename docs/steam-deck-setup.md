# Steam Deck setup

This setup supports the Steam edition of Diablo II: Resurrected through native Steam. Decky and Lutris are not required. The launcher includes the official v0.16.7 update. Battle.net installations using Lutris retain their existing setup.

## Desktop Mode

1. Install Diablo II: Resurrected through Steam, then switch to Desktop Mode. Browser sign-in and mod downloads should be completed here.
2. Download the launcher AppImage, make it executable in its file properties, and open it.
3. On **Launch**, select **Steam** and confirm the detected install directory. Connect Nexus through **Login with Nexus Mods**. Connect your Reimagined account if using Ladder.
4. Use **Install/Update → Download and Install** and complete the Nexus download. The launcher can pick up and extract the completed archive automatically; **Select Zip Manually** is the fallback.
5. **Stay in Desktop Mode.** Launch the original Diablo II: Resurrected entry from desktop Steam, sign in, reach character select, and exit through the game's menu. First launch can be slow while Proton initializes. This establishes the official prefix and authentication; repeat sign-in when the game requires authentication renewal.
6. Back in the launcher, check **Launch → Install directory**. Proton is pre-filled when the initialized base-game prefix identifies one existing tool. Otherwise use **Locate Proton** to choose the same tool used by the base game.
7. Select **D2RLoader** as Play Mode for Online/Ladder and accept its installation prompt if missing. Wait for installation to finish. Enable **Force Desktop (`-forcedesktop`)** under **Settings → Launch Parameters** for multiplayer options.

If Firefox sends you to your Reimagined profile after website login without connecting the launcher, return to the launcher's sign-in action and reopen authorization. This retry was needed during testing; the first-login redirect remains unresolved.

## Register Steam shortcuts

1. Under **Launch → Install directory → Steam shortcuts**, confirm the selected Steam account.
2. In desktop Steam choose **Steam → Exit**. Clicking the window's **X does not fully exit Steam**. Keep the launcher open, or reopen the AppImage afterward.
3. Leave **Reset launcher layout to Mouse Only** checked to configure trackpad movement and clicking for the launcher. Uncheck it to preserve a custom launcher layout. Game-session controls are separate.
4. Click **Set up Steam shortcuts**. The launcher backs up existing shortcut/controller files, preserves unrelated entries, and copies its AppImage to a stable location. No SSH or terminal command is needed.
5. After success, close the desktop launcher and select **Return to Gaming Mode**. This starts Steam with the new shortcuts.

Setup creates **Diablo II: Reimagined (Native Launcher)** and **Diablo II: Reimagined (Launcher Game Session)**. Open **Native Launcher**. The game-session entry is an internal helper; launch games through the GUI. Do not enable Proton compatibility on these native launcher entries.

## Gaming Mode

Select the experience and press **Launch**. Steam switches to the game and its gamepad layout. Exit through D2R's menu to return to the launcher and its mouse layout. Steam can show both launcher entries and the base game during play; these represent the GUI, game-session helper, and game runtime.

Use **Exit Launcher** to close the GUI, including when minimize-to-tray is enabled. Scrollbars remain expanded for trackpad use. If mouse input is unavailable, fully exit Steam in Desktop Mode and rerun setup with the reset option checked. Manual fallback: **Steam → Controller Settings → current layout → Templates → Mouse Only → Apply** for Native Launcher only.

The installed AppImage is at `~/.local/share/ReimaginedLauncher/launcher/D2RReimagined.ReimaginedLauncher.AppImage`. Moving the Downloads copy afterward does not break shortcuts. To install a newer AppImage, open it in Desktop Mode and rerun setup with Steam fully exited. Existing owned shortcut IDs are retained, including when migrating the previous D2R Reimagined names.

See [validation](steam-deck-validation.md) for checks and limitations.
