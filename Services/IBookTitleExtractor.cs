using System.Threading;
using System.Threading.Tasks;

namespace AIDevGallery.Sample.Services;

/// <summary>
/// Submits one pre-processed <see cref="BookCrop"/> to a vision model and returns
/// the extracted title (or the model's free-form description). The real
/// implementation will call Azure OpenAI GPT-5.4 vision over HTTPS; see
/// <see cref="StubBookTitleExtractor"/> for the stub used during preprocessing
/// development.
/// </summary>
internal interface IBookTitleExtractor
{
    Task<string> ExtractAsync(BookCrop crop, CancellationToken ct = default);
}
