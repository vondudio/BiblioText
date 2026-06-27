using System.Text.Json.Serialization;

namespace BiblioText.Contracts;

/// <summary>
/// One reviewed book a station publishes to the cloud catalog. Identity in the
/// cloud is title+author (no ISBN — see whiteboard D3); <see cref="StationBookId"/>
/// is the station-side stable key the cloud upserts against so re-publishing an
/// edited book updates the same row rather than duplicating it.
/// </summary>
public sealed class BookPayload
{
    /// <summary>
    /// Stable, station-assigned identifier for this book (a GUID generated once on
    /// first publish and stored locally). Idempotency / upsert key for the cloud.
    /// </summary>
    [JsonPropertyName("stationBookId")]
    public required string StationBookId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("shortDescription")]
    public string? ShortDescription { get; init; }

    /// <summary>Long description — the text the cloud embeds for semantic search.</summary>
    [JsonPropertyName("longDescription")]
    public string? LongDescription { get; init; }

    /// <summary>Whose shelf this copy lives on (the real owner/household label).</summary>
    [JsonPropertyName("ownerHousehold")]
    public string? OwnerHousehold { get; init; }

    /// <summary>Where the copy physically lives (shelf / room label).</summary>
    [JsonPropertyName("shelfLocation")]
    public string? ShelfLocation { get; init; }

    /// <summary>Spine crop the station captured (usually inline base64).</summary>
    [JsonPropertyName("spineImage")]
    public ImagePayload? SpineImage { get; init; }

    /// <summary>Cover art (usually a provider URL).</summary>
    [JsonPropertyName("coverImage")]
    public ImagePayload? CoverImage { get; init; }

    /// <summary>
    /// Provenance of the description (Google/Wikipedia/Open Library/AI) as the
    /// station's serialized <c>description_sources_json</c>, passed through for
    /// the website's source badges. Opaque to the contract.
    /// </summary>
    [JsonPropertyName("descriptionSourcesJson")]
    public string? DescriptionSourcesJson { get; init; }
}
