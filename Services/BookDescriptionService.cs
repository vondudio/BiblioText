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
    private readonly HttpClient _httpClient = new();
    private readonly ISettingsStore _settingsStore;

    public BookDescriptionService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<List<BookDescription>> GetDescriptionsAsync(
        IReadOnlyList<(int BookId, string Title, string? Author)> books,
        CancellationToken ct = default)
    {
        var settings = _settingsStore.Load();
        if (!settings.IsConfigured)
            return new List<BookDescription>();

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

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", settings.AzureOpenAiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return new List<BookDescription>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                return new List<BookDescription>();

            var parsed = JsonSerializer.Deserialize<DescriptionResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed?.Descriptions == null)
                return new List<BookDescription>();

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
            return results;
        }
        catch
        {
            return new List<BookDescription>();
        }
    }
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
