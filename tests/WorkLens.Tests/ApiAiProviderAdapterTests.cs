using System.Net;
using System.Text;
using System.Text.Json;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ApiAiProviderAdapterTests
{
    [Theory]
    [InlineData("openai", "https://api.openai.com/v1/responses", "Authorization")]
    [InlineData("azure-openai", "https://sample.openai.azure.com/openai/v1/responses?api-version=preview", "api-key")]
    [InlineData("anthropic", "https://api.anthropic.com/v1/messages", "x-api-key")]
    [InlineData("gemini", "https://generativelanguage.googleapis.com/v1beta/models/test-model:generateContent", "x-goog-api-key")]
    [InlineData("openai-compatible", "http://localhost:11434/v1/chat/completions", "Authorization")]
    public async Task Generate_uses_provider_protocol_and_parses_validated_report(
        string providerType,
        string expectedUrl,
        string? expectedAuthHeader)
    {
        var responseJson = ProviderResponse(providerType, ValidReportJson());
        var handler = new RecordingHandler(responseJson);
        var adapter = new ApiAiProviderAdapter(
            providerType,
            new HttpClient(handler),
            new StubSecretProtector());
        var configuration = Configuration(providerType);
        var request = Request();

        var result = await adapter.GenerateAsync(configuration, request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("整理完成", result.Body);
        Assert.Equal(expectedUrl, handler.RequestUri?.ToString());
        if (expectedAuthHeader is not null)
        {
            Assert.Contains(expectedAuthHeader, handler.Headers);
        }
    }

    [Theory]
    [InlineData("openai", "\"reasoning\"")]
    [InlineData("azure-openai", "\"reasoning\"")]
    [InlineData("anthropic", "\"thinking\"")]
    [InlineData("gemini", "\"thinkingConfig\"")]
    [InlineData("openai-compatible", "\"reasoning_effort\"")]
    public async Task Generate_translates_reasoning_level_for_each_provider(
        string providerType,
        string expectedProperty)
    {
        var handler = new RecordingHandler(ProviderResponse(providerType, ValidReportJson()));
        var adapter = new ApiAiProviderAdapter(providerType, new HttpClient(handler), new StubSecretProtector());
        var configuration = Configuration(providerType);
        configuration.ReasoningLevel = AiReasoningLevel.Low;

        var result = await adapter.GenerateAsync(configuration, Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(expectedProperty, handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_rejects_report_when_identifiers_are_changed()
    {
        const string invalidReport = "{\"reportId\":\"33333333-3333-3333-3333-333333333333\",\"workEntryIds\":[],\"totalHours\":1,\"body\":\"錯誤\"}";
        var handler = new RecordingHandler(ProviderResponse("openai", invalidReport));
        var adapter = new ApiAiProviderAdapter("openai", new HttpClient(handler), new StubSecretProtector());

        var result = await adapter.GenerateAsync(Configuration("openai"), Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("未通過", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_sends_the_prepared_masked_content_without_the_raw_value()
    {
        const string marker = "[已遮蔽：機敏憑證]";
        var handler = new RecordingHandler(ProviderResponse("openai", ValidReportJson()));
        var adapter = new ApiAiProviderAdapter("openai", new HttpClient(handler), new StubSecretProtector());
        var request = new AiPreparedRequest(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "test",
            $"工作資料：{marker}",
            [Guid.Parse("22222222-2222-2222-2222-222222222222")],
            1,
            null,
            $"整理偏好：{marker}",
            new AiSanitizationSummary(AiSanitizationStatus.Redacted, "test", [new AiRedactionNotice("工作資料", AiSensitiveDataCategory.Credential, 1)]));

        var result = await adapter.GenerateAsync(Configuration("openai"), request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        using var document = JsonDocument.Parse(handler.Body);
        Assert.Contains(marker, document.RootElement.GetProperty("input").GetString(), StringComparison.Ordinal);
    }

    private static AiProviderConfiguration Configuration(string providerType) => new()
    {
        ProviderType = providerType,
        ProtectedApiKey = "protected",
        ApiEndpoint = providerType switch
        {
            "azure-openai" => "https://sample.openai.azure.com",
            "openai-compatible" => "http://localhost:11434/v1",
            _ => null
        },
        ApiVersion = "preview",
        Model = providerType == "azure-openai" ? "gpt-5-deployment" : "test-model"
    };

    private static AiPreparedRequest Request() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "test",
        "工作資料",
        [Guid.Parse("22222222-2222-2222-2222-222222222222")],
        1,
        null,
        "整理",
        new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []));

    private static string ValidReportJson() =>
        "{\"reportId\":\"11111111-1111-1111-1111-111111111111\",\"workEntryIds\":[\"22222222-2222-2222-2222-222222222222\"],\"totalHours\":1,\"body\":\"整理完成\"}";

    private static string ProviderResponse(string providerType, string report)
    {
        return providerType switch
        {
            "openai" or "azure-openai" => JsonSerializer.Serialize(new { output_text = report }),
            "anthropic" => JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = report } } }),
            "gemini" => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = report } } } } } }),
            "openai-compatible" => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = report } } } }),
            _ => throw new ArgumentOutOfRangeException(nameof(providerType))
        };
    }

    private sealed class StubSecretProtector : IAiSecretProtector
    {
        public string Protect(string value) => "protected";
        public string Unprotect(string value) => "secret";
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public HashSet<string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers) Headers.Add(header.Key);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
