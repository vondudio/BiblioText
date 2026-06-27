using Azure;
using Azure.AI.OpenAI;
using BiblioText.Cloud.Catalog;
using BiblioText.Cloud.Catalog.Entities;
using OpenAI.Embeddings;
using Pgvector;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Embeds text via an Azure OpenAI embeddings deployment
/// (text-embedding-3-small → 1536 dims). Used when the Catalog:AzureOpenAI
/// settings are present.
/// </summary>
public sealed class AzureOpenAIEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;

    public AzureOpenAIEmbeddingService(AzureOpenAIOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);

        var azure = new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey));
        _client = azure.GetEmbeddingClient(options.EmbeddingDeployment);
    }

    public async Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        // Azure rejects empty input; embed a single space so callers always get a vector.
        var input = string.IsNullOrWhiteSpace(text) ? " " : text;
        OpenAIEmbedding embedding = await _client.GenerateEmbeddingAsync(input, cancellationToken: cancellationToken);
        return new Vector(embedding.ToFloats());
    }
}
