# Stop every service this package started. Leaves your Visual Studio instance alone.
Get-CimInstance Win32_Process | Where-Object {
  $_.CommandLine -match 'serve_run2|Companion.Api' -and $_.CommandLine -notmatch 'devenv'
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Host "Shadow services stopped."
