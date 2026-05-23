#nullable enable

namespace BiblioText.Settings;

public sealed class AppSettings
{
    public string? AzureOpenAiEndpoint { get; set; }
    public string? AzureOpenAiApiKey { get; set; }
    public string? AzureOpenAiDeployment { get; set; }
    public string? ApiVersion { get; set; } = "2024-10-21";
    public bool UseCameraCapture { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AzureOpenAiEndpoint)
                             && !string.IsNullOrWhiteSpace(AzureOpenAiApiKey)
                             && !string.IsNullOrWhiteSpace(AzureOpenAiDeployment);
}
