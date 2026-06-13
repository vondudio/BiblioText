#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiblioText.Models;
using BiblioText.Persistence;

namespace BiblioText.Services;

internal interface IReviewApplicationService
{
    Task<ReviewSaveResult> SaveAcceptedAsync(
        IReadOnlyList<ReviewCandidate> acceptedCandidates,
        int? locationId,
        string? sourceImagePath,
        CancellationToken ct = default);
}

internal sealed class ReviewApplicationService : IReviewApplicationService
{
    private readonly ILibraryRepository _repository;

    public ReviewApplicationService(ILibraryRepository repository)
    {
        _repository = repository;
    }

    public async Task<ReviewSaveResult> SaveAcceptedAsync(
        IReadOnlyList<ReviewCandidate> acceptedCandidates,
        int? locationId,
        string? sourceImagePath,
        CancellationToken ct = default)
    {
        var existingBookKeys = (await _repository.GetBooksAsync())
            .Select(b => NormalizeBookKey(b.Title, b.Author))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var booksToSave = new List<Book>(acceptedCandidates.Count);
        var stagedSpinePaths = new List<string>();
        var duplicateCount = 0;
        foreach (var candidate in acceptedCandidates)
        {
            ct.ThrowIfCancellationRequested();

            var book = new Book
            {
                Title = string.IsNullOrWhiteSpace(candidate.EditedTitle) ? candidate.DetectedTitle : candidate.EditedTitle,
                Author = string.IsNullOrWhiteSpace(candidate.EditedAuthor) ? candidate.DetectedAuthor : candidate.EditedAuthor,
                LocationId = locationId,
                DetectionIndex = candidate.Index > 0 ? candidate.Index : null,
                BookshelfImagePath = sourceImagePath,
                CreatedAt = DateTime.UtcNow
            };

            book.IsDuplicate = existingBookKeys.Contains(NormalizeBookKey(book.Title, book.Author));
            if (book.IsDuplicate)
            {
                duplicateCount++;
            }

            if (candidate.CropJpeg is { Length: > 0 })
            {
                var spinesFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BiblioText",
                    "spines");
                Directory.CreateDirectory(spinesFolder);
                var spinePath = Path.Combine(
                    spinesFolder,
                    $"spine_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{candidate.Index}_{Guid.NewGuid():N}.jpg");
                await File.WriteAllBytesAsync(spinePath, candidate.CropJpeg, ct);
                stagedSpinePaths.Add(spinePath);
                book.SpineImagePath = spinePath;
            }

            booksToSave.Add(book);
        }

        try
        {
            await _repository.AddBooksAsync(booksToSave);
        }
        catch (Exception saveException)
        {
            var cleanupFailures = new List<Exception>();
            foreach (var path in stagedSpinePaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception cleanupException)
                {
                    cleanupFailures.Add(cleanupException);
                }
            }

            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, saveException);
                throw new AggregateException(
                    "Book save failed and one or more staged spine files could not be cleaned up.",
                    cleanupFailures);
            }

            throw;
        }

        return new ReviewSaveResult(booksToSave, acceptedCandidates.ToList(), duplicateCount);
    }

    private static string NormalizeBookKey(string title, string? author)
    {
        static string NormalizePart(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value
                .Trim()
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray();
            return new string(chars);
        }

        var normalizedTitle = NormalizePart(title);
        if (normalizedTitle.Length == 0)
        {
            return string.Empty;
        }

        return $"{normalizedTitle}|{NormalizePart(author)}";
    }
}

internal sealed record ReviewSaveResult(
    List<Book> SavedBooks,
    List<ReviewCandidate> SavedCandidates,
    int DuplicateCount);
