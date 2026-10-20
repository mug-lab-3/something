@echo off
setlocal
set "somethingPreviewExe=%~dp0src\Something.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\Something.App.exe"
if not exist "%somethingPreviewExe%" (
    echo Release build was not found.
    echo Run: dotnet build src/Something.App -c Release
    pause
    exit /b 1
)
rem The demo answers the update check with a synthetic release; it does not use the network.
start "" "%somethingPreviewExe%" --demo --demo-history "%~dp0artifacts\demo-history" --preview-update
if errorlevel 1 (
    echo Could not launch the update preview.
    pause
    exit /b 1
)
exit /b 0
