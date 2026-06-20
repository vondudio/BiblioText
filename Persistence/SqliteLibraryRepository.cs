#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BiblioText.Models;
using Microsoft.Data.Sqlite;

namespace BiblioText.Persistence;

public sealed class SqliteLibraryRepository : ILibraryRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private SqliteConnection? _connection;

    public SqliteLibraryRepository()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiblioText");
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(folder, "library.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS locations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                description TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS scans (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                thumbnail_path TEXT,
                scanned_at TEXT NOT NULL DEFAULT (datetime('now')),
                book_count INTEGER NOT NULL DEFAULT 0,
                analysis_method TEXT,
                is_reviewed INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS books (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                author TEXT,
                short_description TEXT,
                long_description TEXT,
                scan_id INTEGER,
                location_id INTEGER,
                spine_image_path TEXT,
                bookshelf_image_path TEXT,
                detection_index INTEGER,
                spine_box_norm TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                modified_at TEXT,
                is_duplicate INTEGER NOT NULL DEFAULT 0,
                is_description_grounded INTEGER NOT NULL DEFAULT 0,
                description_sources_json TEXT,
                description_generated_at TEXT,
                notes TEXT,
                FOREIGN KEY (scan_id) REFERENCES scans(id),
                FOREIGN KEY (location_id) REFERENCES locations(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS image_hashes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                hash TEXT NOT NULL UNIQUE,
                file_path TEXT NOT NULL,
                imported_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        await cmd.ExecuteNonQueryAsync();

        // Migrate existing databases: add new columns if missing
        using var migrate = _connection!.CreateCommand();
        migrate.CommandText = """
            PRAGMA table_info(books);
            """;
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await migrate.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(reader.GetOrdinal("name")));
            }
        }

        if (!columns.Contains("bookshelf_image_path"))
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE books ADD COLUMN bookshelf_image_path TEXT;";
            await alter.ExecuteNonQueryAsync();
        }
        if (!columns.Contains("detection_index"))
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE books ADD COLUMN detection_index INTEGER;";
            await alter.ExecuteNonQueryAsync();
        }
        if (!columns.Contains("spine_box_norm"))
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE books ADD COLUMN spine_box_norm TEXT;";
            await alter.ExecuteNonQueryAsync();
        }
        if (!columns.Contains("short_description"))
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE books ADD COLUMN short_description TEXT;";
            await alter.ExecuteNonQueryAsync();
        }
        if (!columns.Contains("long_description"))
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE books ADD COLUMN long_description TEXT;";
            await alter.ExecuteNonQueryAsync();
        }
        if (!columns.Contains("is_description_grounded"))
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE books ADD COLUMN is_description_grounded INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync();
        }
        if (!columns.Contains("description_sources_json"))
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE books ADD COLUMN description_sources_json TEXT;";
            await alter.ExecuteNonQueryAsync();
        }
        if (!columns.Contains("description_generated_at"))
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE books ADD COLUMN description_generated_at TEXT;";
            await alter.ExecuteNonQueryAsync();
        }

        using var indexes = _connection!.CreateCommand();
        indexes.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_books_created_at ON books(created_at);
            CREATE INDEX IF NOT EXISTS idx_books_location_id ON books(location_id);
            CREATE INDEX IF NOT EXISTS idx_books_title ON books(title COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_books_author ON books(author COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_books_duplicate ON books(is_duplicate);
            """;
        await indexes.ExecuteNonQueryAsync();

        await EnsureFtsAsync();
    }

    /// <summary>
    /// Create the books_fts virtual table (FTS5), keep-in-sync triggers, and
    /// backfill it from existing rows on first run. Safe to call repeatedly.
    /// </summary>
    private async Task EnsureFtsAsync()
    {
        // Create the virtual table. Uses the default unicode61 tokenizer
        // (case-insensitive, accent-folding, diacritic stripping). Prefix
        // indexes 2,3,4 keep partial-word queries cheap.
        using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS books_fts USING fts5(
                    title,
                    author,
                    short_description,
                    long_description,
                    content='books',
                    content_rowid='id',
                    tokenize='unicode61 remove_diacritics 2',
                    prefix='2 3 4'
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Sync triggers (external-content pattern: the FTS index references
        // the books table by rowid; we just push row events through it).
        using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TRIGGER IF NOT EXISTS books_ai AFTER INSERT ON books BEGIN
                    INSERT INTO books_fts(rowid, title, author, short_description, long_description)
                    VALUES (new.id, new.title, new.author, new.short_description, new.long_description);
                END;
                CREATE TRIGGER IF NOT EXISTS books_ad AFTER DELETE ON books BEGIN
                    INSERT INTO books_fts(books_fts, rowid, title, author, short_description, long_description)
                    VALUES('delete', old.id, old.title, old.author, old.short_description, old.long_description);
                END;
                CREATE TRIGGER IF NOT EXISTS books_au AFTER UPDATE ON books BEGIN
                    INSERT INTO books_fts(books_fts, rowid, title, author, short_description, long_description)
                    VALUES('delete', old.id, old.title, old.author, old.short_description, old.long_description);
                    INSERT INTO books_fts(rowid, title, author, short_description, long_description)
                    VALUES (new.id, new.title, new.author, new.short_description, new.long_description);
                END;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Backfill if the FTS index is empty but books exist (first-run on an
        // existing DB, or after a schema upgrade that recreated books_fts).
        // For external-content FTS5 the canonical backfill idiom is the
        // 'rebuild' command — INSERT...SELECT against an external-content
        // table is unreliable. We gate on a stored version marker rather than
        // COUNT(*), because COUNT on an external-content table reads through
        // to the books table and would always equal the book count.
        const int currentFtsVersion = 1;
        int storedFtsVersion;
        using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            storedFtsVersion = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
        }
        if (storedFtsVersion < currentFtsVersion)
        {
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO books_fts(books_fts) VALUES('rebuild');";
                await cmd.ExecuteNonQueryAsync();
            }
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA user_version = {currentFtsVersion};";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    /// <summary>
    /// Convert a free-text search query into an FTS5 MATCH expression. Strips
    /// reserved punctuation, drops tokens shorter than 2 chars, and appends
    /// the prefix operator to each token so "gats" matches "gatsby".
    /// Returns null if no usable tokens remain (caller should fall back to LIKE).
    /// </summary>
    private static string? BuildFtsQuery(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else
            {
                if (sb.Length >= 2)
                {
                    tokens.Add(sb.ToString().ToLowerInvariant() + "*");
                }
                sb.Clear();
            }
        }
        if (sb.Length >= 2)
        {
            tokens.Add(sb.ToString().ToLowerInvariant() + "*");
        }

        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    // Books

    public async Task<int> AddBookAsync(Book book)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            return await AddBookCoreAsync(book);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<List<int>> AddBooksAsync(IReadOnlyList<Book> books)
    {
        var ids = new List<int>(books.Count);
        if (books.Count == 0)
        {
            return ids;
        }

        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var transaction = (SqliteTransaction)await _connection!.BeginTransactionAsync();
            try
            {
                foreach (var book in books)
                {
                    ids.Add(await AddBookCoreAsync(book, transaction));
                }

                await transaction.CommitAsync();
                return ids;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task<int> AddBookCoreAsync(Book book, SqliteTransaction? transaction = null)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO books (title, author, short_description, long_description, scan_id, location_id, spine_image_path, bookshelf_image_path, detection_index, spine_box_norm, created_at, is_duplicate, is_description_grounded, description_sources_json, description_generated_at, notes)
            VALUES (@title, @author, @shortDesc, @longDesc, @scanId, @locationId, @spinePath, @bookshelfPath, @detectionIndex, @spineBoxNorm, @createdAt, @isDuplicate, @isDescriptionGrounded, @descriptionSourcesJson, @descriptionGeneratedAt, @notes);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@title", book.Title);
        cmd.Parameters.AddWithValue("@author", (object?)book.Author ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@shortDesc", (object?)book.ShortDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@longDesc", (object?)book.LongDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scanId", (object?)book.ScanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@locationId", (object?)book.LocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@spinePath", (object?)book.SpineImagePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bookshelfPath", (object?)book.BookshelfImagePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@detectionIndex", (object?)book.DetectionIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@spineBoxNorm", (object?)book.SpineBoxNorm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", book.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@isDuplicate", book.IsDuplicate ? 1 : 0);
        cmd.Parameters.AddWithValue("@isDescriptionGrounded", book.IsDescriptionGrounded ? 1 : 0);
        cmd.Parameters.AddWithValue("@descriptionSourcesJson", (object?)book.DescriptionSourcesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@descriptionGeneratedAt", book.DescriptionGeneratedAt.HasValue ? book.DescriptionGeneratedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("@notes", (object?)book.Notes ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync();
        book.Id = Convert.ToInt32(result);
        return book.Id;
    }

    public async Task UpdateBookAsync(Book book)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                UPDATE books SET title=@title, author=@author, short_description=@shortDesc, long_description=@longDesc,
                    location_id=@locationId, spine_image_path=@spinePath, bookshelf_image_path=@bookshelfPath,
                    detection_index=@detectionIndex, spine_box_norm=@spineBoxNorm, modified_at=@modifiedAt, is_duplicate=@isDuplicate,
                    is_description_grounded=@isDescriptionGrounded, description_sources_json=@descriptionSourcesJson,
                    description_generated_at=@descriptionGeneratedAt, notes=@notes
                WHERE id=@id;
                """;
            cmd.Parameters.AddWithValue("@id", book.Id);
            cmd.Parameters.AddWithValue("@title", book.Title);
            cmd.Parameters.AddWithValue("@author", (object?)book.Author ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@shortDesc", (object?)book.ShortDescription ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@longDesc", (object?)book.LongDescription ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@locationId", (object?)book.LocationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@spinePath", (object?)book.SpineImagePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bookshelfPath", (object?)book.BookshelfImagePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@detectionIndex", (object?)book.DetectionIndex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@spineBoxNorm", (object?)book.SpineBoxNorm ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@isDuplicate", book.IsDuplicate ? 1 : 0);
            cmd.Parameters.AddWithValue("@isDescriptionGrounded", book.IsDescriptionGrounded ? 1 : 0);
            cmd.Parameters.AddWithValue("@descriptionSourcesJson", (object?)book.DescriptionSourcesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@descriptionGeneratedAt", book.DescriptionGeneratedAt.HasValue ? book.DescriptionGeneratedAt.Value.ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("@notes", (object?)book.Notes ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task DeleteBookAsync(int bookId)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM books WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", bookId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<List<Book>> GetBooksAsync(string? searchQuery = null, int? locationId = null)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();

            // For non-trivial searches we run BOTH FTS5 (relevance-ranked,
            // prefix-matched, handles multi-token AND) and LIKE (substring,
            // catches 'art' inside 'Heart'). Merged so FTS hits lead the
            // result set and LIKE-only matches are appended after.
            string? ftsExpr = string.IsNullOrWhiteSpace(searchQuery) ? null : BuildFtsQuery(searchQuery);
            if (ftsExpr != null)
            {
                var ordered = new List<Book>();
                var seen = new HashSet<int>();

                // FTS leg
                using (var ftsCmd = _connection!.CreateCommand())
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("SELECT b.* FROM books b JOIN books_fts f ON f.rowid = b.id ");
                    sb.Append("WHERE books_fts MATCH @match ");
                    ftsCmd.Parameters.AddWithValue("@match", ftsExpr);
                    if (locationId.HasValue)
                    {
                        sb.Append("AND b.location_id = @locId ");
                        ftsCmd.Parameters.AddWithValue("@locId", locationId.Value);
                    }
                    sb.Append("ORDER BY rank;");
                    ftsCmd.CommandText = sb.ToString();

                    try
                    {
                        using var reader = await ftsCmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            var book = ReadBook(reader);
                            if (seen.Add(book.Id))
                            {
                                ordered.Add(book);
                            }
                        }
                    }
                    catch (SqliteException)
                    {
                        // Bad FTS expression: skip FTS leg, rely on LIKE.
                    }
                }

                // LIKE leg (substring matches FTS prefix can miss)
                using (var likeCmd = _connection!.CreateCommand())
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("SELECT * FROM books WHERE ");
                    sb.Append("(title LIKE @search OR author LIKE @search OR short_description LIKE @search OR long_description LIKE @search) ");
                    likeCmd.Parameters.AddWithValue("@search", $"%{searchQuery}%");
                    if (locationId.HasValue)
                    {
                        sb.Append("AND location_id = @locId ");
                        likeCmd.Parameters.AddWithValue("@locId", locationId.Value);
                    }
                    sb.Append("ORDER BY created_at DESC;");
                    likeCmd.CommandText = sb.ToString();

                    using var reader = await likeCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var book = ReadBook(reader);
                        if (seen.Add(book.Id))
                        {
                            ordered.Add(book);
                        }
                    }
                }

                return ordered;
            }

            using var cmd = _connection!.CreateCommand();
            var whereClauses = new List<string>();
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                whereClauses.Add("(title LIKE @search OR author LIKE @search OR short_description LIKE @search OR long_description LIKE @search)");
                cmd.Parameters.AddWithValue("@search", $"%{searchQuery}%");
            }
            if (locationId.HasValue)
            {
                whereClauses.Add("location_id=@locId");
                cmd.Parameters.AddWithValue("@locId", locationId.Value);
            }

            var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            cmd.CommandText = $"SELECT * FROM books {whereClause} ORDER BY created_at DESC;";

            var books = new List<Book>();
            using var unfilteredReader = await cmd.ExecuteReaderAsync();
            while (await unfilteredReader.ReadAsync())
            {
                books.Add(ReadBook(unfilteredReader));
            }
            return books;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<Book?> GetBookByIdAsync(int id)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM books WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadBook(reader) : null;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    // Locations

    public async Task<int> AddLocationAsync(Location location)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO locations (name, description, created_at) VALUES (@name, @desc, @createdAt);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@name", location.Name);
            cmd.Parameters.AddWithValue("@desc", (object?)location.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", location.CreatedAt.ToString("o"));
            var result = await cmd.ExecuteScalarAsync();
            location.Id = Convert.ToInt32(result);
            return location.Id;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task UpdateLocationAsync(Location location)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE locations SET name=@name, description=@desc WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", location.Id);
            cmd.Parameters.AddWithValue("@name", location.Name);
            cmd.Parameters.AddWithValue("@desc", (object?)location.Description ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task DeleteLocationAsync(int locationId)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM locations WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", locationId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<List<Location>> GetLocationsAsync()
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM locations ORDER BY name;";
            var locations = new List<Location>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                locations.Add(new Location
                {
                    Id = reader.GetInt32(reader.GetOrdinal("id")),
                    Name = reader.GetString(reader.GetOrdinal("name")),
                    Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                    CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")))
                });
            }
            return locations;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    // Scans

    public async Task<int> AddScanAsync(Scan scan)
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO scans (file_path, thumbnail_path, scanned_at, book_count, analysis_method, is_reviewed)
                VALUES (@filePath, @thumbPath, @scannedAt, @bookCount, @method, @reviewed);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@filePath", scan.FilePath);
            cmd.Parameters.AddWithValue("@thumbPath", (object?)scan.ThumbnailPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@scannedAt", scan.ScannedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@bookCount", scan.BookCount);
            cmd.Parameters.AddWithValue("@method", (object?)scan.AnalysisMethod ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reviewed", scan.IsReviewed ? 1 : 0);
            var result = await cmd.ExecuteScalarAsync();
            scan.Id = Convert.ToInt32(result);
            return scan.Id;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<List<Scan>> GetScansAsync()
    {
        await _dbGate.WaitAsync();
        try
        {
            EnsureConnected();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM scans ORDER BY scanned_at DESC;";
            var scans = new List<Scan>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                scans.Add(new Scan
                {
                    Id = reader.GetInt32(reader.GetOrdinal("id")),
                    FilePath = reader.GetString(reader.GetOrdinal("file_path")),
                    ThumbnailPath = reader.IsDBNull(reader.GetOrdinal("thumbnail_path")) ? null : reader.GetString(reader.GetOrdinal("thumbnail_path")),
                    ScannedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("scanned_at"))),
                    BookCount = reader.GetInt32(reader.GetOrdinal("book_count")),
                    AnalysisMethod = reader.IsDBNull(reader.GetOrdinal("analysis_method")) ? null : reader.GetString(reader.GetOrdinal("analysis_method")),
                    IsReviewed = reader.GetInt32(reader.GetOrdinal("is_reviewed")) == 1
                });
            }
            return scans;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private static Book ReadBook(SqliteDataReader reader)
    {
        return new Book
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Author = reader.IsDBNull(reader.GetOrdinal("author")) ? null : reader.GetString(reader.GetOrdinal("author")),
            ShortDescription = reader.IsDBNull(reader.GetOrdinal("short_description")) ? null : reader.GetString(reader.GetOrdinal("short_description")),
            LongDescription = reader.IsDBNull(reader.GetOrdinal("long_description")) ? null : reader.GetString(reader.GetOrdinal("long_description")),
            ScanId = reader.IsDBNull(reader.GetOrdinal("scan_id")) ? null : reader.GetInt32(reader.GetOrdinal("scan_id")),
            LocationId = reader.IsDBNull(reader.GetOrdinal("location_id")) ? null : reader.GetInt32(reader.GetOrdinal("location_id")),
            SpineImagePath = reader.IsDBNull(reader.GetOrdinal("spine_image_path")) ? null : reader.GetString(reader.GetOrdinal("spine_image_path")),
            BookshelfImagePath = reader.IsDBNull(reader.GetOrdinal("bookshelf_image_path")) ? null : reader.GetString(reader.GetOrdinal("bookshelf_image_path")),
            DetectionIndex = reader.IsDBNull(reader.GetOrdinal("detection_index")) ? null : reader.GetInt32(reader.GetOrdinal("detection_index")),
            SpineBoxNorm = reader.IsDBNull(reader.GetOrdinal("spine_box_norm")) ? null : reader.GetString(reader.GetOrdinal("spine_box_norm")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            ModifiedAt = reader.IsDBNull(reader.GetOrdinal("modified_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("modified_at"))),
            IsDuplicate = reader.GetInt32(reader.GetOrdinal("is_duplicate")) == 1,
            IsDescriptionGrounded = reader.GetInt32(reader.GetOrdinal("is_description_grounded")) == 1,
            DescriptionSourcesJson = reader.IsDBNull(reader.GetOrdinal("description_sources_json")) ? null : reader.GetString(reader.GetOrdinal("description_sources_json")),
            DescriptionGeneratedAt = reader.IsDBNull(reader.GetOrdinal("description_generated_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("description_generated_at"))),
            Notes = reader.IsDBNull(reader.GetOrdinal("notes")) ? null : reader.GetString(reader.GetOrdinal("notes"))
        };
    }

    private void EnsureConnected()
    {
        if (_connection == null)
            throw new InvalidOperationException("Repository not initialized. Call InitializeAsync first.");
    }

    // Image hashes (dedup)

    public async Task<bool> ImageHashExistsAsync(string hash)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM image_hashes WHERE hash = @hash;";
        cmd.Parameters.AddWithValue("@hash", hash);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    public async Task AddImageHashAsync(string hash, string filePath)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO image_hashes (hash, file_path)
            VALUES (@hash, @filePath);
            """;
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.Parameters.AddWithValue("@filePath", filePath);
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _dbGate.Dispose();
    }
}
