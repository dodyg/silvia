# Publishes a self-contained Windows build of Silvia to publish/win-x64.
# The frontend is built by the NeoAstra build step, so the output carries the bundled UI.
# The models are not part of the build: the app downloads them from Settings > Global.
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\publish\win-x64')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\Silvia.App\Silvia.App.csproj'

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $Output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $Output 'Silvia.exe'
if (-not (Test-Path $exe)) {
    throw "Expected $exe after publish."
}

Write-Host "Published to $((Resolve-Path $Output).Path)"
