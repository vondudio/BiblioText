#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;

namespace BiblioText.Models;

public sealed partial class ReviewCandidate : ObservableObject
{
    [ObservableProperty]
    public partial int Index { get; set; }

    [ObservableProperty]
    public partial string DetectedTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? DetectedAuthor { get; set; }

    [ObservableProperty]
    public partial string EditedTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? EditedAuthor { get; set; }

    [ObservableProperty]
    public partial bool IsAccepted { get; set; } = true;

    [ObservableProperty]
    public partial byte[]? CropJpeg { get; set; }

    [ObservableProperty]
    public partial int PixelWidth { get; set; }

    [ObservableProperty]
    public partial int PixelHeight { get; set; }

    [ObservableProperty]
    public partial double Confidence { get; set; }

    /// <summary>
    /// Detection bounding box on the source bookshelf image, expressed as normalized
    /// 0..1 coordinates (left, top, width, height) so it survives any later resize of
    /// the stored image. Null when the box position is unknown (e.g. full-image AI analysis).
    /// </summary>
    [ObservableProperty]
    public partial double? BoxNormX { get; set; }

    [ObservableProperty]
    public partial double? BoxNormY { get; set; }

    [ObservableProperty]
    public partial double? BoxNormWidth { get; set; }

    [ObservableProperty]
    public partial double? BoxNormHeight { get; set; }

    /// <summary>Formatted display string showing bounding box ID and confidence.</summary>
    public string DisplayLabel => $"#{Index}  •  {Confidence:P0}";
}
