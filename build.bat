@echo off
setlocal

:: ============================================================
:: KekModInstaller build script
:: Requires: .NET Framework 4.x (csc.exe) -- ships with Windows,
:: no extra toolchain needed.
::
:: Builds CivVModInstaller.exe from the same Installer.cs (build.sh does
:: the same on Linux via Proton's wine-mono). CHANNEL
:: (stable/beta) is available via Settings.
::
:: Output: CivVModInstaller.exe, plus a byte-identical KekModInstaller.exe.
:: Publish BOTH to main. Pre-2.3 installers self-update by fetching
:: installer/<their own filename>, and copies are out there under both
:: names (GDR's Discord upload has been CivVModInstaller.exe since 2.0;
:: GitHub/README downloads were KekModInstaller.exe).
::
:: Self-update: whenever Installer.cs changes and you rebuild+publish a new
:: CivVModInstaller.exe to main, bump InstallerCore.InstallerVersion in
:: Installer.cs, installer/installer_version.txt AND VERSION in
:: installer/linux/civ5-mod to the same new value.
:: Running copies compare their own baked-in InstallerVersion against
:: installer_version.txt on main and show an UPDATE button when they differ.
:: ============================================================

set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
)
if not exist "%CSC%" (
    echo ERROR: csc.exe not found. .NET Framework 4.x is required.
    exit /b 1
)

echo Using csc: %CSC%

set REFS=/r:System.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Runtime.Serialization.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll

:: beat.mp3 is gitignored (large binary asset) -- embed it if this machine
:: has it, build without it otherwise. It's handled gracefully at runtime
:: when absent (RetroAudio.PlayEmbeddedLooped), so a from-scratch clone
:: still builds and runs fine either way. EUI isn't embedded: EuiExtra
:: downloads it from the fork releases at install time.
set RESOURCE=
if exist "%~dp0beat.mp3" (
    set RESOURCE=%RESOURCE% /resource:"%~dp0beat.mp3",KekModInstaller.beat.mp3
) else (
    echo WARNING: beat.mp3 not found next to build.bat -- building without embedded music.
)

echo.
echo === Building CivVModInstaller.exe ===
"%CSC%" /nologo /target:winexe /platform:x64 /out:"%~dp0CivVModInstaller.exe" %REFS% %RESOURCE% "%~dp0*.cs"
if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED ^(exit code %ERRORLEVEL%^)
    exit /b %ERRORLEVEL%
)

copy /y "%~dp0CivVModInstaller.exe" "%~dp0KekModInstaller.exe" >nul

echo.
echo BUILD SUCCEEDED
echo   %~dp0CivVModInstaller.exe
echo   %~dp0KekModInstaller.exe ^(legacy self-update alias^)
exit /b 0
