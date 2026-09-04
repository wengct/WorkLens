using System.Text.Json;

namespace WorkLens.Services;

public static class AiReportResponseParser
{
    public static AiReportResult Parse(string raw, AiReportRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(raw));
            var root = document.RootElement;
            var reportId = root.GetProperty("reportId").GetGuid();
            var totalHours = root.GetProperty("totalHours").GetDouble();
            var body = root.GetProperty("body").GetString();
            var returnedIds = root.GetProperty("workEntryIds")
                .EnumerateArray()
                .Select(item => item.GetGuid())
                .ToHashSet();
            if (reportId != request.ReportId ||
                body is null ||
                !double.IsFinite(totalHours) ||
                Math.Abs(totalHours - request.TotalHours) > 0.01 ||
                !returnedIds.SetEquals(request.WorkEntryIds))
            {
                return new AiReportResult(false, null, raw, "AI 回覆未通過報告 ID、工作紀錄或工時驗證。");
            }

            return new AiReportResult(true, body, raw, null);
        }
        catch (JsonException exception)
        {
            return new AiReportResult(false, null, raw, $"AI 回覆不是有效 JSON：{exception.Message}");
        }
        catch (KeyNotFoundException)
        {
            return new AiReportResult(false, null, raw, "AI 回覆缺少必要欄位。");
        }
        catch (InvalidOperationException exception)
        {
            return new AiReportResult(false, null, raw, $"AI 回覆欄位格式錯誤：{exception.Message}");
        }
        catch (FormatException exception)
        {
            return new AiReportResult(false, null, raw, $"AI 回覆 GUID 格式錯誤：{exception.Message}");
        }
    }

    private static string ExtractJson(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }
}
