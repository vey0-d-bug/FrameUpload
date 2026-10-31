# FrameUpload patch notes

## v4 (2026-10-31)

- Click a column header in the headset view to sort by name, size or date; click it again to flip the order. Size and date start biggest/newest first. Folders stay above files.
- Folders show their size (everything in them) as soon as the folder loads, and the total under the list counts them.

## v3 (2026-10-31)

**Headset view**
- The top half of the window lists the headset's `~/Videos`: folders first, sizes, dates, and the free space on the headset.
- Double-click a folder (or press Enter) to open it; **Up** or Backspace goes back; **Refresh** or F5 reloads.
- The folder on show is where copies go, and the view refreshes when a batch finishes.
- **New folder...** makes a folder where you are.
- **Delete...** (or the Delete key) removes the selected files or folders after a confirmation that names each one. Folders go with everything in them.
- Hidden names, like the `.part` of a copy under way, are not shown.

**Connect to Frame**
- New **Connect to Frame** button, also run when the window opens. It tries the `frame` entry in `~/.ssh/config`, then the last IP that worked, then `frame.local`, and asks for an IP if none answers. **Set IP...** asks straight away.
- Copies go to whichever address connected. A fallback address replaces only the entry's HostName, so its user and keys still apply.
- The headset's IP is saved to `%APPDATA%\FrameUpload\host.txt` every time a connection works.
- A new address's host key is accepted on first use; a changed key is still refused.

**Fixes**
- Remote commands are quoted properly for the Windows command line, so double quotes in them reach the headset intact.

## v2 (2026-10-30)

**About 4x faster**
- Uses Git for Windows' `ssh.exe` (OpenSSH 10) when installed: 50-55 MB/s on one stream versus 12-15 MB/s with Windows' built-in ssh. The window says which ssh is in use.
- Files of 128 MB and up go as several byte ranges at once (6 streams with Git's ssh, 8 with Windows'), each into its place in a full-size `.part`, which is size-checked and then renamed. About 62-69 MB/s on the Frame's 5 GHz Wi-Fi.
- Asks for AES-GCM first (faster than chacha20 on the Frame).
- New command-line options: `--streams=N`, `--ssh=PATH`, `--log=FILE`, `--exit`.

**Fixes**
- Quitting mid-copy now stops the ssh processes and removes the half-written `.part` from the headset.
- Fixed a crash at the end of every transfer (checking a disposed ssh process).

## v1 (2026-10-29)

- Drag files onto the window or the exe, or click **Add files...**, to copy them to `~/Videos[/subfolder]` on the Frame over ssh.
- Each file is written as a hidden `.part` and renamed when whole, so FrameCinema never sees a half-copied file.
