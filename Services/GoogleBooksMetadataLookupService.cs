#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BiblioText.Settings;

namespace BiblioText.Services;

/// <summary>
/// Google Books volumes API client. Requires a Google API key in settings — anonymous access
/// has been rate-limited to zero in recent quota policy changes (RESOURCE_EXHAUSTED at first call).
/// Falls back gracefully to an empty result when no key is configured.
/// </summary>
internal sealed class GoogleBooksMetadataLookupService : IBookMetadataLookupService
{
    private const string Endpoint = "https://www.googleapis.com/books/v1/volumes";
    private const int MaxResults = 3;
    private const int MaxDescriptionChars = 1500;

    private readonly HttpClient _httpClient;
    private readonly ISettingsStore _settingsStore;

    public GoogleBooksMetadataLookupService(ISettingsStore settingsStore)
        : this(settingsStore, CreateDefaultClient())
    {
    }

    internal GoogleBooksMetadataLookupService(ISettingsStore settingsStore, HttpClient httpClient)
    {
        _settingsStore = settingsStore;
        _httpClient = httpClient;
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BiblioText/1.0");
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

        var apiKey = _settingsStore.Load().GoogleBooksApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        var q = $"intitle:{QuoteForQuery(title)}";
        if (!string.IsNullOrWhiteSpace(author))
        {
            q += $"+inauthor:{QuoteForQuery(author)}";
        }

        var uri = new Uri(
            $"{Endpoint}?q={Uri.EscapeDataString(q)}" +
            $"&maxResults={MaxResults}&printType=books&projection=full" +
            $"&key={Uri.EscapeDataString(apiKey)}");

        using var response = await _httpClient.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<GoogleVolumesResponse>(stream, cancellationToken: ct);
        if (payload?.Items == null || payload.Items.Count == 0)
        {
            return [];
        }

        var sources = payload.Items
            .Where(v => v.VolumeInfo != null && !string.IsNullOrWhiteSpace(v.VolumeInfo.Title))
            .Select(v => CreateSource(v, title, author))
            .Where(s => s != null)
            .Cast<BookMetadataSource>()
            .OrderByDescending(s => s.MatchScore)
            .ToList();

        return sources;
    }

    private static string QuoteForQuery(string value)
    {
        // Google Books treats quoted phrases as a unit. Strip any embedded quotes to avoid breaking the query.
        var cleaned = value.Replace("\"", " ").Trim();
        return $"\"{cleaned}\"";
    }

    private static BookMetadataSource? CreateSource(GoogleVolume volume, string requestedTitle, string? requestedAuthor)
    {
        var info = volume.VolumeInfo!;
        var authors = info.Authors is { Count: > 0 }
            ? string.Join(", ", info.Authors.Take(3))
            : null;
        var categories = info.Categories is { Count: > 0 }
            ? string.Join(", ", info.Categories.Take(4))
            : null;
        var firstPublishYear = ParseYear(info.PublishedDate);
        var isbn13 = info.IndustryIdentifiers?
            .FirstOrDefault(id => string.Equals(id.Type, "ISBN_13", StringComparison.OrdinalIgnoreCase))?.Identifier;
        var isbn10 = info.IndustryIdentifiers?
            .FirstOrDefault(id => string.Equals(id.Type, "ISBN_10", StringComparison.OrdinalIgnoreCase))?.Identifier;
        var isbn = isbn13 ?? isbn10;
        var description = TruncateDescription(SanitizeDescription(info.Description));

        // Google's HTTP thumbnails work, but ImageLinks come back as https today; force https just in case.
        var coverUrl = info.ImageLinks?.Thumbnail ?? info.ImageLinks?.SmallThumbnail;
        if (!string.IsNullOrWhiteSpace(coverUrl) && coverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            coverUrl = "https://" + coverUrl[7..];
        }

        var url = info.CanonicalVolumeLink
            ?? info.InfoLink
            ?? (string.IsNullOrWhiteSpace(volume.Id)
                ? "https://books.google.com/"
                : $"https://books.google.com/books?id={Uri.EscapeDataString(volume.Id)}");

        var snippetParts = new List<string> { $"Title: {info.Title}" };
        if (!string.IsNullOrWhiteSpace(info.Subtitle)) snippetParts.Add($"Subtitle: {info.Subtitle}");
        if (!string.IsNullOrWhiteSpace(authors)) snippetParts.Add($"Author: {authors}");
        if (!string.IsNullOrWhiteSpace(info.Publisher)) snippetParts.Add($"Publisher: {info.Publisher}");
        if (!string.IsNullOrWhiteSpace(info.PublishedDate)) snippetParts.Add($"Published: {info.PublishedDate}");
        if (info.PageCount is int pages and > 0) snippetParts.Add($"Pages: {pages}");
        if (info.AverageRating is double avg && info.RatingsCount is int count and > 0)
            snippetParts.Add($"Rating: {avg:F2}/5 ({count} ratings)");
        if (!string.IsNullOrWhiteSpace(categories)) snippetParts.Add($"Categories: {categories}");
        if (!string.IsNullOrWhiteSpace(description)) snippetParts.Add($"Description: {description}");

        return new BookMetadataSource
        {
            Provider = "Google Books",
            Title = info.Title!,
            Subtitle = string.IsNullOrWhiteSpace(info.Subtitle) ? null : info.Subtitle,
            Authors = authors,
            Url = url,
            CoverUrl = string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl,
            Description = description,
            FirstSentence = null,
            FirstPublishYear = firstPublishYear,
            Publishers = info.Publisher,
            Subjects = categories,
            Isbn = isbn,
            PageCount = info.PageCount,
            EditionCount = null,
            RatingsAverage = info.AverageRating,
            RatingsCount = info.RatingsCount,
            Snippet = string.Join(". ", snippetParts),
            MatchScore = EstimateMatchScore(requestedTitle, requestedAuthor, info, authors, description != null)
        };
    }

    private static int? ParseYear(string? publishedDate)
    {
        if (string.IsNullOrWhiteSpace(publishedDate))
        {
            return null;
        }

        // Google Books returns "1925", "1925-04", or "1925-04-10".
        var yearPart = publishedDate.Length >= 4 ? publishedDate[..4] : publishedDate;
        return int.TryParse(yearPart, out var year) ? year : null;
    }

    private static string? SanitizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        // Google occasionally returns HTML fragments (e.g., <p>, <br>, <i>). Strip them so the AI
        // prompt and the Library UI render cleanly.
        var withoutTags = System.Text.RegularExpressions.Regex.Replace(description, "<[^>]+>", " ");
        var collapsed = System.Text.RegularExpressions.Regex.Replace(withoutTags, "\\s+", " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    private static string? TruncateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length <= MaxDescriptionChars)
        {
            return description;
        }

        var slice = description[..MaxDescriptionChars];
        var lastBreak = slice.LastIndexOfAny(['.', '!', '?']);
        return lastBreak > MaxDescriptionChars / 2
            ? slice[..(lastBreak + 1)]
            : slice + "…";
    }

    private static double EstimateMatchScore(
        string requestedTitle,
        string? requestedAuthor,
        GoogleVolumeInfo info,
        string? foundAuthors,
        bool hasDescription)
    {
        var titleMatches = Normalize(requestedTitle) == Normalize(info.Title ?? string.Empty);
        // Start higher than Open Library so Google Books wins ties in the composite ranking.
        var score = titleMatches ? 0.7 : 0.45;

        if (!string.IsNullOrWhiteSpace(requestedAuthor) &&
            !string.IsNullOrWhiteSpace(foundAuthors) &&
            Normalize(foundAuthors).Contains(Normalize(requestedAuthor), StringComparison.Ordinal))
        {
            score += 0.15;
        }

        if (info.RatingsCount is int ratings and >= 10 && info.AverageRating is double avg)
        {
            score += Math.Clamp((avg - 3.0) / 10.0, 0.0, 0.05);
        }

        // The whole point of this provider is the description — reward it heavily.
        if (hasDescription)
        {
            score += 0.1;
        }

        return Math.Clamp(score, 0.0, 0.98);
    }

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

internal sealed class GoogleVolumesResponse
{
    [JsonPropertyName("items")] public List<GoogleVolume>? Items { get; set; }
}

internal sealed class GoogleVolume
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("volumeInfo")] public GoogleVolumeInfo? VolumeInfo { get; set; }
}

internal sealed class GoogleVolumeInfo
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("authors")] public List<string>? Authors { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("publishedDate")] public string? PublishedDate { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("industryIdentifiers")] public List<GoogleIndustryIdentifier>? IndustryIdentifiers { get; set; }
    [JsonPropertyName("pageCount")] public int? PageCount { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("averageRating")] public double? AverageRating { get; set; }
    [JsonPropertyName("ratingsCount")] public int? RatingsCount { get; set; }
    [JsonPropertyName("imageLinks")] public GoogleImageLinks? ImageLinks { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("infoLink")] public string? InfoLink { get; set; }
    [JsonPropertyName("canonicalVolumeLink")] public string? CanonicalVolumeLink { get; set; }
}

internal sealed class GoogleIndustryIdentifier
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("identifier")] public string? Identifier { get; set; }
}

internal sealed class GoogleImageLinks
{
    [JsonPropertyName("smallThumbnail")] public string? SmallThumbnail { get; set; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
}
