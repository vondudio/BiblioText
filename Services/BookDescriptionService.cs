#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiblioText.Settings;

namespace BiblioText.Services;

/// <summary>
/// Batch submits book titles and authors to Azure OpenAI to get short and long descriptions.
/// </summary>
internal sealed class BookDescriptionService
{
    private readonly HttpClient _httpClient = AzureOpenAiHttp.CreateClient();
    private readonly ISettingsStore _settingsStore;
    private readonly IBookMetadataLookupService _metadataLookupService;

    public BookDescriptionService(
        ISettingsStore settingsStore,
        IBookMetadataLookupService? metadataLookupService = null)
    {
        _settingsStore = settingsStore;
        _metadataLookupService = metadataLookupService ?? new OpenLibraryBookMetadataLookupService();
    }

    public async Task<List<BookDescription>> GetDescriptionsAsync(
        IReadOnlyList<(int BookId, string Title, string? Author)> books,
        CancellationToken ct = default)
    {
        var result = await GetDescriptionsResultAsync(books, ct);
        return result.IsSuccess ? result.Descriptions : new List<BookDescription>();
    }

    public async Task<BookDescriptionBatchResult> GetDescriptionsResultAsync(
        IReadOnlyList<(int BookId, string Title, string? Author)> books,
        CancellationToken ct = default)
    {
        var settings = _settingsStore.Load();
        if (!settings.IsConfigured)
            return BookDescriptionBatchResult.Failure(AzureOpenAiErrorKind.NotConfigured, "Azure OpenAI not configured.");

        var metadataByBook = await LookupMetadataAsync(books, ct);

        var systemPrompt = string.IsNullOrWhiteSpace(settings.BookDescriptionPrompt)
            ? """
                You are a careful book reference assistant. Given a list of books and source snippets,
                return a JSON object with descriptions for each book.
                 
                For each book provide:
                - "short_description": 1-2 sentences describing what the book is about
                - "long_description": A concise summary paragraph (3-5 sentences) covering the book's main themes, content, and significance
                 
                Use only the supplied source snippets for factual claims. If no sources are supplied,
                or the supplied sources do not appear to match the requested title and author,
                set both description fields to "Description unavailable".
                """
            : settings.BookDescriptionPrompt;

        // Build the book list and retrieved source context for the prompt.
        var sb = new StringBuilder();
        for (int i = 0; i < books.Count; i++)
        {
            var (_, title, author) = books[i];
            sb.AppendLine($"{i + 1}. \"{title}\" by {author ?? "Unknown"}");
            var sources = metadataByBook[i];
            if (sources.Count == 0)
            {
                sb.AppendLine("   Sources: none found");
            }
            else
            {
                for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
                {
                    var source = sources[sourceIndex];
                    sb.AppendLine($"   Source {sourceIndex + 1}: {source.Provider} | {source.Title} | {source.Url}");
                    sb.AppendLine($"   Snippet: {source.Snippet}");
                }
            }
        }

        var requestBody = new
        {
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = systemPrompt
                },
                new
                {
                    role = "user",
                    content = $$"""
                        Provide descriptions for these books. Return a JSON object with this exact structure:
                        {
                          "descriptions": [
                            {
                              "index": 1,
                              "short_description": "...",
                              "long_description": "..."
                            }
                          ]
                        }

                        Books and source snippets:
                        {{sb}}
                        """
                }
            },
            max_completion_tokens = 4096,
            temperature = 0.3,
            response_format = new { type = "json_object" }
        };

        var url = $"{settings.AzureOpenAiEndpoint!.TrimEnd('/')}/openai/deployments/{settings.AzureOpenAiDeployment}/chat/completions?api-version={settings.ApiVersion}";

        var serializedBody = JsonSerializer.Serialize(requestBody);
        var result = await AzureOpenAiHttp.SendAsync(
            _httpClient,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("api-key", settings.AzureOpenAiApiKey);
                request.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");
                return request;
            },
            ct);

        if (!result.IsSuccess || result.Value == null)
        {
            return BookDescriptionBatchResult.Failure(
                result.ErrorKind,
                result.ErrorMessage ?? "Azure OpenAI request failed.",
                result.DiagnosticDetail);
        }

        try
        {
            if (!AzureOpenAiHttp.TryGetMessageContent(result.Value, out var content, out var contentError))
            {
                return BookDescriptionBatchResult.Failure(
                    AzureOpenAiErrorKind.EmptyResponse,
                    contentError);
            }

            var parsed = JsonSerializer.Deserialize<DescriptionResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed?.Descriptions == null)
                return BookDescriptionBatchResult.Failure(
                    AzureOpenAiErrorKind.Parse,
                    "Description response did not contain a descriptions array.");

            // Map indices back to book IDs
            var results = new List<BookDescription>();
            foreach (var desc in parsed.Descriptions)
            {
                int idx = desc.Index - 1; // 1-based to 0-based
                if (idx >= 0 && idx < books.Count)
                {
                    results.Add(new BookDescription
                    {
                        BookId = books[idx].BookId,
                        ShortDescription = desc.ShortDescription,
                        LongDescription = desc.LongDescription,
                        IsGrounded = metadataByBook[idx].Count > 0 &&
                            !IsUnavailable(desc.ShortDescription) &&
                            !IsUnavailable(desc.LongDescription),
                        SourcesJson = metadataByBook[idx].Count == 0
                            ? null
                            : JsonSerializer.Serialize(metadataByBook[idx]),
                        GeneratedAt = DateTime.UtcNow
                    });
                }
            }
            return BookDescriptionBatchResult.Success(results);
        }
        catch (Exception ex)
        {
            return BookDescriptionBatchResult.Failure(
                AzureOpenAiErrorKind.Parse,
                "Failed to parse description response.",
                ex.Message);
        }
    }

    private async Task<List<List<BookMetadataSource>>> LookupMetadataAsync(
        IReadOnlyList<(int BookId, string Title, string? Author)> books,
        CancellationToken ct)
    {
        var results = new List<List<BookMetadataSource>>(books.Count);
        foreach (var (_, title, author) in books)
        {
            try
            {
                var sources = await _metadataLookupService.LookupAsync(title, author, ct);
                results.Add(sources.ToList());
            }
            catch (HttpRequestException)
            {
                results.Add([]);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                results.Add([]);
            }
            catch (JsonException)
            {
                results.Add([]);
            }
        }

        return results;
    }

    private static bool IsUnavailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Contains("Description unavailable", StringComparison.OrdinalIgnoreCase);
}

public sealed class BookDescriptionBatchResult
{
    private BookDescriptionBatchResult(
        List<BookDescription> descriptions,
        AzureOpenAiErrorKind errorKind,
        string? errorMessage,
        string? diagnosticDetail)
    {
        Descriptions = descriptions;
        ErrorKind = errorKind;
        ErrorMessage = errorMessage;
        DiagnosticDetail = diagnosticDetail;
    }

    public List<BookDescription> Descriptions { get; }
    internal AzureOpenAiErrorKind ErrorKind { get; }
    public string? ErrorMessage { get; }
    public string? DiagnosticDetail { get; }
    public bool IsSuccess => ErrorKind == AzureOpenAiErrorKind.None;

    public static BookDescriptionBatchResult Success(List<BookDescription> descriptions) =>
        new(descriptions, AzureOpenAiErrorKind.None, null, null);

    internal static BookDescriptionBatchResult Failure(
        AzureOpenAiErrorKind errorKind,
        string errorMessage,
        string? diagnosticDetail = null) =>
        new(new List<BookDescription>(), errorKind, errorMessage, diagnosticDetail);
}

public sealed class BookDescription
{
    public int BookId { get; set; }
    public string? ShortDescription { get; set; }
    public string? LongDescription { get; set; }
    public bool IsGrounded { get; set; }
    public string? SourcesJson { get; set; }
    public DateTime GeneratedAt { get; set; }
}

internal sealed class DescriptionResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("descriptions")]
    public List<DescriptionItem>? Descriptions { get; set; }
}

internal sealed class DescriptionItem
{
    [System.Text.Json.Serialization.JsonPropertyName("index")]
    public int Index { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("short_description")]
    public string? ShortDescription { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("long_description")]
    public string? LongDescription { get; set; }
}
