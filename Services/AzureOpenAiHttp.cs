#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BiblioText.Services;

internal enum AzureOpenAiErrorKind
{
    None,
    NotConfigured,
    Authentication,
    RateLimit,
    Timeout,
    Transient,
    BadRequest,
    Parse,
    EmptyResponse,
    Unknown
}

internal sealed class AzureOpenAiOperationResult<T>
{
    private AzureOpenAiOperationResult(T? value, AzureOpenAiErrorKind errorKind, string? errorMessage, string? diagnosticDetail)
    {
        Value = value;
        ErrorKind = errorKind;
        ErrorMessage = errorMessage;
        DiagnosticDetail = diagnosticDetail;
    }

    public T? Value { get; }
    public AzureOpenAiErrorKind ErrorKind { get; }
    public string? ErrorMessage { get; }
    public string? DiagnosticDetail { get; }
    public bool IsSuccess => ErrorKind == AzureOpenAiErrorKind.None;

    public static AzureOpenAiOperationResult<T> Success(T value) => new(value, AzureOpenAiErrorKind.None, null, null);

    public static AzureOpenAiOperationResult<T> Failure(
        AzureOpenAiErrorKind errorKind,
        string errorMessage,
        string? diagnosticDetail = null) => new(default, errorKind, errorMessage, diagnosticDetail);
}

internal static class AzureOpenAiHttp
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    public static HttpClient CreateClient() => new() { Timeout = RequestTimeout };

    public static async Task<AzureOpenAiOperationResult<string>> SendAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                using var request = requestFactory();
                using var response = await httpClient.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    return AzureOpenAiOperationResult<string>.Success(body);
                }

                var kind = ClassifyStatus(response.StatusCode);
                if (IsRetryable(response.StatusCode) && attempt < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempt], ct);
                    continue;
                }

                return AzureOpenAiOperationResult<string>.Failure(
                    kind,
                    $"Azure OpenAI request failed with {(int)response.StatusCode} {response.StatusCode}.",
                    Truncate(body));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempt], ct);
                    continue;
                }

                return AzureOpenAiOperationResult<string>.Failure(
                    AzureOpenAiErrorKind.Timeout,
                    "Azure OpenAI request timed out.");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempt], ct);
                    continue;
                }

                return AzureOpenAiOperationResult<string>.Failure(
                    AzureOpenAiErrorKind.Transient,
                    "Azure OpenAI network request failed.",
                    ex.Message);
            }
            catch (JsonException ex)
            {
                return AzureOpenAiOperationResult<string>.Failure(
                    AzureOpenAiErrorKind.Parse,
                    "Azure OpenAI response could not be parsed.",
                    ex.Message);
            }
        }

        return AzureOpenAiOperationResult<string>.Failure(
            AzureOpenAiErrorKind.Unknown,
            "Azure OpenAI request failed.");
    }

    public static bool TryGetMessageContent(string responseJson, out string content, out string error)
    {
        content = string.Empty;
        error = string.Empty;

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            error = "Response did not contain any choices.";
            return false;
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var contentElement))
        {
            error = "Response choice did not contain message content.";
            return false;
        }

        content = contentElement.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            error = "Response message content was empty.";
            return false;
        }

        return true;
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static AzureOpenAiErrorKind ClassifyStatus(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AzureOpenAiErrorKind.Authentication,
            HttpStatusCode.TooManyRequests => AzureOpenAiErrorKind.RateLimit,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => AzureOpenAiErrorKind.Timeout,
            HttpStatusCode.BadRequest => AzureOpenAiErrorKind.BadRequest,
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => AzureOpenAiErrorKind.Transient,
            _ => AzureOpenAiErrorKind.Unknown
        };

    private static string Truncate(string value, int maxLength = 500) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
