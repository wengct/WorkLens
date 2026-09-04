[CmdletBinding()]
param(
    [string]$InstallDir = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$InstallDir = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallDir))
$CurrentFile = Join-Path $InstallDir "current.txt"
$PortFile = Join-Path $InstallDir "port.txt"
$PidFile = Join-Path $InstallDir "app.pid"

if (!(Test-Path -LiteralPath $CurrentFile -PathType Leaf)) {
    throw "WorkLens is not installed correctly: current.txt is missing."
}

$Version = (Get-Content -LiteralPath $CurrentFile -Raw).Trim()
$Port = [int](Get-Content -LiteralPath $PortFile -Raw).Trim()
$Executable = Join-Path $InstallDir "versions\$Version\WorkLens.exe"
if (!(Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "WorkLens executable is missing for version $Version."
}

$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
$env:DOTNET_ENVIRONMENT = "Production"
$Process = $null
try {
    $Process = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath $PidFile -Value $Process.Id -Encoding ASCII
    $Process.WaitForExit()
    exit $Process.ExitCode
} finally {
    if ($Process) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $PidFile) {
        Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
    }
}
