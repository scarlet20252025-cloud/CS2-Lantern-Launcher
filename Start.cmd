@echo off
rem Launch the most recently written build under artifacts\, so this file
rem survives a version bump. Sorted by timestamp, not by name: "v0.5.9" sorts
rem above "v0.5.26" as text, which would silently start an old build.
setlocal enabledelayedexpansion
set "LATEST="
for /f "delims=" %%D in ('dir /b /ad /o-d /tw "%~dp0artifacts\v*" 2^>nul') do (
    if not defined LATEST if exist "%~dp0artifacts\%%D\CS2Lantern.exe" set "LATEST=%~dp0artifacts\%%D"
)
if not defined LATEST goto :nobuild
start "" "!LATEST!\CS2Lantern.exe"
exit /b 0

:nobuild
echo No build found under artifacts\.
echo Run build.ps1 -Portable first, or publish manually.
pause
exit /b 1
