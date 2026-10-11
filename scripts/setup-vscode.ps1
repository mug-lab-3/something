param([switch] $Open)
$ErrorActionPreference = 'Stop'
$taskRepoRoot = Split-Path -Parent $PSScriptRoot
$taskDotnetRoot = Join-Path $taskRepoRoot '.tools/dotnet'
$taskDotnetExe = Join-Path $taskDotnetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnetExe)) {
    throw 'Run scripts/setup.ps1 first to install the pinned SDK and prepare the development environment.'
}
# existingDotnetPath does not expand VS Code variables. Generate a local workspace with real paths.
$taskWorkspaceFile = Join-Path $taskRepoRoot '.tools/Something.code-workspace'
$taskWorkspaceSettings = @{
    folders = @(@{ path = $taskRepoRoot })
    settings = @{
        'dotnet.dotnetPath' = $taskDotnetExe
        'dotnetAcquisitionExtension.existingDotnetPath' = @(
            @{ extensionId = 'ms-dotnettools.csharp'; path = $taskDotnetExe },
            @{ extensionId = 'ms-dotnettools.csdevkit'; path = $taskDotnetExe }
        )
    }
}
$taskWorkspaceJson = $taskWorkspaceSettings | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($taskWorkspaceFile, $taskWorkspaceJson, [Text.UTF8Encoding]::new($false))
Write-Output "Workspace: $taskWorkspaceFile"
if ($Open) {
    $env:DOTNET_ROOT = $taskDotnetRoot
    $env:PATH = "$taskDotnetRoot;$env:PATH"
    & code --new-window $taskWorkspaceFile
    exit $LASTEXITCODE
}
