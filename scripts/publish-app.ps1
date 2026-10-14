$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') { throw 'Publish Something on Windows after running scripts/setup.ps1.' }
$taskRepoRoot = Split-Path -Parent $PSScriptRoot
$taskArtifacts = Join-Path $taskRepoRoot 'artifacts'
$taskPublishRoot = Join-Path $taskArtifacts 'publish'
$taskBuildRoot = Join-Path $taskArtifacts 'publish-build'
$taskOutput = Join-Path $taskPublishRoot 'win-x64'
$taskId = [Guid]::NewGuid().ToString('N')
$taskStage = Join-Path $taskPublishRoot ".win-x64-$taskId"
$taskBackup = Join-Path $taskPublishRoot ".previous-win-x64-$taskId"

function Assert-OwnedOutput([string] $Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($taskPublishRoot) -or
        $resolved -notin @($taskOutput, $taskStage, $taskBackup)) {
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
    & dotnet restore src/Something.App --locked-mode -p:PublishProfile=SingleFile --artifacts-path $taskBuildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore for publishing failed.' }
    & dotnet publish src/Something.App -c Release --no-restore -p:PublishProfile=SingleFile --artifacts-path $taskBuildRoot -o $taskStage
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Run scripts/setup.ps1 if packages are not restored.' }
    $taskPublishedFiles = @(Get-ChildItem -LiteralPath $taskStage -Recurse -File)
    if ($taskPublishedFiles.Count -ne 1 -or $taskPublishedFiles[0].FullName -ne (Join-Path $taskStage 'Something.App.exe')) {
        throw 'Single-file publish must contain exactly Something.App.exe. The previous release was retained.'
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
    Write-Host "Published: $taskOutput\Something.App.exe"
} finally {
    if (Test-Path -LiteralPath $taskStage) {
        Assert-OwnedOutput $taskStage
        Remove-Item -LiteralPath $taskStage -Recurse -Force
    }
    Pop-Location
}
