#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AIDevGallery.Sample.Models;
using Microsoft.Data.Sqlite;

namespace AIDevGallery.Sample.Persistence;

public sealed class SqliteLibraryRepository : ILibraryRepository, IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public SqliteLibraryRepository()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YOLO_Object_DetectionSample");
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
                scan_id INTEGER,
                location_id INTEGER,
                spine_image_path TEXT,
                bookshelf_image_path TEXT,
                detection_index INTEGER,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                modified_at TEXT,
                is_duplicate INTEGER NOT NULL DEFAULT 0,
                notes TEXT,
                FOREIGN KEY (scan_id) REFERENCES scans(id),
                FOREIGN KEY (location_id) REFERENCES locations(id) ON DELETE SET NULL
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
    }

    // Books

    public async Task<int> AddBookAsync(Book book)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO books (title, author, scan_id, location_id, spine_image_path, bookshelf_image_path, detection_index, created_at, is_duplicate, notes)
            VALUES (@title, @author, @scanId, @locationId, @spinePath, @bookshelfPath, @detectionIndex, @createdAt, @isDuplicate, @notes);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@title", book.Title);
        cmd.Parameters.AddWithValue("@author", (object?)book.Author ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scanId", (object?)book.ScanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@locationId", (object?)book.LocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@spinePath", (object?)book.SpineImagePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bookshelfPath", (object?)book.BookshelfImagePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@detectionIndex", (object?)book.DetectionIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", book.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@isDuplicate", book.IsDuplicate ? 1 : 0);
        cmd.Parameters.AddWithValue("@notes", (object?)book.Notes ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync();
        book.Id = Convert.ToInt32(result);
        return book.Id;
    }

    public async Task UpdateBookAsync(Book book)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            UPDATE books SET title=@title, author=@author, location_id=@locationId,
                spine_image_path=@spinePath, bookshelf_image_path=@bookshelfPath,
                detection_index=@detectionIndex, modified_at=@modifiedAt, is_duplicate=@isDuplicate, notes=@notes
            WHERE id=@id;
            """;
        cmd.Parameters.AddWithValue("@id", book.Id);
        cmd.Parameters.AddWithValue("@title", book.Title);
        cmd.Parameters.AddWithValue("@author", (object?)book.Author ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@locationId", (object?)book.LocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@spinePath", (object?)book.SpineImagePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bookshelfPath", (object?)book.BookshelfImagePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@detectionIndex", (object?)book.DetectionIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@isDuplicate", book.IsDuplicate ? 1 : 0);
        cmd.Parameters.AddWithValue("@notes", (object?)book.Notes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteBookAsync(int bookId)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM books WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", bookId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Book>> GetBooksAsync(string? searchQuery = null, int? locationId = null)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            where.Add("(title LIKE @search OR author LIKE @search)");
            cmd.Parameters.AddWithValue("@search", $"%{searchQuery}%");
        }
        if (locationId.HasValue)
        {
            where.Add("location_id=@locId");
            cmd.Parameters.AddWithValue("@locId", locationId.Value);
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"SELECT * FROM books {whereClause} ORDER BY created_at DESC;";

        var books = new List<Book>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            books.Add(ReadBook(reader));
        }
        return books;
    }

    public async Task<Book?> GetBookByIdAsync(int id)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM books WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadBook(reader) : null;
    }

    // Locations

    public async Task<int> AddLocationAsync(Location location)
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

    public async Task UpdateLocationAsync(Location location)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "UPDATE locations SET name=@name, description=@desc WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", location.Id);
        cmd.Parameters.AddWithValue("@name", location.Name);
        cmd.Parameters.AddWithValue("@desc", (object?)location.Description ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteLocationAsync(int locationId)
    {
        EnsureConnected();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM locations WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", locationId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Location>> GetLocationsAsync()
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

    // Scans

    public async Task<int> AddScanAsync(Scan scan)
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

    public async Task<List<Scan>> GetScansAsync()
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

    private static Book ReadBook(SqliteDataReader reader)
    {
        return new Book
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Author = reader.IsDBNull(reader.GetOrdinal("author")) ? null : reader.GetString(reader.GetOrdinal("author")),
            ScanId = reader.IsDBNull(reader.GetOrdinal("scan_id")) ? null : reader.GetInt32(reader.GetOrdinal("scan_id")),
            LocationId = reader.IsDBNull(reader.GetOrdinal("location_id")) ? null : reader.GetInt32(reader.GetOrdinal("location_id")),
            SpineImagePath = reader.IsDBNull(reader.GetOrdinal("spine_image_path")) ? null : reader.GetString(reader.GetOrdinal("spine_image_path")),
            BookshelfImagePath = reader.IsDBNull(reader.GetOrdinal("bookshelf_image_path")) ? null : reader.GetString(reader.GetOrdinal("bookshelf_image_path")),
            DetectionIndex = reader.IsDBNull(reader.GetOrdinal("detection_index")) ? null : reader.GetInt32(reader.GetOrdinal("detection_index")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            ModifiedAt = reader.IsDBNull(reader.GetOrdinal("modified_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("modified_at"))),
            IsDuplicate = reader.GetInt32(reader.GetOrdinal("is_duplicate")) == 1,
            Notes = reader.IsDBNull(reader.GetOrdinal("notes")) ? null : reader.GetString(reader.GetOrdinal("notes"))
        };
    }

    private void EnsureConnected()
    {
        if (_connection == null)
            throw new InvalidOperationException("Repository not initialized. Call InitializeAsync first.");
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
