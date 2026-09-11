using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SensitiveContentSanitizerTests
{
    [Fact]
    public async Task Prepare_redacts_credentials_email_and_custom_words_without_changing_sources()
    {
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            2 => new ProcessResult(1, ReportJson(new object[] { new
            {
                type = "synthetic_secret",
                filePath = "work-data.md",
                lineNumber = 2,
                columnNumber = 4,
                secret = "AbC123"
            }}), string.Empty),
            _ => new ProcessResult(0, ReportJson(), string.Empty)
        });
        await fixture.AddSensitiveWordAsync("客戶代號");

        var input = "😀\n憑證=AbC123\n聯絡 person@example.invalid\n客戶代號 Alpha";
        var request = new AiReportRequest(
            Guid.NewGuid(),
            "chatgpt",
            input,
            [Guid.NewGuid()],
            1,
            fixture.ExecutablePath,
            "請整理客戶代號的工作。");

        var result = await fixture.Sanitizer.PrepareAsync(request);

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Equal(AiSanitizationStatus.Redacted, result.Summary.Status);
        Assert.Equal(4, result.Summary.TotalCount);
        Assert.Contains(result.Summary.Notices, x => x.Category == AiSensitiveDataCategory.Credential && x.Count == 1);
        Assert.Contains(result.Summary.Notices, x => x.Category == AiSensitiveDataCategory.PersonalData && x.Count == 1);
        Assert.Equal(2, result.Summary.Notices
            .Where(x => x.Category == AiSensitiveDataCategory.CustomWord)
            .Sum(x => x.Count));
        Assert.NotNull(result.PreparedRequest);
        Assert.Contains("😀", result.PreparedRequest!.InputMarkdown, StringComparison.Ordinal);
        Assert.Contains("[已遮蔽：機敏憑證]", result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
        Assert.Contains("[已遮蔽：個人資料]", result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
        Assert.Contains("[已遮蔽：自訂敏感詞]", result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("AbC123", result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.invalid", result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("客戶代號", result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
        Assert.Equal(input, request.InputMarkdown);
        Assert.Equal(4, result.PreviewValues.Count);
        Assert.Contains(result.PreviewValues, value => value.OriginalValue == "AbC123");
        foreach (var value in result.PreviewValues)
        {
            var sanitized = value.Segment == "工作資料" ? result.PreparedRequest.InputMarkdown : result.PreparedRequest.EffectivePrompt;
            Assert.StartsWith("[已遮蔽：", sanitized[value.Start..]);
        }
        Assert.DoesNotContain("AbC123", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("person@example.invalid", JsonSerializer.Serialize(result.PreparedRequest));
        var scanRoot = Path.Combine(fixture.Root, "sensitive-scans");
        Assert.True(!Directory.Exists(scanRoot) || !Directory.EnumerateFileSystemEntries(scanRoot).Any());
    }

    [Fact]
    public async Task Exit_code_one_with_valid_json_is_accepted_as_a_scan_result()
    {
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            _ => new ProcessResult(1, ReportJson(), string.Empty)
        });

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, "沒有命中內容。"));

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Equal(AiSanitizationStatus.Clean, result.Summary.Status);
        Assert.Equal(2, fixture.Runner.Calls.Count);
    }

    [Fact]
    public async Task Scanner_finding_matching_an_exclusion_passes_without_redaction()
    {
        const string excludedValue = "ExampleToken";
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            _ => new ProcessResult(1, ReportJson(new object[] { new
            {
                type = "synthetic_secret",
                filePath = "work-data.md",
                lineNumber = 1,
                columnNumber = 1,
                secret = excludedValue
            }}), string.Empty)
        });
        await fixture.AddScanExclusionAsync("exampletoken");

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, excludedValue));

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Equal(AiSanitizationStatus.Clean, result.Summary.Status);
        Assert.Equal(excludedValue, result.PreparedRequest!.InputMarkdown);
        Assert.Equal(2, fixture.Runner.Calls.Count);
    }

    [Fact]
    public async Task Custom_word_overrides_a_matching_scanner_exclusion()
    {
        const string value = "ExampleToken";
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            2 => new ProcessResult(1, ReportJson(new object[] { new
            {
                type = "synthetic_secret",
                filePath = "work-data.md",
                lineNumber = 1,
                columnNumber = 1,
                secret = value
            }}), string.Empty),
            _ => new ProcessResult(0, ReportJson(), string.Empty)
        });
        await fixture.AddScanExclusionAsync(value);
        await fixture.AddSensitiveWordAsync(value);

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, value));

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Equal(AiSanitizationStatus.Redacted, result.Summary.Status);
        Assert.Contains("[已遮蔽：自訂敏感詞]", result.PreparedRequest!.InputMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Email_matching_a_scanner_exclusion_passes_without_redaction()
    {
        const string email = "person@example.invalid";
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            2 => new ProcessResult(1, ReportJson(new object[] { new
            {
                type = "email",
                filePath = "work-data.md",
                lineNumber = 1,
                columnNumber = 1,
                secret = email
            }}), string.Empty),
            _ => new ProcessResult(0, ReportJson(), string.Empty)
        });
        await fixture.AddScanExclusionAsync(email.ToUpperInvariant());

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, email));

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Equal(AiSanitizationStatus.Clean, result.Summary.Status);
        Assert.Equal(email, result.PreparedRequest!.InputMarkdown);
        Assert.Equal(2, fixture.Runner.Calls.Count);
    }

    [Fact]
    public async Task Excluded_finding_remaining_after_other_redaction_is_allowed()
    {
        const string excludedValue = "ExampleToken";
        const string secret = "ActualSecret";
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            2 => new ProcessResult(1, ReportJson(new object[]
            {
                new { type = "synthetic_secret", filePath = "work-data.md", lineNumber = 1, columnNumber = 1, secret = excludedValue },
                new { type = "synthetic_secret", filePath = "work-data.md", lineNumber = 1, columnNumber = 14, secret }
            }), string.Empty),
            _ => new ProcessResult(1, ReportJson(new object[]
            {
                new { type = "synthetic_secret", filePath = "work-data.md", lineNumber = 1, columnNumber = 1, secret = excludedValue }
            }), string.Empty)
        });
        await fixture.AddScanExclusionAsync(excludedValue);

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, $"{excludedValue} {secret}"));

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Equal(AiSanitizationStatus.Redacted, result.Summary.Status);
        Assert.Contains(excludedValue, result.PreparedRequest!.InputMarkdown, StringComparison.Ordinal);
        Assert.Contains("[已遮蔽：機敏憑證]", result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_nonzero_exit_with_complete_json_is_accepted()
    {
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            _ => new ProcessResult(2, ReportJson(), string.Empty)
        });

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, "沒有命中內容。"));

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Equal(AiSanitizationStatus.Clean, result.Summary.Status);
    }

    [Fact]
    public async Task Finding_with_inconsistent_unicode_location_blocks_sending()
    {
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            _ => new ProcessResult(0, ReportJson(new object[] { new
            {
                type = "synthetic_secret",
                filePath = "work-data.md",
                lineNumber = 1,
                columnNumber = 99,
                secret = "AbC123"
            }}), string.Empty)
        });

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, "😀 AbC123"));

        Assert.False(result.Succeeded);
        Assert.Equal(AiSanitizationStatus.Failed, result.Summary.Status);
        Assert.Null(result.PreparedRequest);
        Assert.Contains("定位", result.Summary.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skipped_file_blocks_sending_even_when_json_is_valid()
    {
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            _ => new ProcessResult(0, ReportJson(skipped: true), string.Empty)
        });

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, "資料"));

        Assert.False(result.Succeeded);
        Assert.Equal(AiSanitizationStatus.Failed, result.Summary.Status);
        Assert.Contains("完整掃描", result.Summary.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finding_remaining_after_redaction_blocks_sending()
    {
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            _ => new ProcessResult(1, ReportJson(new object[] { new
            {
                type = "synthetic_secret",
                filePath = "work-data.md",
                lineNumber = 1,
                columnNumber = 1,
                secret = "AbC123"
            }}), string.Empty)
        });

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, "AbC123"));

        Assert.False(result.Succeeded);
        Assert.Equal(AiSanitizationStatus.Failed, result.Summary.Status);
        Assert.Equal(3, fixture.Runner.Calls.Count);
        Assert.Contains("遮蔽後", result.Summary.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bundled_scanner_version_mismatch_blocks_sending()
    {
        await using var fixture = await SanitizerFixture.CreateAsync(
            (_, _) => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            expectedVersion: "0.5.5");

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, "資料"));

        Assert.False(result.Succeeded);
        Assert.Equal(AiSanitizationStatus.Failed, result.Summary.Status);
        Assert.Contains("版本", result.Summary.Error, StringComparison.Ordinal);
        Assert.Single(fixture.Runner.Calls);
    }

    [Fact]
    public async Task Timed_out_scan_blocks_sending_even_with_partial_json()
    {
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            _ => new ProcessResult(1, ReportJson(), string.Empty, TimedOut: true)
        });

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, "資料"));

        Assert.False(result.Succeeded);
        Assert.Equal(AiSanitizationStatus.Failed, result.Summary.Status);
        Assert.Contains("逾時", result.Summary.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_scanner_and_local_findings_are_replaced_once()
    {
        const string email = "person@example.invalid";
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            2 => new ProcessResult(1, ReportJson(new object[] { new
            {
                type = "email",
                filePath = "work-data.md",
                lineNumber = 1,
                columnNumber = 1,
                secret = email
            }}), string.Empty),
            _ => new ProcessResult(0, ReportJson(), string.Empty)
        });

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, email + "\r\n下一行"));

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Equal(1, result.Summary.TotalCount);
        Assert.Single(result.Summary.Notices);
        Assert.Equal(AiSensitiveDataCategory.PersonalData, result.Summary.Notices[0].Category);
        Assert.Equal(1, CountOccurrences(result.PreparedRequest!.InputMarkdown, "[已遮蔽：個人資料]"));
        Assert.DoesNotContain(email, result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unicode_location_can_replace_a_secret_that_crosses_crlf()
    {
        const string secret = "AbC\r\n下一行";
        await using var fixture = await SanitizerFixture.CreateAsync((_, invocation) => invocation switch
        {
            1 => new ProcessResult(0, "leak-hunter 0.5.4", string.Empty),
            2 => new ProcessResult(1, ReportJson(new object[] { new
            {
                type = "synthetic_secret",
                filePath = "work-data.md",
                lineNumber = 1,
                columnNumber = 1,
                secret
            }}), string.Empty),
            _ => new ProcessResult(0, ReportJson(), string.Empty)
        });

        var result = await fixture.Sanitizer.PrepareAsync(CreateRequest(fixture, secret + "\r\n尾端"));

        Assert.True(result.Succeeded, result.Summary.Error);
        Assert.Contains("[已遮蔽：機敏憑證]", result.PreparedRequest!.InputMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("AbC", result.PreparedRequest.InputMarkdown, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static AiReportRequest CreateRequest(SanitizerFixture fixture, string input) => new(
        Guid.NewGuid(),
        "chatgpt",
        input,
        [Guid.NewGuid()],
        1,
        fixture.ExecutablePath,
        "請整理。");

    private static string ReportJson(params object[] findings) => ReportJsonCore(findings, skipped: false);

    private static string ReportJson(bool skipped) => ReportJsonCore([], skipped);

    private static string ReportJsonCore(object[] findings, bool skipped) =>
        JsonSerializer.Serialize(new
        {
            summary = new
            {
                filesEnumerated = 2,
                filesScanned = 2,
                skipped = skipped ? 1 : 0,
                findings = findings.Length,
                redact = false
            },
            findings,
            skipped = skipped ? new object[] { new { filePath = "prompt.txt", reason = "test" } } : []
        });

    private sealed class SanitizerFixture : IAsyncDisposable
    {
        private SanitizerFixture(
            string root,
            SqliteConnection connection,
            DbContextOptions<WorkLensDbContext> options,
            FakeProcessRunner runner,
            SensitiveContentSanitizer sanitizer,
            string executablePath)
        {
            Root = root;
            Connection = connection;
            Options = options;
            Runner = runner;
            Sanitizer = sanitizer;
            ExecutablePath = executablePath;
        }

        public string Root { get; }
        public SqliteConnection Connection { get; }
        public DbContextOptions<WorkLensDbContext> Options { get; }
        public FakeProcessRunner Runner { get; }
        public SensitiveContentSanitizer Sanitizer { get; }
        public string ExecutablePath { get; }

        public static async Task<SanitizerFixture> CreateAsync(
            Func<ProcessRequest, int, ProcessResult> handler,
            string expectedVersion = "0.5.4")
        {
            var root = Path.Combine(
                Environment.CurrentDirectory,
                ".test-build",
                $"sanitizer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var executablePath = Path.Combine(root, "leak-hunter.exe");
            await File.WriteAllTextAsync(executablePath, "test executable");
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<WorkLensDbContext>()
                .UseSqlite(connection)
                .Options;
            await using (var db = new WorkLensDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var factory = new Factory(options);
            var runner = new FakeProcessRunner(handler);
            var paths = new AppPaths(
                Path.Combine(root, "worklens.db"),
                Path.Combine(root, "backups"),
                Path.Combine(root, "logs"));
            var sanitizer = new SensitiveContentSanitizer(
                paths,
                runner,
                factory,
                NullLogger<SensitiveContentSanitizer>.Instance,
                executablePath,
                expectedVersion);
            return new SanitizerFixture(root, connection, options, runner, sanitizer, executablePath);
        }

        public async Task AddSensitiveWordAsync(string value)
        {
            await using var db = new WorkLensDbContext(Options);
            db.SensitiveWords.Add(new SensitiveWord { Value = value });
            await db.SaveChangesAsync();
        }

        public async Task AddScanExclusionAsync(string value)
        {
            await using var db = new WorkLensDbContext(Options);
            db.SensitiveScanExclusions.Add(new SensitiveScanExclusion { Value = value });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeProcessRunner(Func<ProcessRequest, int, ProcessResult> handler) : IProcessRunner
    {
        private int invocationCount;

        public List<ProcessRequest> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            string? standardInput = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            var invocation = Interlocked.Increment(ref invocationCount);
            return Task.FromResult(handler(request, invocation));
        }
    }
}
