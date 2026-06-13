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

    // Limit the search payload to fields we actually use. Without this, Open Library returns
    // hundreds of edition_key / ia ids per doc, which dominates the response size.
    private const string SearchFields =
        "key,title,subtitle,author_name,first_publish_year," +
        "number_of_pages_median,publisher,isbn,cover_i," +
        "ratings_average,ratings_count,subject,first_sentence,edition_count";

    private const int MaxSubjects = 6;
    private const int MaxPublishers = 2;
    private const int MaxAuthors = 3;

    private readonly HttpClient _httpClient;

    public OpenLibraryBookMetadataLookupService()
        : this(CreateDefaultClient())
    {
    }

    internal OpenLibraryBookMetadataLookupService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BiblioText/1.0 (+https://github.com/vondudio/BiblioText)");
        return client;
    }

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

        var uri = new Uri($"{SearchEndpoint}?{query}&limit=3&fields={SearchFields}");
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

        var sources = result.Docs
            .Where(doc => !string.IsNullOrWhiteSpace(doc.Title))
            .Select(doc => CreateSource(doc, title, author))
            .Where(source => source != null)
            .Cast<BookMetadataSource>()
            .OrderByDescending(s => s.MatchScore)
            .ToList();

        return sources;
    }

    private static BookMetadataSource? CreateSource(OpenLibraryDoc doc, string requestedTitle, string? requestedAuthor)
    {
        if (string.IsNullOrWhiteSpace(doc.Title))
        {
            return null;
        }

        var authors = doc.AuthorName is { Count: > 0 }
            ? string.Join(", ", doc.AuthorName.Take(MaxAuthors))
            : null;
        var firstPublishYear = doc.FirstPublishYear?.ToString();
        var subjects = doc.Subject is { Count: > 0 }
            ? string.Join(", ", doc.Subject.Take(MaxSubjects))
            : null;
        var publishers = doc.Publisher is { Count: > 0 }
            ? string.Join(", ", doc.Publisher.Take(MaxPublishers))
            : null;
        var firstSentence = doc.FirstSentence is { Count: > 0 }
            ? doc.FirstSentence.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            : null;
        var isbn = doc.Isbn is { Count: > 0 }
            ? doc.Isbn.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            : null;

        var workKey = doc.Key?.Trim('/');
        var url = string.IsNullOrWhiteSpace(workKey)
            ? "https://openlibrary.org/search"
            : $"https://openlibrary.org/{workKey}";

        var coverUrl = doc.CoverId.HasValue
            ? $"https://covers.openlibrary.org/b/id/{doc.CoverId.Value}-L.jpg"
            : null;

        var snippetParts = new List<string> { $"Title: {doc.Title}" };
        if (!string.IsNullOrWhiteSpace(doc.Subtitle)) snippetParts.Add($"Subtitle: {doc.Subtitle}");
        if (!string.IsNullOrWhiteSpace(authors)) snippetParts.Add($"Author: {authors}");
        if (!string.IsNullOrWhiteSpace(firstPublishYear)) snippetParts.Add($"First published: {firstPublishYear}");
        if (!string.IsNullOrWhiteSpace(publishers)) snippetParts.Add($"Publishers: {publishers}");
        if (doc.NumberOfPagesMedian is int pages and > 0) snippetParts.Add($"Typical length: {pages} pages");
        if (doc.RatingsAverage is double avg && doc.RatingsCount is int count and > 0)
            snippetParts.Add($"Rating: {avg:F2}/5 ({count} ratings)");
        if (!string.IsNullOrWhiteSpace(subjects)) snippetParts.Add($"Subjects: {subjects}");
        if (!string.IsNullOrWhiteSpace(firstSentence)) snippetParts.Add($"First sentence: {firstSentence}");

        return new BookMetadataSource
        {
            Provider = "Open Library",
            Title = doc.Title,
            Subtitle = string.IsNullOrWhiteSpace(doc.Subtitle) ? null : doc.Subtitle,
            Authors = authors,
            Url = url,
            CoverUrl = coverUrl,
            // We deliberately do NOT fetch /works/{key}.json for descriptions — the responses are
            // inconsistent (often missing, sometimes raw markdown with embedded links). The Google
            // Books provider supplies higher-quality blurbs; Open Library is now metadata-only.
            Description = null,
            FirstSentence = string.IsNullOrWhiteSpace(firstSentence) ? null : firstSentence,
            FirstPublishYear = doc.FirstPublishYear,
            Publishers = publishers,
            Subjects = subjects,
            Isbn = string.IsNullOrWhiteSpace(isbn) ? null : isbn,
            PageCount = doc.NumberOfPagesMedian,
            EditionCount = doc.EditionCount,
            RatingsAverage = doc.RatingsAverage,
            RatingsCount = doc.RatingsCount,
            Snippet = string.Join(". ", snippetParts),
            MatchScore = EstimateMatchScore(requestedTitle, requestedAuthor, doc, authors)
        };
    }

    private static double EstimateMatchScore(
        string requestedTitle,
        string? requestedAuthor,
        OpenLibraryDoc doc,
        string? foundAuthors)
    {
        var titleMatches = Normalize(requestedTitle) == Normalize(doc.Title ?? string.Empty);
        var score = titleMatches ? 0.55 : 0.3;

        if (!string.IsNullOrWhiteSpace(requestedAuthor) &&
            !string.IsNullOrWhiteSpace(foundAuthors) &&
            Normalize(foundAuthors).Contains(Normalize(requestedAuthor), StringComparison.Ordinal))
        {
            score += 0.2;
        }

        if (doc.EditionCount is int editions)
        {
            if (editions >= 50) score += 0.05;
            else if (editions >= 10) score += 0.03;
        }

        if (doc.RatingsCount is int ratings and >= 10 && doc.RatingsAverage is double avg)
        {
            score += Math.Clamp((avg - 3.0) / 10.0, 0.0, 0.05);
        }

        return Math.Clamp(score, 0.0, 0.9);
    }

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

internal sealed class BookMetadataSource
{
    public required string Provider { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Authors { get; init; }
    public required string Url { get; init; }
    public string? CoverUrl { get; init; }
    public string? Description { get; init; }
    public string? FirstSentence { get; init; }
    public int? FirstPublishYear { get; init; }
    public string? Publishers { get; init; }
    public string? Subjects { get; init; }
    public string? Isbn { get; init; }
    public int? PageCount { get; init; }
    public int? EditionCount { get; init; }
    public double? RatingsAverage { get; init; }
    public int? RatingsCount { get; init; }
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
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("author_name")] public List<string>? AuthorName { get; set; }
    [JsonPropertyName("first_publish_year")] public int? FirstPublishYear { get; set; }
    [JsonPropertyName("subject")] public List<string>? Subject { get; set; }
    [JsonPropertyName("publisher")] public List<string>? Publisher { get; set; }
    [JsonPropertyName("isbn")] public List<string>? Isbn { get; set; }
    [JsonPropertyName("cover_i")] public int? CoverId { get; set; }
    [JsonPropertyName("number_of_pages_median")] public int? NumberOfPagesMedian { get; set; }
    [JsonPropertyName("edition_count")] public int? EditionCount { get; set; }
    [JsonPropertyName("ratings_average")] public double? RatingsAverage { get; set; }
    [JsonPropertyName("ratings_count")] public int? RatingsCount { get; set; }
    [JsonPropertyName("first_sentence")] public List<string>? FirstSentence { get; set; }
}
