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

    /// <summary>Formatted display string showing bounding box ID and confidence.</summary>
    public string DisplayLabel => $"#{Index}  •  {Confidence:P0}";
}
