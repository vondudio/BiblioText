#nullable enable

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiblioText.Settings;

namespace BiblioText.Services;

/// <summary>
/// Real Azure OpenAI implementation of <see cref="IBookTitleExtractor"/>.
/// Sends a single crop as a base64 data URL to GPT vision and extracts
/// the title, author, and confidence score.
/// </summary>
internal sealed class AzureOpenAiTitleExtractor : IBookTitleExtractor
{
    private readonly HttpClient _httpClient = new();
    private readonly ISettingsStore _settingsStore;

    public AzureOpenAiTitleExtractor(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<ExtractionResult> ExtractAsync(BookCrop crop, CancellationToken ct = default)
    {
        var settings = _settingsStore.Load();
        if (!settings.IsConfigured)
            return new ExtractionResult { Title = "(not configured)", Author = "", Confidence = 0 };

        var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(crop.Jpeg);

        var requestBody = new
        {
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = """
                            Analyze this book spine image. Return a JSON object with exactly these fields:
                            {"title": "Book Title", "author": "Author Name", "confidence": 0.95}
                            - title: the book title visible on the spine
                            - author: the author name if visible, or "" if not
                            - confidence: a number 0.0 to 1.0 indicating how confident you are in the reading (1.0 = clearly readable, 0.0 = unreadable)
                            If this is not a book spine or text is completely unreadable, return {"title": "unknown", "author": "", "confidence": 0.0}
                            Return ONLY the JSON object, no markdown formatting.
                            """ },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "high" } }
                    }
                }
            },
            max_completion_tokens = 128,
            temperature = 0
        };

        var url = $"{settings.AzureOpenAiEndpoint!.TrimEnd('/')}/openai/deployments/{settings.AzureOpenAiDeployment}/chat/completions?api-version={settings.ApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", settings.AzureOpenAiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return new ExtractionResult { Title = $"(AI error: {response.StatusCode})", Author = "", Confidence = 0 };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? "";

            return ParseExtractionJson(content);
        }
        catch
        {
            return new ExtractionResult { Title = "unknown", Author = "", Confidence = 0 };
        }
    }

    private static ExtractionResult ParseExtractionJson(string content)
    {
        try
        {
            // Strip markdown code fences if present
            if (content.StartsWith("```"))
            {
                int start = content.IndexOf('{');
                int end = content.LastIndexOf('}');
                if (start >= 0 && end > start)
                    content = content[start..(end + 1)];
            }

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "unknown" : "unknown";
            string author = root.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
            double confidence = root.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5;

            return new ExtractionResult { Title = title, Author = author, Confidence = confidence };
        }
        catch
        {
            // Fallback: try comma-separated parsing
            return ParsePlainText(content);
        }
    }

    private static ExtractionResult ParsePlainText(string text)
    {
        string title = text;
        string author = "";

        if (text.Contains(" - "))
        {
            var parts = text.Split(" - ", 2);
            title = parts[0].Trim();
            author = parts[1].Trim();
        }
        else if (text.Contains(" by "))
        {
            var parts = text.Split(" by ", 2);
            title = parts[0].Trim();
            author = parts[1].Trim();
        }
        else if (text.Contains(','))
        {
            var parts = text.Split(',', 2);
            title = parts[0].Trim();
            author = parts[1].Trim();
        }

        return new ExtractionResult { Title = title, Author = author, Confidence = 0.5 };
    }
}
