Option Explicit

' Hide the console at process creation, before Windows Terminal can host it.
' Waiting preserves the runner exit code and Task Scheduler restart behavior.
Dim shell, command, exitCode
If WScript.Arguments.Count <> 2 Then WScript.Quit 2
Set shell = CreateObject("WScript.Shell")
command = Quote(shell.ExpandEnvironmentStrings("%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")) _
    & " -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File " _
    & Quote(WScript.Arguments(0)) & " -InstallDir " & Quote(WScript.Arguments(1))
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode

Function Quote(value)
    Quote = Chr(34) & value & Chr(34)
End Function
