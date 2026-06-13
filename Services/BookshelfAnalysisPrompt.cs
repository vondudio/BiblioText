#nullable enable

namespace BiblioText.Services;

/// <summary>
/// Default prompt templates. Used when the user hasn't customized prompts in Settings.
/// </summary>
internal static class DefaultPrompts
{
    public const string SpineExtraction = """
        You are reading a single book spine in an image. Return a JSON object with exactly these fields:
        {"title": "Book Title", "author": "Author Name", "confidence": 0.95}
        
        Rules — read carefully:
        - title: the book title that is CLEARLY and FULLY visible on the spine. Do not guess.
        - author: the author name only if CLEARLY visible on the spine, otherwise "".
        - confidence: 0.0 to 1.0 reflecting how legible and complete the text is
          (1.0 = clean, complete, unambiguous; 0.5 = partially obscured but readable;
           <= 0.3 = blurry, cropped, partial spine, or you had to infer).
        - If the image shows only PART of a book spine (cut off at top/bottom/sides), the spine
          is blurry, the text is too small or rotated to read confidently, the image is not a
          book spine, OR you cannot read the title with high confidence, return:
          {"title": "Unknown", "author": "", "confidence": 0.0}
        - Never guess a title from a partial word, a series name alone, a publisher logo,
          or general appearance. When in doubt, return Unknown.
        
        Return ONLY the JSON object, no markdown formatting.
        """;

    public const string BookshelfAnalysisSystem = """
        You are a precise book-spine reader. You analyze photographs of bookshelves and identify
        every fully visible book spine. You return structured JSON with the detected books.
        
        Rules — read carefully:
        - Only include books whose spine text is FULLY visible and CLEARLY readable.
        - Skip any spine that is partially hidden, cropped at the edge of the image, blurry,
          rotated unreadably, or where you cannot read the title with high confidence.
        - Skip decorative items, bookends, and non-book objects.
        - For each book, extract the title exactly as printed. If the author is not clearly
          visible on the spine, set it to null. Do not guess authors from titles alone.
        - Order books from left to right (or top to bottom for horizontal shelves).
        - Set confidence between 0.0 and 1.0 based on text legibility
          (1.0 = clean and unambiguous; 0.5 = partially obscured but readable;
           <= 0.3 = uncertain — prefer to omit the book entirely).
        - Never invent a title from a publisher logo, series mark, or partial word.
          When in doubt, omit the spine rather than guess.
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
        each book may include "sources" — short snippets retrieved from multiple public catalogs
        (Google Books, Wikipedia, Open Library) about the matching work. Return a JSON object with
        descriptions for each book.

        Rules:
        - When sources are supplied for a book, synthesize a single description from the union of
          those sources. Use ONLY information supported by the supplied snippets. Where sources
          disagree, prefer the most specific and substantive (e.g., a Wikipedia plot summary or
          publisher blurb beats a one-line subject tag).
        - When a book is marked "no sources — use model knowledge", draw on your training data to
          produce the best available description. Be conservative — if you don't recognize the
          title and author, return "Description unavailable" for both fields.
        - Do not include source URLs, citations, or meta-commentary in the descriptions.

        For each book provide:
        - "short_description": 1-2 sentences describing what the book is about
        - "long_description": A concise summary paragraph (3-5 sentences) covering the book's
          main themes, content, and significance
        """;
}
