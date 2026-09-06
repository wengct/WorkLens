using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class SensitiveWordService(IDbContextFactory<WorkLensDbContext> factory)
{
    public async Task<IReadOnlyList<SensitiveWord>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.SensitiveWords
            .AsNoTracking()
            .OrderBy(x => x.Value)
            .ToListAsync(cancellationToken);
    }

    public async Task<SensitiveWord> SaveAsync(
        SensitiveWord word,
        CancellationToken cancellationToken = default)
    {
        var value = word.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("自訂敏感詞不可空白。", nameof(word));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var duplicate = await db.SensitiveWords.AnyAsync(
            x => x.Id != word.Id && x.Value == value,
            cancellationToken);
        if (duplicate)
        {
            throw new ArgumentException("自訂敏感詞不可重複。", nameof(word));
        }

        var existing = await db.SensitiveWords.SingleOrDefaultAsync(x => x.Id == word.Id, cancellationToken);
        if (existing is null)
        {
            word.Value = value;
            word.CreatedAt = DateTimeOffset.UtcNow;
            word.UpdatedAt = word.CreatedAt;
            db.SensitiveWords.Add(word);
        }
        else
        {
            existing.Value = value;
            existing.Enabled = word.Enabled;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing ?? word;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.SensitiveWords.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (existing is null)
        {
            return;
        }

        db.SensitiveWords.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}
