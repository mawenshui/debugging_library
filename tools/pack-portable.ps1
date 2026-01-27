param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$zipPath = Join-Path $root "artifacts\FieldKb_portable_$Runtime.zip"

dotnet publish (Join-Path $root "src\FieldKb.Client.Wpf\FieldKb.Client.Wpf.csproj") `
  -c $Configuration -r $Runtime --self-contained false -o $publishDir

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Output $zipPath

