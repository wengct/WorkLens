[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDir,
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64")]
    [string]$Rid
)

$ErrorActionPreference = "Stop"
$Version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot "leak-hunter.version") -Raw).Trim()
$Asset = "leak-hunter-x86_64-pc-windows-msvc.zip"
$BaseUrl = "https://github.com/doggy8088/leak-hunter/releases/download/$Version"
$workDir = Join-Path ([IO.Path]::GetTempPath()) ("worklens-leak-hunter-" + [Guid]::NewGuid().ToString("N"))
$archive = Join-Path $workDir $Asset
$checksumFile = "$archive.sha256"
$extractDir = Join-Path $workDir "extract"
$destination = Join-Path $PackageDir "tools\leak-hunter"

try {
    New-Item -ItemType Directory -Force -Path $workDir, $extractDir, $destination | Out-Null
    Invoke-WebRequest -Uri "$BaseUrl/$Asset" -OutFile $archive
    Invoke-WebRequest -Uri "$BaseUrl/$Asset.sha256" -OutFile $checksumFile
    $expected = ((Get-Content -LiteralPath $checksumFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "leak-hunter archive checksum mismatch." }
    Expand-Archive -LiteralPath $archive -DestinationPath $extractDir -Force
    $binary = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter "leak-hunter.exe" | Select-Object -First 1
    if ($null -eq $binary) { throw "The leak-hunter Windows executable was not found in the verified archive." }
    $installedBinary = Join-Path $destination "leak-hunter.exe"
    Copy-Item -LiteralPath $binary.FullName -Destination $installedBinary -Force
    $versionOutput = (& $installedBinary --version 2>$null | Out-String).Trim()
    $expectedVersion = [Regex]::Escape($Version.TrimStart('v'))
    if ([string]::IsNullOrWhiteSpace($versionOutput) -or $versionOutput -notmatch "(?i)(?<![0-9A-Za-z.-])v?$expectedVersion(?![0-9A-Za-z.-])") {
        throw "The verified leak-hunter executable reported an unexpected version: $versionOutput"
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\third-party\leak-hunter-LICENSE.txt") -Destination (Join-Path $destination "LICENSE.txt") -Force
}
finally {
    if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue }
}
