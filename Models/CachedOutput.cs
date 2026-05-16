using AIDevGallery.Sample.Utils;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Generic;

namespace AIDevGallery.Sample.Models;

/// <summary>
/// Cached result of one DetectObjects run, keyed in ImageItem.Outputs by
/// "{modelId}|{conf2dp}". Holds the rendered BitmapImage to display, the raw
/// box predictions (for cropping by bounding box on any model), and the per-pixel
/// masks if the model was a segmentation variant.
/// </summary>
internal sealed class CachedOutput
{
    public required BitmapImage Image { get; init; }
    public required IReadOnlyList<Prediction> BoxPredictions { get; init; }
    public IReadOnlyList<MaskedPrediction>? MaskedPredictions { get; init; }
}
