$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Native runtime setup supports Windows only.' }

$taskRuntimeKey = 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
$taskDownload = Get-Content (Join-Path $PSScriptRoot 'native-runtime-download.json') -Raw -Encoding utf8 | ConvertFrom-Json
$taskMinimumRuntime = [version]$taskDownload.version
function Test-NativeRuntime {
    $taskRuntime = Get-ItemProperty -LiteralPath $taskRuntimeKey -ErrorAction SilentlyContinue
    return $null -ne $taskRuntime -and $taskRuntime.Installed -eq 1 -and [version]$taskRuntime.Version.TrimStart('v') -ge $taskMinimumRuntime
}
if (Test-NativeRuntime) { return }

$taskCache = Join-Path (Split-Path -Parent $PSScriptRoot) '.tools/installers'
New-Item -ItemType Directory -Force -Path $taskCache | Out-Null
$taskInstaller = Join-Path $taskCache "vc-redist-$($taskDownload.version)-x64.exe"
if (!(Test-Path -LiteralPath $taskInstaller)) {
    $taskPartial = "$taskInstaller.$([Guid]::NewGuid().ToString('N')).partial"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $taskDownload.url -OutFile $taskPartial
    if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash -ne $taskDownload.sha256) { throw 'Native runtime installer SHA-256 mismatch.' }
    Move-Item -LiteralPath $taskPartial -Destination $taskInstaller
}
if ((Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash -ne $taskDownload.sha256) { throw 'Cached native runtime installer SHA-256 mismatch.' }
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskInstaller
if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Native runtime installer is not signed by Microsoft.' }
Write-Host 'Installing Microsoft Visual C++ Redistributable x64 for DuckDB. Windows may request administrator approval.'
$taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList @('/install', '/quiet', '/norestart') -WindowStyle Hidden -PassThru -Wait
if ($taskProcess.ExitCode -notin @(0,3010) -or !(Test-NativeRuntime)) { throw "Native runtime installation failed: $($taskProcess.ExitCode)." }
if ($taskProcess.ExitCode -eq 3010) { Write-Host 'The native runtime installer requested a Windows restart.' }
