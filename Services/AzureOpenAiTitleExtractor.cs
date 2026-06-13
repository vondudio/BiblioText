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
    private readonly HttpClient _httpClient = AzureOpenAiHttp.CreateClient();
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
            return $"(AI error: {result.ErrorMessage ?? "request failed"})";
        }

        try
        {
            return AzureOpenAiHttp.TryGetMessageContent(result.Value, out var content, out _)
                ? content
                : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }
}
