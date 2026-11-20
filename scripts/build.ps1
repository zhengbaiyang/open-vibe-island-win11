#Requires -Version 5.1
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$InstallHooksBinary
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $RepoRoot "dist\win-x64"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[ERROR] .NET 8 SDK not found. Install it with:" -ForegroundColor Red
    Write-Host "  winget install Microsoft.DotNet.SDK.8" -ForegroundColor Yellow
    exit 1
}

Write-Host "==> Building Open Island for Windows 11 ($Configuration)..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

$scFlag = if ($SelfContained) { "true" } else { "false" }

$projects = @(
    "src\OpenIsland.Hooks\OpenIsland.Hooks.csproj",
    "src\OpenIsland.Setup\OpenIsland.Setup.csproj",
    "src\OpenIsland.App\OpenIsland.App.csproj"
)

foreach ($proj in $projects) {
    $fullProj = Join-Path $RepoRoot $proj
    Write-Host "  -> Publishing $proj ..." -ForegroundColor Gray
    dotnet publish $fullProj `
        -c $Configuration `
        -r win-x64 `
        --self-contained $scFlag `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $DistDir
}

# Copy bundled plugins and scripts alongside dist (recreate to keep the layout flat on repeated builds)
$PluginsDest = Join-Path $DistDir "plugins"
if (Test-Path $PluginsDest) {
    Remove-Item -Path $PluginsDest -Recurse -Force
}
Copy-Item -Path (Join-Path $RepoRoot "plugins") -Destination $PluginsDest -Recurse -Force

if ($InstallHooksBinary) {
    $BinDir = Join-Path $env:LOCALAPPDATA "OpenIsland\bin"
    New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
    Copy-Item (Join-Path $DistDir "OpenIslandHooks.exe") (Join-Path $BinDir "OpenIslandHooks.exe") -Force
    Write-Host "==> Installed OpenIslandHooks.exe to $BinDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "Build complete! Output directory: $DistDir" -ForegroundColor Green
Write-Host "  1. Run:  .\dist\win-x64\OpenIsland.exe" -ForegroundColor White
Write-Host "  2. Setup hooks via Settings UI or CLI: .\dist\win-x64\OpenIslandSetup.exe status" -ForegroundColor White
