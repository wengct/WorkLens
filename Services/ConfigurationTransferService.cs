using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class ConfigurationTransferService(IDbContextFactory<WorkLensDbContext> factory)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> ExportAsync(ConfigurationTransferSelection selection, CancellationToken cancellationToken = default)
    {
        if (!selection.HasSelection) throw new ArgumentException("請至少選擇一種設定。", nameof(selection));
        if (selection.GitAuthorEmails && !selection.Sources) throw new ArgumentException("Git 作者 Email 必須與資料來源一併匯出。", nameof(selection));

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var document = new ConfigurationTransferDocument
        {
            ExportedAt = DateTimeOffset.UtcNow,
            Includes = selection
        };
        if (selection.AiSettings)
        {
            document.AiEnabled = (await db.AiFeatureSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == AiFeatureSettings.SingletonId, cancellationToken))?.Enabled ?? false;
            document.AiProviders = await db.AiProviders.AsNoTracking().OrderBy(x => x.Name).Select(x => new TransferAiProvider(
                x.Name, x.IsDefault, x.ProviderType, x.UseHeadless, x.Provider, x.ExecutablePath, x.ApiEndpoint, x.Model, x.ApiVersion, x.ReasoningLevel,
                x.GeneralReportPrompt, x.DailyReportPromptOverride, x.WeeklyReportPromptOverride)).ToListAsync(cancellationToken);
        }
        if (selection.Prompts)
            document.Prompts = await db.PromptTemplates.AsNoTracking().Where(x => !x.IsArchived).OrderBy(x => x.Name)
                .Select(x => new TransferPrompt(x.Name, x.Content, x.IsDefault)).ToListAsync(cancellationToken);
        if (selection.Sources)
        {
            var projects = await db.Projects.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);
            var sources = await db.ActivitySources.AsNoTracking().Where(x => !x.IsArchived).OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
            document.Projects = projects.Select(x => new TransferProject(x.Name, x.Color, x.IncludeInAi)).ToList();
            document.Sources = sources.Select(x => new TransferSource(x.DisplayName, x.SourceType, x.CollectionIntervalMinutes, x.InitialImportDays,
                x.IncludeInAi, projects.FirstOrDefault(p => p.Id == x.ProjectId)?.Name, CleanSourceSettings(x.SettingsJson, selection.GitAuthorEmails))).ToList();
        }
        if (selection.SensitiveWords)
            document.SensitiveWords = await db.SensitiveWords.AsNoTracking().OrderBy(x => x.Value).Select(x => new TransferSensitiveWord(x.Value, x.Enabled)).ToListAsync(cancellationToken);
        if (selection.ScanExclusions)
            document.ScanExclusions = await db.SensitiveScanExclusions.AsNoTracking().OrderBy(x => x.Value).Select(x => x.Value).ToListAsync(cancellationToken);
        return JsonSerializer.Serialize(document, Json);
    }

    public ConfigurationTransferPreview Preview(string content)
    {
        ConfigurationTransferDocument? document;
        try { document = JsonSerializer.Deserialize<ConfigurationTransferDocument>(content, Json); }
        catch (JsonException exception) { return new ConfigurationTransferPreview(null, [$"設定檔 JSON 無法解析：{exception.Message}"], []); }
        if (document is null || document.Format != "worklens-settings") return new ConfigurationTransferPreview(null, ["這不是 WorkLens 設定移轉檔。"], []);
        if (document.SchemaVersion > CurrentSchemaVersion) return new ConfigurationTransferPreview(null, ["設定檔版本較新，請先更新 WorkLens。"], []);
        if (document.SchemaVersion < 1) return new ConfigurationTransferPreview(null, ["設定檔版本不受支援。"], []);
        if (!document.Includes.HasSelection) return new ConfigurationTransferPreview(null, ["設定檔未包含任何設定分類。"], []);
        var errors = Validate(document);
        var notices = new List<string>();
        if (document.Includes.Sources) notices.Add("資料來源匯入後會保持停用，並清除收集 checkpoint 與診斷狀態；請重新驗證本機路徑與環境後再手動啟用。");
        if (document.Includes.AiSettings) notices.Add("AI Provider 匯入後需要重新驗證，AI 報告整理會保持停用；請確認本機路徑、登入狀態與 API Key 後再手動啟用。");
        return new ConfigurationTransferPreview(document, errors, notices);
    }

    public async Task<ConfigurationTransferApplyResult> ApplyAsync(ConfigurationTransferDocument document, IReadOnlyDictionary<string, TransferConflictAction> actions, CancellationToken cancellationToken = default)
    {
        var validation = Validate(document);
        if (validation.Count > 0) return new ConfigurationTransferApplyResult(false, validation, 0);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var applied = 0;
        var projectIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (document.Includes.Sources)
        {
            foreach (var project in document.Projects)
            {
                var existing = await db.Projects.SingleOrDefaultAsync(x => x.Name == project.Name, cancellationToken);
                if (existing is null) { existing = new Project { Name = project.Name.Trim(), Color = project.Color, IncludeInAi = project.IncludeInAi }; db.Projects.Add(existing); applied++; }
                projectIds[project.Name] = existing.Id;
            }
        }
        if (document.Includes.AiSettings)
        {
            var settings = await db.AiFeatureSettings.SingleOrDefaultAsync(x => x.Id == AiFeatureSettings.SingletonId, cancellationToken);
            // A transferred provider can reference paths, credentials, or tools that are not available on this computer.
            // Do not let an import enable AI processing before the user has verified the local environment.
            if (settings is null) db.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = false }); else settings.Enabled = false;
            foreach (var item in document.AiProviders) applied += await ApplyProviderAsync(db, item, actions, cancellationToken);
        }
        if (document.Includes.Prompts) foreach (var item in document.Prompts) applied += await ApplyPromptAsync(db, item, actions, cancellationToken);
        if (document.Includes.Sources) foreach (var item in document.Sources) applied += await ApplySourceAsync(db, item, projectIds, actions, cancellationToken);
        if (document.Includes.SensitiveWords) foreach (var item in document.SensitiveWords) applied += await ApplyWordAsync(db, item, actions, cancellationToken);
        if (document.Includes.ScanExclusions) foreach (var value in document.ScanExclusions) applied += await ApplyExclusionAsync(db, value, actions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConfigurationTransferApplyResult(true, [], applied);
    }

    private static async Task<int> ApplyProviderAsync(WorkLensDbContext db, TransferAiProvider item, IReadOnlyDictionary<string, TransferConflictAction> actions, CancellationToken ct)
    {
        var key = $"ai:{item.Name}"; var existing = await db.AiProviders.SingleOrDefaultAsync(x => x.Name == item.Name, ct);
        var action = actions.GetValueOrDefault(key, existing is null ? TransferConflictAction.Update : TransferConflictAction.Skip);
        if (action == TransferConflictAction.Skip) return 0;
        if (existing is not null && action == TransferConflictAction.Copy) { item = item with { Name = UniqueName(item.Name, db.AiProviders.Select(x => x.Name)) }; existing = null; }
        if (existing is null) { existing = new AiProviderConfiguration { Name = item.Name }; db.AiProviders.Add(existing); }
        if (item.IsDefault) foreach (var other in db.AiProviders.Where(x => x.Id != existing.Id && x.IsDefault)) other.IsDefault = false;
        existing.IsDefault = item.IsDefault; existing.ProviderType = item.ProviderType; existing.UseHeadless = item.UseHeadless; existing.Provider = item.Provider; existing.ExecutablePath = item.ExecutablePath; existing.ApiEndpoint = item.ApiEndpoint; existing.Model = item.Model; existing.ApiVersion = item.ApiVersion; existing.ReasoningLevel = item.ReasoningLevel; existing.GeneralReportPrompt = item.GeneralReportPrompt; existing.DailyReportPromptOverride = item.DailyReportPromptOverride; existing.WeeklyReportPromptOverride = item.WeeklyReportPromptOverride; existing.Status = "NotConfigured"; existing.DetectedVersion = null; existing.LastError = null; existing.LastCheckedAt = null; existing.UpdatedAt = DateTimeOffset.UtcNow;
        return 1;
    }
    private static async Task<int> ApplyPromptAsync(WorkLensDbContext db, TransferPrompt item, IReadOnlyDictionary<string, TransferConflictAction> actions, CancellationToken ct)
    { var key=$"prompt:{item.Name}"; var existing=await db.PromptTemplates.SingleOrDefaultAsync(x=>x.Name==item.Name,ct); var action=actions.GetValueOrDefault(key,existing is null?TransferConflictAction.Update:TransferConflictAction.Skip); if(action==TransferConflictAction.Skip)return 0; if(existing is not null&&action==TransferConflictAction.Copy){item=item with{Name=UniqueName(item.Name,db.PromptTemplates.Select(x=>x.Name))};existing=null;} if(existing is null){existing=new PromptTemplate{Name=item.Name};db.PromptTemplates.Add(existing);} if(item.IsDefault)foreach(var other in db.PromptTemplates.Where(x=>x.Id!=existing.Id&&x.IsDefault))other.IsDefault=false; existing.Content=item.Content;existing.IsDefault=item.IsDefault;existing.IsArchived=false;existing.UpdatedAt=DateTimeOffset.UtcNow;return 1; }
    private static async Task<int> ApplySourceAsync(WorkLensDbContext db, TransferSource item, IReadOnlyDictionary<string, Guid> projects, IReadOnlyDictionary<string, TransferConflictAction> actions, CancellationToken ct)
    { var key=$"source:{item.SourceType}:{item.DisplayName}";var existing=await db.ActivitySources.SingleOrDefaultAsync(x=>x.SourceType==item.SourceType&&x.DisplayName==item.DisplayName&&!x.IsArchived,ct);var action=actions.GetValueOrDefault(key,existing is null?TransferConflictAction.Update:TransferConflictAction.Skip);if(action==TransferConflictAction.Skip)return 0;if(existing is not null&&action==TransferConflictAction.Copy){item=item with{DisplayName=UniqueName(item.DisplayName,db.ActivitySources.Where(x=>x.SourceType==item.SourceType).Select(x=>x.DisplayName))};existing=null;}if(existing is null){existing=new ActivitySource{DisplayName=item.DisplayName,SourceType=item.SourceType};db.ActivitySources.Add(existing);}existing.CollectionIntervalMinutes=Math.Clamp(item.CollectionIntervalMinutes,1,1440);existing.InitialImportDays=Math.Clamp(item.InitialImportDays,1,90);existing.IncludeInAi=item.IncludeInAi;existing.ProjectId=item.ProjectName is not null&&projects.TryGetValue(item.ProjectName,out var projectId)?projectId:null;existing.SettingsJson=item.SettingsJson;existing.Enabled=false;existing.HealthStatus=SourceHealthStatus.Disabled;existing.CheckpointJson="{}";existing.LastError=null;existing.LastSuccessAt=null;existing.UpdatedAt=DateTimeOffset.UtcNow;return 1; }
    private static async Task<int> ApplyWordAsync(WorkLensDbContext db, TransferSensitiveWord item, IReadOnlyDictionary<string, TransferConflictAction> actions, CancellationToken ct)
    { var key=$"word:{item.Value}";var existing=await db.SensitiveWords.SingleOrDefaultAsync(x=>x.Value==item.Value,ct);if(actions.GetValueOrDefault(key,existing is null?TransferConflictAction.Update:TransferConflictAction.Skip)==TransferConflictAction.Skip)return 0;if(existing is null){db.SensitiveWords.Add(new SensitiveWord{Value=item.Value,Enabled=item.Enabled});}else existing.Enabled=item.Enabled;return 1; }
    private static async Task<int> ApplyExclusionAsync(WorkLensDbContext db, string value, IReadOnlyDictionary<string, TransferConflictAction> actions, CancellationToken ct)
    { var key=$"exclusion:{value}";var existing=await db.SensitiveScanExclusions.SingleOrDefaultAsync(x=>x.Value==value,ct);if(actions.GetValueOrDefault(key,existing is null?TransferConflictAction.Update:TransferConflictAction.Skip)==TransferConflictAction.Skip)return 0;if(existing is null)db.SensitiveScanExclusions.Add(new SensitiveScanExclusion{Value=value});return 1; }
    private static List<string> Validate(ConfigurationTransferDocument document)
    { var errors=new List<string>(); if(document.Includes.GitAuthorEmails&&!document.Includes.Sources)errors.Add("Git 作者 Email 必須與資料來源一併匯入。"); if(document.AiProviders.Any(x=>string.IsNullOrWhiteSpace(x.Name)||string.IsNullOrWhiteSpace(x.GeneralReportPrompt)))errors.Add("AI 設定缺少名稱或整理指令。"); if(document.Prompts.Any(x=>string.IsNullOrWhiteSpace(x.Name)||string.IsNullOrWhiteSpace(x.Content)))errors.Add("Prompt 範本缺少名稱或內容。"); if(document.Sources.Any(x=>string.IsNullOrWhiteSpace(x.DisplayName)||!Enum.IsDefined(x.SourceType)))errors.Add("資料來源包含無效名稱或類型。"); return errors; }
    private static string CleanSourceSettings(string value, bool includeEmails)
    { if(includeEmails)return value; try{var node=JsonNode.Parse(value)?.AsObject();node?.Remove("authorEmails");return node?.ToJsonString()??"{}";}catch(JsonException){return "{}";} }
    private static string UniqueName(string name, IEnumerable<string> existing) { var names=existing.ToHashSet(StringComparer.OrdinalIgnoreCase); var candidate=$"{name}（匯入）";var index=2;while(names.Contains(candidate))candidate=$"{name}（匯入 {index++}）";return candidate; }
}

public sealed record ConfigurationTransferSelection(bool AiSettings = false, bool Prompts = false, bool Sources = false, bool GitAuthorEmails = false, bool SensitiveWords = false, bool ScanExclusions = false) { public bool HasSelection => AiSettings || Prompts || Sources || SensitiveWords || ScanExclusions; }
public enum TransferConflictAction { Skip, Update, Copy }
public sealed record ConfigurationTransferPreview(ConfigurationTransferDocument? Document, IReadOnlyList<string> Errors, IReadOnlyList<string> Notices) { public bool IsValid => Document is not null && Errors.Count == 0; }
public sealed record ConfigurationTransferApplyResult(bool Succeeded, IReadOnlyList<string> Errors, int AppliedCount);
public sealed class ConfigurationTransferDocument { public string Format { get; init; } = "worklens-settings"; public int SchemaVersion { get; init; } = ConfigurationTransferService.CurrentSchemaVersion; public DateTimeOffset ExportedAt { get; init; } public ConfigurationTransferSelection Includes { get; init; } = new(); public bool AiEnabled { get; set; } public List<TransferAiProvider> AiProviders { get; set; } = []; public List<TransferPrompt> Prompts { get; set; } = []; public List<TransferProject> Projects { get; set; } = []; public List<TransferSource> Sources { get; set; } = []; public List<TransferSensitiveWord> SensitiveWords { get; set; } = []; public List<string> ScanExclusions { get; set; } = []; }
public sealed record TransferAiProvider(string Name,bool IsDefault,string ProviderType,bool UseHeadless,string Provider,string? ExecutablePath,string? ApiEndpoint,string? Model,string? ApiVersion,AiReasoningLevel ReasoningLevel,string GeneralReportPrompt,string DailyReportPromptOverride,string WeeklyReportPromptOverride);
public sealed record TransferPrompt(string Name,string Content,bool IsDefault);
public sealed record TransferProject(string Name,string Color,bool IncludeInAi);
public sealed record TransferSource(string DisplayName,ActivitySourceType SourceType,int CollectionIntervalMinutes,int InitialImportDays,bool IncludeInAi,string? ProjectName,string SettingsJson);
public sealed record TransferSensitiveWord(string Value,bool Enabled);
