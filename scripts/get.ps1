<#
.SYNOPSIS
Downloads, verifies, installs, and starts the latest WorkLens release.
#>
[CmdletBinding()]
param(
    [string]$Version = "latest",
    [string]$InstallDir,
    [string]$BinDir,
    [ValidateRange(1, 65535)]
    [int]$Port = 5077,
    [switch]$NoAutostart,
    [switch]$NoOpenBrowser
)

$ErrorActionPreference = "Stop"
$Repo = "wengct/WorkLens"
$Target = "win-x64"

if ($Version -eq "latest") {
    Write-Host "Resolving the latest WorkLens release..."
    $Release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing
    $Tag = $Release.tag_name
    if ([string]::IsNullOrWhiteSpace($Tag)) {
        throw "The latest WorkLens release tag could not be resolved."
    }
} else {
    $Tag = $Version
}

$Archive = "worklens-$Tag-$Target.zip"
$ReleaseBase = "https://github.com/$Repo/releases/download/$Tag"
$WorkDir = Join-Path ([IO.Path]::GetTempPath()) ("worklens-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

try {
    $ArchivePath = Join-Path $WorkDir $Archive
    $ChecksumsPath = Join-Path $WorkDir "SHA256SUMS"
    Write-Host "Downloading WorkLens $Tag..."
    Invoke-WebRequest -Uri "$ReleaseBase/$Archive" -OutFile $ArchivePath -UseBasicParsing
    Invoke-WebRequest -Uri "$ReleaseBase/SHA256SUMS" -OutFile $ChecksumsPath -UseBasicParsing

    $ChecksumLine = Get-Content $ChecksumsPath | Where-Object {
        $_ -match "^[0-9a-fA-F]{64}\s+\*?$([Regex]::Escape($Archive))$"
    } | Select-Object -First 1
    if (-not $ChecksumLine) {
        throw "SHA256SUMS does not contain an entry for $Archive."
    }

    $ExpectedHash = ($ChecksumLine -split '\s+')[0].ToUpperInvariant()
    $ActualHash = (Get-FileHash -Algorithm SHA256 -Path $ArchivePath).Hash.ToUpperInvariant()
    if ($ActualHash -ne $ExpectedHash) {
        throw "The WorkLens archive checksum did not match the published SHA-256 value."
    }

    $ExtractDir = Join-Path $WorkDir "package"
    Expand-Archive -Path $ArchivePath -DestinationPath $ExtractDir -Force
    $InstallScript = Join-Path $ExtractDir "scripts\install.ps1"
    if (!(Test-Path -LiteralPath $InstallScript -PathType Leaf)) {
        throw "The release package does not contain scripts\install.ps1."
    }

    $InstallArgs = @{ Port = $Port }
    if ($InstallDir) { $InstallArgs.InstallDir = $InstallDir }
    if ($BinDir) { $InstallArgs.BinDir = $BinDir }
    if ($NoAutostart) { $InstallArgs.NoAutostart = $true }
    if ($NoOpenBrowser) { $InstallArgs.NoOpenBrowser = $true }
    & $InstallScript @InstallArgs
} finally {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}
