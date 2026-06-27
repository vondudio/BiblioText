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
/// Analyzes a full bookshelf image using Azure OpenAI vision to detect all visible book spines.
/// Returns structured results with titles, authors, and approximate positions.
/// </summary>
    internal sealed class AzureOpenAiAnalysisClient
{
    private readonly HttpClient _httpClient = AzureOpenAiHttp.CreateClient();
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

        var systemPrompt = string.IsNullOrWhiteSpace(settings.BookshelfAnalysisSystemPrompt)
            ? DefaultPrompts.BookshelfAnalysisSystem
            : settings.BookshelfAnalysisSystemPrompt;
        var userPrompt = string.IsNullOrWhiteSpace(settings.BookshelfAnalysisUserPrompt)
            ? DefaultPrompts.BookshelfAnalysisUser
            : settings.BookshelfAnalysisUserPrompt;

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
                    content = new object[]
                    {
                        new { type = "text", text = userPrompt },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "high" } }
                    }
                }
            },
            max_completion_tokens = 2048,
            temperature = 0,
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
            return new FullImageAnalysisResult
            {
                Error = result.ErrorMessage ?? "Azure OpenAI request failed.",
                ErrorKind = result.ErrorKind,
                DiagnosticDetail = result.DiagnosticDetail
            };
        }

        try
        {
            if (!AzureOpenAiHttp.TryGetMessageContent(result.Value, out var content, out var contentError))
            {
                return new FullImageAnalysisResult
                {
                    Error = contentError,
                    ErrorKind = AzureOpenAiErrorKind.EmptyResponse
                };
            }

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
            return new FullImageAnalysisResult
            {
                Error = $"Failed to parse AI response: {ex.Message}",
                ErrorKind = AzureOpenAiErrorKind.Parse
            };
        }
    }
}

public sealed class FullImageAnalysisResult
{
    public List<DetectedBook>? Books { get; set; }
    public string? Error { get; set; }
    internal AzureOpenAiErrorKind ErrorKind { get; set; }
    public string? DiagnosticDetail { get; set; }
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
