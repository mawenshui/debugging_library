param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Project = "src/FieldKb.Client.Wpf/FieldKb.Client.Wpf.csproj"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime"

Write-Host "Publishing: $Project ($Configuration, $Runtime) -> $publishDir"
dotnet publish (Join-Path $repoRoot $Project) -c $Configuration -r $Runtime --self-contained false -o $publishDir

$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "未找到 Inno Setup 编译器 ISCC.exe。请安装 Inno Setup 6，然后重试。"
}

$iss = Join-Path $repoRoot "tools\installer.iss"
Write-Host "Building installer: $iss"
& $iscc $iss | Write-Host
