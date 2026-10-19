@echo off
setlocal
set "somethingPreviewExe=%~dp0src\Something.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\Something.App.exe"
if not exist "%somethingPreviewExe%" (
    echo Release build was not found.
    echo Run: dotnet build src/Something.App -c Release
    pause
    exit /b 1
)
start "" "%somethingPreviewExe%" --animation-preview
if errorlevel 1 (
    echo Could not launch the animation preview.
    pause
    exit /b 1
)
exit /b 0
