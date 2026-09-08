using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using WorkLens;
using WorkLens.Components;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Logging;
using WorkLens.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
var startupHealth = new StartupHealth();
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5077");
}

var defaultRoot = AppPaths.GetDefaultRoot();
var configuredConnection = builder.Configuration.GetConnectionString("WorkLens");
configuredConnection = string.IsNullOrWhiteSpace(configuredConnection)
    ? $"Data Source={Path.Combine(defaultRoot, "data", "worklens.db")}"
    : configuredConnection;
var configuredDatabasePath = configuredConnection.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
    ? configuredConnection["Data Source=".Length..]
    : configuredConnection;
var databasePath = AppPaths.ExpandPath(configuredDatabasePath);
var backupPath = AppPaths.ExpandPath(
    builder.Configuration["WorkLens:BackupPath"]
    ?? Path.Combine(defaultRoot, "backups"));
var logPath = AppPaths.ExpandPath(
    builder.Configuration["WorkLens:LogPath"]
    ?? Path.Combine(defaultRoot, "logs"));

var paths = new AppPaths(databasePath, backupPath, logPath);
Exception? pathSetupException = null;
try
{
    paths.EnsureDirectories();
}
catch (Exception exception) when (
    exception is IOException or
    UnauthorizedAccessException or
    ArgumentException or
    NotSupportedException)
{
    pathSetupException = exception;
    Console.Error.WriteLine($"WorkLens runtime directory setup failed: {exception.Message}");
}

var dataProtectionPath = Path.Combine(paths.DataDirectory, "keys");
try
{
    Directory.CreateDirectory(dataProtectionPath);
}
catch (Exception exception) when (
    exception is IOException or
    UnauthorizedAccessException or
    ArgumentException or
    NotSupportedException)
{
    pathSetupException ??= exception;
    Console.Error.WriteLine($"WorkLens data-protection directory setup failed: {exception.Message}");
}

builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
builder.Logging.AddConsole();
builder.Logging.AddLocalFile(paths.LogDirectory);
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("WorkLens");
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<RuntimeSettingsService>();
builder.Services.AddSingleton<FolderPickerService>();
builder.Services.AddSingleton(startupHealth);
builder.Services.AddDbContextFactory<WorkLensDbContext>(options =>
    options.UseSqlite($"Data Source={paths.DatabasePath}"));
builder.Services.AddSingleton<DatabaseInitializer>();

builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<IProcessRunner>(serviceProvider =>
    serviceProvider.GetRequiredService<ProcessRunner>());
builder.Services.AddSingleton<IAiContentSanitizer>(serviceProvider =>
    new SensitiveContentSanitizer(
        paths,
        serviceProvider.GetRequiredService<IProcessRunner>(),
        serviceProvider.GetRequiredService<IDbContextFactory<WorkLensDbContext>>(),
        serviceProvider.GetRequiredService<ILogger<SensitiveContentSanitizer>>()));
builder.Services.AddSingleton<AzureDevOpsCliService>();
builder.Services.AddSingleton<AskBridgeService>();
builder.Services.AddSingleton<IAiSecretProtector, AiSecretProtector>();
builder.Services.AddHttpClient("AiProvider", client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("GitHubReleases", client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("WorkLens-Update-Checker");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<ReleaseUpdateService>();
builder.Services.AddSingleton<IAiProviderAdapter>(serviceProvider =>
    serviceProvider.GetRequiredService<AskBridgeService>());
foreach (var providerType in new[] { "openai", "azure-openai", "anthropic", "gemini", "openai-compatible" })
{
    builder.Services.AddSingleton<IAiProviderAdapter>(serviceProvider =>
        new ApiAiProviderAdapter(
            providerType,
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("AiProvider"),
            serviceProvider.GetRequiredService<IAiSecretProtector>()));
}
builder.Services.AddSingleton<AiProviderRegistry>();
builder.Services.AddSingleton<AiProviderOrchestrator>();
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new GitSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WindowsGit));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new GitSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WslGit));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new GitSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.MacOsGit));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new CodexSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WindowsCodex));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new CodexSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WslCodex));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new CodexSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.MacOsCodex));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new ClaudeCodeSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WindowsClaudeCode));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new ClaudeCodeSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WslClaudeCode));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new ClaudeCodeSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.MacOsClaudeCode));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new CopilotSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WindowsCopilot));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new CopilotSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WslCopilot));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new CopilotSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.MacOsCopilot));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new VsCodeCopilotSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WindowsVsCodeCopilot));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new VsCodeCopilotSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.WslVsCodeCopilot));
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new VsCodeCopilotSourceAdapter(
        serviceProvider.GetRequiredService<ProcessRunner>(),
        ActivitySourceType.MacOsVsCodeCopilot));
builder.Services.AddSingleton<IActivitySourceAdapter, VisualStudioCopilotSourceAdapter>();
builder.Services.AddSingleton<IActivitySourceAdapter>(serviceProvider =>
    new AzureDevOpsPullRequestSourceAdapter(
        serviceProvider.GetRequiredService<AzureDevOpsCliService>()));
builder.Services.AddSingleton<SourceRegistry>();
builder.Services.AddSingleton<SourceOrchestrator>();
builder.Services.AddSingleton<ReportInvalidationService>();

builder.Services.AddScoped<WorkLogService>();
builder.Services.AddScoped<WorkDraftService>();
builder.Services.AddScoped<SourceConfigurationService>();
builder.Services.AddScoped<ActivityQueryService>();
builder.Services.AddScoped<ManualSourceService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<AiConfigurationService>();
builder.Services.AddScoped<SensitiveWordService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<PromptTemplateService>();
builder.Services.AddScoped<ScheduleRunner>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<ToastService>();

builder.Services.AddHostedService<SourceCollectionHostedService>();
builder.Services.AddHostedService<ReportScheduleHostedService>();

var app = builder.Build();

if (pathSetupException is not null)
{
    app.Logger.LogCritical(pathSetupException, "WorkLens 本機執行目錄初始化失敗；應用程式將繼續啟動並保留錯誤頁。");
}

try
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    if (pathSetupException is null)
    {
        startupHealth.MarkHealthy();
    }
}
catch (Exception exception)
{
    startupHealth.MarkUnhealthy();
    app.Logger.LogCritical(
        exception,
        "WorkLens 資料庫初始化失敗；應用程式將繼續啟動，請檢查本機資料目錄與權限。");
}

app.UseExceptionHandler("/error");
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/healthz", (StartupHealth health) =>
{
    var payload = new { status = health.IsHealthy ? "Healthy" : "Unhealthy", version = health.Version };
    return Results.Json(payload, statusCode: health.IsHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/reports/{id:guid}/markdown", async (Guid id, IDbContextFactory<WorkLensDbContext> factory) =>
{
    await using var db = await factory.CreateDbContextAsync();
    var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    return report is null
        ? Results.NotFound()
        : Results.Text(report.Body, "text/markdown; charset=utf-8");
});

app.MapGet("/reports/{id:guid}/csv", async (Guid id, ReportService reports) =>
{
    var csv = await reports.ExportCsvAsync(id);
    return csv is null
        ? Results.NotFound()
        : Results.Text(csv, "text/csv; charset=utf-8");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

try
{
    app.Run();
}
catch (Exception exception)
{
    app.Logger.LogCritical(exception, "WorkLens 主機執行失敗；詳細資訊已寫入本機 log。");
    Environment.ExitCode = 1;
}

public partial class Program;
