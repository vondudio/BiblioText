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
    /// </summary>
    public async Task<List<int>> SearchAsync(string query, int maxResults = 20)
    {
        if (_indexer == null || !_isAvailable || string.IsNullOrWhiteSpace(query))
            return new List<int>();

        try
        {
            return await Task.Run(() =>
            {
                var textQuery = _indexer.CreateTextQuery(query);
                var matches = textQuery.GetNextMatches(maxResults);
                var bookIds = new List<int>();
                foreach (var match in matches)
                {
                    if (int.TryParse(match.ContentId, out int id))
                    {
                        bookIds.Add(id);
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
}
