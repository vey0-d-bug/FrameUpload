@echo off
rem Builds FrameUpload.exe into C:\dev\FrameUpload with the csc.exe that ships with Windows.
if not exist C:\dev\FrameUpload mkdir C:\dev\FrameUpload
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /optimize+ ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /out:C:\dev\FrameUpload\FrameUpload.exe "%~dp0FrameUpload.cs"
