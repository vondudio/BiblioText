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
}
