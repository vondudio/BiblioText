using BiblioText.Cloud.Catalog;
using BiblioText.Cloud.Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace BiblioText.Cloud.Services;

/// <summary>
/// Catalog read side. Semantic search embeds the query with the same model used
/// at publish and ranks books by pgvector cosine distance; an empty query falls
/// back to a recency browse. Each result carries its physical copies (owner +
/// location) for the "who has it / where" view.
/// </summary>
public sealed class SearchService(CatalogDbContext db, IEmbeddingService embeddingService)
{
    public async Task<IReadOnlyList<BookResult>> SearchAsync(
        string? query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        if (string.IsNullOrWhiteSpace(query))
        {
            return await BrowseAsync(limit, cancellationToken);
        }

        Vector queryVector = await embeddingService.EmbedAsync(query, cancellationToken);

        var ranked = await db.Books
            .Where(b => b.Embedding != null)
            .OrderBy(b => b.Embedding!.CosineDistance(queryVector))
            .Take(limit)
            .Select(b => new
            {
                Book = b,
                Distance = (double?)b.Embedding!.CosineDistance(queryVector),
                Copies = b.Copies.ToList(),
            })
            .ToListAsync(cancellationToken);

        return ranked.Select(x => Map(x.Book, x.Distance)).ToList();
    }

    private async Task<IReadOnlyList<BookResult>> BrowseAsync(int limit, CancellationToken cancellationToken)
    {
        var books = await db.Books
            .OrderByDescending(b => b.UpdatedAt)
            .Take(limit)
            .Include(b => b.Copies)
            .ToListAsync(cancellationToken);

        return books.Select(b => Map(b, null)).ToList();
    }

    private static BookResult Map(Book book, double? distance) => new()
    {
        BookId = book.Id,
        Title = book.Title,
        Author = book.Author,
        ShortDescription = book.ShortDescription,
        LongDescription = book.LongDescription,
        CoverImageUrl = book.CoverImageUrl,
        DescriptionSourcesJson = book.DescriptionSourcesJson,
        Distance = distance,
        Copies = book.Copies
            .Select(c => new CopyResult
            {
                CopyId = c.Id,
                OwnerHousehold = c.OwnerHousehold,
                ShelfLocation = c.ShelfLocation,
                SpineImageUrl = c.SpineImageUrl,
            })
            .ToList(),
    };
}
