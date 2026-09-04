using System.Reflection;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AiSourcePrivacyTests
{
    [Fact]
    public void Ai_context_does_not_reuse_the_unfiltered_human_report_body()
    {
        var report = new ReportDocument
        {
            TotalHours = 2,
            DeterministicBody = "不允許提供給 AI 的來源內容"
        };
        IReadOnlyList<WorkEntry> entries =
        [
            new WorkEntry
            {
                WorkDate = new DateOnly(2026, 9, 3),
                Hours = 2,
                Title = "允許的人工紀錄",
                WorkContent = "人工紀錄內容"
            }
        ];
        IReadOnlyList<SourceEvidence> evidence =
        [
            new SourceEvidence
            {
                OccurredAt = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero),
                Kind = EvidenceKind.Commit,
                Title = "允許的來源活動"
            }
        ];

        var method = typeof(ReportService).GetMethod(
            "BuildAiInput",
            BindingFlags.NonPublic | BindingFlags.Static);

        IReadOnlyDictionary<Guid, string> projectNames = new Dictionary<Guid, string>();
        var context = Assert.IsType<string>(method?.Invoke(null, [report, entries, evidence, projectNames]));

        Assert.DoesNotContain(report.DeterministicBody, context, StringComparison.Ordinal);
        Assert.Contains("允許的人工紀錄", context, StringComparison.Ordinal);
        Assert.Contains("允許的來源活動", context, StringComparison.Ordinal);
    }
}
