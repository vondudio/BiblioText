using BiblioText.Utils;

namespace BiblioText.Services;

/// <summary>
/// One pre-processed image ready to send to a vision model API. The JPEG bytes
/// are sized and quality-tuned for vision-model token economy (default ~1024 px
/// long edge, JPEG quality 85). For Azure OpenAI GPT-5.4 vision the payload
/// goes in as a base64 data URL: $"data:image/jpeg;base64,{Convert.ToBase64String(crop.Jpeg)}".
/// </summary>
internal sealed class BookCrop
{
    public required string Label { get; init; }
    public required float Confidence { get; init; }
    public required Box Box { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required byte[] Jpeg { get; init; }

    /// <summary>Set after a successful save-to-disk for inspection.</summary>
    public string? SavedPath { get; set; }
}
