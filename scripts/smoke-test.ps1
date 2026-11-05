# Checks a published build of Silvia as a fresh machine would run it: an empty data folder and, unless -Models is
# given, no local models. Runs `Silvia.exe --smoke-test`, which checks the bundled interface, the icon and the
# WebView2 runtime, then creates a project, indexes a sample file, finds it and deletes the project.
#
#   scripts/smoke-test.ps1 -Publish               # publish to publish/win-x64, then check it
#   scripts/smoke-test.ps1                        # check the build already in publish/win-x64
#   scripts/smoke-test.ps1 -Models models         # also check vector search with the local models
#
# On a clean computer or VM, copy the publish folder and this script there and run it with -Exe.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\win-x64\Silvia.exe'),
    [string]$Models,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

if ($Publish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Output (Split-Path $Exe)
}

if (-not (Test-Path $Exe)) {
    throw "No build at $Exe. Run with -Publish, or pass -Exe."
}

$data = Join-Path ([System.IO.Path]::GetTempPath()) ("silvia-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $data | Out-Null
$saved = @{ Data = $env:SILVIA_DATA; Models = $env:SILVIA_MODELS; Key = $env:DEEPSEEK_API_KEY }
try {
    $env:SILVIA_DATA = $data
    # A fresh machine has no models and no key: the empty folder makes the build search by keyword.
    $env:SILVIA_MODELS = if ($Models) { (Resolve-Path $Models).Path } else { Join-Path $data 'models' }
    $env:DEEPSEEK_API_KEY = $null

    Write-Host "Checking $((Resolve-Path $Exe).Path) with data in $data"
    & $Exe --smoke-test
    $code = $LASTEXITCODE
}
finally {
    $env:SILVIA_DATA = $saved.Data
    $env:SILVIA_MODELS = $saved.Models
    $env:DEEPSEEK_API_KEY = $saved.Key
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}

if ($code -ne 0) {
    throw "Smoke test failed with exit code $code."
}
