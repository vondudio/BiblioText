using System.Text.Json.Serialization;

namespace BiblioText.Contracts;

/// <summary>
/// An image attached to a published book. The station may send either an inline
/// copy (<see cref="ContentBase64"/>, used for spine crops it owns) or a remote
/// <see cref="Url"/> (used for provider cover art it doesn't host). At least one
/// of the two should be set; the cloud persists inline content to Blob storage
/// and rewrites it to a hosted URL.
/// </summary>
public sealed class ImagePayload
{
    /// <summary>Original file name (e.g. "spine_0007.png"); used for the Blob key.</summary>
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    /// <summary>MIME type, e.g. "image/png".</summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    /// <summary>Base64-encoded image bytes when the station hosts the image inline.</summary>
    [JsonPropertyName("contentBase64")]
    public string? ContentBase64 { get; init; }

    /// <summary>Remote URL when the image is hosted elsewhere (e.g. a provider cover).</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}
