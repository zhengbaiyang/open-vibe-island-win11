#Requires -Version 5.1
<#
.SYNOPSIS
    Optional Authenticode signing helper for Open Island release builds.

.DESCRIPTION
    Signs the given executables with a *real* code-signing certificate (paid EV/OV
    certificate recommended for public distribution), using SHA256 and an RFC3161
    timestamp.

    Open Island development builds are intentionally NOT signed with a self-signed
    certificate: Windows SmartScreen would still warn and users would have to trust a
    private certificate. Instead, downloads are shipped with the SmartScreen helper
    (Unblock-and-Install.cmd + README) which removes the Mark-of-the-Web.

    Call this script (or scripts\package.ps1 -Sign -PfxPath ...) only when you have a
    certificate issued by a trusted certificate authority.

.PARAMETER Files
    Executables to sign (installer, OpenIsland.exe, OpenIslandHooks.exe, OpenIslandSetup.exe...).

.PARAMETER PfxPath
    Path to the PFX containing the code-signing certificate (required).

.PARAMETER PfxPassword
    Password of the PFX.

.PARAMETER TimestampUrl
    RFC3161 time-stamp server. Pass an empty string to disable timestamping.

.EXAMPLE
    .\installer\sign.ps1 -Files .\dist\OpenIsland-Setup-0.1.0-win-x64.exe -PfxPath cert.pfx -PfxPassword pass
#>
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Files,
    [Parameter(Mandatory = $true)]
    [string]$PfxPath,
    [string]$PfxPassword = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PfxPath)) {
    throw "Code-signing PFX not found: $PfxPath. Provide a certificate issued by a trusted CA (paid EV/OV recommended for public releases)."
}
Write-Host "  PFX       : $PfxPath" -ForegroundColor DarkGray

function Find-SignTool {
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path $_) }
    $candidates = foreach ($root in $roots) {
        Get-ChildItem $root -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\" }
    }
    $best = $candidates | Sort-Object FullName -Descending | Select-Object -First 1
    if ($best) { return $best.FullName }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

$signtool = Find-SignTool
if (-not $signtool) {
    throw "signtool.exe not found. Install the Windows 10/11 SDK (Signing Tools)."
}
Write-Host "  sign tool : $signtool" -ForegroundColor DarkGray

$failed = @()
foreach ($file in $Files) {
    if (-not (Test-Path $file)) {
        Write-Warning "skip (missing): $file"
        continue
    }

    $arguments = @("sign", "/fd", "SHA256", "/f", $PfxPath)
    if ($PfxPassword) { $arguments += @("/p", $PfxPassword) }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @("/tr", $TimestampUrl, "/td", "SHA256")
    }
    $arguments += $file

    $out = & $signtool @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        # Time stamp servers may be unreachable; retry without a timestamp.
        Write-Warning "timestamp sign failed for $(Split-Path -Leaf $file), retrying without timestamp..."
        $arguments = @("sign", "/fd", "SHA256", "/f", $PfxPath)
        if ($PfxPassword) { $arguments += @("/p", $PfxPassword) }
        $arguments += $file
        $out = & $signtool @arguments 2>&1
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host "  signed: $(Split-Path -Leaf $file)" -ForegroundColor Green
    } else {
        $failed += $file
        Write-Warning "sign failed: $file`n$($out -join "`n")"
    }
}

if ($failed.Count -gt 0) {
    throw "Failed to sign: $($failed -join ', ')"
}
