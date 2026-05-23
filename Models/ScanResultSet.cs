#nullable enable

using System.Collections.Generic;

namespace AIDevGallery.Sample.Models;

/// <summary>
/// Represents a single scan result set containing detected book candidates and the source image.
/// Multiple sets can be queued in the Review page for sequential review.
/// </summary>
public sealed class ScanResultSet
{
    public required List<ReviewCandidate> Candidates { get; set; }
    public string? SourceImagePath { get; set; }
    public string Label { get; set; } = "Scan";
}
