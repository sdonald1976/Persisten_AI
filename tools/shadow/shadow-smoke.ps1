# One documented action to run representative tests: drive a handful of turns through the API so
# retrieval (and the reranker shadow) runs and records comparisons. Uses demo-user (guard-critical
# mouth) plus a second user (production Stheno) so both routes' retrieval is exercised.
param([string]$ApiBase = "http://127.0.0.1:5266")
$ErrorActionPreference = "Stop"
function Chat($conv, $msg) {
  $body = @{ conversationId = $conv; message = $msg } | ConvertTo-Json
  try { Invoke-RestMethod -Method Post -Uri "$ApiBase/chat" -ContentType "application/json" -Body $body -TimeoutSec 300 | Out-Null }
  catch { Write-Host "  (turn error: $($_.Exception.Message))" }
}
function NewConv { (Invoke-RestMethod -Method Post -Uri "$ApiBase/conversations" -ContentType "application/json" -Body '{"title":"shadow-smoke","source":"probe"}').conversationId }
$c = NewConv
$msgs = @(
  "what do you remember about my shed?",
  "how's the bird feeder situation?",
  "did the socket wrench ever turn up?",
  "remind me what we said about the buoy sensor project",
  "what was that thing about the squirrel baffle?"
)
foreach ($m in $msgs) { Write-Host "USER: $m"; Chat $c $m }
Write-Host "Done. Now run: tools\shadow\shadow-report.ps1"
