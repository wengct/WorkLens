using System.Text.Json;

namespace WorkLens.Services;

public static class AiReportResponseParser
{
    public static AiReportResult Parse(string raw, AiPreparedRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(raw));
            var root = document.RootElement;
            if (!root.GetProperty("reportId").TryGetGuid(out var reportId))
                return new AiReportResult(false, null, raw, "AI 回覆的 reportId 不是有效 GUID，請重新產生。原草稿已保留。");
            var body = root.GetProperty("body").GetString();
            var returnedIds = new HashSet<Guid>();
            foreach (var item in root.GetProperty("workEntryIds").EnumerateArray())
            {
                if (!item.TryGetGuid(out var id))
                    return new AiReportResult(false, null, raw, "AI 回覆的 workEntryIds 含有無效 GUID，請重新產生。原草稿已保留。");
                returnedIds.Add(id);
            }
            if (reportId != request.ReportId ||
                body is null ||
                !returnedIds.SetEquals(request.WorkEntryIds))
            {
                return new AiReportResult(false, null, raw, "AI 回覆未通過報告 ID 或工作紀錄驗證。");
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
