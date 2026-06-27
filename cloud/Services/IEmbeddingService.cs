using Pgvector;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Produces semantic-search vectors. The SAME implementation embeds both stored
/// book text (at publish) and search queries (at request time) so document and
/// query vectors are always comparable — the core reason embeddings are cloud-
/// owned (the station has no exportable vectors).
/// </summary>
public interface IEmbeddingService
{
    Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
