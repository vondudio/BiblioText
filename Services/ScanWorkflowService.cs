#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIDevGallery.Sample.Models;
using AIDevGallery.Sample.Persistence;

namespace AIDevGallery.Sample.Services;

/// <summary>
/// Orchestrates the scan workflow: takes detection results (from YOLO crops or full-image AI)
/// and produces ReviewCandidates for the Review page.
/// </summary>
internal sealed class ScanWorkflowService
{
    private readonly IBookTitleExtractor _titleExtractor;
    private readonly AzureOpenAiAnalysisClient _analysisClient;
    private readonly ILibraryRepository _repository;

    public ScanWorkflowService(
        IBookTitleExtractor titleExtractor,
        AzureOpenAiAnalysisClient analysisClient,
        ILibraryRepository repository)
    {
        _titleExtractor = titleExtractor;
        _analysisClient = analysisClient;
        _repository = repository;
    }

    /// <summary>
    /// Process YOLO-detected crops: extract title from each crop via Azure OpenAI per-crop vision.
    /// </summary>
    public async Task<List<ReviewCandidate>> ProcessCropsAsync(
        IReadOnlyList<BookCrop> crops,
        string sourceFilePath,
        CancellationToken ct = default)
    {
        var candidates = new List<ReviewCandidate>();

        // Record the scan
        var scan = new Scan
        {
            FilePath = sourceFilePath,
            ScannedAt = DateTime.UtcNow,
            BookCount = crops.Count,
            AnalysisMethod = "yolo_crop"
        };
        await _repository.AddScanAsync(scan);

        for (int i = 0; i < crops.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var crop = crops[i];
            var title = await _titleExtractor.ExtractAsync(crop, ct);

            // Split "title, author" format if present
            string detectedTitle = title;
            string? detectedAuthor = null;
            if (title.Contains(',') && !title.StartsWith("("))
            {
                var parts = title.Split(',', 2);
                detectedTitle = parts[0].Trim();
                detectedAuthor = parts.Length > 1 ? parts[1].Trim() : null;
            }

            candidates.Add(new ReviewCandidate
            {
                Index = i,
                DetectedTitle = detectedTitle,
                DetectedAuthor = detectedAuthor,
                EditedTitle = detectedTitle,
                EditedAuthor = detectedAuthor,
                IsAccepted = true,
                CropJpeg = crop.Jpeg,
                PixelWidth = crop.PixelWidth,
                PixelHeight = crop.PixelHeight,
                Confidence = crop.Confidence
            });
        }

        return candidates;
    }

    /// <summary>
    /// Full-image AI analysis: send the entire bookshelf photo to Azure OpenAI for detection.
    /// </summary>
    public async Task<List<ReviewCandidate>> AnalyzeFullImageAsync(
        byte[] imageJpeg,
        string sourceFilePath,
        CancellationToken ct = default)
    {
        var result = await _analysisClient.AnalyzeAsync(imageJpeg, ct);

        if (!result.IsSuccess || result.Books == null || result.Books.Count == 0)
        {
            // Return empty or throw based on error
            return new List<ReviewCandidate>();
        }

        // Record the scan
        var scan = new Scan
        {
            FilePath = sourceFilePath,
            ScannedAt = DateTime.UtcNow,
            BookCount = result.Books.Count,
            AnalysisMethod = "full_image_ai"
        };
        await _repository.AddScanAsync(scan);

        var candidates = new List<ReviewCandidate>();
        for (int i = 0; i < result.Books.Count; i++)
        {
            var book = result.Books[i];
            candidates.Add(new ReviewCandidate
            {
                Index = i,
                DetectedTitle = book.Title,
                DetectedAuthor = book.Author,
                EditedTitle = book.Title,
                EditedAuthor = book.Author,
                IsAccepted = true,
                Confidence = book.Confidence
            });
        }

        return candidates;
    }
}
