#nullable enable

using System;

namespace BiblioText.Models;

public sealed class Scan
{
    public int Id { get; set; }
    public required string FilePath { get; set; }
    public string? ThumbnailPath { get; set; }
    public DateTime ScannedAt { get; set; }
    public int BookCount { get; set; }
    public string? AnalysisMethod { get; set; }
    public bool IsReviewed { get; set; }
}
