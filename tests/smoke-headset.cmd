@echo off
rem Installs, lists and uninstalls two tiny test apps on the real headset.
rem Usage: tests\smoke-headset.cmd [-Exe PATH\FrameUpload.exe] [-Target frame] [-KeepApps]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0smoke-headset.ps1" %*
exit /b %ERRORLEVEL%
