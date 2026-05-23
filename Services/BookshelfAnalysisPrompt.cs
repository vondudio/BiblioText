#nullable enable

namespace BiblioText.Services;

/// <summary>
/// Prompt templates for full-image bookshelf analysis via Azure OpenAI vision.
/// </summary>
internal static class BookshelfAnalysisPrompt
{
    public const string SystemPrompt = """
        You are a precise book-spine reader. You analyze photographs of bookshelves and identify
        every fully visible book spine. You return structured JSON with the detected books.
        
        Rules:
        - Only include books whose spine text is fully visible and readable.
        - Ignore partially hidden books, decorative items, and non-book objects.
        - For each book, extract the title and author if visible on the spine.
        - If the author is not visible, set it to null.
        - Order books from left to right (or top to bottom for horizontal shelves).
        - Set confidence to a value between 0.0 and 1.0 based on text legibility.
        """;

    public const string UserPrompt = """
        Analyze this bookshelf image. Identify all fully visible book spines and return a JSON object with this exact structure:
        
        {
          "books": [
            {
              "title": "Book Title",
              "author": "Author Name or null",
              "confidence": 0.95,
              "positionIndex": 0
            }
          ]
        }
        
        Return ONLY the JSON object. Include every readable book spine.
        """;
}
