using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class AiProviderOrchestrator(
    AiProviderRegistry registry,
    IAiContentSanitizer sanitizer)
{
    public Task<AiProviderValidationResult> ValidateAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        registry.Get(configuration).ValidateAsync(configuration, cancellationToken);

    public async Task<AiReportResult> GenerateAsync(
        AiProviderConfiguration configuration,
        AiReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(request, cancellationToken);
        if (!prepared.Succeeded)
        {
            return new AiReportResult(false, null, null, prepared.Summary.Error, prepared.Summary);
        }

        return await GeneratePreparedAsync(configuration, prepared.PreparedRequest!, cancellationToken);
    }

    public Task<AiSanitizationResult> PrepareAsync(
        AiReportRequest request,
        CancellationToken cancellationToken = default) =>
        sanitizer.PrepareAsync(request, cancellationToken);

    public async Task<AiReportResult> GeneratePreparedAsync(
        AiProviderConfiguration configuration,
        AiPreparedRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Sanitization.IsReady)
        {
            return new AiReportResult(
                false,
                null,
                null,
                "AI 請求未通過機敏資訊檢查，未傳送。",
                request.Sanitization);
        }

        var result = await registry.Get(configuration).GenerateAsync(configuration, request, cancellationToken);
        return result with { Sanitization = request.Sanitization };
    }

    public Task<AiConnectionTestResult> TestConnectionAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        registry.Get(configuration).TestConnectionAsync(configuration, cancellationToken);

    public Task<AiReportResult> TestPromptAsync(
        AiProviderConfiguration configuration,
        string effectivePrompt,
        CancellationToken cancellationToken = default)
    {
        var reportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var entryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        const string sample = "# 每日工作回報 範例\n\n確認工時：1 小時\n\n## 工作項目\n- 完成範例功能與測試。";
        return GenerateAsync(
            configuration,
            new AiReportRequest(
                reportId,
                ResolveTarget(configuration),
                sample,
                [entryId],
                1,
                configuration.ExecutablePath,
                effectivePrompt),
            cancellationToken);
    }

    private static string ResolveTarget(AiProviderConfiguration configuration) =>
        configuration.ProviderType == "ask-bridge"
            ? configuration.Provider
            : configuration.Model ?? configuration.ProviderType;
}
