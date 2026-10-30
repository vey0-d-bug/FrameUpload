# FrameUpload

Tiny Windows debug tool that copies videos and pictures from the PC to the Steam Frame's `~/Videos` folder, which FrameCinema's Library scans.

- Drag files onto the window (or the exe), or click **Add files...**.
- Optional subfolder under `~/Videos`.
- Per-file and batch progress, then "Done".

It copies over ssh (the Frame has no SMB share), using the `frame` host entry in `%USERPROFILE%\.ssh\config`. Each file is written as a hidden `.name.part` and renamed when complete, so the player never sees a half-copied file.

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

`FrameUpload.exe [--host=NAME] [--sub=FOLDER] [--streams=N] [--ssh=PATH] [--log=FILE] [--exit] [FILE...]`

- `--streams=N`: ranges per big file (1-16; 1 = v1's single stream).
- `--ssh=PATH`: a specific `ssh.exe`.
- `--log=FILE`: appends one tab-separated line per file (size, seconds, MB/s, streams, ssh, ok or the error).
- `--exit`: closes the window when the batch is done; the exit code is the number of failed files.

## Build

Run `build.cmd`. It uses the .NET Framework `csc.exe` that ships with Windows (no SDK needed) and writes `C:\dev\FrameUpload\FrameUpload.exe`.
