using System.Text;

namespace WorkLens.Services;

public sealed record WslDistributionResult(IReadOnlyList<string> Names, string? Error = null);

public sealed class WslDistributionService(IProcessRunner processRunner)
{
    public async Task<WslDistributionResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = await processRunner.RunAsync(
            new ProcessRequest("wsl.exe", ["--list", "--quiet"], OutputEncoding: Encoding.Unicode),
            timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
        if (!result.Succeeded)
        {
            return new([], result.TimedOut
                ? "偵測 WSL 逾時，請重新偵測或手動輸入環境名稱。"
                : "無法偵測 WSL，請確認已安裝 WSL，或手動輸入環境名稱。");
        }

        var names = result.StandardOutput.TrimStart('\uFEFF')
            .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(names, names.Length == 0 ? "未找到已安裝的 WSL 環境，可重新偵測或手動輸入。" : null);
    }
}
