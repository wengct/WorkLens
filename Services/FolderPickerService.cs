using System.Text;

namespace WorkLens.Services;

public sealed record FolderPickerResult(string? Path, string? Error = null)
{
    public bool Selected => !string.IsNullOrWhiteSpace(Path);
}

public sealed class FolderPickerService(IProcessRunner processRunner)
{
    public async Task<FolderPickerResult> PickAsync(
        string? initialPath = null,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(initialPath);
        if (request is null)
        {
            return new FolderPickerResult(null, "此作業系統尚未支援原生資料夾選擇視窗。");
        }

        var result = await processRunner.RunAsync(
            request,
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken);
        var selectedPath = result.StandardOutput.Trim();
        if (result.Succeeded && !string.IsNullOrWhiteSpace(selectedPath))
        {
            return new FolderPickerResult(Path.GetFullPath(selectedPath));
        }

        if (result.Succeeded || IsUserCancellation(result))
        {
            return new FolderPickerResult(null);
        }

        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? "無法開啟資料夾選擇視窗。"
            : result.StandardError.Trim();
        return new FolderPickerResult(null, error);
    }

    private static ProcessRequest? CreateRequest(string? initialPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var initialEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(initialPath ?? string.Empty));
            var script = """
                Add-Type -AssemblyName System.Windows.Forms
                [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
                $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
                $dialog.Description = '選擇 WorkLens 備份資料夾'
                $dialog.ShowNewFolderButton = $true
                $initial = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('__INITIAL__'))
                if ($initial -and (Test-Path -LiteralPath $initial -PathType Container)) { $dialog.SelectedPath = $initial }
                $owner = New-Object System.Windows.Forms.Form
                $owner.Text = 'WorkLens'
                $owner.ShowInTaskbar = $false
                $owner.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
                $owner.Size = New-Object System.Drawing.Size(1, 1)
                $owner.Opacity = 0
                $owner.TopMost = $true
                try {
                    $owner.Show()
                    $owner.Activate()
                    if ($dialog.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dialog.SelectedPath }
                }
                finally {
                    $owner.Close()
                    $owner.Dispose()
                    $dialog.Dispose()
                }
                """.Replace("__INITIAL__", initialEncoded, StringComparison.Ordinal);
            var commandEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return new ProcessRequest("powershell.exe", ["-NoProfile", "-STA", "-EncodedCommand", commandEncoded]);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new ProcessRequest("osascript", ["-e", "POSIX path of (choose folder with prompt \"選擇 WorkLens 備份資料夾\")"]);
        }

        if (OperatingSystem.IsLinux())
        {
            var arguments = new List<string> { "--file-selection", "--directory", "--title=選擇 WorkLens 備份資料夾" };
            if (!string.IsNullOrWhiteSpace(initialPath)) arguments.Add($"--filename={Path.GetFullPath(initialPath)}{Path.DirectorySeparatorChar}");
            return new ProcessRequest("zenity", arguments);
        }

        return null;
    }

    private static bool IsUserCancellation(ProcessResult result) =>
        (OperatingSystem.IsMacOS() && result.StandardError.Contains("(-128)", StringComparison.Ordinal)) ||
        (OperatingSystem.IsLinux() && result.ExitCode == 1);
}
