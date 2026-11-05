@echo off
rem Runs FrameUpload's automated tests against a fake headset (no headset needed).
rem Usage: tests\run-tests.cmd [-Exe PATH\FrameUpload.exe] [-Only NAME] [-NoUi] [-Keep]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-tests.ps1" %*
exit /b %ERRORLEVEL%
