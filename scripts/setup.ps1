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
if (!$SkipExtensions -or $Open) {
    $taskCode = Get-Command code -ErrorAction SilentlyContinue
    if (!$taskCode) { throw 'Install VS Code with its code command on PATH, then reopen the terminal. CLI-only setup: -SkipExtensions.' }
}

function Invoke-SetupDotnet([string[]] $CommandArguments) {
    & $taskDotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet command failed: $($CommandArguments -join ' ')" }
}

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
    $taskDotnet = & (Join-Path $PSScriptRoot 'setup-system-sdk.ps1')
    $taskSdkRoot = Split-Path -Parent $taskDotnet
    $env:PATH = "$taskSdkRoot;$taskOldPath"
    Invoke-SetupDotnet @('--version')
    Invoke-SetupDotnet @('tool', 'restore')
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
    Write-Output "Setup verified. Open repository folder: $taskRepoRoot"
    if ($Open) {
        & $taskCode.Source --new-window $taskRepoRoot
        if ($LASTEXITCODE -ne 0) { throw 'Opening the VS Code workspace failed.' }
    }
}
finally {
    $env:PATH = $taskOldPath
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $taskOldTelemetry
    $env:DOTNET_NOLOGO = $taskOldNoLogo
    $ProgressPreference = $taskOldProgress
    Pop-Location
}
