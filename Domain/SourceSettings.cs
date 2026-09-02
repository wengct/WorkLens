using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkLens.Domain;

public sealed class GitSourceSettings
{
    public List<string> RepositoryPaths { get; set; } = [];
    public List<string> AuthorEmails { get; set; } = [];
    public string? Distro { get; set; }
}

public sealed class CodexSourceSettings
{
    public string? CodexHome { get; set; }
    public string? Distro { get; set; }
}

public sealed class CodexSessionMetadata
{
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Cwd { get; set; }
    public string Platform { get; set; } = string.Empty;
    public bool Archived { get; set; }
    public string? CliVersion { get; set; }
    public int AttachmentCount { get; set; }
    public List<CodexSessionMessage> Messages { get; set; } = [];
}

public sealed class CodexSessionMessage
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Text { get; set; } = string.Empty;
}

public static class SourceSettingsSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static GitSourceSettings DeserializeGit(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GitSourceSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<GitSourceSettings>(json, Options)
                ?? new GitSourceSettings();
            settings.RepositoryPaths ??= [];
            settings.AuthorEmails ??= [];
            return settings;
        }
        catch (JsonException)
        {
            return new GitSourceSettings();
        }
    }

    public static string Serialize(GitSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static CodexSourceSettings DeserializeCodex(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CodexSourceSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<CodexSourceSettings>(json, Options)
                ?? new CodexSourceSettings();
        }
        catch (JsonException)
        {
            return new CodexSourceSettings();
        }
    }

    public static string Serialize(CodexSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static string SerializeMetadata(CodexSessionMetadata metadata) =>
        JsonSerializer.Serialize(metadata, Options);

    public static CodexSessionMetadata? DeserializeCodexMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CodexSessionMetadata>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> NormalizeAuthorEmails(IEnumerable<string>? emails) =>
        (emails ?? Enumerable.Empty<string>())
            .Select(email => email.Trim())
            .Where(email => email.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsValidAuthorEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var value = email.Trim();
        var at = value.IndexOf('@');
        return value.Length <= 320 &&
               !value.Any(char.IsWhiteSpace) &&
               at > 0 &&
               at == value.LastIndexOf('@') &&
               at < value.Length - 1;
    }
}
