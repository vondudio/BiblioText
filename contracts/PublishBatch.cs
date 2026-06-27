using System.Text.Json.Serialization;

namespace BiblioText.Contracts;

/// <summary>
/// A single "Update cloud" push from a station. Carries every reviewed book that
/// is new or changed since the last sync (<see cref="Books"/>) plus the
/// station-side ids of books removed locally that should be unpublished
/// (<see cref="UnpublishStationBookIds"/>). Idempotent: re-sending the same batch
/// upserts to the same rows.
/// </summary>
public sealed class PublishBatch
{
    /// <summary>Wire schema version; defaults to <see cref="PublishContract.SchemaVersion"/>.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = PublishContract.SchemaVersion;

    /// <summary>
    /// Stable identifier for the publishing station/operator. Namespaces all of
    /// this station's books in the shared catalog.
    /// </summary>
    [JsonPropertyName("stationId")]
    public required string StationId { get; init; }

    /// <summary>Books to create or update.</summary>
    [JsonPropertyName("books")]
    public IReadOnlyList<BookPayload> Books { get; init; } = [];

    /// <summary>Station-side ids of books to remove from the cloud catalog.</summary>
    [JsonPropertyName("unpublishStationBookIds")]
    public IReadOnlyList<string> UnpublishStationBookIds { get; init; } = [];
}

/// <summary>Per-batch result the cloud returns to the station.</summary>
public sealed class PublishResult
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("upsertedCount")]
    public int UpsertedCount { get; init; }

    [JsonPropertyName("unpublishedCount")]
    public int UnpublishedCount { get; init; }

    /// <summary>Human-readable error/warning when <see cref="Accepted"/> is false.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
