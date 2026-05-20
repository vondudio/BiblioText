#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDevGallery.Sample.Models;

public sealed partial class ReviewCandidate : ObservableObject
{
    [ObservableProperty]
    private int index;

    [ObservableProperty]
    private string detectedTitle = string.Empty;

    [ObservableProperty]
    private string? detectedAuthor;

    [ObservableProperty]
    private string editedTitle = string.Empty;

    [ObservableProperty]
    private string? editedAuthor;

    [ObservableProperty]
    private bool isAccepted = true;

    [ObservableProperty]
    private byte[]? cropJpeg;

    [ObservableProperty]
    private int pixelWidth;

    [ObservableProperty]
    private int pixelHeight;

    [ObservableProperty]
    private double confidence;
}
