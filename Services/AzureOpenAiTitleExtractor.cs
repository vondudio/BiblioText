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
    private readonly HttpClient _httpClient = AzureOpenAiHttp.CreateClient();
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

        var spinePrompt = string.IsNullOrWhiteSpace(settings.SpineExtractionPrompt)
            ? DefaultPrompts.SpineExtraction
            : settings.SpineExtractionPrompt;

        var requestBody = new
        {
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = spinePrompt },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "high" } }
                    }
                }
            },
            max_completion_tokens = 128,
            temperature = 0
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
            return new ExtractionResult { Title = $"(AI error: {result.ErrorMessage ?? "request failed"})", Author = "", Confidence = 0 };
        }

        try
        {
            if (!AzureOpenAiHttp.TryGetMessageContent(result.Value, out var content, out _))
            {
                return new ExtractionResult { Title = "unknown", Author = "", Confidence = 0 };
            }

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
            // Fallback: try comma/dash/by-separated parsing
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
