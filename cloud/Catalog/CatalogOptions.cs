namespace BiblioText.Cloud.Catalog;

/// <summary>
/// Bound from the "Catalog" configuration section. Azure/Blob settings are
/// optional: when they're absent the app falls back to a deterministic local
/// embedder and on-disk image storage so it runs end-to-end without Azure
/// (handy for local dev). Secrets come from App Service config / Key Vault /
/// user-secrets — never source.
/// </summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>
    /// Shared secret the station sends as <c>X-Operator-Token</c> to publish.
    /// When null/empty, publish is left open (intended for local dev only).
    /// </summary>
    public string? OperatorToken { get; set; }

    public AzureOpenAIOptions AzureOpenAI { get; set; } = new();
    public BlobOptions Blob { get; set; } = new();
}

public sealed class AzureOpenAIOptions
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class BlobOptions
{
    public string? ConnectionString { get; set; }
    public string Container { get; set; } = "book-images";

    /// <summary>Optional CDN/public base URL prefix for stored blobs.</summary>
    public string? PublicBaseUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
