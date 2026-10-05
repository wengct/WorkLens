using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace WorkLens.Services;

public static class McpToolResponse
{
    private const int MaxResultBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static CallToolResult Normalize(CallToolResult result)
    {
        if (result.StructuredContent is null) return result;
        var payload = JsonSerializer.SerializeToElement(result.StructuredContent);
        if (!payload.TryGetProperty("success", out var success)) return result;
        result.IsError = !success.GetBoolean();
        // Keep the full data only in structuredContent; the text is a brief explanation.
        result.Content = [new TextContentBlock { Text = payload.GetProperty("message").GetString() ?? "" }];
        if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length > MaxResultBytes)
        {
            var failure = new McpToolResult(false, "context_too_large", "當日資料超過 256 KiB，未回傳正文。");
            result.StructuredContent = JsonSerializer.SerializeToElement(failure, JsonOptions);
            result.Content = [new TextContentBlock { Text = failure.Message }];
            result.IsError = true;
        }
        return result;
    }
}
