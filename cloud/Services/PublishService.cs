using BiblioText.Cloud.Catalog;
using BiblioText.Cloud.Catalog.Entities;
using BiblioText.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Applies a station's <see cref="PublishBatch"/> to the catalog: idempotent
/// upsert of canonical <see cref="Book"/> rows (by title+author key) and physical
/// <see cref="Copy"/> rows (by station id + station book id), (re)embedding when
/// the searchable text changes, plus unpublish of removed copies and cleanup of
/// orphaned books.
/// </summary>
public sealed class PublishService(
    CatalogDbContext db,
    IEmbeddingService embeddingService,
    IImageStorageService imageStorage)
{
    public async Task<PublishResult> ApplyAsync(PublishBatch batch, CancellationToken cancellationToken = default)
    {
        if (batch.SchemaVersion != PublishContract.SchemaVersion)
        {
            return new PublishResult
            {
                Accepted = false,
                Message = $"Unsupported schema version {batch.SchemaVersion}; expected {PublishContract.SchemaVersion}.",
            };
        }

        if (string.IsNullOrWhiteSpace(batch.StationId))
        {
            return new PublishResult { Accepted = false, Message = "Missing stationId." };
        }

        var upserted = 0;
        foreach (var payload in batch.Books)
        {
            await UpsertBookAsync(batch.StationId, payload, cancellationToken);
            upserted++;
        }

        var unpublished = 0;
        foreach (var stationBookId in batch.UnpublishStationBookIds)
        {
            if (await UnpublishAsync(batch.StationId, stationBookId, cancellationToken))
            {
                unpublished++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new PublishResult
        {
            Accepted = true,
            UpsertedCount = upserted,
            UnpublishedCount = unpublished,
        };
    }

    private async Task UpsertBookAsync(string stationId, BookPayload payload, CancellationToken cancellationToken)
    {
        var key = CatalogKey.Normalize(payload.Title, payload.Author);
        if (key.Length == 0)
        {
            return; // No title — nothing to catalog.
        }

        var now = DateTimeOffset.UtcNow;

        var book = await db.Books.FirstOrDefaultAsync(b => b.NormalizedKey == key, cancellationToken);
        var isNewBook = book is null;
        if (book is null)
        {
            book = new Book
            {
                NormalizedKey = key,
                Title = payload.Title,
                CreatedAt = now,
            };
            db.Books.Add(book);
        }

        var newSearchText = BuildSearchText(payload);
        var oldSearchText = isNewBook ? null : BuildSearchText(book);
        var needsEmbedding = isNewBook || book.Embedding is null || newSearchText != oldSearchText;

        book.Title = payload.Title;
        book.Author = payload.Author;
        book.ShortDescription = payload.ShortDescription;
        book.LongDescription = payload.LongDescription;
        book.DescriptionSourcesJson = payload.DescriptionSourcesJson;
        book.UpdatedAt = now;

        var coverUrl = await imageStorage.StoreAsync(payload.CoverImage, $"covers/{key}", cancellationToken);
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            book.CoverImageUrl = coverUrl;
        }

        if (needsEmbedding)
        {
            book.Embedding = await embeddingService.EmbedAsync(newSearchText, cancellationToken);
        }

        // Upsert the physical copy keyed by (stationId, stationBookId).
        var copy = await db.Copies.FirstOrDefaultAsync(
            c => c.StationId == stationId && c.StationBookId == payload.StationBookId,
            cancellationToken);
        if (copy is null)
        {
            copy = new Copy
            {
                StationId = stationId,
                StationBookId = payload.StationBookId,
                Book = book,
                CreatedAt = now,
            };
            db.Copies.Add(copy);
        }
        else if (copy.BookId != book.Id || copy.Book != book)
        {
            // Title/author edited so the copy now maps to a different canonical book.
            copy.Book = book;
        }

        copy.OwnerHousehold = payload.OwnerHousehold;
        copy.ShelfLocation = payload.ShelfLocation;
        copy.SpineBoxNorm = payload.SpineBoxNorm;
        copy.UpdatedAt = now;

        var spineUrl = await imageStorage.StoreAsync(
            payload.SpineImage,
            $"spines/{stationId}/{payload.StationBookId}",
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(spineUrl))
        {
            copy.SpineImageUrl = spineUrl;
        }

        var shelfUrl = await imageStorage.StoreAsync(
            payload.BookshelfImage,
            $"shelves/{stationId}/{payload.StationBookId}",
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(shelfUrl))
        {
            copy.BookshelfImageUrl = shelfUrl;
        }
    }

    private async Task<bool> UnpublishAsync(string stationId, string stationBookId, CancellationToken cancellationToken)
    {
        var copy = await db.Copies
            .Include(c => c.Book)
            .ThenInclude(b => b.Copies)
            .FirstOrDefaultAsync(
                c => c.StationId == stationId && c.StationBookId == stationBookId,
                cancellationToken);
        if (copy is null)
        {
            return false;
        }

        var book = copy.Book;
        db.Copies.Remove(copy);

        // Remove the canonical book if this was its last copy.
        if (book.Copies.Count(c => c.Id != copy.Id) == 0)
        {
            db.Books.Remove(book);
        }

        return true;
    }

    private static string BuildSearchText(BookPayload payload) =>
        Combine(payload.Title, payload.Author, payload.LongDescription ?? payload.ShortDescription);

    private static string BuildSearchText(Book book) =>
        Combine(book.Title, book.Author, book.LongDescription ?? book.ShortDescription);

    private static string Combine(params string?[] parts) =>
        string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
