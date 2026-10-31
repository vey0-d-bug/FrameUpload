@echo off
rem Builds FrameUpload.exe with the csc.exe that ships with Windows.
rem Usage: build.cmd [output folder]   (default: this folder)
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0.
if not exist "%OUT%" mkdir "%OUT%"
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /optimize+ ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /out:"%OUT%\FrameUpload.exe" "%~dp0FrameUpload.cs"
