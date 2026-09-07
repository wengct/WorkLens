[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDir
)

$ErrorActionPreference = "Stop"
$ReleaseDir = [IO.Path]::GetFullPath($ReleaseDir)
$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("worklens-install-test-" + [Guid]::NewGuid().ToString("N"))
$InstallDir = Join-Path $TestRoot "Install Path 中文"
$BinDir = Join-Path $TestRoot "bin path"
$RuntimeDir = Join-Path $TestRoot "runtime data"
$Port = Get-Random -Minimum 18000 -Maximum 28000
$InstallScript = Join-Path $ReleaseDir "scripts\install.ps1"
$PreviousUserPath = [Environment]::GetEnvironmentVariable("Path", "User")
$PreviousProcessPath = $env:Path
$PreviousEnvironment = @{
    Connection = $env:ConnectionStrings__WorkLens
    Backup = $env:WorkLens__BackupPath
    Log = $env:WorkLens__LogPath
}

& (Join-Path $PSScriptRoot "test-windows-launch.ps1") -ScriptsDir (Join-Path $ReleaseDir "scripts")

try {
    New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null
    $env:ConnectionStrings__WorkLens = "Data Source=$(Join-Path $RuntimeDir 'worklens.db')"
    $env:WorkLens__BackupPath = Join-Path $RuntimeDir "backups"
    $env:WorkLens__LogPath = Join-Path $RuntimeDir "logs"

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $InstallScript `
        -InstallDir $InstallDir -BinDir $BinDir -Port $Port -NoAutostart -NoOpenBrowser
    if ($LASTEXITCODE -ne 0) { throw "The installer subprocess failed." }
    $InstalledManager = Get-Content -LiteralPath (Join-Path $InstallDir "scripts\manage.ps1") -Raw
    if (!$InstalledManager.Contains("-AllowStartIfOnBatteries") -or !$InstalledManager.Contains("-DontStopIfGoingOnBatteries")) {
        throw "The installed scheduled task configuration does not remain running on battery power."
    }
    $InstalledRunScript = Get-Content -LiteralPath (Join-Path $InstallDir "scripts\run.ps1") -Raw
    if ($InstalledRunScript.Contains("-NoNewWindow") -or !$InstalledRunScript.Contains("-WindowStyle Hidden")) {
        throw "The installed application process is not isolated from console close events."
    }
    $PathEntries = @([Environment]::GetEnvironmentVariable("Path", "User") -split ";")
    if (@($PathEntries | Where-Object { $_ -ieq $BinDir }).Count -ne 1) { throw "The installer did not add the command directory to the user PATH exactly once." }
    $Health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/healthz" -TimeoutSec 5
    if ($Health.status -ne "Healthy") { throw "Installed WorkLens did not report healthy." }
    & (Join-Path $BinDir "worklens.cmd") status
    if ($LASTEXITCODE -ne 0) { throw "The worklens command could not run from a path containing non-ASCII characters." }
    $Asset = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/app.css" -UseBasicParsing -TimeoutSec 5
    if ($Asset.StatusCode -ne 200 -or $Asset.RawContentLength -eq 0) { throw "Installed WorkLens did not serve app.css." }
    if (!(Test-Path -LiteralPath (Join-Path $RuntimeDir "worklens.db"))) { throw "WorkLens did not create its SQLite database." }

    # Stopping must still find the installed process if its PID file was lost.
    $InstalledManagerPath = Join-Path $InstallDir "scripts\manage.ps1"
    Remove-Item -LiteralPath (Join-Path $InstallDir "app.pid") -Force
    & $InstalledManagerPath stop -InstallDir $InstallDir
    $RemainingListener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($RemainingListener) { throw "Stopping left an installed WorkLens process listening after its PID file was lost." }
    & $InstalledManagerPath start -InstallDir $InstallDir

    # Updating must use the release manager when the installed manager cannot stop an orphaned process.
    $LegacyManager = @'
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command,
    [string]$InstallDir
)
throw "The legacy WorkLens manager cannot stop the installed process."
'@
    Set-Content -LiteralPath $InstalledManagerPath -Value $LegacyManager -Encoding UTF8
    Remove-Item -LiteralPath (Join-Path $InstallDir "app.pid") -Force

    # Reinstalling the same version must recover safely and must preserve the database.
    & $InstallScript -InstallDir $InstallDir -BinDir $BinDir -Port $Port -NoAutostart -NoOpenBrowser
    $PathEntries = @([Environment]::GetEnvironmentVariable("Path", "User") -split ";")
    if (@($PathEntries | Where-Object { $_ -ieq $BinDir }).Count -ne 1) { throw "Reinstalling duplicated the command directory in the user PATH." }
    if (!(Test-Path -LiteralPath (Join-Path $RuntimeDir "worklens.db"))) { throw "Reinstalling removed the SQLite database." }

    # A package whose declared version does not match the running assembly must roll back.
    $BadRelease = Join-Path $TestRoot "bad release"
    Copy-Item -LiteralPath $ReleaseDir -Destination $BadRelease -Recurse
    Set-Content -LiteralPath (Join-Path $BadRelease "VERSION") -Value "0.0.0-broken" -Encoding ASCII
    $BadInstallFailed = $false
    try {
        & (Join-Path $BadRelease "scripts\install.ps1") -InstallDir $InstallDir -BinDir $BinDir -Port $Port -NoAutostart -NoOpenBrowser
    } catch {
        $BadInstallFailed = $true
    }
    if (!$BadInstallFailed) { throw "A mismatched package version unexpectedly installed successfully." }
    $ExpectedVersion = (Get-Content -LiteralPath (Join-Path $ReleaseDir "VERSION") -Raw).Trim().TrimStart('v')
    $CurrentVersion = (Get-Content -LiteralPath (Join-Path $InstallDir "current.txt") -Raw).Trim()
    if ($CurrentVersion -ne $ExpectedVersion) { throw "The failed update did not restore the previous version." }
    $HealthAfterRollback = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/healthz" -TimeoutSec 5
    if ($HealthAfterRollback.version -ne $ExpectedVersion) { throw "WorkLens was not healthy on the previous version after rollback." }

    & (Join-Path $InstallDir "scripts\manage.ps1") uninstall -InstallDir $InstallDir
    if (!(Test-Path -LiteralPath (Join-Path $RuntimeDir "worklens.db"))) { throw "Default uninstall removed the SQLite database." }
    Write-Host "Windows installation smoke test passed."
} finally {
    if (Test-Path -LiteralPath (Join-Path $InstallDir "scripts\manage.ps1")) {
        & (Join-Path $InstallDir "scripts\manage.ps1") stop -InstallDir $InstallDir -ErrorAction SilentlyContinue
    }
    $env:ConnectionStrings__WorkLens = $PreviousEnvironment.Connection
    $env:WorkLens__BackupPath = $PreviousEnvironment.Backup
    $env:WorkLens__LogPath = $PreviousEnvironment.Log
    [Environment]::SetEnvironmentVariable("Path", $PreviousUserPath, "User")
    $env:Path = $PreviousProcessPath
    Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
}
