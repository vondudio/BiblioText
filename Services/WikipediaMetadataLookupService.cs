#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BiblioText.Services;

/// <summary>
/// Wikipedia plot/summary provider. Uses the opensearch endpoint to find the canonical page
/// title, then fetches the lede extract from the REST summary endpoint. Returns no results
/// (rather than guessing) when the top hit isn't clearly about a literary work.
/// </summary>
internal sealed class WikipediaMetadataLookupService : IBookMetadataLookupService
{
    private static readonly string[] BookKeywords =
    [
        "novel", "novella", "book", "memoir", "play", "story", "poem",
        "anthology", "novelization", "essay", "biography", "autobiography"
    ];

    private const int MaxExtractChars = 1500;

    private readonly HttpClient _httpClient;

    public WikipediaMetadataLookupService()
        : this(CreateDefaultClient())
    {
    }

    internal WikipediaMetadataLookupService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Wikipedia requires a descriptive UA per https://meta.wikimedia.org/wiki/User-Agent_policy.
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

        var canonicalTitle = await ResolveCanonicalTitleAsync(title, author, ct);
        if (string.IsNullOrWhiteSpace(canonicalTitle))
        {
            return [];
        }

        var summary = await FetchSummaryAsync(canonicalTitle, ct);
        if (summary == null || string.IsNullOrWhiteSpace(summary.Extract))
        {
            return [];
        }

        // Bail out on disambiguation pages — they'd send nonsense into the prompt.
        if (string.Equals(summary.Type, "disambiguation", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var looksLikeBook = !string.IsNullOrWhiteSpace(summary.Description) &&
            BookKeywords.Any(kw => summary.Description!.Contains(kw, StringComparison.OrdinalIgnoreCase));

        // If we can't confirm it's about a book and the author was given but the extract doesn't
        // mention them, the page is probably about something else with the same name (movie,
        // album, person). Drop it rather than poison the description.
        if (!looksLikeBook &&
            !string.IsNullOrWhiteSpace(author) &&
            !summary.Extract!.Contains(author, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var extract = TruncateExtract(summary.Extract!);
        var pageUrl = summary.ContentUrls?.Desktop?.Page
            ?? $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(canonicalTitle.Replace(' ', '_'))}";

        var snippetParts = new List<string> { $"Title: {summary.Title ?? canonicalTitle}" };
        if (!string.IsNullOrWhiteSpace(summary.Description)) snippetParts.Add($"Wikipedia: {summary.Description}");
        snippetParts.Add($"Extract: {extract}");

        var score = looksLikeBook ? 0.72 : 0.45;
        if (!string.IsNullOrWhiteSpace(author) && summary.Extract!.Contains(author, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.1;
        }

        var source = new BookMetadataSource
        {
            Provider = "Wikipedia",
            Title = summary.Title ?? canonicalTitle,
            Subtitle = null,
            Authors = null,
            Url = pageUrl,
            CoverUrl = summary.Thumbnail?.Source,
            Description = extract,
            FirstSentence = null,
            FirstPublishYear = null,
            Publishers = null,
            Subjects = null,
            Isbn = null,
            PageCount = null,
            EditionCount = null,
            RatingsAverage = null,
            RatingsCount = null,
            Snippet = string.Join(". ", snippetParts),
            MatchScore = Math.Clamp(score, 0.0, 0.95)
        };

        return [source];
    }

    private async Task<string?> ResolveCanonicalTitleAsync(string title, string? author, CancellationToken ct)
    {
        var searchTerm = string.IsNullOrWhiteSpace(author) ? title : $"{title} {author}";
        var uri = new Uri(
            "https://en.wikipedia.org/w/api.php?action=opensearch&format=json&limit=3" +
            $"&search={Uri.EscapeDataString(searchTerm)}");

        try
        {
            using var response = await _httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            // Response shape: [query, [titles], [descriptions], [urls]]
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2)
            {
                return null;
            }

            var titles = doc.RootElement[1];
            if (titles.ValueKind != JsonValueKind.Array || titles.GetArrayLength() == 0)
            {
                return null;
            }

            return titles[0].GetString();
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<WikipediaSummary?> FetchSummaryAsync(string canonicalTitle, CancellationToken ct)
    {
        var uri = new Uri(
            "https://en.wikipedia.org/api/rest_v1/page/summary/" +
            Uri.EscapeDataString(canonicalTitle.Replace(' ', '_')));

        try
        {
            using var response = await _httpClient.GetAsync(uri, ct);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<WikipediaSummary>(stream, cancellationToken: ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TruncateExtract(string extract)
    {
        if (extract.Length <= MaxExtractChars)
        {
            return extract;
        }

        var slice = extract[..MaxExtractChars];
        var lastBreak = slice.LastIndexOfAny(['.', '!', '?']);
        return lastBreak > MaxExtractChars / 2
            ? slice[..(lastBreak + 1)]
            : slice + "…";
    }
}

internal sealed class WikipediaSummary
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("extract")] public string? Extract { get; set; }
    [JsonPropertyName("thumbnail")] public WikipediaImage? Thumbnail { get; set; }
    [JsonPropertyName("content_urls")] public WikipediaContentUrls? ContentUrls { get; set; }
}

internal sealed class WikipediaImage
{
    [JsonPropertyName("source")] public string? Source { get; set; }
}

internal sealed class WikipediaContentUrls
{
    [JsonPropertyName("desktop")] public WikipediaUrlSet? Desktop { get; set; }
}

internal sealed class WikipediaUrlSet
{
    [JsonPropertyName("page")] public string? Page { get; set; }
}
