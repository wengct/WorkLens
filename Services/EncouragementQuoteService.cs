using System.Text.Json;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class EncouragementQuoteService(string path)
{
    private static readonly EncouragementQuote Fallback = new(
        "fallback", "鼓勵", "今天辛苦了，也記得把溫柔留一點給自己。");
    private readonly Lazy<EncouragementQuote[]> quotes = new(() => Load(path));

    public bool CanChange => quotes.Value.Length > 1;

    public EncouragementQuote Next(string? previousId = null)
    {
        var available = quotes.Value;
        var previous = Array.FindIndex(available, quote => quote.Id == previousId);
        if (available.Length == 1) return available[0];

        var index = Random.Shared.Next(available.Length - (previous >= 0 ? 1 : 0));
        if (previous >= 0 && index >= previous) index++;
        return available[index];
    }

    private static EncouragementQuote[] Load(string path)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<EncouragementQuote?[]>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var valid = (entries ?? [])
                .OfType<EncouragementQuote>()
                .Where(quote => !string.IsNullOrWhiteSpace(quote.Id)
                    && !string.IsNullOrWhiteSpace(quote.Category)
                    && !string.IsNullOrWhiteSpace(quote.Text))
                .DistinctBy(quote => quote.Id)
                .ToArray();
            return valid.Length > 0 ? valid : [Fallback];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [Fallback];
        }
    }
}
