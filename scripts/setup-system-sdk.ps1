$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'System SDK setup supports Windows only.' }
$taskRepoRoot = Split-Path -Parent $PSScriptRoot
$taskDownloads = Get-Content (Join-Path $PSScriptRoot 'sdk-downloads.json') -Raw -Encoding utf8 | ConvertFrom-Json
$taskGlobal = Get-Content (Join-Path $taskRepoRoot 'global.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($taskDownloads.version -ne $taskGlobal.sdk.version) { throw 'SDK manifest and global.json must match.' }
$taskArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($taskArchitecture -notin @('X64', 'Arm64')) { throw "Unsupported architecture: $taskArchitecture" }
$taskProgramFiles = [Environment]::GetEnvironmentVariable('ProgramW6432')
if (!$taskProgramFiles) { $taskProgramFiles = [Environment]::GetFolderPath('ProgramFiles') }
$taskDotnet = Join-Path $taskProgramFiles 'dotnet/dotnet.exe'
function Test-PinnedSystemSdk {
    if (!(Test-Path -LiteralPath $taskDotnet)) { return $false }
    $taskSdks = & $taskDotnet --list-sdks
    return $LASTEXITCODE -eq 0 -and [bool]($taskSdks -match "^$([regex]::Escape($taskDownloads.version)) ")
}
if (!(Test-PinnedSystemSdk)) {
    $taskInstallerInfo = $taskDownloads.installers.$taskArchitecture
    $taskCache = Join-Path $taskRepoRoot '.tools/installers'
    New-Item -ItemType Directory -Force -Path $taskCache | Out-Null
    $taskInstaller = Join-Path $taskCache "dotnet-sdk-$($taskDownloads.version)-$taskArchitecture.exe"
    if (!(Test-Path -LiteralPath $taskInstaller)) {
        $taskPartial = "$taskInstaller.$([Guid]::NewGuid().ToString('N')).partial"
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Write-Host "Downloading official .NET SDK $($taskDownloads.version) installer..."
        Invoke-WebRequest -UseBasicParsing -Uri $taskInstallerInfo.url -OutFile $taskPartial
        if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA512).Hash -ne $taskInstallerInfo.sha512) {
            throw 'Downloaded SDK installer SHA-512 mismatch.'
        }
        Move-Item -LiteralPath $taskPartial -Destination $taskInstaller
    }
    if ((Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA512).Hash -ne $taskInstallerInfo.sha512) {
        throw 'Cached SDK installer SHA-512 mismatch.'
    }
    Write-Host 'Installing the system SDK. Accept the Windows administrator prompt if shown.'
    $taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList @('/install', '/quiet', '/norestart') -WindowStyle Hidden -PassThru -Wait
    if ($taskProcess.ExitCode -notin @(0, 3010)) { throw "SDK installer failed with exit code $($taskProcess.ExitCode)." }
    if (!(Test-PinnedSystemSdk)) { throw 'The pinned SDK was not found after installation.' }
    if ($taskProcess.ExitCode -eq 3010) { Write-Host 'The SDK installer requested a Windows restart.' }
}
Write-Output $taskDotnet
