#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiblioText.Models;
using BiblioText.Persistence;
using BiblioText.Settings;

namespace BiblioText.Services;

/// <summary>
/// App-wide queue for AI book-description generation. Jobs run on background
/// threads so they survive page navigation, and a pending-count is exposed so
/// the main window can show a persistent "N descriptions pending" badge that
/// stays visible no matter which page is in view.
/// </summary>
internal sealed class BackgroundDescriptionService
{
    private readonly ILibraryRepository _repo;
    private readonly ISettingsStore _settingsStore;
    private readonly SemanticSearchService _search;
    private readonly BookDescriptionService _descService;

    private int _pendingBooks;

    public BackgroundDescriptionService(
        ILibraryRepository repo,
        ISettingsStore settingsStore,
        SemanticSearchService search,
        BookDescriptionService descService)
    {
        _repo = repo;
        _settingsStore = settingsStore;
        _search = search;
        _descService = descService;
    }

    /// <summary>Books still awaiting description completion across all jobs.</summary>
    public int PendingCount => Volatile.Read(ref _pendingBooks);

    /// <summary>
    /// Raised whenever <see cref="PendingCount"/> changes. May fire on a
    /// background thread — subscribers must marshal to their UI thread.
    /// </summary>
    public event EventHandler? PendingChanged;

    /// <summary>
    /// Queue a batch of freshly-saved books for description generation. Returns
    /// immediately; the work runs in the background and updates the DB and
    /// semantic index as it completes.
    /// </summary>
    public void Enqueue(IReadOnlyList<Book> books)
    {
        if (books == null || books.Count == 0) return;

        // Snapshot so later caller mutations can't affect the in-flight job.
        var batch = books.ToList();
        Interlocked.Add(ref _pendingBooks, batch.Count);
        RaiseChanged();

        _ = Task.Run(() => RunAsync(batch));
    }

    private async Task RunAsync(List<Book> books)
    {
        try
        {
            var settings = _settingsStore.Load();
            if (!settings.IsConfigured) return;

            var bookList = books.Select(b => (b.Id, b.Title, b.Author)).ToList();
            var result = await _descService.GetDescriptionsResultAsync(bookList);
            if (!result.IsSuccess) return;

            foreach (var desc in result.Descriptions)
            {
                var book = books.FirstOrDefault(b => b.Id == desc.BookId);
                if (book == null) continue;

                book.ShortDescription = desc.ShortDescription;
                book.LongDescription = desc.LongDescription;
                book.IsDescriptionGrounded = desc.IsGrounded;
                book.DescriptionSourcesJson = desc.SourcesJson;
                book.DescriptionGeneratedAt = desc.GeneratedAt;
                await _repo.UpdateBookAsync(book);

                await _search.IndexBookAsync(book.Id, book.Title, book.Author, book.LongDescription);
            }
        }
        catch
        {
            // Background work — swallow failures; affected books simply remain
            // without descriptions and can be regenerated later.
        }
        finally
        {
            Interlocked.Add(ref _pendingBooks, -books.Count);
            RaiseChanged();
        }
    }

    private void RaiseChanged() => PendingChanged?.Invoke(this, EventArgs.Empty);
}
