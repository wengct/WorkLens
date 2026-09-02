using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AiContextLoggingTests
{
    [Fact]
    public void Context_size_uses_utf8_bytes_before_converting_to_kilobytes()
    {
        var context = new string('a', 1024) + "工作";

        var bytes = ReportService.GetUtf8ContextByteCount(context);
        var kilobytes = bytes / 1024d;

        Assert.Equal(1030, bytes);
        Assert.Equal(1.005859375d, kilobytes, precision: 9);
    }

    [Fact]
    public async Task Ai_generation_logs_context_size_without_logging_the_context_body()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Services", "ReportService.cs"));
        var source = await File.ReadAllTextAsync(path);

        Assert.Contains("logger.LogInformation", source, StringComparison.Ordinal);
        Assert.Contains("AI 報告上下文大小", source, StringComparison.Ordinal);
        Assert.Contains("{ContextSizeKb:F2} KB", source, StringComparison.Ordinal);
        Assert.Contains("GetUtf8ContextByteCount(input)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("{InputMarkdown}", source, StringComparison.Ordinal);
    }
}
