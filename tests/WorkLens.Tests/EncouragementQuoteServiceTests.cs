using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class EncouragementQuoteServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"worklens-quotes-{Guid.NewGuid():N}");

    private EncouragementQuoteService Create(string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "quotes.json");
        File.WriteAllText(path, content);
        return new EncouragementQuoteService(path);
    }

    [Fact]
    public void Quotes_are_loaded_once_and_never_repeat_the_previous_id()
    {
        var service = Create("""
            [{"id":"a","category":"休息","text":"休息一下。"},
             {"id":"b","category":"鼓勵","text":"慢慢來。"}]
            """);
        Assert.True(service.CanChange);
        File.WriteAllText(Path.Combine(directory, "quotes.json"), "[]");
        var previous = "a";
        for (var i = 0; i < 100; i++)
        {
            var quote = service.Next(previous);
            Assert.NotEqual(previous, quote.Id);
            Assert.Contains(quote.Id, new[] { "a", "b" });
            previous = quote.Id;
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("invalid json")]
    [InlineData("[null, {}, {\"id\":\"a\",\"category\":\"休息\",\"text\":\" \"}]")]
    public void Invalid_or_empty_catalog_uses_a_single_fallback(string content)
    {
        var service = Create(content);
        Assert.False(service.CanChange);
        Assert.Equal("fallback", service.Next().Id);
        Assert.False(string.IsNullOrWhiteSpace(service.Next("fallback").Text));
    }

    [Fact]
    public void Missing_file_uses_fallback_without_creating_a_file()
    {
        var service = new EncouragementQuoteService(Path.Combine(directory, "missing.json"));
        Assert.Equal("fallback", service.Next().Id);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Duplicate_ids_and_invalid_entries_do_not_create_a_false_change_option()
    {
        var service = Create("""
            [null, {}, {"id":"a","category":"鼓勵","text":"第一句"},
             {"id":"a","category":"鼓勵","text":"重複 ID"}]
            """);
        Assert.False(service.CanChange);
        Assert.Equal("第一句", service.Next("a").Text);
    }

    [Fact]
    public void Bundled_catalog_is_copied_to_the_output_and_contains_the_work_quotes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "encouragement-quotes.json");
        Assert.True(File.Exists(path));
        var entries = System.Text.Json.JsonSerializer.Deserialize<WorkLens.Domain.EncouragementQuote[]>(
            File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(100, entries.Length);
        Assert.Equal(100, entries.Select(quote => quote.Id).Distinct().Count());
        Assert.Equal(100, entries.Select(quote => quote.Text).Distinct().Count());
        Assert.All(entries, quote => Assert.False(string.IsNullOrWhiteSpace(quote.Text)));
        Assert.True(new EncouragementQuoteService(path).CanChange);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
