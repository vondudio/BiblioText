namespace BiblioText.Cloud.Catalog.Entities;

/// <summary>
/// A physical copy of a <see cref="Book"/> on a specific owner's shelf, as
/// published by a station. Identity is (<see cref="StationId"/>,
/// <see cref="StationBookId"/>) — the station-side stable key — so re-publishing
/// updates the same copy instead of duplicating it. Owner/location are stored as
/// labels here (sufficient for a small family catalog; can be promoted to their
/// own tables later if needed).
/// </summary>
public sealed class Copy
{
    public int Id { get; set; }

    /// <summary>Publishing station/operator identifier.</summary>
    public required string StationId { get; set; }

    /// <summary>Station-side stable book id (idempotency key within a station).</summary>
    public required string StationBookId { get; set; }

    public int BookId { get; set; }
    public Book Book { get; set; } = null!;

    /// <summary>Whose shelf this copy lives on.</summary>
    public string? OwnerHousehold { get; set; }

    /// <summary>Where the copy physically lives (shelf / room label).</summary>
    public string? ShelfLocation { get; set; }

    /// <summary>Hosted spine-crop image URL.</summary>
    public string? SpineImageUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
