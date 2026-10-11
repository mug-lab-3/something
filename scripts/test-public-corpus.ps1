param([switch] $Download)
$ErrorActionPreference = 'Stop'
$taskRepoRoot = Split-Path -Parent $PSScriptRoot
$taskRoot = Join-Path $taskRepoRoot '.tools/corpus/nist'
$taskManifest = Get-Content (Join-Path $taskRepoRoot 'tests/fixtures/nist-corpus.json') -Raw -Encoding utf8 | ConvertFrom-Json
$taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source

function Assert-CorpusHash([string] $Path, [string] $Expected) {
    if (!(Test-Path -LiteralPath $Path)) { throw "Missing corpus file: $Path" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "Corpus SHA-256 mismatch: $Path. Do not update the manifest without investigating the source change."
    }
}

Push-Location $taskRepoRoot
$taskPreviousCorpus = $env:SOMETHING_NIST_CORPUS
try {
    New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
    & $taskDotnet restore Something.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $taskDotnet build Something.slnx --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($taskCase in $taskManifest.Cases) {
        $taskCompressed = Join-Path $taskRoot $taskCase.Source
        if (!(Test-Path -LiteralPath $taskCompressed)) {
            if (!$Download) { throw 'Corpus not downloaded. Run this script with -Download to fetch the NIST images.' }
            Invoke-WebRequest -Uri $taskCase.Url -OutFile $taskCompressed
        }
        Assert-CorpusHash $taskCompressed $taskCase.CompressedSha256
        $taskOutput = Join-Path $taskRoot $taskCase.Id
        if (!(Test-Path -LiteralPath (Join-Path $taskOutput 'report.json'))) {
            & $taskDotnet run --project tools/Something.CorpusCheck --no-build -- $taskCompressed $taskOutput
            if ($LASTEXITCODE -ne 0) { throw "Corpus extraction/check failed: $($taskCase.Id). Interrupted outputs must be retried in a fresh directory." }
        }
        Assert-CorpusHash (Join-Path $taskOutput 'image.dd') $taskCase.ImageSha256
        Assert-CorpusHash (Join-Path $taskOutput 'mft.raw') $taskCase.MftSha256
    }
    $env:SOMETHING_NIST_CORPUS = $taskRoot
    & $taskDotnet test --solution Something.slnx --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    $env:SOMETHING_NIST_CORPUS = $taskPreviousCorpus
    Pop-Location
}
