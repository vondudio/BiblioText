using BiblioText.Utils;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Generic;

namespace BiblioText.Models;

/// <summary>
/// Cached result of one DetectObjects run, keyed in ImageItem.Outputs by
/// "{modelId}|{conf2dp}". Holds the rendered BitmapImage to display, the raw
/// box predictions (for cropping by bounding box on any model), and the per-pixel
/// masks if the model was a segmentation variant.
/// </summary>
internal sealed class CachedOutput
{
    public required BitmapImage Image { get; init; }

    /// <summary>
    /// Mutable so the scan-pane overlay can toggle per-box exclusion in place
    /// and append user-drawn boxes. Replaced wholesale when detection re-runs.
    /// </summary>
    public required List<Prediction> BoxPredictions { get; init; }

    public IReadOnlyList<MaskedPrediction>? MaskedPredictions { get; init; }

    // Perf snapshot from the detection run that produced this cache entry.
    // Used to redraw the acrylic status chip with consistent timings when
    // selections change or when a cache hit re-displays this entry.
    public long PreprocessMs { get; init; }
    public long InferenceMs { get; init; }
    public long PostprocessMs { get; init; }
    public long TotalMs { get; init; }
    public bool HasSegmentation { get; init; }
}
