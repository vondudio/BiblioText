namespace BiblioText.Cloud.Services;

/// <summary>A book in search/browse results, with its available copies.</summary>
public sealed class BookResult
{
    public required int BookId { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public string? ShortDescription { get; init; }
    public string? LongDescription { get; init; }
    public string? CoverImageUrl { get; init; }
    public string? DescriptionSourcesJson { get; init; }

    /// <summary>Lower = closer (cosine distance); null for plain browse.</summary>
    public double? Distance { get; init; }

    public IReadOnlyList<CopyResult> Copies { get; init; } = [];
}

/// <summary>A physical copy: who owns it and where it lives.</summary>
public sealed class CopyResult
{
    public required int CopyId { get; init; }
    public string? OwnerHousehold { get; init; }
    public string? ShelfLocation { get; init; }
    public string? SpineImageUrl { get; init; }
}
