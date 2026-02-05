@echo off
REM Build Webull Analytics

echo Building Webull Analytics...
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
echo ============================================
echo Build completed successfully!
echo ============================================
echo.
echo Output location:
echo bin\Release\net10.0\win-x64\publish\
echo.
pause
