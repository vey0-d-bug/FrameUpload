# FrameUpload

Tiny Windows debug tool that copies videos and pictures from the PC to the Steam Frame's `~/Videos` folder, which FrameCinema's Library scans.

- Drag files onto the window (or the exe), or click **Add files...**.
- Optional subfolder under `~/Videos`.
- Per-file and batch progress, then "Done".

It copies over ssh (the Frame has no SMB share), using Windows' built-in `ssh.exe` and the `frame` host entry in `%USERPROFILE%\.ssh\config`. Each file is written as a hidden `.name.part` and renamed when complete, so the player never sees a half-copied file.

Command line: `FrameUpload.exe [--host=NAME] [--sub=FOLDER] [FILE...]`

## Build

Run `build.cmd`. It uses the .NET Framework `csc.exe` that ships with Windows (no SDK needed) and writes `C:\dev\FrameUpload\FrameUpload.exe`.
