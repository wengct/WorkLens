using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AiReportResponseParserTests
{
    [Fact]
    public void Valid_annual_response_succeeds_but_wrong_ids_are_rejected()
    {
        var id = Guid.NewGuid();
        var request = new AiPreparedRequest(id, "test", "", [], 0, null, "",
            new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []));
        string Response(Guid reportId, string[] ids) => System.Text.Json.JsonSerializer.Serialize(new { reportId, body = "草稿", workEntryIds = ids });
        Assert.True(AiReportResponseParser.Parse(Response(id, []), request).Succeeded);
        Assert.False(AiReportResponseParser.Parse(Response(Guid.NewGuid(), []), request).Succeeded);
        Assert.False(AiReportResponseParser.Parse(Response(id, [Guid.NewGuid().ToString()]), request).Succeeded);
        var invalidEntry = AiReportResponseParser.Parse(Response(id, ["來源活動"]), request);
        Assert.False(invalidEntry.Succeeded);
        Assert.Contains("workEntryIds", invalidEntry.Error);
    }

    [Theory]
    [InlineData("輸入的 review ID")]
    [InlineData("")]
    public void Invalid_report_id_identifies_the_field_and_preserves_validation(string id)
    {
        var request = new AiPreparedRequest(Guid.NewGuid(), "test", "", [], 0, null, "",
            new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []));
        var raw = System.Text.Json.JsonSerializer.Serialize(new { reportId = id, body = "草稿", workEntryIds = Array.Empty<string>() });
        var result = AiReportResponseParser.Parse(raw, request);
        Assert.False(result.Succeeded);
        Assert.Contains("reportId", result.Error);
        Assert.Null(result.Body);
    }
}
