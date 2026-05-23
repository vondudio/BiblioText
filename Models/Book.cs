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
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public bool IsDuplicate { get; set; }
    public string? Notes { get; set; }
}
