[CmdletBinding()]
param(
    [string]$ScriptsDir = $PSScriptRoot,
    [switch]$LegacyLauncher
)

$ErrorActionPreference = "Stop"
if (!$IsWindows -and $PSVersionTable.PSEdition -ne "Desktop") { throw "This test requires Windows." }

# Observe actual visible console windows, not just the presence of Hidden in source.
if (!("WorkLensLaunchWindowProbe" -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WorkLensLaunchWindowProbe {
    private delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    public static long[] VisibleConsoles() {
        var windows = new List<long>();
        EnumWindows((window, state) => {
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (IsWindowVisible(window) && (name.ToString() == "ConsoleWindowClass" || name.ToString() == "CASCADIA_HOSTING_WINDOW_CLASS"))
                windows.Add(window.ToInt64());
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
}
'@
}

$TempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$TestRoot = Join-Path $TempRoot ("worklens-launch-test-" + [guid]::NewGuid().ToString("N") + " 中文 space")
$TestTask = "WorkLens-LaunchTest-" + [guid]::NewGuid().ToString("N")
$TestScripts = Join-Path $TestRoot "scripts"
New-Item -ItemType Directory -Path $TestScripts | Out-Null
$Manager = Join-Path $TestScripts "manage.ps1"
$Runner = Join-Path $TestScripts "run.ps1"
$Marker = Join-Path $TestRoot "started.txt"
try {
    # Isolate the task name while executing the production registration functions.
    $Source = Get-Content -LiteralPath (Join-Path $ScriptsDir "manage.ps1") -Raw
    Set-Content -LiteralPath $Manager -Encoding UTF8 -Value $Source.Replace('$TaskName = "WorkLens"', ('$TaskName = "' + $TestTask + '"'))
    Copy-Item -LiteralPath (Join-Path $ScriptsDir "launch.vbs") -Destination $TestScripts
    Set-Content -LiteralPath $Runner -Encoding UTF8 -Value @'
param([string]$InstallDir)
Set-Content -LiteralPath (Join-Path $InstallDir 'started.txt') -Value $InstallDir -Encoding UTF8
Start-Sleep -Seconds 5
exit 23
'@
    # Register an old-style task first to exercise migration and ownership checks.
    $Action = New-ScheduledTaskAction -Execute (Get-Command powershell.exe).Source -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$Runner`" -InstallDir `"$TestRoot`""
    $Principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TestTask -Action $Action -Principal $Principal | Out-Null
    if (!$LegacyLauncher) { & $Manager register -InstallDir $TestRoot }
    $Baseline = @([WorkLensLaunchWindowProbe]::VisibleConsoles())
    $Visible = [Collections.Generic.HashSet[long]]::new()
    Start-ScheduledTask -TaskName $TestTask
    $Watch = [Diagnostics.Stopwatch]::StartNew()
    while ($Watch.Elapsed.TotalSeconds -lt 10) {
        foreach ($Window in [WorkLensLaunchWindowProbe]::VisibleConsoles()) {
            if ($Window -notin $Baseline) { [void]$Visible.Add($Window) }
        }
        Start-Sleep -Milliseconds 25
    }
    if (!(Test-Path -LiteralPath $Marker) -or (Get-Content -LiteralPath $Marker -Raw).Trim() -ne $TestRoot) {
        throw "The runner did not receive the installation path intact."
    }
    $Result = (Get-ScheduledTaskInfo -TaskName $TestTask).LastTaskResult
    if ($Result -ne 23) { throw "Runner exit code was lost: $Result" }
    if ($Visible.Count -gt 0) { throw "Background launch displayed $($Visible.Count) console window(s)." }
    & $Manager unregister -InstallDir $TestRoot
    if (Get-ScheduledTask -TaskName $TestTask -ErrorAction SilentlyContinue) { throw "Unregister did not remove the migrated task." }
    Write-Host "PASS: no visible console; Unicode path, exit code, legacy migration and unregister verified."
} finally {
    $Task = Get-ScheduledTask -TaskName $TestTask -ErrorAction SilentlyContinue
    if ($Task) {
        Stop-ScheduledTask -TaskName $TestTask -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TestTask -Confirm:$false
    }
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($TestRoot)) -ne $TempRoot) { throw "Unsafe cleanup path." }
    Remove-Item -LiteralPath $TestRoot -Recurse -Force
}
