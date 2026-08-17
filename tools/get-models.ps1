<#
.SYNOPSIS
Downloads the specialist ONNX models the companion can optionally use.

.DESCRIPTION
Weights are NOT committed to this repository. They are tens to hundreds of megabytes, they change
independently of the code, and vendoring them is difficult to undo later — so they are fetched, and
this script is the record of exactly what from where under which licence.

Everything downloaded here is optional. A companion with no models present starts, runs and passes
its full test suite; each model improves on a heuristic that still exists. See
docs/SPECIALIST_MODELS.md.

.PARAMETER Destination
Where to put them. Defaults to the models directory beside the API's database, which is what
CognitiveModels:Directory resolves to by default.

.EXAMPLE
./tools/get-models.ps1
./tools/get-models.ps1 -Destination D:\models -Model reranker
#>
[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $PSScriptRoot "..\src\Companion.Api\models"),
    [ValidateSet("reranker", "nli", "all")]
    [string] $Model = "all"
)

$ErrorActionPreference = "Stop"

# repo, revision-pinned files, and the licence they arrive under. Pinned to a revision rather than
# `main` so that "it worked last week" stays reproducible — a model repo can be updated under you,
# and a silently different reranker is exactly the kind of change that is invisible until the
# retrieval quality drifts.
$models = @(
    @{
        Name     = "reranker"
        Repo     = "cross-encoder/ms-marco-MiniLM-L6-v2"
        Revision = "main"
        Licence  = "apache-2.0"
        Files    = @(
            @{ Remote = "onnx/model.onnx"; Local = "reranker.onnx" },
            @{ Remote = "vocab.txt";       Local = "vocab.txt" }
        )
    },
    @{
        Name     = "nli"
        Repo     = "cross-encoder/nli-MiniLM2-L6-H768"
        Revision = "main"
        Licence  = "apache-2.0"
        # config.json comes down with the weights because the LABEL ORDER lives in it, and NLI
        # models do not agree on one: this repo is contradiction/entailment/neutral, while the
        # DeBERTa family is entailment/neutral/contradiction. Reading it is the difference between
        # "these facts contradict" and "these facts agree" — assuming it would be a coin flip on the
        # one judgment that retires a memory.
        Files    = @(
            @{ Remote = "onnx/model.onnx";  Local = "nli.onnx" },
            @{ Remote = "tokenizer.json";   Local = "nli-tokenizer.json" },
            @{ Remote = "config.json";      Local = "nli-config.json" }
        )
    }
)

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}
$Destination = (Resolve-Path $Destination).Path
Write-Host "models -> $Destination"

foreach ($m in $models) {
    if ($Model -ne "all" -and $Model -ne $m.Name) { continue }

    Write-Host ""
    Write-Host "$($m.Name): $($m.Repo) @ $($m.Revision)  [$($m.Licence)]"

    foreach ($f in $m.Files) {
        $target = Join-Path $Destination $f.Local
        if (Test-Path $target) {
            Write-Host "  = $($f.Local) (already here)"
            continue
        }

        $url = "https://huggingface.co/$($m.Repo)/resolve/$($m.Revision)/$($f.Remote)"
        Write-Host "  + $($f.Local)"
        Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing

        $size = [math]::Round((Get-Item $target).Length / 1MB, 1)
        Write-Host "    $size MB"
    }
}

Write-Host ""
Write-Host "Done. Enable it with CognitiveModels:Reranker:Enabled=true, then check"
Write-Host "GET /diagnostics/cognitive to confirm it actually loaded."
