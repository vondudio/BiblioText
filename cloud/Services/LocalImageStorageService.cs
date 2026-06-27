using BiblioText.Contracts;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Dev fallback that writes inline image bytes under <c>wwwroot/uploads</c> and
/// returns an app-relative URL. Used when Blob storage isn't configured so the
/// pipeline runs locally without Azure.
/// </summary>
public sealed class LocalImageStorageService : IImageStorageService
{
    private readonly string _rootPath;
    private const string UploadsFolder = "uploads";

    public LocalImageStorageService(IWebHostEnvironment environment)
    {
        var webRoot = environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        _rootPath = Path.Combine(webRoot, UploadsFolder);
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

        var extension = (image.ContentType?.ToLowerInvariant()) switch
        {
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };

        var relativePath = $"{key}{extension}";
        var fullPath = Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var bytes = Convert.FromBase64String(image.ContentBase64);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

        return $"/{UploadsFolder}/{relativePath}";
    }
}
