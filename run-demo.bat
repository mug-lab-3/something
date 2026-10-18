@echo off
setlocal
set "somethingDemoExe=%~dp0src\Something.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\Something.App.exe"

if not exist "%somethingDemoExe%" (
    echo Release build was not found.
    echo Run this command from the repository root first:
    echo   dotnet build src/Something.App -c Release
    pause
    exit /b 1
)

start "" "%somethingDemoExe%" --demo --demo-history "%~dp0artifacts\demo-history"
if errorlevel 1 (
    echo Could not launch the demo.
    pause
    exit /b 1
)
exit /b 0
