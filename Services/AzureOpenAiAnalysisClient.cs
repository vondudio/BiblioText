#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIDevGallery.Sample.Settings;

namespace AIDevGallery.Sample.Services;

/// <summary>
/// Analyzes a full bookshelf image using Azure OpenAI vision to detect all visible book spines.
/// Returns structured results with titles, authors, and approximate positions.
/// </summary>
internal sealed class AzureOpenAiAnalysisClient
{
    private readonly HttpClient _httpClient = new();
    private readonly ISettingsStore _settingsStore;

    public AzureOpenAiAnalysisClient(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<FullImageAnalysisResult> AnalyzeAsync(byte[] imageJpeg, CancellationToken ct = default)
    {
        var settings = _settingsStore.Load();
        if (!settings.IsConfigured)
            return new FullImageAnalysisResult { Error = "Azure OpenAI not configured. Go to Settings." };

        var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(imageJpeg);

        var requestBody = new
        {
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = BookshelfAnalysisPrompt.SystemPrompt
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = BookshelfAnalysisPrompt.UserPrompt },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "high" } }
                    }
                }
            },
            max_completion_tokens = 2048,
            temperature = 0,
            response_format = new { type = "json_object" }
        };

        var url = $"{settings.AzureOpenAiEndpoint!.TrimEnd('/')}/openai/deployments/{settings.AzureOpenAiDeployment}/chat/completions?api-version={settings.ApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", settings.AzureOpenAiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return new FullImageAnalysisResult { Error = $"API error: {response.StatusCode} — {json[..Math.Min(json.Length, 200)]}" };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                return new FullImageAnalysisResult { Error = "Empty response from AI." };

            // Parse the JSON response
            var analysisResponse = JsonSerializer.Deserialize<AnalysisJsonResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (analysisResponse?.Books == null || analysisResponse.Books.Count == 0)
                return new FullImageAnalysisResult { Books = new List<DetectedBook>(), Error = null };

            return new FullImageAnalysisResult { Books = analysisResponse.Books, Error = null };
        }
        catch (Exception ex)
        {
            return new FullImageAnalysisResult { Error = $"Failed to parse AI response: {ex.Message}" };
        }
    }
}

public sealed class FullImageAnalysisResult
{
    public List<DetectedBook>? Books { get; set; }
    public string? Error { get; set; }
    public bool IsSuccess => Error == null && Books != null;
}

public sealed class DetectedBook
{
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public double Confidence { get; set; } = 1.0;
    public int? PositionIndex { get; set; }
}

internal sealed class AnalysisJsonResponse
{
    public List<DetectedBook>? Books { get; set; }
}
