@echo off
rem Runs probe-sideload.sh on the Steam Frame and saves what it prints to
rem probe-sideload.txt next to this file. Read-only on the headset.
rem Usage: probe-sideload.cmd [headset IP]   (default: the "frame" ssh entry)
setlocal
set SSH=ssh
if exist "C:\Program Files\Git\usr\bin\ssh.exe" set SSH="C:\Program Files\Git\usr\bin\ssh.exe"
if "%HOME%"=="" set HOME=%USERPROFILE%
set HOSTOPT=
if not "%~1"=="" set HOSTOPT=-o HostName=%~1
%SSH% -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new %HOSTOPT% frame "bash -s" < "%~dp0probe-sideload.sh" > "%~dp0probe-sideload.txt" 2>&1
echo Saved %~dp0probe-sideload.txt
type "%~dp0probe-sideload.txt" | more
