namespace WorkLens.Domain;

public static class ContentTitle
{
    public static string Normalize(string? title)
    {
        var normalized = title?.Trim() ?? string.Empty;
        return normalized.Length <= 100 ? normalized : normalized[..100] + "…";
    }

    public static string Resolve(string? title, string content, string fallback = "未命名紀錄")
    {
        var candidate = string.IsNullOrWhiteSpace(title)
            ? content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault()
            : title;

        var resolved = (candidate ?? fallback).Trim().TrimStart('#', '-', '*', ' ');
        if (resolved.Length == 0)
        {
            resolved = fallback;
        }

        return resolved.Length <= 100 ? resolved : resolved[..100] + "…";
    }
}
