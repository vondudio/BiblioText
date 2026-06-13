#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Windows.Search.AppContentIndex;

namespace BiblioText.Services;

/// <summary>
/// Indexes book summaries using Windows AppContentIndex for semantic search.
/// Falls back gracefully if the semantic search capability is unavailable.
/// </summary>
internal sealed class SemanticSearchService
{
    private AppContentIndexer? _indexer;
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                var result = AppContentIndexer.GetOrCreateIndex("biblioTextBooks");
                if (result.Succeeded)
                {
                    _indexer = result.Indexer;
                    _isAvailable = true;
                }
            });
        }
        catch
        {
            _isAvailable = false;
        }
    }

    /// <summary>
    /// Index a book's summary for semantic search.
    /// </summary>
    public async Task IndexBookAsync(int bookId, string title, string? author, string? longDescription)
    {
        if (_indexer == null || !_isAvailable) return;

        try
        {
            await Task.Run(() =>
            {
                var searchableText = $"{title}. {author ?? ""}. {longDescription ?? ""}";
                var content = AppManagedIndexableAppContent.CreateFromString(
                    bookId.ToString(), searchableText);
                _indexer.AddOrUpdate(content);
            });
        }
        catch
        {
            // Silently fail — search is a non-critical feature
        }
    }

    /// <summary>
    /// Remove a book from the search index.
    /// </summary>
    public async Task RemoveBookAsync(int bookId)
    {
        if (_indexer == null || !_isAvailable) return;

        try
        {
            await Task.Run(() =>
            {
                _indexer.RemoveContentItem(bookId.ToString());
            });
        }
        catch
        {
            // Silently fail
        }
    }

    /// <summary>
    /// Search for books by semantic query. Returns matching book IDs.
    /// The Windows AppContentIndex caps each GetNextMatches call at ~200,
    /// so this method pages until either the index is exhausted or
    /// <paramref name="maxResults"/> is reached.
    /// </summary>
    public async Task<List<int>> SearchAsync(string query, int maxResults = 1000)
    {
        if (_indexer == null || !_isAvailable || string.IsNullOrWhiteSpace(query))
            return new List<int>();

        try
        {
            return await Task.Run(() =>
            {
                var textQuery = _indexer.CreateTextQuery(query);
                var bookIds = new List<int>();
                const int pageSize = 200;
                while (bookIds.Count < maxResults)
                {
                    int remaining = maxResults - bookIds.Count;
                    int request = remaining < pageSize ? remaining : pageSize;
                    var matches = textQuery.GetNextMatches(request);
                    if (matches == null || matches.Count == 0)
                    {
                        break;
                    }
                    foreach (var match in matches)
                    {
                        if (int.TryParse(match.ContentId, out int id))
                        {
                            bookIds.Add(id);
                        }
                    }
                    if (matches.Count < request)
                    {
                        // Index exhausted before we hit the cap.
                        break;
                    }
                }
                return bookIds;
            });
        }
        catch
        {
            return new List<int>();
        }
    }

    /// <summary>
    /// Re-add every supplied book to the AppContentIndex. Used at startup to
    /// catch books saved before the indexer was wired up, or whose
    /// IndexBookAsync call failed transiently. AddOrUpdate is idempotent, so
    /// calling this on an already-indexed library is harmless.
    /// </summary>
    public async Task ReindexAllAsync(IEnumerable<(int Id, string Title, string? Author, string? LongDescription)> books)
    {
        if (_indexer == null || !_isAvailable) return;

        try
        {
            await Task.Run(() =>
            {
                foreach (var b in books)
                {
                    try
                    {
                        var searchableText = $"{b.Title}. {b.Author ?? ""}. {b.LongDescription ?? ""}";
                        var content = AppManagedIndexableAppContent.CreateFromString(
                            b.Id.ToString(), searchableText);
                        _indexer.AddOrUpdate(content);
                    }
                    catch
                    {
                        // Skip any one book that fails; don't abort the sweep.
                    }
                }
            });
        }
        catch
        {
            // Silently fail — background sweep, non-critical.
        }
    }
}
