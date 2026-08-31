# Generate the comparison report from whatever shadow + reviewed data exists.
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& "$repo\training\.venv-train\Scripts\python.exe" "$repo\training\cognition\rerank_eval.py"
Write-Host "Report: training\cognition\rerank-shadow\RERANK_SHADOW_REPORT.md"
