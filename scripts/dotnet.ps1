param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)
$ErrorActionPreference = 'Stop'
$taskRepoRoot = Split-Path -Parent $PSScriptRoot
$taskDotnetRoot = Join-Path $taskRepoRoot '.tools/dotnet'
$taskDotnetExe = Join-Path $taskDotnetRoot 'dotnet.exe'
if (Test-Path -LiteralPath $taskDotnetExe) {
    $env:DOTNET_ROOT = $taskDotnetRoot
    $env:PATH = "$taskDotnetRoot;$env:PATH"
} else {
    $taskDotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
}
& $taskDotnetExe @Arguments
exit $LASTEXITCODE
