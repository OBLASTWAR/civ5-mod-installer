@echo off
setlocal

:: ============================================================
:: KekModInstaller build script
:: Requires: .NET Framework 4.x (csc.exe) -- ships with Windows,
:: no extra toolchain needed.
::
:: Builds TWO exes from the same Installer.cs:
::   KekModInstaller.exe           public build -- CHANNEL (stable/beta) is
::                                  available, no BUILD (prod/dev) box
::   KekModInstaller.Internal.exe  dev-machine build -- adds the BUILD
::                                  (prod/dev) radio buttons on top
::
:: Self-update: whenever Installer.cs changes and you rebuild+publish a new
:: KekModInstaller.exe to main, bump InstallerCore.InstallerVersion in
:: Installer.cs AND installer/installer_version.txt to the same new value.
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

:: beat.mp3 and the two EUI zips (UI_bc1.zip, UI_bc1_xits.zip) are all
:: gitignored (large binary assets) -- embed whichever this machine actually
:: has, build without whatever's missing. Each is handled gracefully at
:: runtime when absent (RetroAudio.PlayEmbeddedLooped / EuiExtra.Install),
:: so a from-scratch clone still builds and runs fine either way.
set RESOURCE=
if exist "%~dp0beat.mp3" (
    set RESOURCE=%RESOURCE% /resource:"%~dp0beat.mp3",KekModInstaller.beat.mp3
) else (
    echo WARNING: beat.mp3 not found next to build.bat -- building without embedded music.
)
if exist "%~dp0UI_bc1.zip" (
    set RESOURCE=%RESOURCE% /resource:"%~dp0UI_bc1.zip",KekModInstaller.UI_bc1.zip
) else (
    echo WARNING: UI_bc1.zip not found next to build.bat -- building without bundled EUI.
)
if exist "%~dp0UI_bc1_xits.zip" (
    set RESOURCE=%RESOURCE% /resource:"%~dp0UI_bc1_xits.zip",KekModInstaller.UI_bc1_xits.zip
) else (
    echo WARNING: UI_bc1_xits.zip not found next to build.bat -- building without bundled EUI XITS.
)

echo.
echo === Building public (stable/beta channel, no dev build option) ===
"%CSC%" /nologo /target:winexe /platform:x64 /out:"%~dp0KekModInstaller.exe" %REFS% %RESOURCE% "%~dp0*.cs"
if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED ^(public, exit code %ERRORLEVEL%^)
    exit /b %ERRORLEVEL%
)

echo.
echo === Building internal (beta/dev channel options) ===
"%CSC%" /nologo /target:winexe /platform:x64 /define:INTERNAL_BUILD /out:"%~dp0KekModInstaller.Internal.exe" %REFS% %RESOURCE% "%~dp0*.cs"
if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED ^(internal, exit code %ERRORLEVEL%^)
    exit /b %ERRORLEVEL%
)

echo.
echo BUILD SUCCEEDED
echo   %~dp0KekModInstaller.exe
echo   %~dp0KekModInstaller.Internal.exe
exit /b 0
