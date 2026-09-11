using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class SensitiveContentSanitizer : IAiContentSanitizer
{
    private const string WorkSegmentFile = "work-data.md";
    private const string PromptSegmentFile = "prompt.txt";
    private const string CredentialMarker = "[已遮蔽：機敏憑證]";
    private const string PersonalDataMarker = "[已遮蔽：個人資料]";
    private const string CustomWordMarker = "[已遮蔽：自訂敏感詞]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex EmailRegex = new(
        @"(?<![A-Za-z0-9.!#$%&'*+/=?^_`{|}~-])([A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly AppPaths paths;
    private readonly IProcessRunner processRunner;
    private readonly IDbContextFactory<WorkLensDbContext> factory;
    private readonly ILogger<SensitiveContentSanitizer> logger;
    private readonly string executablePath;
    private readonly string? expectedVersion;

    public SensitiveContentSanitizer(
        AppPaths paths,
        IProcessRunner processRunner,
        IDbContextFactory<WorkLensDbContext> factory,
        ILogger<SensitiveContentSanitizer> logger,
        string? executablePath = null,
        string? expectedVersion = null)
    {
        this.paths = paths;
        this.processRunner = processRunner;
        this.factory = factory;
        this.logger = logger;
        this.executablePath = executablePath ?? ResolveBundledExecutablePath();
        this.expectedVersion = expectedVersion ?? ReadBundledVersionMarker();
    }

    public async Task<AiSanitizerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            return new AiSanitizerStatus(false, string.Empty, "找不到隨 WorkLens 附帶的 leak-hunter。請更新 WorkLens。");
        }

        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return new AiSanitizerStatus(false, string.Empty, "找不到隨 WorkLens 附帶的 leak-hunter 版本標記。請更新 WorkLens。");
        }

        var result = await processRunner.RunAsync(
            new ProcessRequest(executablePath, ["--version"]),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new AiSanitizerStatus(false, string.Empty, "leak-hunter 無法執行。請更新 WorkLens。");
        }

        var actualVersion = result.StandardOutput.Trim();
        if (!HasExpectedVersion(actualVersion, expectedVersion))
        {
            return new AiSanitizerStatus(false, actualVersion, "隨附的 leak-hunter 版本不符合 WorkLens 要求，請更新 WorkLens。");
        }

        return new AiSanitizerStatus(true, actualVersion, "機敏資訊檢查可用。");
    }

    public async Task<AiSanitizationResult> PrepareAsync(
        AiReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Available)
        {
            return Failed(status.Summary, status.Version);
        }

        var temporaryDirectory = Path.Combine(
            paths.DataDirectory,
            "sensitive-scans",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var segments = new Dictionary<string, ScanSegment>(StringComparer.OrdinalIgnoreCase)
            {
                [WorkSegmentFile] = new("工作資料", request.InputMarkdown),
                [PromptSegmentFile] = new("Prompt", request.EffectivePrompt)
            };
            await WriteSegmentsAsync(temporaryDirectory, segments, cancellationToken);

            var maxFileSizeMb = GetMaxFileSizeMb(segments.Values);
            var firstScan = await RunLeakHunterAsync(
                temporaryDirectory,
                segments.Count,
                maxFileSizeMb,
                status.Version,
                cancellationToken);
            if (!firstScan.IsValid)
            {
                return Failed(firstScan.Error ?? "機敏資訊檢查未完成。", status.Version);
            }

            var customWords = await GetCustomWordsAsync(cancellationToken);
            var scanExclusions = await GetScanExclusionsAsync(cancellationToken);
            var rangesByFile = new Dictionary<string, List<RedactionRange>>(StringComparer.OrdinalIgnoreCase);
            foreach (var finding in firstScan.Findings)
            {
                var fileKey = finding.FilePath.Replace('\\', '/');
                if (!segments.TryGetValue(fileKey, out var segment))
                {
                    return Failed("機敏資訊檢查回傳了無法識別的資料區段。", status.Version);
                }

                var category = CategoryFor(finding.Type);
                var range = LocateFinding(segment.Text, finding);
                if (range is null)
                {
                    return Failed("無法可靠定位機敏資訊，為保護資料安全，本次未傳送 AI。", status.Version);
                }

                if (IsScanExcluded(finding, scanExclusions))
                {
                    continue;
                }

                AddRange(rangesByFile, fileKey, range with { Category = category });
            }

            foreach (var pair in segments)
            {
                foreach (var match in FindEmailMatches(pair.Value.Text)
                             .Where(match => !IsScanExcluded(pair.Value.Text, match, scanExclusions)))
                {
                    AddRange(rangesByFile, pair.Key, match with { Category = AiSensitiveDataCategory.PersonalData });
                }

                foreach (var word in customWords)
                {
                    foreach (var match in FindLiteralMatches(pair.Value.Text, word))
                    {
                        AddRange(rangesByFile, pair.Key, match with { Category = AiSensitiveDataCategory.CustomWord });
                    }
                }
            }

            if (rangesByFile.Count == 0)
            {
                var cleanSummary = new AiSanitizationSummary(
                    AiSanitizationStatus.Clean,
                    status.Version,
                    []);
                return new AiSanitizationResult(
                    new AiPreparedRequest(
                        request.ReportId,
                        request.Target,
                        request.InputMarkdown,
                        request.WorkEntryIds,
                        request.TotalHours,
                        request.ExecutablePath,
                        request.EffectivePrompt,
                        cleanSummary),
                    cleanSummary);
            }

            var previewValues = new List<AiRedactionPreview>();
            var sanitizedSegments = segments.ToDictionary(
                pair => pair.Key,
                pair => pair.Value with { Text = ApplyRedactions(pair.Value.Text, rangesByFile.GetValueOrDefault(pair.Key) ?? [], pair.Value.Label, previewValues) },
                StringComparer.OrdinalIgnoreCase);
            await WriteSegmentsAsync(temporaryDirectory, sanitizedSegments, cancellationToken);

            var secondScan = await RunLeakHunterAsync(
                temporaryDirectory,
                sanitizedSegments.Count,
                maxFileSizeMb,
                status.Version,
                cancellationToken);
            if (!secondScan.IsValid)
            {
                return Failed("機敏資訊遮蔽後仍未通過安全檢查，本次未傳送 AI。", status.Version);
            }

            foreach (var finding in secondScan.Findings)
            {
                var fileKey = finding.FilePath.Replace('\\', '/');
                if (!sanitizedSegments.TryGetValue(fileKey, out var segment) ||
                    LocateFinding(segment.Text, finding) is null ||
                    !IsScanExcluded(finding, scanExclusions))
                {
                    return Failed("機敏資訊遮蔽後仍未通過安全檢查，本次未傳送 AI。", status.Version);
                }
            }

            foreach (var pair in sanitizedSegments)
            {
                if (FindEmailMatches(pair.Value.Text).Any(match =>
                        !IsInsideRedactionMarker(pair.Value.Text, match.Start, match.End) &&
                        !IsScanExcluded(pair.Value.Text, match, scanExclusions)) ||
                    customWords.Any(word => FindLiteralMatches(pair.Value.Text, word)
                        .Any(match => !IsInsideRedactionMarker(pair.Value.Text, match.Start, match.End))))
                {
                    return Failed("機敏資訊遮蔽後仍未通過本機規則檢查，本次未傳送 AI。", status.Version);
                }
            }

            var summary = new AiSanitizationSummary(
                AiSanitizationStatus.Redacted,
                status.Version,
                rangesByFile
                    .SelectMany(pair => MergeRanges(pair.Value)
                        .Select(range => new AiRedactionNotice(
                            segments[pair.Key].Label,
                            range.Category,
                            1)))
                    .GroupBy(x => new { x.Segment, x.Category })
                    .Select(group => new AiRedactionNotice(
                        group.Key.Segment,
                        group.Key.Category,
                        group.Count()))
                    .ToList());
            var prepared = new AiPreparedRequest(
                request.ReportId,
                request.Target,
                sanitizedSegments[WorkSegmentFile].Text,
                request.WorkEntryIds,
                request.TotalHours,
                request.ExecutablePath,
                sanitizedSegments[PromptSegmentFile].Text,
                summary);
            return new AiSanitizationResult(prepared, summary) { PreviewValues = previewValues };
        }
        catch (OperationCanceledException)
        {
            return Failed("機敏資訊檢查已取消，本次未傳送 AI。", status.Version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("機敏資訊檢查未完成：{Reason}", exception.GetType().Name);
            return Failed("機敏資訊檢查未完成，本次未傳送 AI。", status.Version);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("機敏資訊檢查暫存資料清理失敗：{Reason}", exception.GetType().Name);
            }
        }
    }

    private async Task<LeakHunterScan> RunLeakHunterAsync(
        string directory,
        int expectedFileCount,
        int maxFileSizeMb,
        string scannerVersion,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new ProcessRequest(
                executablePath,
                [
                    "--json",
                    "--no-redact",
                    "--min-risk", "0",
                    "--no-default-exclude",
                    "--max-file-size-mb", maxFileSizeMb.ToString(),
                    directory
                ],
                directory),
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);

        if (result.TimedOut)
        {
            return LeakHunterScan.Invalid("leak-hunter 執行逾時。");
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return LeakHunterScan.Invalid("leak-hunter 沒有回傳有效結果。");
        }

        LeakHunterReport? report;
        try
        {
            report = JsonSerializer.Deserialize<LeakHunterReport>(result.StandardOutput, JsonOptions);
        }
        catch (JsonException)
        {
            return LeakHunterScan.Invalid("leak-hunter 回傳格式無法解析。");
        }

        if (report?.Summary is null || report.Findings is null || report.Skipped is null)
        {
            return LeakHunterScan.Invalid("leak-hunter 回傳缺少必要欄位。");
        }

        if (report.Summary.Redact != false ||
            report.Summary.FilesEnumerated != expectedFileCount ||
            report.Summary.FilesScanned != expectedFileCount ||
            report.Summary.Skipped != 0 ||
            report.Skipped.Count != 0)
        {
            return LeakHunterScan.Invalid("leak-hunter 未完整掃描待送內容。");
        }

        return new LeakHunterScan(true, report.Findings, scannerVersion, null);
    }

    private async Task<IReadOnlyList<string>> GetCustomWordsAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var values = await db.SensitiveWords
            .AsNoTracking()
            .Where(x => x.Enabled)
            .Select(x => x.Value)
            .Select(x => x.Trim())
            .Where(x => x != string.Empty)
            .ToListAsync(cancellationToken);
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<string>> GetScanExclusionsAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var values = await db.SensitiveScanExclusions
            .AsNoTracking()
            .Select(x => x.Value)
            .Select(x => x.Trim())
            .Where(x => x != string.Empty)
            .ToListAsync(cancellationToken);
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task WriteSegmentsAsync(
        string directory,
        IReadOnlyDictionary<string, ScanSegment> segments,
        CancellationToken cancellationToken)
    {
        foreach (var pair in segments)
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, pair.Key),
                pair.Value.Text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
    }

    private static int GetMaxFileSizeMb(IEnumerable<ScanSegment> segments)
    {
        var maxBytes = segments.Max(segment => Encoding.UTF8.GetByteCount(segment.Text));
        return Math.Max(5, checked((int)Math.Ceiling(maxBytes / 1024d / 1024d)));
    }

    private static RedactionRange? LocateFinding(string text, LeakHunterFinding finding)
    {
        if (string.IsNullOrEmpty(finding.Secret) || finding.LineNumber < 1 || finding.ColumnNumber < 1)
        {
            return null;
        }

        var start = FindUnicodeScalarColumnOffset(text, finding.LineNumber, finding.ColumnNumber);
        if (start < 0 || start + finding.Secret.Length > text.Length ||
            !text.AsSpan(start, finding.Secret.Length).SequenceEqual(finding.Secret.AsSpan()))
        {
            return null;
        }

        return new RedactionRange(start, start + finding.Secret.Length, AiSensitiveDataCategory.Credential);
    }

    private static int FindUnicodeScalarColumnOffset(string text, int lineNumber, int columnNumber)
    {
        var line = 1;
        var lineStart = 0;
        for (var index = 0; index < text.Length && line < lineNumber; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        if (line != lineNumber) return -1;
        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = text.Length;
        var scalar = 1;
        var offset = lineStart;
        foreach (var rune in text.AsSpan(lineStart, lineEnd - lineStart).EnumerateRunes())
        {
            if (scalar == columnNumber) return offset;
            offset += rune.Utf16SequenceLength;
            scalar++;
        }

        return scalar == columnNumber ? offset : -1;
    }

    private static IEnumerable<RedactionRange> FindEmailMatches(string text)
    {
        return EmailRegex.Matches(text)
            .Select(match => new RedactionRange(match.Index, match.Index + match.Length, AiSensitiveDataCategory.PersonalData));
    }

    private static IEnumerable<RedactionRange> FindLiteralMatches(string text, string value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) yield break;
            yield return new RedactionRange(index, index + value.Length, AiSensitiveDataCategory.CustomWord);
            start = index + 1;
        }
    }

    private static bool IsScanExcluded(LeakHunterFinding finding, IReadOnlyList<string> exclusions) =>
        !string.IsNullOrEmpty(finding.Secret) &&
        exclusions.Any(value => value.Equals(finding.Secret, StringComparison.OrdinalIgnoreCase));

    private static bool IsScanExcluded(
        string text,
        RedactionRange range,
        IReadOnlyList<string> exclusions) =>
        exclusions.Any(value =>
            value.Length == range.End - range.Start &&
            text.AsSpan(range.Start, value.Length).Equals(value, StringComparison.OrdinalIgnoreCase));

    private static void AddRange(
        IDictionary<string, List<RedactionRange>> rangesByFile,
        string file,
        RedactionRange range)
    {
        if (!rangesByFile.TryGetValue(file, out var ranges))
        {
            ranges = [];
            rangesByFile[file] = ranges;
        }

        ranges.Add(range);
    }

    private static string ApplyRedactions(string text, IReadOnlyList<RedactionRange> ranges,
        string segment, List<AiRedactionPreview> previewValues)
    {
        if (ranges.Count == 0) return text;
        var merged = MergeRanges(ranges);
        var builder = new StringBuilder();
        var start = 0;
        foreach (var range in merged)
        {
            builder.Append(text.AsSpan(start, range.Start - start));
            previewValues.Add(new AiRedactionPreview(segment, builder.Length, text[range.Start..range.End]));
            builder.Append(MarkerFor(range.Category));
            start = range.End;
        }
        builder.Append(text.AsSpan(start));

        return builder.ToString();
    }

    private static List<RedactionRange> MergeRanges(IReadOnlyList<RedactionRange> ranges)
    {
        var merged = new List<RedactionRange>();
        foreach (var range in ranges.OrderBy(x => x.Start).ThenByDescending(x => x.End))
        {
            if (merged.Count == 0 || range.Start >= merged[^1].End)
            {
                merged.Add(range);
                continue;
            }

            var current = merged[^1];
            merged[^1] = new RedactionRange(
                current.Start,
                Math.Max(current.End, range.End),
                CategoryPriority(current.Category) >= CategoryPriority(range.Category) ? current.Category : range.Category);
        }

        return merged;
    }

    private static bool IsInsideRedactionMarker(string text, int start, int end)
    {
        var markerStart = text.LastIndexOf("[已遮蔽：", Math.Min(start, text.Length - 1), StringComparison.Ordinal);
        var markerEnd = markerStart < 0 ? -1 : text.IndexOf(']', markerStart);
        return markerStart >= 0 && markerEnd >= end - 1;
    }

    private static AiSensitiveDataCategory CategoryFor(string type) =>
        type.Contains("taiwan", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("email", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("identity", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("resident", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("phone", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("mobile", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("invoice", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("natural", StringComparison.OrdinalIgnoreCase)
            ? AiSensitiveDataCategory.PersonalData
            : AiSensitiveDataCategory.Credential;

    private static int CategoryPriority(AiSensitiveDataCategory category) => category switch
    {
        AiSensitiveDataCategory.Credential => 3,
        AiSensitiveDataCategory.PersonalData => 2,
        _ => 1
    };

    private static string MarkerFor(AiSensitiveDataCategory category) => category switch
    {
        AiSensitiveDataCategory.Credential => CredentialMarker,
        AiSensitiveDataCategory.PersonalData => PersonalDataMarker,
        _ => CustomWordMarker
    };

    private static AiSanitizationResult Failed(string error, string scannerVersion) =>
        new(
            null,
            new AiSanitizationSummary(AiSanitizationStatus.Failed, scannerVersion, [], error));

    private static string ResolveBundledExecutablePath()
    {
        var name = OperatingSystem.IsWindows() ? "leak-hunter.exe" : "leak-hunter";
        return Path.Combine(AppContext.BaseDirectory, "tools", "leak-hunter", name);
    }

    private static string? ReadBundledVersionMarker()
    {
        try
        {
            var marker = Path.Combine(AppContext.BaseDirectory, "scripts", "leak-hunter.version");
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasExpectedVersion(string actualVersion, string expectedVersion)
    {
        var number = expectedVersion.Trim().TrimStart('v');
        return !string.IsNullOrWhiteSpace(number) &&
               Regex.IsMatch(
                   actualVersion,
                   $"(?<![0-9A-Za-z.-])v?{Regex.Escape(number)}(?![0-9A-Za-z.-])",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed record ScanSegment(string Label, string Text);

    private sealed record RedactionRange(int Start, int End, AiSensitiveDataCategory Category);

    private sealed record LeakHunterScan(
        bool IsValid,
        IReadOnlyList<LeakHunterFinding> Findings,
        string ScannerVersion,
        string? Error)
    {
        public static LeakHunterScan Invalid(string error) => new(false, [], string.Empty, error);
    }

    private sealed class LeakHunterReport
    {
        [JsonPropertyName("summary")] public LeakHunterSummary? Summary { get; set; }
        [JsonPropertyName("findings")] public List<LeakHunterFinding>? Findings { get; set; }
        [JsonPropertyName("skipped")] public List<LeakHunterSkipped>? Skipped { get; set; }
    }

    private sealed class LeakHunterSummary
    {
        [JsonPropertyName("filesEnumerated")] public int FilesEnumerated { get; set; }
        [JsonPropertyName("filesScanned")] public int FilesScanned { get; set; }
        [JsonPropertyName("skipped")] public int Skipped { get; set; }
        [JsonPropertyName("redact")] public bool? Redact { get; set; }
    }

    private sealed class LeakHunterFinding
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("filePath")] public string FilePath { get; set; } = string.Empty;
        [JsonPropertyName("lineNumber")] public int LineNumber { get; set; }
        [JsonPropertyName("columnNumber")] public int ColumnNumber { get; set; }
        [JsonPropertyName("secret")] public string Secret { get; set; } = string.Empty;
    }

    private sealed class LeakHunterSkipped
    {
        [JsonPropertyName("filePath")] public string FilePath { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }
}
