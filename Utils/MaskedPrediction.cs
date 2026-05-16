namespace AIDevGallery.Sample.Utils;

/// <summary>
/// One YOLO26-seg detection. Carries the same Box/Label/Confidence as a
/// <see cref="Prediction"/> plus a soft per-pixel mask sized to the bounding box
/// in original-image coordinates (Width = Box.Xmax-Xmin, Height = Box.Ymax-Ymin).
/// Mask values are 0..255 (sigmoid-quantized); &gt;= 128 means "inside the object".
/// </summary>
internal sealed class MaskedPrediction
{
    public required Box Box { get; init; }
    public required string Label { get; init; }
    public required float Confidence { get; init; }
    public required byte[] Mask { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Drops the mask data and returns a regular Prediction (for box-only render paths).</summary>
    public Prediction ToPrediction() => new()
    {
        Box = Box,
        Label = Label,
        Confidence = Confidence,
    };
}
