# FrameUpload patch notes

## v6 (2026-10-31)

**Whole-headset file browser**
- The headset view is no longer limited to `~/Videos`. The **Copy to** box takes any headset folder: `~/...`, `/...`, or a name inside the folder on show (`..` works, also above `~`). Enter opens it; **Up** goes all the way to `/`.
- **Places** jumps to Videos, Home, Downloads, SD card / USB (`/run/media`) or the whole headset (`/`).
- **Show hidden** lists dot files and folders (greyed). The `.part` of a copy under way stays hidden.
- Copies go to whatever folder is on show. The FrameCinema Library hint only shows for copies into `~/Videos`.
- The line under the list shows the free space where you are and says when a folder is read-only; a folder you can't open says so.
- Folder sizes are worked out in the home folder and on drives only, at most 3 s per folder and 4 s in all, so big trees and `/` still list fast.

**Download to the PC**
- New **Download...** copies the selected files or folders (everything in them, following a selected link) into a PC folder you pick, through the same queue and progress bars as uploads.
- Files already in that PC folder are left as they are and counted as such. Names Windows can't hold are listed and skipped.
- Each file is written as `name.part` and renamed once its size matches; a quit mid-copy removes the `.part`.

**Safety**
- Delete and rename work only in the home folder and on SD cards and USB drives (greyed out elsewhere), and never on hidden items right in `~`, where the headset's settings and this tool's ssh key live.
- Deleting outside `~/Videos` adds a line saying so to the confirmation. The extension and hidden-name checks of v5 stay for `~/Videos`, where FrameCinema reads them.

**Other**
- New `--dir=PATH` starts in any headset folder (`--sub=` still means under `~/Videos`).

## v5 (2026-10-31)

- **Rename...** (or F2) in the headset view renames the selected file or folder. The box starts with the name selected up to its extension, and the item stays selected after the view refreshes.
- A name that is already taken is refused (a folder of that name too), so nothing is overwritten or moved into another folder.
- Changing or dropping a file's extension asks first, since FrameCinema goes by the extension. Names can't contain / or \, start with a dot (that would hide the item), or exceed 255 bytes.
- Folders can't be renamed while copies are under way.
- The window's minimum width grew a little to fit the extra button.

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
