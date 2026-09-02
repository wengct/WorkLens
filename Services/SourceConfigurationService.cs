using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class SourceConfigurationService(
    IDbContextFactory<WorkLensDbContext> factory,
    ReportInvalidationService invalidation)
{
    public async Task<IReadOnlyList<ActivitySource>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ActivitySources.AsNoTracking()
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<ActivitySource> SaveAsync(ActivitySource source, CancellationToken cancellationToken = default)
    {
        source.DisplayName = source.DisplayName.Trim();
        source.CollectionIntervalMinutes = Math.Clamp(source.CollectionIntervalMinutes, 1, 1440);
        source.InitialImportDays = Math.Clamp(source.InitialImportDays, 1, 90);
        source.SettingsJson = string.IsNullOrWhiteSpace(source.SettingsJson) ? "{}" : source.SettingsJson;
        if (source.SourceType is ActivitySourceType.WindowsGit or ActivitySourceType.WslGit)
        {
            var gitSettings = SourceSettingsSerializer.DeserializeGit(source.SettingsJson);
            gitSettings.AuthorEmails = SourceSettingsSerializer
                .NormalizeAuthorEmails(gitSettings.AuthorEmails)
                .ToList();
            if (gitSettings.AuthorEmails.Count == 0)
            {
                throw new ArgumentException(
                    "Git 來源至少需要設定一組自己的作者 email。",
                    nameof(source));
            }

            if (gitSettings.AuthorEmails.Any(email => !SourceSettingsSerializer.IsValidAuthorEmail(email)))
            {
                throw new ArgumentException(
                    "Git 來源包含格式無效的作者 email。",
                    nameof(source));
            }

            source.SettingsJson = SourceSettingsSerializer.Serialize(gitSettings);
        }
        else if (source.SourceType is ActivitySourceType.WindowsCodex or ActivitySourceType.WslCodex)
        {
            var codexSettings = SourceSettingsSerializer.DeserializeCodex(source.SettingsJson);
            codexSettings.CodexHome = string.IsNullOrWhiteSpace(codexSettings.CodexHome)
                ? null
                : codexSettings.CodexHome.Trim();
            codexSettings.Distro = string.IsNullOrWhiteSpace(codexSettings.Distro)
                ? null
                : codexSettings.Distro.Trim();
            if (source.SourceType == ActivitySourceType.WslCodex && codexSettings.Distro is null)
            {
                throw new ArgumentException("WSL Codex 來源必須指定 Linux 環境名稱，例如 Ubuntu。", nameof(source));
            }

            if (source.SourceType == ActivitySourceType.WindowsCodex)
            {
                codexSettings.Distro = null;
            }

            source.SettingsJson = SourceSettingsSerializer.Serialize(codexSettings);
        }
        source.UpdatedAt = DateTimeOffset.UtcNow;
        source.HealthStatus = source.Enabled ? SourceHealthStatus.Unavailable : SourceHealthStatus.Disabled;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == source.Id, cancellationToken);
        if (existing is null)
        {
            db.ActivitySources.Add(source);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(source);
        }

        await db.SaveChangesAsync(cancellationToken);
        return source;
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (source is null)
        {
            return;
        }

        source.Enabled = enabled;
        source.HealthStatus = enabled ? SourceHealthStatus.Unavailable : SourceHealthStatus.Disabled;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetIncludeInAiAsync(
        Guid id,
        bool includeInAi,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (source is null)
        {
            return;
        }

        source.IncludeInAi = includeInAi;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (source is null)
        {
            return;
        }

        source.Enabled = false;
        source.IsArchived = true;
        source.HealthStatus = SourceHealthStatus.Disabled;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> GetProjectsAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Projects.AsNoTracking()
            .Where(x => includeArchived || !x.IsArchived)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Project?> CreateProjectAsync(
        string name,
        string color = "#6d7cff",
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existingNames = await db.Projects
            .Where(x => !x.IsArchived)
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);
        if (existingNames.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var project = new Project
        {
            Name = name,
            Color = string.IsNullOrWhiteSpace(color) ? "#6d7cff" : color.Trim()
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task SetProjectAiAsync(
        Guid projectId,
        bool includeInAi,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        project.IncludeInAi = includeInAi;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Project?> UpdateProjectAsync(
        Guid id,
        string name,
        string color,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (project is null || await db.Projects.AnyAsync(x => x.Id != id && !x.IsArchived && x.Name == name, cancellationToken))
        {
            return null;
        }

        var changed = !string.Equals(project.Name, name, StringComparison.Ordinal);
        project.Name = name;
        project.Color = string.IsNullOrWhiteSpace(color) ? "#6d7cff" : color.Trim();
        await db.SaveChangesAsync(cancellationToken);
        if (changed)
        {
            await invalidation.MarkProjectReportsStaleAsync(id, cancellationToken);
        }
        return project;
    }

    public async Task SetProjectArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (project is null)
        {
            return;
        }

        project.IsArchived = archived;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetCheckpointAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return;
        }

        source.CheckpointJson = "{}";
        source.LastSuccessAt = null;
        source.HealthStatus = !source.Enabled
            ? SourceHealthStatus.Disabled
            : source.HealthStatus == SourceHealthStatus.Ready
                ? SourceHealthStatus.Ready
                : SourceHealthStatus.Unavailable;
        source.LastError = null;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
