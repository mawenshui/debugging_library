param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$wixProj = Join-Path $repoRoot "installer\wix\DebugSummaryPlatform.wixproj"

Write-Host "Building MSI: $wixProj ($Configuration)"
dotnet build $wixProj -c $Configuration
