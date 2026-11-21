#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Open Island Windows 11 installer (and optional portable ZIP).

.DESCRIPTION
    Produces a self-contained per-user installer and release artifacts in dist\:
      dist\OpenIsland-Setup-<version>-win-x64.exe
      dist\OpenIsland-<version>-portable-win-x64.zip     (when -PortableZip is set)
      dist\OpenIsland-<version>-smartscreen-helper.zip
      dist\SHA256SUMS.txt

    The payload is a self-contained .NET 8 publish, so end users do NOT need to
    install any .NET runtime. Inno Setup 6.7+ (ISCC.exe) must be installed on the
    build machine; the script auto-detects the standard install locations.

    Note: When uploading a release to GitHub Releases, upload ALL release assets
    including SHA256SUMS.txt alongside the installer and portable ZIP — the in-app
    auto-updater strictly requires SHA256SUMS.txt for SHA-256 verification.

.PARAMETER Version
    Release/assembly version (3-part e.g. 0.1.4 or 4-part e.g. 0.1.4.0).
    Defaults to <Version> in Directory.Build.props.

.PARAMETER DisplayVersion
    Human-readable version shown in the installer and product metadata.
    Defaults to -Version when omitted.

.PARAMETER SkipPublish
    Reuse the existing artifacts\publish\win-x64 staging folder.

.PARAMETER PortableZip
    Also create a portable ZIP of the staged self-contained build.

.PARAMETER Sign
    Enable Authenticode signing. Requires -PfxPath/-PfxPassword with a real (e.g. paid
    EV/OV) code-signing certificate. Off by default: the development builds are unsigned
    and the SmartScreen helper (unblock script + instructions) is shipped instead.

.PARAMETER PfxPath
    Code-signing PFX (only used together with -Sign). Use a paid certificate for public
    releases; self-signed development certificates are not used by this project.

.PARAMETER PfxPassword
    Password for the PFX.

.EXAMPLE
    .\scripts\package.ps1 -PortableZip
.EXAMPLE
    .\scripts\package.ps1 -Version 0.1.5 -PortableZip
#>
param(
    [string]$Version = "",
    [string]$DisplayVersion = "",
    [switch]$SkipPublish,
    [switch]$PortableZip,
    [switch]$Sign,
    [string]$PfxPath = "",
    [string]$PfxPassword = "",
    [string]$TimestampUrl = ""
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$BuildPropsPath = Join-Path $Root "Directory.Build.props"
$Stage = Join-Path $Root "artifacts\publish\win-x64"
$Dist = Join-Path $Root "dist"
$InstallerDir = Join-Path $Root "installer"
$IconPath = Join-Path $InstallerDir "OpenIsland.ico"
$SignScript = Join-Path $InstallerDir "sign.ps1"

function Resolve-DefaultVersion([string]$PropsPath) {
    if (-not (Test-Path $PropsPath)) {
        throw "Cannot resolve default version: Directory.Build.props not found at '$PropsPath'. Pass -Version explicitly."
    }
    [xml]$propsXml = Get-Content -Path $PropsPath -Raw -Encoding UTF8
    $versionNodes = @(
        $propsXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Version']") |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.InnerText) }
    )
    if ($versionNodes.Count -eq 0) {
        throw "Cannot resolve default version: <Version> element not found or empty in '$PropsPath'. Pass -Version explicitly."
    }
    return $versionNodes[0].InnerText.Trim()
}

function Resolve-FourPartVersion([string]$Ver) {
    $trimmed = $Ver.Trim()
    if ($trimmed -match '^\d+\.\d+\.\d+\.\d+$') {
        return $trimmed
    }
    if ($trimmed -match '^\d+\.\d+\.\d+$') {
        return "$trimmed.0"
    }
    throw "Invalid -Version '$Ver'. Expected a 3-part (e.g. 0.1.4) or 4-part (e.g. 0.1.4.0) numeric version."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Resolve-DefaultVersion $BuildPropsPath
} else {
    $Version = $Version.Trim()
}

$FourPartVersion = Resolve-FourPartVersion $Version

if ([string]::IsNullOrWhiteSpace($DisplayVersion)) {
    $DisplayVersion = $Version
} else {
    $DisplayVersion = $DisplayVersion.Trim()
}

$InformationalVersion = if ($DisplayVersion.EndsWith("-win11")) { $DisplayVersion } else { "$DisplayVersion-win11" }

Write-Host "Packaging Open Island v$Version (DisplayVersion: $DisplayVersion, FileVersion: $FourPartVersion)" -ForegroundColor Cyan

# Signing is opt-in and requires a real PFX (paid EV/OV certificate recommended).
$SigningEnabled = $Sign -or (-not [string]::IsNullOrWhiteSpace($PfxPath))
if ($SigningEnabled -and [string]::IsNullOrWhiteSpace($PfxPath)) {
    throw "Signing requested but -PfxPath was not provided."
}

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-CodeSign([string]$File) {
    if (-not $SigningEnabled -or -not (Test-Path $SignScript)) { return }
    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $SignScript,
        "-Files", $File, "-PfxPath", $PfxPath
    )
    if ($PfxPassword) { $arguments += @("-PfxPassword", $PfxPassword) }
    if ($TimestampUrl) { $arguments += @("-TimestampUrl", $TimestampUrl) }
    & powershell @arguments
    if ($LASTEXITCODE -ne 0) { throw "code signing failed for $File" }
}

function Find-Tool([string[]]$Candidates) {
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    return $null
}

$isccCmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    "C:\Program Files\Inno Setup 7\ISCC.exe"
)
if ($isccCmd -and $isccCmd.Source) { $isccCandidates += $isccCmd.Source }
$iscc = Find-Tool $isccCandidates
if (-not $iscc) {
    throw @"
Inno Setup compiler (ISCC.exe) not found.
Install it with one of:
  winget install --id JRSoftware.InnoSetup -e --scope user
  https://jrsoftware.org/isdl.php
"@
}
Write-Host "Using Inno Setup compiler: $iscc" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 1. Publish self-contained payload (no .NET runtime required on target PCs)
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Step "Publishing self-contained payload to $Stage"
    if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
    New-Item -ItemType Directory -Force $Stage | Out-Null

    $projects = @(
        "src\OpenIsland.App\OpenIsland.App.csproj",
        "src\OpenIsland.Hooks\OpenIsland.Hooks.csproj",
        "src\OpenIsland.Setup\OpenIsland.Setup.csproj"
    )
    foreach ($project in $projects) {
        Write-Host "  -> $project"
        & dotnet publish (Join-Path $Root $project) `
            -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
            "-p:Version=$Version" `
            "-p:FileVersion=$FourPartVersion" `
            "-p:AssemblyVersion=$FourPartVersion" `
            "-p:InformationalVersion=$InformationalVersion" `
            -o $Stage -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
    }

    Write-Host "  -> plugins / docs"
    Copy-Item (Join-Path $Root "plugins") $Stage -Recurse -Force
    foreach ($doc in @("LICENSE", "README.md", "README.en.md")) {
        $source = Join-Path $Root $doc
        if (Test-Path $source) { Copy-Item $source $Stage -Force }
    }
    if (Test-Path (Join-Path $Root "docs\screenshots")) {
        Copy-Item (Join-Path $Root "docs\screenshots") (Join-Path $Stage "screenshots") -Recurse -Force
    }

    Get-ChildItem $Stage -Filter "*.log" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path (Join-Path $Stage "OpenIsland.exe"))) {
    throw "Staged payload not found at $Stage. Run without -SkipPublish first."
}

# ---------------------------------------------------------------------------
# 1b. Optional Authenticode signing (off by default; requires a real certificate)
# ---------------------------------------------------------------------------
if ($SigningEnabled) {
    Write-Step "Signing staged executables"
    foreach ($exe in @("OpenIsland.exe", "OpenIslandHooks.exe", "OpenIslandSetup.exe")) {
        Invoke-CodeSign (Join-Path $Stage $exe)
    }
}

# ---------------------------------------------------------------------------
# 2. Generate icon if missing
# ---------------------------------------------------------------------------
if (-not (Test-Path $IconPath)) {
    Write-Step "Generating application icon"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $InstallerDir "make-icon.ps1")
    if ($LASTEXITCODE -ne 0) { throw "icon generation failed" }
}

# ---------------------------------------------------------------------------
# 3. Compile the Inno Setup installer
# ---------------------------------------------------------------------------
Write-Step "Compiling installer (Inno Setup)"
New-Item -ItemType Directory -Force $Dist | Out-Null
& $iscc `
    "/DAppVersion=$Version" `
    "/DAppDisplayVersion=$DisplayVersion" `
    "/DSourceDir=$Stage" `
    "/DOutputDir=$Dist" `
    (Join-Path $InstallerDir "OpenIsland.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit $LASTEXITCODE)" }

$setupExe = Join-Path $Dist ("OpenIsland-Setup-{0}-win-x64.exe" -f $Version)
if (-not (Test-Path $setupExe)) { throw "Expected installer not found: $setupExe" }

# Sign the installer itself only when a real certificate was supplied.
if ($SigningEnabled) {
    Write-Step "Signing installer"
    Invoke-CodeSign $setupExe
}

$setupSizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host "  Installer: $setupExe ($setupSizeMb MB)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 4. Optional portable ZIP
# ---------------------------------------------------------------------------
if ($PortableZip) {
    Write-Step "Creating portable ZIP"
    $sevenZip = Find-Tool @("C:\Program Files\7-Zip\7z.exe", "C:\Program Files (x86)\7-Zip\7z.exe")
    $zipPath = Join-Path $Dist ("OpenIsland-{0}-portable-win-x64.zip" -f $Version)
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if ($sevenZip) {
        & $sevenZip a -tzip -mx=9 -mmt=on $zipPath (Join-Path $Stage "*") | Out-Null
    } else {
        Compress-Archive -Path (Join-Path $Stage "*") -DestinationPath $zipPath -CompressionLevel Optimal
    }
    if ($LASTEXITCODE -ne 0 -and $sevenZip) { throw "7-Zip failed to create portable zip" }
    Write-Host "  Portable: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 5. SmartScreen helper package (unblock/install script + instructions)
# ---------------------------------------------------------------------------
Write-Step "Creating SmartScreen helper package"
$helperCmd = Join-Path $InstallerDir "Unblock-and-Install.cmd"
$helperTxt = Join-Path $InstallerDir "SMARTSCREEN-说明.txt"
if (Test-Path $helperCmd) { Copy-Item $helperCmd $Dist -Force }
if (Test-Path $helperTxt) { Copy-Item $helperTxt (Join-Path $Dist "README-SmartScreen.txt") -Force }

# Never ship a certificate: builds are unsigned unless the user provides a real one.
$staleCert = Join-Path $Dist "OpenIsland-CodeSigning.cer"
if (Test-Path $staleCert) { Remove-Item $staleCert -Force }

$sevenZip = Find-Tool @("C:\Program Files\7-Zip\7z.exe", "C:\Program Files (x86)\7-Zip\7z.exe")
$helperZip = Join-Path $Dist ("OpenIsland-{0}-smartscreen-helper.zip" -f $Version)
if (Test-Path $helperZip) { Remove-Item $helperZip -Force }
$helperNames = @(
    (Split-Path -Leaf $setupExe),
    "Unblock-and-Install.cmd",
    "README-SmartScreen.txt"
) | Where-Object { Test-Path (Join-Path $Dist $_) }

if ($helperNames.Count -gt 0) {
    Push-Location $Dist
    try {
        if ($sevenZip) {
            & $sevenZip a -tzip -mx=9 -mmt=on $helperZip @helperNames | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7-Zip failed to create SmartScreen helper zip" }
        } else {
            Compress-Archive -Path $helperNames -DestinationPath $helperZip -CompressionLevel Optimal -Force
        }
    } finally {
        Pop-Location
    }
    Write-Host "  Helper   : $helperZip" -ForegroundColor Green
    Write-Host "  Unblock-and-Install.cmd + README-SmartScreen.txt + installer" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 6. Checksums for release artifacts
# ---------------------------------------------------------------------------
Write-Step "Writing SHA256 checksums"
$checksumFile = Join-Path $Dist "SHA256SUMS.txt"
$lines = @()
$versionPattern = [regex]::Escape($Version)
Get-ChildItem $Dist -File | Where-Object {
    $_.Name -match "^(OpenIsland-Setup-$versionPattern-win-x64\.exe|OpenIsland-$versionPattern-portable-win-x64\.zip|OpenIsland-$versionPattern-smartscreen-helper\.zip)$"
} | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += "$hash  $($_.Name)"
}
Set-Content -Path $checksumFile -Value $lines -Encoding ASCII
$lines | ForEach-Object { Write-Host "  $_" }

Write-Host ""
Write-Host "Done. Artifacts in: $Dist" -ForegroundColor Green
Write-Host "GitHub Release upload: upload ALL artifacts including SHA256SUMS.txt alongside the installer and portable ZIP" -ForegroundColor Cyan
Write-Host "(SHA256SUMS.txt is strictly required for in-app auto-updates to verify downloaded packages)." -ForegroundColor Cyan
if (-not $SigningEnabled) {
    Write-Host "Build is UNSIGNED (SmartScreen will warn on first download). Ship the *-smartscreen-helper.zip" -ForegroundColor Yellow
    Write-Host "and/or tell users to right-click the installer -> Properties -> Unblock before running." -ForegroundColor Yellow
    Write-Host "To sign public releases later, pass a paid EV/OV certificate: .\scripts\package.ps1 -PortableZip -Sign -PfxPath cert.pfx" -ForegroundColor Yellow
}
