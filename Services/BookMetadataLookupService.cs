#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BiblioText.Services;

internal interface IBookMetadataLookupService
{
    Task<IReadOnlyList<BookMetadataSource>> LookupAsync(
        string title,
        string? author,
        CancellationToken ct = default);
}

internal sealed class OpenLibraryBookMetadataLookupService : IBookMetadataLookupService
{
    private static readonly Uri SearchEndpoint = new("https://openlibrary.org/search.json");
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<IReadOnlyList<BookMetadataSource>> LookupAsync(
        string title,
        string? author,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var query = $"title={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrWhiteSpace(author))
        {
            query += $"&author={Uri.EscapeDataString(author)}";
        }

        var uri = new Uri($"{SearchEndpoint}?{query}&limit=3");
        using var response = await _httpClient.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync<OpenLibrarySearchResponse>(stream, cancellationToken: ct);
        if (result?.Docs == null || result.Docs.Count == 0)
        {
            return [];
        }

        return result.Docs
            .Where(doc => !string.IsNullOrWhiteSpace(doc.Title))
            .Select(doc => CreateSource(doc, title, author))
            .Where(source => source != null)
            .Cast<BookMetadataSource>()
            .ToList();
    }

    private static BookMetadataSource? CreateSource(OpenLibraryDoc doc, string requestedTitle, string? requestedAuthor)
    {
        if (string.IsNullOrWhiteSpace(doc.Title))
        {
            return null;
        }

        var authors = doc.AuthorName is { Count: > 0 }
            ? string.Join(", ", doc.AuthorName.Take(3))
            : null;
        var firstPublishYear = doc.FirstPublishYear?.ToString();
        var subjects = doc.Subject is { Count: > 0 }
            ? string.Join(", ", doc.Subject.Take(6))
            : null;
        var snippetParts = new List<string>
        {
            $"Title: {doc.Title}"
        };

        if (!string.IsNullOrWhiteSpace(authors))
        {
            snippetParts.Add($"Author: {authors}");
        }

        if (!string.IsNullOrWhiteSpace(firstPublishYear))
        {
            snippetParts.Add($"First published: {firstPublishYear}");
        }

        if (!string.IsNullOrWhiteSpace(subjects))
        {
            snippetParts.Add($"Subjects: {subjects}");
        }

        var workKey = doc.Key?.Trim('/');
        var url = string.IsNullOrWhiteSpace(workKey)
            ? "https://openlibrary.org/search"
            : $"https://openlibrary.org/{workKey}";

        return new BookMetadataSource
        {
            Provider = "Open Library",
            Title = doc.Title,
            Url = url,
            Snippet = string.Join(". ", snippetParts),
            MatchScore = EstimateMatchScore(requestedTitle, requestedAuthor, doc.Title, authors)
        };
    }

    private static double EstimateMatchScore(string requestedTitle, string? requestedAuthor, string foundTitle, string? foundAuthor)
    {
        var score = Normalize(requestedTitle) == Normalize(foundTitle) ? 0.75 : 0.45;
        if (!string.IsNullOrWhiteSpace(requestedAuthor) &&
            !string.IsNullOrWhiteSpace(foundAuthor) &&
            Normalize(foundAuthor).Contains(Normalize(requestedAuthor), StringComparison.Ordinal))
        {
            score += 0.2;
        }

        return Math.Min(score, 0.95);
    }

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

internal sealed class BookMetadataSource
{
    public required string Provider { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Snippet { get; init; }
    public double MatchScore { get; init; }
}

internal sealed class OpenLibrarySearchResponse
{
    [JsonPropertyName("docs")]
    public List<OpenLibraryDoc>? Docs { get; set; }
}

internal sealed class OpenLibraryDoc
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("author_name")]
    public List<string>? AuthorName { get; set; }

    [JsonPropertyName("first_publish_year")]
    public int? FirstPublishYear { get; set; }

    [JsonPropertyName("subject")]
    public List<string>? Subject { get; set; }
}
