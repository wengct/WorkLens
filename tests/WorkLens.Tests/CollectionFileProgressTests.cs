using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class CollectionFileProgressTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Reports_total_before_reading_and_counts_skipped_files(int count)
    {
        var updates = new List<SourceCollectionProgress>();
        var source = new ActivitySource();
        var request = new CollectionRequest(source, DateTimeOffset.UtcNow, Progress: updates.Add);
        var files = Enumerable.Range(0, count).ToArray();
        foreach (var file in CollectionFileProgress.Track(files, request))
        {
            Assert.Equal(0, updates[0].ProcessedFiles);
            Assert.Equal(count, updates[0].TotalFiles);
            if (file % 2 == 0) continue;
        }
        Assert.Equal(count, updates[^1].ProcessedFiles);
        Assert.Equal("整理資料", updates[^1].Stage);
        Assert.All(updates, item => Assert.Equal(source.Id, item.SourceId));
    }

    [Fact]
    public void Cancellation_does_not_report_all_files_as_read()
    {
        using var cancellation = new CancellationTokenSource();
        var updates = new List<SourceCollectionProgress>();
        var request = new CollectionRequest(new ActivitySource(), DateTimeOffset.UtcNow,
            CancellationToken: cancellation.Token, Progress: updates.Add);
        Assert.Throws<OperationCanceledException>(() =>
        {
            foreach (var file in CollectionFileProgress.Track(new[] { 1, 2, 3 }, request))
                cancellation.Cancel();
        });
        Assert.DoesNotContain(updates, item => item.ProcessedFiles == 3 || item.Stage == "整理資料");
    }
}
