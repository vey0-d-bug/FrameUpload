# FrameUpload

[Patch notes](CHANGELOG.md)

Tiny Windows debug tool that copies videos and pictures from the PC to the Steam Frame (by default its `~/Videos` folder, which FrameCinema's Library scans), browses the headset's files, and copies files back to the PC.

- Drag files onto the window (or the exe), or click **Add files...**.
- Copies go to the folder on show in the headset view: `~/Videos` to start, or any folder on the headset (v6).
- Per-file and batch progress, then "Done".

It copies over ssh (the Frame has no SMB share), using the `frame` host entry in `%USERPROFILE%\.ssh\config`. Each file is written as a hidden `.name.part` and renamed when complete, so the player never sees a half-copied file.

## Headset view and Connect to Frame (v3)

- The top half lists the headset's `~/Videos` folder: folders first, sizes, dates, and the free space on the headset. Double-click a folder (or press Enter) to open it, **Up** or Backspace to go back. The folder on show is where copies go. **New folder...** makes one there; **Rename...** (or F2) renames the selected file or folder, refusing a name that is taken (v5); **Delete...** (or the Delete key) removes the selected files or folders after a confirmation that names them. Hidden names, like the `.part` of a copy under way, are not shown.
- **Whole headset (v6).** The **Copy to** box takes any headset folder: `~/...` (home), `/...`, or a name inside the folder on show, with `..` worked out; Enter opens it. **Places** jumps to Videos, Home, Downloads, SD card / USB (`/run/media`) or the whole headset (`/`). **Show hidden** lists dot files too (a copy's `.part` still stays hidden). The line under the list says when a folder is read-only. Folder sizes are worked out in the home folder and on drives only, with a time limit.
- **Download... (v6)** copies the selected headset files or folders (with everything in them) into a PC folder you pick. Files already in that PC folder are left as they are, and names Windows can't hold (like `a:b`) are listed and skipped. Each file is written as `name.part` and renamed once its size matches.
- **Delete and rename** work only in the home folder and on SD cards and USB drives, never on hidden items right in `~` (the headset's settings and the ssh key this tool uses). Outside `~/Videos` the delete confirmation says so; the extension and hidden-name checks of v5 apply in `~/Videos`.
- **Connect to Frame** (also run when the window opens) tries the `frame` entry in `~/.ssh/config`, then the last IP that worked, then `frame.local`. If none answers, it asks for an IP. **Set IP...** asks straight away. The headset's address is saved in `%APPDATA%\FrameUpload\host.txt` whenever a connection works.
- A fallback address replaces only the entry's `HostName` (`ssh -o HostName=IP frame`), so the entry's user and keys still apply, and copies go to whichever address connected. A new address's host key is accepted on first use (`StrictHostKeyChecking=accept-new`); a changed key is still refused.

## Speed (v2)

Windows' built-in `ssh.exe` (OpenSSH for Windows 9.5) is the bottleneck: it tops out at 12-15 MB/s, while the link to the Frame does about 55 MB/s on one TCP stream and 70 on several, and the headset's disk writes at 2 GB/s. So v2:

- uses Git for Windows' `ssh.exe` (`C:\Program Files\Git\usr\bin\ssh.exe`, OpenSSH 10) when it is installed, and falls back to Windows' own (the window says which);
- sends files of 128 MB and up as several byte ranges at once (6 with Git's ssh, 8 with Windows'), each over its own ssh connection into `dd` at its offset of a `.part` allocated at full size, then checks the size and renames it;
- asks for AES-GCM first (both ends have AES instructions).

Measured on the test PC (2.5 GbE wired) to the Frame (5 GHz, 80 MHz), 1 GiB of random data:

| path | MB/s |
| --- | --- |
| v1, Windows ssh, 1 stream | 12-15 |
| Git ssh, 1 stream | 50-55 |
| Git ssh, 4 / 8 streams | 62 / 69 |
| raw TCP, 1 / 4 streams (ceiling) | 55 / 72 |

## Command line

`FrameUpload.exe [--host=NAME] [--sub=FOLDER | --dir=PATH] [--streams=N] [--ssh=PATH] [--log=FILE] [--exit] [FILE...]`

- `--streams=N`: ranges per big file (1-16; 1 = v1's single stream).
- `--sub=FOLDER`: start in `~/Videos/FOLDER`; `--dir=PATH`: start in any headset folder (`~/...` or `/...`).
- `--ssh=PATH`: a specific `ssh.exe`.
- `--log=FILE`: appends one tab-separated line per file (size, seconds, MB/s, streams, ssh, ok or the error).
- `--exit`: closes the window when the batch is done; the exit code is the number of failed files.

## Build

Run `build.cmd` (or `build.cmd OUTPUT_FOLDER`). It uses the .NET Framework `csc.exe` that ships with Windows (no SDK needed) and writes `FrameUpload.exe` next to the script by default.
