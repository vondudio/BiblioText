using Pgvector;

namespace BiblioText.Cloud.Catalog.Entities;

/// <summary>
/// A canonical book in the shared catalog, deduplicated by title+author
/// (<see cref="NormalizedKey"/>). Multiple physical <see cref="Copy"/> rows from
/// different owners point at one Book. The semantic-search <see cref="Embedding"/>
/// and the cover image live here because they describe the work, not a copy.
/// </summary>
public sealed class Book
{
    /// <summary>Embedding dimension for Azure OpenAI text-embedding-3-small.</summary>
    public const int EmbeddingDimensions = 1536;

    public int Id { get; set; }

    /// <summary>Unique title+author key (see <see cref="CatalogKey"/>).</summary>
    public required string NormalizedKey { get; set; }

    public required string Title { get; set; }
    public string? Author { get; set; }
    public string? ShortDescription { get; set; }
    public string? LongDescription { get; set; }

    /// <summary>Provenance badges passthrough (station's description_sources_json).</summary>
    public string? DescriptionSourcesJson { get; set; }

    /// <summary>Hosted cover image URL (provider URL or uploaded blob).</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// Semantic-search vector over title+author+long description. Null until the
    /// embedding service has run for this book.
    /// </summary>
    public Vector? Embedding { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Copy> Copies { get; set; } = [];
}
