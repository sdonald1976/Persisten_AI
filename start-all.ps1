<#
.SYNOPSIS
    Make sure Ava has everything her configuration asks for, then start her.

.DESCRIPTION
    The one command for a newly cloned machine. It resolves the SAME effective configuration the
    application loads (appsettings.json, the environment-specific file, appsettings.local.json,
    then environment variables), works out which models and adapters that configuration actually
    requires, acquires what is missing, verifies what is there, and only then starts anything.

    A required model that cannot be acquired or verified STOPS startup. Nothing is ever quietly
    substituted: a companion that comes up with a different model than she was configured with is
    the failure this exists to prevent, and it is the kind that looks like success.

    Already-provisioned machines pay for a catalog query and a hash or two — no downloads.

    The interpretation of configuration lives in the typed .NET tool (tools/Companion.Bootstrap),
    not here, because a PowerShell reimplementation of option binding is a second thing to keep
    in step with the application, and it would be wrong the first time the app changed.

.PARAMETER DryRun
    List what would be checked and acquired. Downloads nothing, changes nothing, starts nothing.

.PARAMETER VerifyOnly
    Check only. Downloads nothing and exits non-zero if anything required is missing or invalid.
    Starts nothing.

.PARAMETER Force
    Reacquire a specific dependency by id (repeatable), even though it looks present. Ids come
    from -Inventory. There is deliberately no "force everything": on this roster that is tens of
    gigabytes of intentional waste.

.PARAMETER AllConfigured
    Include models that configuration names but no enabled capability uses. Normal startup checks
    only what is actually required.

.PARAMETER Inventory
    Print the configured model inventory and exit.

.PARAMETER SkipBootstrap
    Start without checking anything. For when you already know.

.EXAMPLE
    .\start-all.ps1
    .\start-all.ps1 -DryRun
    .\start-all.ps1 -VerifyOnly
    .\start-all.ps1 -Force model.conversation
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$VerifyOnly,
    [string[]]$Force,
    [switch]$AllConfigured,
    [switch]$Inventory,
    [switch]$SkipBootstrap,
    [switch]$NoClient,
    [string]$World
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

# ---- 1. bootstrap -----------------------------------------------------------------------------

if (-not $SkipBootstrap) {
    $bootstrapArgs = @()
    if ($DryRun)        { $bootstrapArgs += '--dry-run' }
    if ($VerifyOnly)    { $bootstrapArgs += '--verify-only' }
    if ($AllConfigured) { $bootstrapArgs += '--all-configured' }
    if ($Inventory)     { $bootstrapArgs += '--inventory' }
    foreach ($id in $Force) { $bootstrapArgs += @('--force', $id) }

    $project = Join-Path $root 'tools\Companion.Bootstrap\Companion.Bootstrap.csproj'
    if (-not (Test-Path $project)) {
        Say "bootstrap  not found at $project" Red
        exit 1
    }

    # `dotnet run` prints build output that buries the report; build first, then run quietly.
    & dotnet build $project -v quiet --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Say 'bootstrap  could not be built - fix the build before starting' Red
        exit 1
    }

    & dotnet run --project $project --no-build -- @bootstrapArgs
    $bootstrapExit = $LASTEXITCODE

    if ($Inventory -or $DryRun -or $VerifyOnly) {
        # These modes are questions, not startup. Their answer is the exit code.
        exit $bootstrapExit
    }

    if ($bootstrapExit -ne 0) {
        Say ''
        Say 'Refusing to start: a required model is unavailable (see above).' Red
        Say 'Nothing was substituted. Fix the dependency, or re-run with -SkipBootstrap if you' Red
        Say 'intend to start anyway and know what will fail.' Red
        exit $bootstrapExit
    }
}

# ---- 2. start ---------------------------------------------------------------------------------

# The world and its window live in the AvaWorld repository, and its start-all.ps1 already knows
# how to bring up all three pieces. Delegate when it is there; otherwise start just the API, so a
# clone of THIS repository alone is still one command to a running companion.

$avaWorld = if ($World) { $World } else { Join-Path (Split-Path $root -Parent) 'AvaWorld' }
$worldScript = Join-Path $avaWorld 'start-all.ps1'

if (Test-Path $worldScript) {
    Say "world      delegating to $worldScript" DarkGray
    $passthrough = @{ Companion = $root }
    if ($NoClient) { $passthrough['NoClient'] = $true }
    & $worldScript @passthrough
    exit $LASTEXITCODE
}

Say 'world      not found beside this repository - starting the companion alone' DarkGray

$api = Join-Path $root 'src\Companion.Api'
Push-Location $root
try {
    Start-Process -FilePath 'dotnet' -ArgumentList 'run', '--project', $api -WindowStyle Minimized
}
finally { Pop-Location }

Say 'companion  starting' Green
$deadline = (Get-Date).AddSeconds(180)
$up = $false
while (-not $up -and (Get-Date) -lt $deadline) {
    try {
        $null = Invoke-WebRequest -Uri 'http://localhost:5266/health' -TimeoutSec 2 -UseBasicParsing
        $up = $true
    }
    catch { Start-Sleep -Seconds 2 }
}

if ($up) { Say 'companion  up (api on 5266)' Green }
else     { Say '           still starting - it may just be building' Yellow }
