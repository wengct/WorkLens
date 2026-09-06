[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Programs\WorkLens"),
    [string]$BinDir = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) "bin"),
    [ValidateRange(1, 65535)]
    [int]$Port = 5077,
    [switch]$NoAutostart,
    [switch]$NoOpenBrowser
)

$ErrorActionPreference = "Stop"
$InstallDir = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallDir))
$BinDir = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($BinDir))
$InstallRoot = [IO.Path]::GetPathRoot($InstallDir)
$DataRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "WorkLens"))
if ($InstallDir -eq $InstallRoot -or $InstallDir.Length -lt ($InstallRoot.Length + 8) -or $InstallDir -eq $DataRoot) {
    throw "Refusing to install WorkLens into an unsafe or runtime-data path: $InstallDir"
}
$ReleaseDir = Split-Path -Parent $PSScriptRoot
$VersionFile = Join-Path $ReleaseDir "VERSION"
$Executable = Join-Path $ReleaseDir "WorkLens.exe"
$ReleaseManager = Join-Path $ReleaseDir "scripts\manage.ps1"
$LeakHunterExecutable = Join-Path $ReleaseDir "tools\leak-hunter\leak-hunter.exe"
$LeakHunterVersionFile = Join-Path $ReleaseDir "scripts\leak-hunter.version"
if (!(Test-Path -LiteralPath $VersionFile -PathType Leaf) -or !(Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Run install.ps1 from an extracted WorkLens Windows release package."
}
if (!(Test-Path -LiteralPath $ReleaseManager -PathType Leaf)) {
    throw "The WorkLens release package does not contain scripts\manage.ps1."
}
if (!(Test-Path -LiteralPath $LeakHunterExecutable -PathType Leaf) -or !(Test-Path -LiteralPath $LeakHunterVersionFile -PathType Leaf)) {
    throw "The WorkLens release package does not contain the verified leak-hunter executable and version marker."
}
$ExpectedLeakHunterVersion = (Get-Content -LiteralPath $LeakHunterVersionFile -Raw).Trim().TrimStart('v')
$ActualLeakHunterVersion = (& $LeakHunterExecutable --version 2>$null | Out-String).Trim()
$EscapedLeakHunterVersion = [Regex]::Escape($ExpectedLeakHunterVersion)
if ([string]::IsNullOrWhiteSpace($ActualLeakHunterVersion) -or $ActualLeakHunterVersion -notmatch "(?i)(?<![0-9A-Za-z.-])v?$EscapedLeakHunterVersion(?![0-9A-Za-z.-])") {
    throw "The bundled leak-hunter version does not match the release marker."
}

$Version = (Get-Content -LiteralPath $VersionFile -Raw).Trim().TrimStart('v')
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') { throw "The release VERSION is invalid." }
$VersionsDir = Join-Path $InstallDir "versions"
$VersionDir = Join-Path $VersionsDir $Version
$InstalledLeakHunter = Join-Path $VersionDir "tools\leak-hunter\leak-hunter.exe"
$CurrentFile = Join-Path $InstallDir "current.txt"
$PortFile = Join-Path $InstallDir "port.txt"
$InstalledScripts = Join-Path $InstallDir "scripts"
$ExistingManager = Join-Path $InstalledScripts "manage.ps1"
$PreviousVersion = if (Test-Path -LiteralPath $CurrentFile) { (Get-Content -LiteralPath $CurrentFile -Raw).Trim() } else { $null }
$PreviousPort = if (Test-Path -LiteralPath $PortFile) { (Get-Content -LiteralPath $PortFile -Raw).Trim() } else { $null }
$ExistingTask = Get-ScheduledTask -TaskName "WorkLens" -ErrorAction SilentlyContinue
$HadAutostart = $null -ne ($ExistingTask.Actions | Where-Object { $_.Arguments -like "*$InstallDir*" })

New-Item -ItemType Directory -Force -Path $VersionsDir, $BinDir | Out-Null
$StagingDir = Join-Path $VersionsDir (".staging-" + [Guid]::NewGuid().ToString("N"))

function Add-WorkLensBinToPath {
    $UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $UserEntries = @($UserPath -split ";" | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
    $BinDirKey = $BinDir.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $IsInUserPath = $UserEntries | Where-Object {
        $_.Trim().TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -ieq $BinDirKey
    }
    if (!$IsInUserPath) {
        $NewUserPath = (@($UserEntries) + $BinDir) -join ";"
        [Environment]::SetEnvironmentVariable("Path", $NewUserPath, "User")
    }

    $ProcessEntries = @($env:Path -split ";" | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
    $IsInProcessPath = $ProcessEntries | Where-Object {
        $_.Trim().TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -ieq $BinDirKey
    }
    if (!$IsInProcessPath) {
        $env:Path = (@($ProcessEntries) + $BinDir) -join ";"
    }
}

try {
    if ($PreviousVersion -ne $Version -or !(Test-Path -LiteralPath $VersionDir) -or !(Test-Path -LiteralPath $InstalledLeakHunter)) {
        New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
        Copy-Item -Path (Join-Path $ReleaseDir "*") -Destination $StagingDir -Recurse -Force
    }

    if (Test-Path -LiteralPath $ExistingManager) {
        # The installed manager may be the version whose stop behavior is being fixed.
        & $ReleaseManager stop -InstallDir $InstallDir
    }

    if (Test-Path -LiteralPath $StagingDir) {
        if (Test-Path -LiteralPath $VersionDir) { Remove-Item -LiteralPath $VersionDir -Recurse -Force }
        Move-Item -LiteralPath $StagingDir -Destination $VersionDir
    }

    New-Item -ItemType Directory -Force -Path $InstalledScripts | Out-Null
    Copy-Item -Path (Join-Path $VersionDir "scripts\*.ps1") -Destination $InstalledScripts -Force
    Set-Content -LiteralPath "$CurrentFile.new" -Value $Version -Encoding ASCII
    Move-Item -LiteralPath "$CurrentFile.new" -Destination $CurrentFile -Force
    Set-Content -LiteralPath $PortFile -Value $Port -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $InstallDir "bin-dir.txt") -Value $BinDir -Encoding UTF8

    $Shim = @"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0worklens.ps1" %*
exit /b %ERRORLEVEL%
"@
    $PowerShellShim = @'
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArguments
)

$ErrorActionPreference = "Stop"
$InstallDirFile = Join-Path $PSScriptRoot "worklens-install-dir.txt"
$InstallDir = (Get-Content -LiteralPath $InstallDirFile -Raw).Trim()
& (Join-Path $InstallDir "scripts\manage.ps1") @RemainingArguments -InstallDir $InstallDir
exit $LASTEXITCODE
'@
    Set-Content -LiteralPath (Join-Path $BinDir "worklens.cmd") -Value $Shim -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $BinDir "worklens.ps1") -Value $PowerShellShim -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $BinDir "worklens-install-dir.txt") -Value $InstallDir -Encoding UTF8
    Add-WorkLensBinToPath

    $Manager = Join-Path $InstalledScripts "manage.ps1"
    if ($NoAutostart) { & $Manager unregister -InstallDir $InstallDir }
    else { & $Manager register -InstallDir $InstallDir }
    & $Manager start -InstallDir $InstallDir
    $Health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/healthz" -TimeoutSec 5
    if ($Health.status -ne "Healthy" -or $Health.version -ne $Version) {
        throw "WorkLens started, but its health version '$($Health.version)' did not match the installed version '$Version'."
    }

    if (!$NoOpenBrowser) {
        Start-Process "http://127.0.0.1:$Port"
    }

    $Keep = @($Version, $PreviousVersion) | Where-Object { $_ } | Select-Object -Unique
    Get-ChildItem -LiteralPath $VersionsDir -Directory | Where-Object {
        !$_.Name.StartsWith(".staging-") -and $_.Name -notin $Keep
    } | Remove-Item -Recurse -Force

    Write-Host "WorkLens $Version is installed and running at http://127.0.0.1:$Port"
    Write-Host "The 'worklens' command is available now and in new terminal windows."
} catch {
    $Failure = $_
    try {
        $Manager = Join-Path $InstalledScripts "manage.ps1"
        if (Test-Path -LiteralPath $Manager) { & $Manager stop -InstallDir $InstallDir }
        if ($PreviousVersion -and (Test-Path -LiteralPath (Join-Path $VersionsDir $PreviousVersion))) {
            Set-Content -LiteralPath $CurrentFile -Value $PreviousVersion -Encoding ASCII
            if ($PreviousPort) { Set-Content -LiteralPath $PortFile -Value $PreviousPort -Encoding ASCII }
            Copy-Item -Path (Join-Path $VersionsDir "$PreviousVersion\scripts\*.ps1") -Destination $InstalledScripts -Force
            $Manager = Join-Path $InstalledScripts "manage.ps1"
            if ($HadAutostart) { & $Manager register -InstallDir $InstallDir }
            else { & $Manager unregister -InstallDir $InstallDir }
            & $Manager start -InstallDir $InstallDir
            Write-Warning "The new version failed to start. WorkLens was rolled back to $PreviousVersion."
        } elseif (Test-Path -LiteralPath $Manager) {
            & $Manager unregister -InstallDir $InstallDir
            Remove-Item -LiteralPath $CurrentFile -Force -ErrorAction SilentlyContinue
        }
        if ($Version -ne $PreviousVersion -and (Test-Path -LiteralPath $VersionDir)) {
            Remove-Item -LiteralPath $VersionDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Warning "WorkLens rollback also failed. Check the WorkLens logs and installation directory."
    }
    throw $Failure
} finally {
    if (Test-Path -LiteralPath $StagingDir) {
        Remove-Item -LiteralPath $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
