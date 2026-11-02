# Downloads the local ONNX models Silvia uses for embeddings and reranking.
# Files land in <repo>/models (git-ignored) unless -Target is given.
param(
    [string]$Target = (Join-Path $PSScriptRoot '..\models')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Target | Out-Null

$files = @(
    @{ Url = 'https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main/onnx/model.onnx'; Name = 'bge-small-en-v1.5.onnx' },
    @{ Url = 'https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/main/vocab.txt';        Name = 'bge-small-en-v1.5.vocab.txt' },
    @{ Url = 'https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main/onnx/model.onnx'; Name = 'ms-marco-MiniLM-L-6-v2.onnx' },
    @{ Url = 'https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main/vocab.txt'; Name = 'ms-marco-MiniLM-L-6-v2.vocab.txt' }
)

foreach ($f in $files) {
    $dest = Join-Path $Target $f.Name
    if (Test-Path $dest) {
        Write-Host "Already present: $($f.Name)"
        continue
    }
    Write-Host "Downloading $($f.Name) ..."
    Invoke-WebRequest -Uri $f.Url -OutFile "$dest.partial" -UseBasicParsing
    Move-Item -Force "$dest.partial" $dest
}
Write-Host "Models ready in $((Resolve-Path $Target).Path)"
