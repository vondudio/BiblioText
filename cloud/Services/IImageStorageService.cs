using BiblioText.Contracts;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Persists image bytes that arrive in a publish payload and returns a hosted
/// URL the website can render. Implemented by Azure Blob in production and by
/// the local filesystem (wwwroot/uploads) for dev.
/// </summary>
public interface IImageStorageService
{
    /// <summary>
    /// Stores the image and returns its public URL, or null when
    /// <paramref name="image"/> is null/empty.
    /// </summary>
    /// <param name="image">Image payload from the publish batch.</param>
    /// <param name="key">Stable key (e.g. "covers/{bookKey}") used for naming.</param>
    Task<string?> StoreAsync(ImagePayload? image, string key, CancellationToken cancellationToken = default);
}
