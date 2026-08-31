# One command to start the shadow testing services.
# Requires: Ollama running (the 3B reranker + Stheno), reranker.onnx present (it is).
# Starts the Run-2.2 mouth server (for demo-user guard-critical testing) and the API with the
# reranker shadow ON. Nothing here promotes anything: the 3B stays authoritative.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Write-Host "Starting Run-2.2 mouth server on :11436 ..."
Start-Process -WindowStyle Minimized -FilePath "$repo\training\.venv-train\Scripts\python.exe" `
  -ArgumentList "$repo\training\mouth\serve_run2.py","--run","run-2.2","--port","11436" -WorkingDirectory "$repo\training\mouth"
Write-Host "Starting API on :5266 with RerankShadow=true ..."
$env:CognitiveModels__RerankShadow = "true"
$env:CognitiveModels__Reranker__Enabled = "true"
Start-Process -WindowStyle Minimized -FilePath "dotnet" `
  -ArgumentList "run","-c","Release","--no-launch-profile","--project","$repo\src\Companion.Api" -WorkingDirectory "$repo\src\Companion.Api"
Write-Host "Services starting. API http://127.0.0.1:5266  Mouth http://127.0.0.1:11436"
Write-Host "Stop with: tools\shadow\shadow-stop.ps1"
