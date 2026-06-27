using System.Threading;
using System.Threading.Tasks;

namespace BiblioText.Services;

/// <summary>Result from title extraction including AI confidence.</summary>
internal sealed class ExtractionResult
{
    public required string Title { get; init; }
    public required string Author { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
/// Submits one pre-processed <see cref="BookCrop"/> to a vision model and returns
/// the extracted title, author, and confidence. The real implementation calls
/// Azure OpenAI GPT-5.4 vision over HTTPS.
/// </summary>
internal interface IBookTitleExtractor
{
    Task<ExtractionResult> ExtractAsync(BookCrop crop, CancellationToken ct = default);
}
