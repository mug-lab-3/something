param(
    [switch] $SkipExtensions,
    [switch] $WithPublicCorpus,
    [switch] $Open
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') { throw 'Something development setup currently supports Windows only.' }
$taskRepoRoot = Split-Path -Parent $PSScriptRoot
$taskToolsRoot = Join-Path $taskRepoRoot '.tools'
$taskSdkRoot = Join-Path $taskToolsRoot 'dotnet'
$taskDotnet = Join-Path $taskSdkRoot 'dotnet.exe'
$taskGlobal = Get-Content (Join-Path $taskRepoRoot 'global.json') -Raw -Encoding utf8 | ConvertFrom-Json
$taskDownloads = Get-Content (Join-Path $PSScriptRoot 'sdk-downloads.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($taskGlobal.sdk.version -ne $taskDownloads.version) { throw 'global.json and sdk-downloads.json SDK versions must match.' }
# OS architecture, not the bitness of the PowerShell process.
$taskArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($taskArchitecture -notin @('X64', 'Arm64')) { throw "Unsupported Windows architecture: $taskArchitecture" }
$taskArchive = $taskDownloads.archives.$taskArchitecture
if (!$SkipExtensions -or $Open) {
    $taskCode = Get-Command code -ErrorAction SilentlyContinue
    if (!$taskCode) { throw 'Install VS Code with its code command on PATH, then reopen the terminal. CLI-only setup: -SkipExtensions.' }
}

function Invoke-SetupDotnet([string[]] $CommandArguments) {
    & $taskDotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet command failed: $($CommandArguments -join ' ')" }
}

$taskOldDotnetRoot = $env:DOTNET_ROOT
$taskOldPath = $env:PATH
$taskOldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$taskOldNoLogo = $env:DOTNET_NOLOGO
$taskOldProgress = $ProgressPreference
Push-Location $taskRepoRoot
try {
    New-Item -ItemType Directory -Force -Path $taskToolsRoot | Out-Null
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $ProgressPreference = 'SilentlyContinue'
    $taskSdkPresent = $false
    if (Test-Path -LiteralPath $taskDotnet) {
        $taskVersion = & $taskDotnet --version
        $taskSdkPresent = $LASTEXITCODE -eq 0 -and $taskVersion -eq $taskDownloads.version
    }
    if (!$taskSdkPresent) {
        Write-Output "Installing local .NET SDK $($taskDownloads.version) ($taskArchitecture)..."
        $taskZip = Join-Path $taskToolsRoot "dotnet-sdk-$($taskDownloads.version)-$taskArchitecture.zip"
        if (!(Test-Path -LiteralPath $taskZip)) {
            $taskPartial = "$taskZip.$([Guid]::NewGuid().ToString('N')).partial"
            # TLS 1.2 is needed on some Windows PowerShell 5.1 installations.
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Uri $taskArchive.url -OutFile $taskPartial
            if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA512).Hash -ne $taskArchive.sha512) {
                throw "SDK download SHA-512 mismatch: $taskPartial"
            }
            Move-Item -LiteralPath $taskPartial -Destination $taskZip
        }
        if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA512).Hash -ne $taskArchive.sha512) {
            throw "Cached SDK archive SHA-512 mismatch: $taskZip"
        }
        $taskStage = Join-Path $taskToolsRoot "dotnet-install-$([Guid]::NewGuid().ToString('N'))"
        Expand-Archive -LiteralPath $taskZip -DestinationPath $taskStage
        $taskStageExe = Join-Path $taskStage 'dotnet.exe'
        $taskInstalledSdks = & $taskStageExe --list-sdks
        if ($LASTEXITCODE -ne 0 -or !($taskInstalledSdks -match "^$([regex]::Escape($taskDownloads.version)) ")) {
            throw 'Downloaded SDK could not be executed or did not contain the expected version.'
        }
        # Preserve an older installation; never delete directories in setup.
        if (Test-Path -LiteralPath $taskSdkRoot) {
            $taskBackup = Join-Path $taskToolsRoot "dotnet-backup-$([Guid]::NewGuid().ToString('N'))"
            Move-Item -LiteralPath $taskSdkRoot -Destination $taskBackup
            Write-Output "Previous SDK preserved at: $taskBackup"
        }
        Move-Item -LiteralPath $taskStage -Destination $taskSdkRoot
    }
    $env:DOTNET_ROOT = $taskSdkRoot
    $env:PATH = "$taskSdkRoot;$taskOldPath"
    Invoke-SetupDotnet @('--version')
    Invoke-SetupDotnet @('tool', 'restore')
    & (Join-Path $PSScriptRoot 'setup-vscode.ps1')
    if (!$SkipExtensions) {
        $taskRecommendations = Get-Content (Join-Path $taskRepoRoot '.vscode/extensions.json') -Raw -Encoding utf8 | ConvertFrom-Json
        foreach ($taskExtension in $taskRecommendations.recommendations) {
            & $taskCode.Source --install-extension $taskExtension
            if ($LASTEXITCODE -ne 0) { throw "VS Code extension installation failed: $taskExtension" }
        }
    }
    Invoke-SetupDotnet @('restore', 'Something.slnx', '--locked-mode')
    Invoke-SetupDotnet @('csharpier', 'check', '.', '--ignore-path', '.csharpierignore')
    Invoke-SetupDotnet @('build', 'Something.slnx', '--no-restore')
    if ($WithPublicCorpus) {
        & (Join-Path $PSScriptRoot 'test-public-corpus.ps1') -Download
    } else {
        Invoke-SetupDotnet @('test', '--solution', 'Something.slnx', '--no-restore')
    }
    Write-Output "Setup verified. Workspace: $(Join-Path $taskToolsRoot 'Something.code-workspace')"
    if ($Open) {
        & $taskCode.Source --new-window (Join-Path $taskToolsRoot 'Something.code-workspace')
        if ($LASTEXITCODE -ne 0) { throw 'Opening the VS Code workspace failed.' }
    }
}
finally {
    $env:DOTNET_ROOT = $taskOldDotnetRoot
    $env:PATH = $taskOldPath
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $taskOldTelemetry
    $env:DOTNET_NOLOGO = $taskOldNoLogo
    $ProgressPreference = $taskOldProgress
    Pop-Location
}
