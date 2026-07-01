#nullable enable

using System;

namespace BiblioText.Models;

public sealed class Book
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string? Author { get; set; }
    public string? ShortDescription { get; set; }
    public string? LongDescription { get; set; }
    public int? ScanId { get; set; }
    public int? LocationId { get; set; }
    public string? SpineImagePath { get; set; }
    public string? BookshelfImagePath { get; set; }
    public int? DetectionIndex { get; set; }

    /// <summary>
    /// Detection bounding box on <see cref="BookshelfImagePath"/>, stored as
    /// normalized 0..1 coordinates in the form "x,y,w,h" (invariant culture).
    /// Null for books saved before this was captured, or from full-image AI analysis.
    /// </summary>
    public string? SpineBoxNorm { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public bool IsDuplicate { get; set; }
    public bool IsDescriptionGrounded { get; set; }
    public string? DescriptionSourcesJson { get; set; }
    public DateTime? DescriptionGeneratedAt { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Stable, station-assigned identifier used as the cloud upsert/idempotency
    /// key. Generated once (a GUID) when the book is first inserted; never reused.
    /// </summary>
    public string? StationBookId { get; set; }

    /// <summary>UTC time this book was last successfully published to the cloud; null if never.</summary>
    public DateTime? CloudSyncedAt { get; set; }

    /// <summary>
    /// Content hash captured at the last successful publish. Compared against the
    /// current content hash to decide whether the book is "dirty" (needs re-upload).
    /// </summary>
    public string? SyncHash { get; set; }
}
