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

    // Cloud publish (Phase 2) — where "Sync all reviewed" pushes the catalog.
    // The whole settings file is DPAPI-encrypted at rest, so the operator token
    // is protected without extra handling.

    /// <summary>Base URL of the cloud app, e.g. "https://bibliotext.example.com".</summary>
    public string? CloudEndpoint { get; set; }

    /// <summary>Shared operator secret sent as the X-Operator-Token header on publish.</summary>
    public string? CloudOperatorToken { get; set; }

    /// <summary>
    /// Stable identifier for this station in the shared catalog. Generated once
    /// (a GUID) the first time it's needed; namespaces this station's copies.
    /// </summary>
    public string? StationId { get; set; }

    /// <summary>Owner/household label applied to every copy this station publishes.</summary>
    public string? OwnerHousehold { get; set; }

    /// <summary>True cloud publishing is set up (endpoint present).</summary>
    public bool IsCloudConfigured => !string.IsNullOrWhiteSpace(CloudEndpoint);

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
