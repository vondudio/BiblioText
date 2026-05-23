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
/// Sends a single crop as a base64 data URL to GPT vision and extracts the title.
/// </summary>
internal sealed class AzureOpenAiTitleExtractor : IBookTitleExtractor
{
    private readonly HttpClient _httpClient = new();
    private readonly ISettingsStore _settingsStore;

    public AzureOpenAiTitleExtractor(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<string> ExtractAsync(BookCrop crop, CancellationToken ct = default)
    {
        var settings = _settingsStore.Load();
        if (!settings.IsConfigured)
            return "(not configured — set Azure OpenAI credentials in Settings)";

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
                        new { type = "text", text = "Return ONLY the book title and author from this spine image, comma-separated (e.g. \"The Great Gatsby, F. Scott Fitzgerald\"). If the text is unreadable or this is not a book spine, reply 'unknown'." },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "high" } }
                    }
                }
            },
            max_completion_tokens = 64,
            temperature = 0
        };

        var url = $"{settings.AzureOpenAiEndpoint!.TrimEnd('/')}/openai/deployments/{settings.AzureOpenAiDeployment}/chat/completions?api-version={settings.ApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", settings.AzureOpenAiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return $"(AI error: {response.StatusCode})";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return content?.Trim() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
