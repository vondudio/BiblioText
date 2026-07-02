#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiblioText.Contracts;
using BiblioText.Models;
using BiblioText.Persistence;
using BiblioText.Settings;

namespace BiblioText.Services;

/// <summary>
/// Pushes reviewed books to the BiblioText cloud. A book is "pending" when it has
/// never been published or its content hash differs from the hash recorded at the
/// last successful publish. Deletions of previously-published books are unpublished
/// via the repository's tombstone queue. Fully local/offline-safe: does nothing
/// unless a cloud endpoint is configured.
/// </summary>
public sealed class CloudPublishService
{
    private readonly ILibraryRepository _repository;
    private readonly ISettingsStore _settingsStore;

    public CloudPublishService(ILibraryRepository repository, ISettingsStore settingsStore)
    {
        _repository = repository;
        _settingsStore = settingsStore;
    }

    public sealed record SyncOutcome(bool Success, int Uploaded, int Unpublished, string? Error)
    {
        public static SyncOutcome Failed(string error) => new(false, 0, 0, error);
    }

    /// <summary>Number of books that need uploading plus deletions awaiting unpublish.</summary>
    public async Task<int> GetPendingCountAsync()
    {
        var settings = _settingsStore.Load();
        if (!settings.IsCloudConfigured)
        {
            return 0;
        }

        var locations = await LoadLocationNamesAsync();
        var books = await _repository.GetBooksAsync();
        var owner = settings.OwnerHousehold;

        var dirty = books.Count(b => IsDirty(b, owner, ShelfFor(b, locations)));
        var queued = (await _repository.GetCloudUnpublishQueueAsync()).Count;
        return dirty + queued;
    }

    /// <summary>
    /// Uploads all pending books and processes queued unpublishes. Reports progress
    /// as (completed, total) uploads. Safe to call repeatedly; idempotent.
    /// </summary>
    public async Task<SyncOutcome> SyncAllReviewedAsync(
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        if (!settings.IsCloudConfigured)
        {
            return SyncOutcome.Failed("Cloud endpoint is not configured. Set it in Settings.");
        }
        if (string.IsNullOrWhiteSpace(settings.StationId))
        {
            return SyncOutcome.Failed("Station ID is missing. Open Settings once to generate it.");
        }

        var locations = await LoadLocationNamesAsync();
        var books = await _repository.GetBooksAsync();
        var owner = settings.OwnerHousehold;

        var pending = books
            .Where(b => IsDirty(b, owner, ShelfFor(b, locations)))
            .ToList();
        var unpublishIds = await _repository.GetCloudUnpublishQueueAsync();

        if (pending.Count == 0 && unpublishIds.Count == 0)
        {
            return new SyncOutcome(true, 0, 0, null);
        }

        var payloads = new List<(Book book, BookPayload payload, string hash)>();
        foreach (var book in pending)
        {
            var shelf = ShelfFor(book, locations);
            payloads.Add((book, BuildPayload(book, owner, shelf), ComputeHash(book, owner, shelf)));
        }

        var batch = new PublishBatch
        {
            StationId = settings.StationId!,
            Books = payloads.Select(p => p.payload).ToList(),
            UnpublishStationBookIds = unpublishIds,
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var url = $"{settings.CloudEndpoint!.TrimEnd('/')}{PublishContract.PublishRoute}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(batch),
        };
        if (!string.IsNullOrWhiteSpace(settings.CloudOperatorToken))
        {
            request.Headers.Add("X-Operator-Token", settings.CloudOperatorToken);
        }

        progress?.Report((0, payloads.Count));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return SyncOutcome.Failed($"Could not reach the cloud: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(response);
            return SyncOutcome.Failed($"Cloud rejected the sync ({(int)response.StatusCode}). {detail}");
        }

        // Persist sync state now that the batch was accepted as a whole.
        var syncedAt = DateTime.UtcNow;
        var completed = 0;
        foreach (var (book, _, hash) in payloads)
        {
            await _repository.MarkBookSyncedAsync(book.Id, hash, syncedAt);
            progress?.Report((++completed, payloads.Count));
        }

        if (unpublishIds.Count > 0)
        {
            await _repository.ClearCloudUnpublishQueueAsync(unpublishIds);
        }

        return new SyncOutcome(true, payloads.Count, unpublishIds.Count, null);
    }

    private bool IsDirty(Book book, string? owner, string? shelf)
    {
        if (book.CloudSyncedAt is null || string.IsNullOrEmpty(book.SyncHash))
        {
            return true;
        }
        return !string.Equals(book.SyncHash, ComputeHash(book, owner, shelf), StringComparison.Ordinal);
    }

    private static string ShelfFor(Book book, IReadOnlyDictionary<int, string> locations) =>
        book.LocationId is int id && locations.TryGetValue(id, out var name) ? name : string.Empty;

    private async Task<Dictionary<int, string>> LoadLocationNamesAsync()
    {
        var locations = await _repository.GetLocationsAsync();
        return locations.ToDictionary(l => l.Id, l => l.Name);
    }

    private static BookPayload BuildPayload(Book book, string? owner, string? shelf) => new()
    {
        StationBookId = book.StationBookId ?? Guid.NewGuid().ToString("n"),
        Title = book.Title,
        Author = book.Author,
        ShortDescription = book.ShortDescription,
        LongDescription = book.LongDescription,
        OwnerHousehold = string.IsNullOrWhiteSpace(owner) ? null : owner,
        ShelfLocation = string.IsNullOrWhiteSpace(shelf) ? null : shelf,
        SpineImage = LoadSpineImage(book.SpineImagePath),
        CoverImage = LoadCoverImage(book.DescriptionSourcesJson),
        BookshelfImage = LoadInlineImage(book.BookshelfImagePath),
        SpineBoxNorm = string.IsNullOrWhiteSpace(book.SpineBoxNorm) ? null : book.SpineBoxNorm,
        DescriptionSourcesJson = book.DescriptionSourcesJson,
    };

    private static ImagePayload? LoadSpineImage(string? path) => LoadInlineImage(path);

    private static ImagePayload? LoadInlineImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            return new ImagePayload
            {
                FileName = Path.GetFileName(path),
                ContentType = ContentTypeFor(path),
                ContentBase64 = Convert.ToBase64String(bytes),
            };
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ImagePayload? LoadCoverImage(string? sourcesJson)
    {
        var url = ExtractCoverUrl(sourcesJson);
        return url is null ? null : new ImagePayload { Url = url };
    }

    private static string? ExtractCoverUrl(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
        {
            return null;
        }

        try
        {
            var sources = JsonSerializer.Deserialize<List<CoverSource>>(sourcesJson);
            return sources?
                .Select(s => s.CoverUrl)
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    /// <summary>
    /// Stable content hash over the fields that end up in the cloud payload. Image
    /// bytes are represented by the spine path (a new crop writes a new path), which
    /// keeps hashing cheap while still catching image changes.
    /// </summary>
    private static string ComputeHash(Book book, string? owner, string? shelf)
    {
        var material = string.Join(
            "\u001f",
            book.Title,
            book.Author,
            book.ShortDescription,
            book.LongDescription,
            owner,
            shelf,
            book.SpineImagePath,
            book.BookshelfImagePath,
            book.SpineBoxNorm,
            ExtractCoverUrl(book.DescriptionSourcesJson));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            return body.Length > 300 ? body[..300] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class CoverSource
    {
        public string? CoverUrl { get; set; }
    }
}
