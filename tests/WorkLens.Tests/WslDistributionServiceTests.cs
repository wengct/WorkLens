using System.Text;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class WslDistributionServiceTests
{
    [Fact]
    public async Task Discovery_preserves_names_and_removes_blank_lines_and_duplicates()
    {
        var runner = new StubRunner(new(0, "\uFEFFUbuntu\r\nDebian\r\n測試 Linux\r\nUbuntu\r\n\r\n", ""));
        var result = await new WslDistributionService(runner).DiscoverAsync();

        Assert.Null(result.Error);
        Assert.Equal(new[] { "Ubuntu", "Debian", "測試 Linux" }, result.Names);
        Assert.Equal("wsl.exe", runner.Request!.FileName);
        Assert.Equal(new[] { "--list", "--quiet" }, runner.Request.Arguments);
        Assert.Equal(Encoding.Unicode, runner.Request.OutputEncoding);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Timeout);
    }

    [Theory]
    [InlineData(0, "", false, "未找到")]
    [InlineData(1, "diagnostic output", false, "無法偵測")]
    [InlineData(-1, "", true, "逾時")]
    public async Task Empty_failed_and_timed_out_discovery_allow_manual_entry(int exitCode, string output, bool timedOut, string message)
    {
        var result = await new WslDistributionService(new StubRunner(new(exitCode, output, "", timedOut))).DiscoverAsync();

        Assert.Empty(result.Names);
        Assert.Contains(message, result.Error);
        Assert.Contains("手動輸入", result.Error);
    }

    private sealed class StubRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }
        public TimeSpan? Timeout { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, string? standardInput = null,
            TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Request = request;
            Timeout = timeout;
            return Task.FromResult(result);
        }
    }
}
