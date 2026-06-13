#nullable enable

namespace BiblioText.Settings;

public sealed class AppSettings
{
    public string? AzureOpenAiEndpoint { get; set; }
    public string? AzureOpenAiApiKey { get; set; }
    public string? AzureOpenAiDeployment { get; set; }
    public string? ApiVersion { get; set; } = "2024-10-21";
    public bool UseCameraCapture { get; set; } = true;

    public string? GoogleBooksApiKey { get; set; }

    // Editable prompts (null = use default)
    public string? SpineExtractionPrompt { get; set; }
    public string? BookshelfAnalysisSystemPrompt { get; set; }
    public string? BookshelfAnalysisUserPrompt { get; set; }
    public string? BookDescriptionPrompt { get; set; }

    // Bump in DefaultPrompts.CurrentVersion whenever a default prompt changes
    // so stale user-saved overrides are auto-discarded on next load.
    public int PromptsVersion { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AzureOpenAiEndpoint)
                             && !string.IsNullOrWhiteSpace(AzureOpenAiApiKey)
                             && !string.IsNullOrWhiteSpace(AzureOpenAiDeployment);
}
