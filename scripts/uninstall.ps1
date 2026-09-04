[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallDir = (Split-Path -Parent $PSScriptRoot),
    [string]$BinDir = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) "bin"),
    [switch]$PurgeData
)

$ErrorActionPreference = "Stop"
$InstallDir = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallDir))
$BinDir = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($BinDir))
$DataDir = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "WorkLens"))
$Root = [IO.Path]::GetPathRoot($InstallDir)
if ($InstallDir -eq $Root -or $InstallDir.Length -lt ($Root.Length + 8)) {
    throw "Refusing to uninstall from an unsafe installation path: $InstallDir"
}

$Manager = Join-Path $InstallDir "scripts\manage.ps1"
if (Test-Path -LiteralPath $Manager -PathType Leaf) {
    & $Manager stop -InstallDir $InstallDir
    & $Manager unregister -InstallDir $InstallDir
}

Remove-Item -LiteralPath (Join-Path $BinDir "worklens.cmd") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $BinDir "worklens.ps1") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $BinDir "worklens-install-dir.txt") -Force -ErrorAction SilentlyContinue
if ($PSCmdlet.ShouldProcess($InstallDir, "Remove the WorkLens application")) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

$DataRemoved = $false
if ($PurgeData -and (Test-Path -LiteralPath $DataDir)) {
    $Confirmation = Read-Host "Type DELETE to permanently remove all WorkLens data from '$DataDir'"
    if ($Confirmation -ne "DELETE") {
        Write-Host "WorkLens was removed, but its data was preserved."
        exit 0
    }
    if ($PSCmdlet.ShouldProcess($DataDir, "Permanently remove all WorkLens data")) {
        Remove-Item -LiteralPath $DataDir -Recurse -Force
        $DataRemoved = $true
    }
}

if ($DataRemoved) {
    Write-Host "WorkLens was uninstalled and its user data was removed."
} else {
    Write-Host "WorkLens was uninstalled. Its user data was preserved."
}
