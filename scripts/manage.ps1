[CmdletBinding()]
param(
    [ValidateSet("open", "start", "stop", "restart", "status", "register", "unregister", "uninstall")]
    [string]$Command = "open",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArguments,
    [string]$InstallDir = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$TaskName = "WorkLens"
$InstallDir = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallDir))
$RunScript = Join-Path $InstallDir "scripts\run.ps1"
$PortFile = Join-Path $InstallDir "port.txt"
$PidFile = Join-Path $InstallDir "app.pid"
$VersionsDir = Join-Path $InstallDir "versions"

function Get-WorkLensPort {
    if (!(Test-Path -LiteralPath $PortFile -PathType Leaf)) { throw "WorkLens port configuration is missing." }
    return [int](Get-Content -LiteralPath $PortFile -Raw).Trim()
}

function Test-WorkLensHealth {
    try {
        $Port = Get-WorkLensPort
        $Response = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/healthz" -TimeoutSec 2
        return $Response.status -eq "Healthy"
    } catch {
        return $false
    }
}

function Wait-WorkLensHealth([int]$Seconds = 30) {
    $Deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (Test-WorkLensHealth) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $Deadline)
    throw "WorkLens did not become healthy within $Seconds seconds. Check the WorkLens log directory for details."
}

function Get-WorkLensTask {
    $Task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($Task -and ($Task.Actions | Where-Object { $_.Arguments -like "*$RunScript*" })) {
        return $Task
    }
    return $null
}

function Test-IsInstalledWorkLensProcess($Process) {
    try {
        $ExecutablePath = [IO.Path]::GetFullPath($Process.Path)
        $VersionsRoot = [IO.Path]::GetFullPath($VersionsDir).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        return $ExecutablePath.StartsWith($VersionsRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($ExecutablePath).Equals("WorkLens.exe", [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Get-InstalledWorkLensProcesses {
    return @(Get-Process -Name "WorkLens" -ErrorAction SilentlyContinue | Where-Object {
        Test-IsInstalledWorkLensProcess $_
    })
}

function Register-WorkLensTask {
    $ConflictingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($ConflictingTask -and !(Get-WorkLensTask)) {
        throw "A scheduled task named WorkLens already exists and belongs to another installation."
    }
    $Identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $PowerShellExe = (Get-Command powershell.exe -ErrorAction Stop).Source
    $Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$RunScript`" -InstallDir `"$InstallDir`""
    $Action = New-ScheduledTaskAction -Execute $PowerShellExe -Argument $Arguments -WorkingDirectory $InstallDir
    $Trigger = New-ScheduledTaskTrigger -AtLogOn -User $Identity
    $Principal = New-ScheduledTaskPrincipal -UserId $Identity -LogonType Interactive -RunLevel Limited
    $Settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -StartWhenAvailable `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $TaskName -Action $Action -Trigger $Trigger -Principal $Principal -Settings $Settings -Force | Out-Null
}

function Unregister-WorkLensTask {
    if (Get-WorkLensTask) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
}

function Start-WorkLens {
    if (Test-WorkLensHealth) { return }
    $Task = Get-WorkLensTask
    if ($Task) {
        Enable-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    } else {
        $PowerShellExe = (Get-Command powershell.exe -ErrorAction Stop).Source
        $Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$RunScript`" -InstallDir `"$InstallDir`""
        Start-Process -FilePath $PowerShellExe -ArgumentList $Arguments -WindowStyle Hidden | Out-Null
    }
    Wait-WorkLensHealth
}

function Stop-WorkLens {
    $Task = Get-WorkLensTask
    if ($Task -and $Task.State -eq "Running") {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    }

    $Processes = @(Get-InstalledWorkLensProcesses)
    if (Test-Path -LiteralPath $PidFile -PathType Leaf) {
        $AppPid = 0
        if ([int]::TryParse((Get-Content -LiteralPath $PidFile -Raw).Trim(), [ref]$AppPid)) {
            $TrackedProcess = Get-Process -Id $AppPid -ErrorAction SilentlyContinue
            if ($TrackedProcess -and (Test-IsInstalledWorkLensProcess $TrackedProcess)) {
                $Processes = @($Processes) + $TrackedProcess
            }
        }
    }

    $Processes | Sort-Object Id -Unique | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue

    $Deadline = [DateTime]::UtcNow.AddSeconds(10)
    $RemainingProcesses = @(Get-InstalledWorkLensProcesses)
    while ($RemainingProcesses.Count -gt 0 -and [DateTime]::UtcNow -lt $Deadline) {
        $RemainingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 250
        $RemainingProcesses = @(Get-InstalledWorkLensProcesses)
    }
    if ($RemainingProcesses.Count -gt 0) {
        $RemainingIds = ($RemainingProcesses.Id | Sort-Object) -join ", "
        throw "WorkLens processes did not stop within 10 seconds (PID: $RemainingIds)."
    }
}

switch ($Command) {
    "register" { Register-WorkLensTask }
    "unregister" { Unregister-WorkLensTask }
    "start" { Start-WorkLens; Write-Host "WorkLens is running at http://127.0.0.1:$(Get-WorkLensPort)" }
    "stop" { Stop-WorkLens; Write-Host "WorkLens is stopped." }
    "restart" { Stop-WorkLens; Start-WorkLens; Write-Host "WorkLens restarted." }
    "status" {
        if (Test-WorkLensHealth) {
            Write-Host "WorkLens is healthy at http://127.0.0.1:$(Get-WorkLensPort)"
        } else {
            Write-Host "WorkLens is not running or is unhealthy."
            exit 1
        }
    }
    "open" {
        Start-WorkLens
        Start-Process "http://127.0.0.1:$(Get-WorkLensPort)"
    }
    "uninstall" {
        $PurgeData = $RemainingArguments -contains "--purge-data"
        $BinDirFile = Join-Path $InstallDir "bin-dir.txt"
        $UninstallArgs = @{ InstallDir = $InstallDir; PurgeData = $PurgeData }
        if (Test-Path -LiteralPath $BinDirFile) {
            $UninstallArgs.BinDir = (Get-Content -LiteralPath $BinDirFile -Raw).Trim()
        }
        & (Join-Path $InstallDir "scripts\uninstall.ps1") @UninstallArgs
    }
}
