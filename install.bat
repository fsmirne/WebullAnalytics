@echo off
REM Install Webull Analytics
REM Builds the project and copies the executable to the install directory.
REM Adds the install directory to the user's PATH if not already present.
REM
REM Usage:
REM   install.bat              - installs to %LOCALAPPDATA%\WebullAnalytics
REM   install.bat "C:\mydir"   - installs to the specified directory

setlocal enabledelayedexpansion

set "INSTALL_DIR=%~1"
if "%INSTALL_DIR%"=="" set "INSTALL_DIR=%LOCALAPPDATA%\WebullAnalytics"

echo ============================================
echo  Webull Analytics Installer
echo ============================================
echo.
echo Install directory: %INSTALL_DIR%
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

echo Building self-contained executable...
echo.

dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
if %errorLevel% neq 0 (
    echo.
    echo ERROR: Build failed!
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

REM Copy the executable
echo Copying WebullAnalytics.exe to %INSTALL_DIR%...
copy /y "bin\Release\net10.0\win-x64\publish\WebullAnalytics.exe" "%INSTALL_DIR%\" >nul
if %errorLevel% neq 0 (
    echo ERROR: Failed to copy executable.
    pause
    exit /b 1
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
echo You can now run: WebullAnalytics
echo.
pause
