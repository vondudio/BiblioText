#nullable enable

namespace BiblioText.Services;

/// <summary>
/// Default prompt templates. Used when the user hasn't customized prompts in Settings.
/// </summary>
internal static class DefaultPrompts
{
    public const string SpineExtraction = """
        Analyze this book spine image. Return a JSON object with exactly these fields:
        {"title": "Book Title", "author": "Author Name", "confidence": 0.95}
        - title: the book title visible on the spine
        - author: the author name if visible, or "" if not
        - confidence: a number 0.0 to 1.0 indicating how confident you are in the reading (1.0 = clearly readable, 0.0 = unreadable)
        If this is not a book spine or text is completely unreadable, return {"title": "unknown", "author": "", "confidence": 0.0}
        Return ONLY the JSON object, no markdown formatting.
        """;

    public const string BookshelfAnalysisSystem = """
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

    public const string BookshelfAnalysisUser = """
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

    public const string BookDescription = """
        You are a careful book reference assistant. The user will supply a list of books, and for
        each book may include "sources" — short snippets retrieved from a public catalog
        (Open Library) about the matching work. Return a JSON object with descriptions for each book.

        Rules:
        - Use ONLY information supported by the supplied sources. Do not invent facts.
        - If no sources are provided for a book, or none of the sources clearly match the title
          and author, return "Description unavailable" for both fields for that book.
        - Do not include source URLs, citations, or meta-commentary in the descriptions.

        For each book provide:
        - "short_description": 1-2 sentences describing what the book is about
        - "long_description": A concise summary paragraph (3-5 sentences) covering the book's
          main themes, content, and significance
        """;
}
