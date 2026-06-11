@echo off
REM Install wa (Webull Analytics)
REM Builds the project, copies the executable to the install directory, and seeds
REM the data directory at the canonical location the binary looks for at startup.
REM
REM Layout:
REM   %LOCALAPPDATA%\WebullAnalytics\wa.exe          - executable (default install location)
REM   %LOCALAPPDATA%\WebullAnalytics\wa-scraper.exe  - chain-snapshot scraper
REM   %LOCALAPPDATA%\WebullAnalytics\data\           - configs, history, intraday, etc.
REM
REM Program.BaseDir resolves to %LOCALAPPDATA%\WebullAnalytics when its data\ subdir
REM exists, so any wa.exe invocation (this one, a dev build, a published release)
REM reads the same config. The data dir always lives at the canonical location even
REM when the user overrides the exe install path.
REM
REM Usage:
REM   install.bat                 - installs exe to %LOCALAPPDATA%\WebullAnalytics
REM   install.bat "C:\mydir"      - installs exe to C:\mydir, data still at %LOCALAPPDATA%\WebullAnalytics\data

setlocal enabledelayedexpansion

set "INSTALL_DIR=%~1"
if "%INSTALL_DIR%"=="" set "INSTALL_DIR=%LOCALAPPDATA%\WebullAnalytics"
set "DATA_DIR=%LOCALAPPDATA%\WebullAnalytics\data"

echo ============================================
echo  wa (Webull Analytics) Installer
echo ============================================
echo.
echo Executable:  %INSTALL_DIR%\wa.exe
echo Scraper:     %INSTALL_DIR%\wa-scraper.exe
echo Data dir:    %DATA_DIR%
echo.

REM Check if dotnet is installed
dotnet --version >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: .NET SDK is not installed!
    echo Please download and install .NET 10.0 SDK from:
    echo https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Building self-contained executables...
echo.

dotnet publish WebullAnalytics.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
if %errorLevel% neq 0 (
    echo.
    echo ERROR: Build failed!
    pause
    exit /b 1
)

dotnet publish WebullAnalytics.Scraper\WebullAnalytics.Scraper.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
if %errorLevel% neq 0 (
    echo.
    echo ERROR: Scraper build failed!
    pause
    exit /b 1
)

echo.
echo Build succeeded.
echo.

REM Create install directory if it doesn't exist
if not exist "%INSTALL_DIR%" (
    echo Creating install directory...
    mkdir "%INSTALL_DIR%"
    if %errorLevel% neq 0 (
        echo ERROR: Failed to create install directory.
        pause
        exit /b 1
    )
)

REM Copy the executables.
REM A running process (wa ai watch, wa-scraper capturing until 16:05) locks its exe against
REM overwrite, which used to make the copy fail silently-ish and leave a stale install. Windows
REM does allow RENAMING a running exe, so: move the live file aside to *.old (the running process
REM keeps executing the renamed file untouched), copy the new build into place, and sweep the
REM *.old files on the next install once those processes have exited. Running processes still
REM execute the OLD code until restarted — the install just stops being blocked by them.
del "%INSTALL_DIR%\wa.exe.*.old" >nul 2>&1
del "%INSTALL_DIR%\wa-scraper.exe.*.old" >nul 2>&1

echo Copying wa.exe to %INSTALL_DIR%...
if exist "%INSTALL_DIR%\wa.exe" move /y "%INSTALL_DIR%\wa.exe" "%INSTALL_DIR%\wa.exe.%RANDOM%.old" >nul
copy /y "bin\Release\net10.0\win-x64\publish\wa.exe" "%INSTALL_DIR%\" >nul
if %errorLevel% neq 0 (
    echo ERROR: Failed to copy executable.
    pause
    exit /b 1
)

echo Copying wa-scraper.exe to %INSTALL_DIR%...
if exist "%INSTALL_DIR%\wa-scraper.exe" move /y "%INSTALL_DIR%\wa-scraper.exe" "%INSTALL_DIR%\wa-scraper.exe.%RANDOM%.old" >nul
copy /y "WebullAnalytics.Scraper\bin\Release\net10.0\win-x64\publish\wa-scraper.exe" "%INSTALL_DIR%\" >nul
if %errorLevel% neq 0 (
    echo ERROR: Failed to copy scraper executable.
    pause
    exit /b 1
)

REM Create the canonical data dir that Program.BaseDir resolves to at startup.
REM This is the same path whether the user accepted the default INSTALL_DIR or
REM overrode it — the binary always reads config from here when it exists.
if not exist "%DATA_DIR%" (
    echo Creating data directory at %DATA_DIR%...
    mkdir "%DATA_DIR%"
)

REM Add install directory to user PATH if not already present
echo Checking PATH...

REM Read the current user PATH from the registry
for /f "tokens=2*" %%A in ('reg query "HKCU\Environment" /v Path 2^>nul') do set "USER_PATH=%%B"

REM Check if the install dir is already in PATH
echo ;%USER_PATH%;|findstr /i /c:";%INSTALL_DIR%;" >nul 2>&1
if %errorLevel% equ 0 (
    echo Install directory is already in PATH.
) else (
    echo Adding %INSTALL_DIR% to user PATH...
    if defined USER_PATH (
        setx PATH "%USER_PATH%;%INSTALL_DIR%"
    ) else (
        setx PATH "%INSTALL_DIR%"
    )
    if %errorLevel% neq 0 (
        echo WARNING: Failed to update PATH. You may need to add it manually:
        echo   %INSTALL_DIR%
    ) else (
        echo PATH updated. Restart your terminal for changes to take effect.
    )
)

echo.
echo ============================================
echo  Installation complete!
echo ============================================
echo.
echo You can now run: wa
echo Capture a day's chain snapshots with: wa-scraper SPXW
echo.
pause
