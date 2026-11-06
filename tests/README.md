# Tests

## Automated tests (no headset needed)

`tests\run-tests.cmd` builds `FrameUpload.exe` from the source and runs it against a fake headset: a folder on the PC, reached through a fake `ssh.exe` that runs FrameUpload's commands with Git for Windows' bash. Stand-ins for `steamos-add-to-steam`, `pgrep` (is Steam running) and `flatpak` record what they were asked to do. It takes a couple of minutes and prints `ok` or `FAIL` for each check; the exit code is the number of failed tests.

- `-Exe PATH` tests a given `FrameUpload.exe` instead of building one (for example a build you are about to hand out).
- `-Only NAME` runs only the tests whose name contains NAME (`-Only Manage`).
- `-NoUi` skips the tests that click through the window.
- `-Keep` keeps the fake headsets and logs in `%TEMP%\FrameUpload-tests-*` (they are kept anyway when a test fails).

What it covers:

- **Install app** (`--install`): `.apk`, `.exe` and AppImage files land in `~/Applications` byte for byte and go to `steamos-add-to-steam`; `.flatpak` and `.flatpakref` go to `flatpak install --user` (`--bundle` / `--from`), after Flathub is added, and the copied file is removed. Failures say why: Steam not running, the add failing, no Flatpak, Flatpak failing, the headset unreachable. Names with spaces, quotes, `$`, backticks and accents arrive unchanged.
- **Copying** as before v7: files go to the folder given, apps included, without being installed; a big file goes as parallel ranges and arrives whole.
- **Apps... > Manage installed apps** (clicked through with Windows UI Automation): it lists `~/Applications` and the user's Flatpaks, not hidden files, says when there are none, and Uninstall... removes the selected file and Flatpak after the confirmation and relists.

They run on GitHub Actions (Windows) for every push and pull request; the workflow also keeps the built `FrameUpload.exe` as a download.

## On-headset smoke test

`tests\smoke-headset.cmd` checks a build on the real Steam Frame. Turn the headset on, with Steam running, then run it on the PC. It:

1. checks `ssh frame` works, Steam is running and `steamos-add-to-steam` is there;
2. installs two tiny test apps (`FrameUpload-smoke-test.AppImage` and `.exe`) with `FrameUpload.exe --install`;
3. checks they are in `~/Applications` at the right size, the AppImage is executable, and Steam's `shortcuts.vdf` names them;
4. opens **Apps... > Manage installed apps**, checks both are listed, and uninstalls them.

Afterwards remove the two `FrameUpload-smoke-test` entries from the Steam library (Manage > Remove non-Steam game). `-KeepApps` leaves the apps installed, `-Exe PATH` tests a given build, `-Target NAME` uses another ssh host entry than `frame`.
