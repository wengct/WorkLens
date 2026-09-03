using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class FolderPickerServiceTests
{
    [Fact]
    public async Task Selected_folder_is_returned_as_an_absolute_path()
    {
        var selected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "worklens-picked"));
        var runner = new StubRunner(new ProcessResult(0, selected + Environment.NewLine, string.Empty));

        var result = await new FolderPickerService(runner).PickAsync();

        Assert.True(result.Selected);
        Assert.Equal(selected, result.Path);
        Assert.NotNull(runner.Request);
    }

    [Fact]
    public async Task Empty_successful_output_is_treated_as_user_cancellation()
    {
        var result = await new FolderPickerService(
            new StubRunner(new ProcessResult(0, string.Empty, string.Empty))).PickAsync();

        Assert.False(result.Selected);
        Assert.Null(result.Error);
    }

    private sealed class StubRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            string? standardInput = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }
}
