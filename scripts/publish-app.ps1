$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Publishes the self-contained application folder and its per-machine MSI.
# The elevated application must load every file from Program Files, so no single-file
# self-extraction is produced: that would load DLLs from a user-writable %TEMP% folder.
if ($env:OS -ne 'Windows_NT') { throw 'Publish Something on Windows after running scripts/setup.ps1.' }
$taskRepoRoot = Split-Path -Parent $PSScriptRoot
$taskArtifacts = Join-Path $taskRepoRoot 'artifacts'
$taskPublishRoot = Join-Path $taskArtifacts 'publish'
$taskBuildRoot = Join-Path $taskArtifacts 'publish-build'
$taskOutput = Join-Path $taskPublishRoot 'win-x64'
$taskId = [Guid]::NewGuid().ToString('N')
$taskStage = Join-Path $taskPublishRoot ".win-x64-$taskId"
$taskBackup = Join-Path $taskPublishRoot ".previous-win-x64-$taskId"
$taskInstallerStage = Join-Path $taskPublishRoot ".installer-$taskId"

[xml] $taskProject = Get-Content (Join-Path $taskRepoRoot 'src/Something.App/Something.App.csproj') -Raw
$taskVersion = $taskProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($taskVersion -notmatch '^([0-9]+\.[0-9]+\.[0-9]+)(?:-[0-9A-Za-z.-]+)?$') {
    throw "Unsupported application version: $taskVersion"
}
# Windows Installer versions are numeric; a prerelease shares its release's MSI version.
$taskInstallerVersion = $Matches[1]
$taskInstaller = Join-Path $taskPublishRoot "Something-v$taskVersion-win-x64.msi"

function Assert-OwnedOutput([string] $Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($taskPublishRoot) -or
        $resolved -notin @($taskOutput, $taskStage, $taskBackup, $taskInstallerStage, $taskInstaller)) {
        throw "Not an owned release output: $resolved"
    }
    if ((Test-Path -LiteralPath $resolved) -and
        ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Release outputs must not be reparse points: $resolved"
    }
}

foreach ($directory in @($taskArtifacts, $taskPublishRoot, $taskBuildRoot)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Generated output directories must not be reparse points: $directory"
    }
}
Assert-OwnedOutput $taskOutput
Assert-OwnedOutput $taskInstaller
foreach ($process in @(Get-Process -Name Something.App -ErrorAction SilentlyContinue)) {
    try { $runningPath = $process.MainModule.FileName } catch { $runningPath = $null }
    if ($runningPath -eq (Join-Path $taskOutput 'Something.App.exe')) {
        throw 'Close the app running from artifacts/publish/win-x64 before replacing that publish output.'
    }
}

Push-Location $taskRepoRoot
try {
    # Isolate both output and intermediate files from development and diagnostic builds.
    # Restore into that isolated layout without changing the checked-in dependencies.
    & dotnet restore src/Something.App --locked-mode -p:PublishProfile=Folder --artifacts-path $taskBuildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore for publishing failed.' }
    & dotnet publish src/Something.App -c Release --no-restore -p:PublishProfile=Folder --artifacts-path $taskBuildRoot -o $taskStage
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Run scripts/setup.ps1 if packages are not restored.' }
    foreach ($required in @('Something.App.exe', 'Something.App.pri', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Assets/Something.ico')) {
        if (!(Test-Path -LiteralPath (Join-Path $taskStage $required) -PathType Leaf)) {
            throw "Published folder lacks $required. The previous release was retained."
        }
    }
    if (@(Get-ChildItem -LiteralPath $taskStage -Recurse -Filter *.pdb).Count -ne 0) {
        throw 'Published folder must not contain symbols. The previous release was retained.'
    }

    Assert-OwnedOutput $taskInstallerStage
    & dotnet restore installer/Something.Installer.wixproj --locked-mode --artifacts-path $taskBuildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore for the installer failed.' }
    & dotnet build installer/Something.Installer.wixproj -c Release --no-restore --artifacts-path $taskBuildRoot `
        "-p:ProductVersion=$taskInstallerVersion" "-p:PublishDir=$taskStage" -o $taskInstallerStage
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed. The previous release was retained.' }
    # The single Japanese culture is built into a ja-JP subfolder.
    $taskBuiltInstaller = Join-Path $taskInstallerStage 'ja-JP/Something.msi'
    if (!(Test-Path -LiteralPath $taskBuiltInstaller -PathType Leaf)) {
        throw 'Installer build produced no Something.msi. The previous release was retained.'
    }

    Assert-OwnedOutput $taskStage
    Assert-OwnedOutput $taskBackup
    if (Test-Path -LiteralPath $taskOutput) {
        Move-Item -LiteralPath $taskOutput -Destination $taskBackup
    }
    try {
        Move-Item -LiteralPath $taskStage -Destination $taskOutput
    } catch {
        if ((Test-Path -LiteralPath $taskBackup) -and !(Test-Path -LiteralPath $taskOutput)) {
            Move-Item -LiteralPath $taskBackup -Destination $taskOutput
        }
        throw
    }
    if (Test-Path -LiteralPath $taskBackup) {
        Assert-OwnedOutput $taskBackup
        Remove-Item -LiteralPath $taskBackup -Recurse -Force
    }
    Move-Item -LiteralPath $taskBuiltInstaller -Destination $taskInstaller -Force
    Write-Host "Published: $taskOutput"
    Write-Host "Installer: $taskInstaller"
} finally {
    foreach ($temporary in @($taskStage, $taskInstallerStage)) {
        if (Test-Path -LiteralPath $temporary) {
            Assert-OwnedOutput $temporary
            Remove-Item -LiteralPath $temporary -Recurse -Force
        }
    }
    Pop-Location
}
