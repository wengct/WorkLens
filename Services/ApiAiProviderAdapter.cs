using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class ApiAiProviderAdapter(
    string providerType,
    HttpClient httpClient,
    IAiSecretProtector secrets) : IAiProviderAdapter
{
    public string ProviderType { get; } = providerType;

    public Task<AiProviderValidationResult> ValidateAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var error = ValidateConfiguration(configuration);
        return Task.FromResult(error is null
            ? new AiProviderValidationResult(true, "Ready", $"{DisplayName()} API 設定已完成。")
            : new AiProviderValidationResult(false, "NotConfigured", error));
    }

    public async Task<AiConnectionTestResult> TestConnectionAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var reportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var entryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var result = await GenerateAsync(
            configuration,
            new AiReportRequest(
                reportId,
                configuration.ProviderType,
                "工作項目：完成 API 連線測試。",
                [entryId],
                1,
                EffectivePrompt: "只需以一句話表示連線測試成功。"),
            cancellationToken);
        return new AiConnectionTestResult(result.Succeeded, result.RawResponse, result.Error);
    }

    public async Task<AiReportResult> GenerateAsync(
        AiProviderConfiguration configuration,
        AiReportRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateConfiguration(configuration);
        if (validationError is not null)
        {
            return new AiReportResult(false, null, null, validationError);
        }

        try
        {
            using var message = BuildRequest(configuration, request);
            using var response = await httpClient.SendAsync(message, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AiReportResult(
                    false,
                    null,
                    null,
                    $"{DisplayName()} API 回傳 {(int)response.StatusCode}：{SummarizeError(responseBody)}");
            }

            var text = ExtractText(responseBody);
            return string.IsNullOrWhiteSpace(text)
                ? new AiReportResult(false, null, responseBody, $"{DisplayName()} API 未回傳文字內容。")
                : AiReportResponseParser.Parse(text, request);
        }
        catch (OperationCanceledException)
        {
            return new AiReportResult(
                false,
                null,
                null,
                cancellationToken.IsCancellationRequested ? "AI API 作業已取消。" : $"{DisplayName()} API 連線逾時。");
        }
        catch (HttpRequestException exception)
        {
            return new AiReportResult(false, null, null, $"{DisplayName()} API 連線失敗：{exception.Message}");
        }
        catch (JsonException exception)
        {
            return new AiReportResult(false, null, null, $"{DisplayName()} API 回覆格式錯誤：{exception.Message}");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return new AiReportResult(false, null, null, "API Key 無法解密，請重新輸入並儲存。");
        }
    }

    private HttpRequestMessage BuildRequest(AiProviderConfiguration configuration, AiReportRequest request)
    {
        var apiKey = string.IsNullOrWhiteSpace(configuration.ProtectedApiKey)
            ? string.Empty
            : secrets.Unprotect(configuration.ProtectedApiKey);
        var prompt = BuildPrompt(request);
        return ProviderType switch
        {
            "openai" => BuildOpenAiRequest("https://api.openai.com/v1/responses", apiKey, configuration, prompt, true),
            "azure-openai" => BuildAzureRequest(apiKey, configuration, prompt),
            "anthropic" => BuildAnthropicRequest(apiKey, configuration, prompt),
            "gemini" => BuildGeminiRequest(apiKey, configuration, prompt),
            "openai-compatible" => BuildOpenAiRequest(
                AppendPath(configuration.ApiEndpoint!, "chat/completions"), apiKey, configuration, prompt, false),
            _ => throw new InvalidOperationException($"不支援的 API Provider：{ProviderType}")
        };
    }

    private static HttpRequestMessage BuildOpenAiRequest(
        string url,
        string apiKey,
        AiProviderConfiguration configuration,
        string prompt,
        bool responsesApi)
    {
        JsonObject payload;
        if (responsesApi)
        {
            payload = new JsonObject
            {
                ["model"] = configuration.Model,
                ["input"] = prompt,
                ["max_output_tokens"] = 8192
            };
            AddResponsesReasoning(payload, configuration.ReasoningLevel);
        }
        else
        {
            payload = new JsonObject
            {
                ["model"] = configuration.Model,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
                ["max_tokens"] = 8192
            };
            AddCompatibleReasoning(payload, configuration.ReasoningLevel);
        }

        var message = JsonRequest(url, payload);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return message;
    }

    private static HttpRequestMessage BuildAzureRequest(
        string apiKey,
        AiProviderConfiguration configuration,
        string prompt)
    {
        var endpoint = configuration.ApiEndpoint!.TrimEnd('/');
        var version = string.IsNullOrWhiteSpace(configuration.ApiVersion) ? "preview" : configuration.ApiVersion.Trim();
        var useResponsesApi = UsesResponsesApi(configuration.Model!);
        var url = useResponsesApi
            ? $"{endpoint}/openai/v1/responses?api-version={Uri.EscapeDataString(version)}"
            : $"{endpoint}/openai/deployments/{Uri.EscapeDataString(configuration.Model!)}/chat/completions?api-version={Uri.EscapeDataString(version)}";
        var payload = useResponsesApi ? new JsonObject
        {
            ["model"] = configuration.Model,
            ["input"] = prompt,
            ["max_output_tokens"] = 8192
        } : new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = 8192
        };
        if (useResponsesApi) AddResponsesReasoning(payload, configuration.ReasoningLevel);
        else AddCompatibleReasoning(payload, configuration.ReasoningLevel);
        var message = JsonRequest(url, payload);
        message.Headers.Add("api-key", apiKey);
        return message;
    }

    private static HttpRequestMessage BuildAnthropicRequest(
        string apiKey,
        AiProviderConfiguration configuration,
        string prompt)
    {
        var payload = new JsonObject
        {
            ["model"] = configuration.Model,
            ["max_tokens"] = 8192,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt })
        };
        var budget = ThinkingBudget(configuration.ReasoningLevel);
        if (budget > 0)
        {
            payload["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
        }
        var message = JsonRequest("https://api.anthropic.com/v1/messages", payload);
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        return message;
    }

    private static HttpRequestMessage BuildGeminiRequest(
        string apiKey,
        AiProviderConfiguration configuration,
        string prompt)
    {
        var generationConfig = new JsonObject { ["responseMimeType"] = "application/json" };
        if (ThinkingBudget(configuration.ReasoningLevel) is var budget && budget > 0)
        {
            generationConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = budget };
        }
        var payload = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt })
            }),
            ["generationConfig"] = generationConfig
        };
        var model = Uri.EscapeDataString(configuration.Model!);
        var message = JsonRequest(
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent",
            payload);
        message.Headers.Add("x-goog-api-key", apiKey);
        return message;
    }

    private static HttpRequestMessage JsonRequest(string url, JsonObject payload) =>
        new(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };

    private static void AddResponsesReasoning(JsonObject payload, AiReasoningLevel level)
    {
        if (level is AiReasoningLevel.Low or AiReasoningLevel.Medium or AiReasoningLevel.High)
        {
            payload["reasoning"] = new JsonObject { ["effort"] = level.ToString().ToLowerInvariant() };
        }
    }

    private static void AddCompatibleReasoning(JsonObject payload, AiReasoningLevel level)
    {
        if (level is AiReasoningLevel.Low or AiReasoningLevel.Medium or AiReasoningLevel.High)
        {
            payload["reasoning_effort"] = level.ToString().ToLowerInvariant();
        }
    }

    private static int ThinkingBudget(AiReasoningLevel level) => level switch
    {
        AiReasoningLevel.Low => 1024,
        AiReasoningLevel.Medium => 4096,
        AiReasoningLevel.High => 6144,
        _ => 0
    };

    private string? ValidateConfiguration(AiProviderConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Model)) return "請選擇或輸入模型。";
        if (ProviderType is "azure-openai" or "openai-compatible" &&
            (!Uri.TryCreate(configuration.ApiEndpoint, UriKind.Absolute, out var endpoint) ||
             endpoint.Scheme is not ("http" or "https")))
        {
            return "請輸入有效的 HTTP(S) API Endpoint。";
        }
        if (ProviderType != "openai-compatible" && string.IsNullOrWhiteSpace(configuration.ProtectedApiKey))
        {
            return "請輸入 API Key。";
        }
        return null;
    }

    private string DisplayName() => ProviderType switch
    {
        "openai" => "OpenAI",
        "azure-openai" => "Azure OpenAI",
        "anthropic" => "Anthropic",
        "gemini" => "Gemini",
        "openai-compatible" => "OpenAI Compatible",
        _ => ProviderType
    };

    private string ExtractText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        return ProviderType switch
        {
            "openai" or "azure-openai" => ExtractOpenAiText(root),
            "openai-compatible" => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty,
            "anthropic" => string.Join("", root.GetProperty("content").EnumerateArray()
                .Where(item => item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").GetString())),
            "gemini" => string.Join("", root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")
                .EnumerateArray().Where(item => item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").GetString())),
            _ => string.Empty
        };
    }

    private static string ExtractOpenAiText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText))
        {
            return outputText.GetString() ?? string.Empty;
        }
        if (root.TryGetProperty("choices", out var choices))
        {
            return choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        }
        if (!root.TryGetProperty("output", out var output)) return string.Empty;
        return string.Join("", output.EnumerateArray()
            .Where(item => item.TryGetProperty("content", out _))
            .SelectMany(item => item.GetProperty("content").EnumerateArray())
            .Where(item => item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString()));
    }

    private static bool UsesResponsesApi(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        return normalized.Contains("gpt-5", StringComparison.Ordinal) ||
               normalized.Contains("gpt-4.1", StringComparison.Ordinal);
    }

    private static string AppendPath(string endpoint, string path)
    {
        var normalized = endpoint.TrimEnd('/');
        return normalized.EndsWith('/' + path, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}/{path}";
    }

    private static string BuildPrompt(AiReportRequest request) =>
        "請根據以下工作資料產生工時回報。只輸出 JSON，不要輸出 markdown code fence。" +
        "JSON 必須包含 reportId、workEntryIds、totalHours、body；不得虛構、變更或省略輸入的工時與工作紀錄 ID。\n" +
        $"reportId={request.ReportId}\nworkEntryIds={JsonSerializer.Serialize(request.WorkEntryIds)}\n" +
        $"totalHours={request.TotalHours}\n整理偏好：\n{request.EffectivePrompt.Trim()}\n工作資料：\n{request.InputMarkdown}";

    private static string SummarizeError(string responseBody)
    {
        var normalized = responseBody.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "…";
    }
}
