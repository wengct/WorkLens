using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class SourceCollectionHostedService(
    IDbContextFactory<WorkLensDbContext> factory,
    SourceOrchestrator orchestrator,
    ILogger<SourceCollectionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await CollectDueSourcesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "資料來源排程發生未預期錯誤");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "資料來源背景服務已停止，但 WorkLens 將繼續提供網頁功能");
        }
    }

    private async Task CollectDueSourcesAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var sources = await db.ActivitySources.AsNoTracking()
            .Where(x => x.Enabled && x.HealthStatus == SourceHealthStatus.Ready && !x.IsArchived)
            .ToListAsync(cancellationToken);

        foreach (var source in sources)
        {
            var due = source.LastSuccessAt is null ||
                      source.LastSuccessAt.Value.AddMinutes(Math.Max(1, source.CollectionIntervalMinutes)) <= now;
            if (due)
            {
                await orchestrator.CollectAsync(source.Id, cancellationToken);
            }
        }
    }
}

public sealed class ReportScheduleHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReportScheduleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunSchedulesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "報告排程發生未預期錯誤");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "報告背景服務已停止，但 WorkLens 將繼續提供網頁功能");
        }
    }

    private async Task RunSchedulesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
        await runner.RunDueAsync(DateTime.Now, cancellationToken);
    }
}
