<#
.SYNOPSIS
    Check every model and adapter the current configuration requires. Downloads nothing.

.DESCRIPTION
    The standalone verification command. Exits 0 when every ACTIVE dependency is present and
    (where a hash is pinned) verified; non-zero otherwise, so it works in a health check or a
    pre-flight step as well as by hand.

    It never downloads, never starts the application, and never prints a token, key, or
    credential-bearing URL.

.PARAMETER AllConfigured
    Also check models that configuration names but no enabled capability uses. Their absence is
    reported but never fails the run — a disabled model is not a problem.

.PARAMETER Inventory
    Print the configured inventory instead of verifying.

.EXAMPLE
    .\verify-models.ps1
    .\verify-models.ps1 -AllConfigured
    .\verify-models.ps1 -Inventory
#>
[CmdletBinding()]
param(
    [switch]$AllConfigured,
    [switch]$Inventory
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'tools\Companion.Bootstrap\Companion.Bootstrap.csproj'

$toolArgs = @()
if ($Inventory) { $toolArgs += '--inventory' } else { $toolArgs += '--verify-only' }
if ($AllConfigured) { $toolArgs += '--all-configured' }

& dotnet build $project -v quiet --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { exit 1 }

& dotnet run --project $project --no-build -- @toolArgs
exit $LASTEXITCODE
