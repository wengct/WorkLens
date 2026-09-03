using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class PromptTemplateService(IDbContextFactory<WorkLensDbContext> factory)
{
    public async Task<IReadOnlyList<PromptTemplate>> GetAllAsync(
        bool includeArchived = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.PromptTemplates.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(x => !x.IsArchived);
        }

        return await query
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.IsArchived)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<PromptTemplate> GetEffectiveAsync(
        Guid? templateId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        PromptTemplate? template = null;
        if (templateId is Guid id)
        {
            template = await db.PromptTemplates.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        }

        template ??= await db.PromptTemplates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IsDefault && !x.IsArchived, cancellationToken);
        return template ?? throw new InvalidOperationException("找不到可用的預設 Prompt 範本。");
    }

    public async Task<PromptTemplate> SaveAsync(
        PromptTemplate template,
        CancellationToken cancellationToken = default)
    {
        template.Name = template.Name?.Trim() ?? string.Empty;
        template.Content = template.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            throw new ArgumentException("Prompt 名稱不可空白。", nameof(template));
        }
        if (string.IsNullOrWhiteSpace(template.Content))
        {
            throw new ArgumentException("Prompt 內容不可空白。", nameof(template));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var duplicate = await db.PromptTemplates.AnyAsync(
            x => x.Id != template.Id && x.Name.ToLower() == template.Name.ToLower(),
            cancellationToken);
        if (duplicate)
        {
            throw new ArgumentException("Prompt 名稱不可重複。", nameof(template));
        }

        var existing = await db.PromptTemplates.SingleOrDefaultAsync(x => x.Id == template.Id, cancellationToken);
        if (existing is null)
        {
            template.IsDefault = false;
            template.CreatedAt = DateTimeOffset.UtcNow;
            template.UpdatedAt = template.CreatedAt;
            db.PromptTemplates.Add(template);
            existing = template;
        }
        else
        {
            existing.Name = template.Name;
            existing.Content = template.Content;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var selected = await db.PromptTemplates.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ArgumentException("找不到 Prompt 範本。", nameof(id));
        if (selected.IsArchived)
        {
            throw new InvalidOperationException("封存的 Prompt 不可設為預設。");
        }

        var currentDefaults = await db.PromptTemplates.Where(x => x.IsDefault && x.Id != id).ToListAsync(cancellationToken);
        foreach (var template in currentDefaults)
        {
            template.IsDefault = false;
            template.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (currentDefaults.Count > 0) await db.SaveChangesAsync(cancellationToken);
        selected.IsDefault = true;
        selected.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var template = await db.PromptTemplates.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ArgumentException("找不到 Prompt 範本。", nameof(id));
        if (archived && template.IsDefault)
        {
            throw new InvalidOperationException("預設 Prompt 不可封存；請先設定另一個預設範本。");
        }

        template.IsArchived = archived;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
