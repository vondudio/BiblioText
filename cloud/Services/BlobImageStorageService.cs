using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BiblioText.Cloud.Catalog;
using BiblioText.Contracts;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Stores inline image bytes in Azure Blob storage and returns their public URL.
/// When a payload only carries a remote <see cref="ImagePayload.Url"/> (e.g. a
/// provider cover) it's passed through unchanged.
/// </summary>
public sealed class BlobImageStorageService : IImageStorageService
{
    private readonly BlobContainerClient _container;
    private readonly string? _publicBaseUrl;

    public BlobImageStorageService(BlobOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        _container = new BlobContainerClient(options.ConnectionString, options.Container);
        _publicBaseUrl = options.PublicBaseUrl?.TrimEnd('/');
    }

    public async Task<string?> StoreAsync(ImagePayload? image, string key, CancellationToken cancellationToken = default)
    {
        if (image is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(image.ContentBase64))
        {
            return string.IsNullOrWhiteSpace(image.Url) ? null : image.Url;
        }

        await _container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: cancellationToken);

        var bytes = Convert.FromBase64String(image.ContentBase64);
        var blobName = BuildBlobName(image, key);
        var blob = _container.GetBlobClient(blobName);

        using var stream = new MemoryStream(bytes);
        var headers = new BlobHttpHeaders
        {
            ContentType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/png" : image.ContentType,
        };
        await blob.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = headers }, cancellationToken);

        return _publicBaseUrl is null
            ? blob.Uri.ToString()
            : $"{_publicBaseUrl}/{blobName}";
    }

    private static string BuildBlobName(ImagePayload image, string key)
    {
        var extension = (image.ContentType?.ToLowerInvariant()) switch
        {
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };
        return $"{key}{extension}";
    }
}
