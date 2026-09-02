using Microsoft.Extensions.Logging;
using WorkLens.Logging;

namespace WorkLens.Tests;

public sealed class LocalFileLoggerTests
{
    [Fact]
    public void Writes_log_entries_to_a_daily_file()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "WorkLens.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            using var provider = new LocalFileLoggerProvider(root);
            var logger = provider.CreateLogger("WorkLens.Tests");

            logger.LogError(
                new InvalidOperationException("diagnostic failure"),
                "A local log entry was written.");

            var logFile = Assert.Single(Directory.GetFiles(root, "worklens-*.log"));
            var content = File.ReadAllText(logFile);

            Assert.Contains("[Error]", content, StringComparison.Ordinal);
            Assert.Contains("A local log entry was written.", content, StringComparison.Ordinal);
            Assert.Contains("diagnostic failure", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
