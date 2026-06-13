#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
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

    public BookDescriptionService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
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

        // Build the book list for the prompt
        var sb = new StringBuilder();
        for (int i = 0; i < books.Count; i++)
        {
            var (_, title, author) = books[i];
            sb.AppendLine($"{i + 1}. \"{title}\" by {author ?? "Unknown"}");
        }

        var requestBody = new
        {
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
                        You are a knowledgeable book reference assistant. Given a list of books (title and author),
                        return a JSON object with descriptions for each book.
                        
                        For each book provide:
                        - "short_description": 1-2 sentences describing what the book is about
                        - "long_description": A concise summary paragraph (3-5 sentences) covering the book's main themes, content, and significance
                        
                        If you don't recognize a book, provide your best guess based on the title and author,
                        or state "Description unavailable" for both fields.
                        """
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

                        Books:
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
                        LongDescription = desc.LongDescription
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
