#nullable enable

using BiblioText.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BiblioText.Persistence;

public interface ILibraryRepository
{
    Task InitializeAsync();

    /// <summary>Absolute path to the backing SQLite database file.</summary>
    string DatabasePath { get; }

    // Books
    Task<int> AddBookAsync(Book book);
    Task<List<int>> AddBooksAsync(IReadOnlyList<Book> books);
    Task UpdateBookAsync(Book book);
    Task DeleteBookAsync(int bookId);
    Task ResetLibraryAsync();
    Task RecomputeDuplicateFlagsAsync();
    Task<List<Book>> GetBooksAsync(string? searchQuery = null, int? locationId = null);
    Task<Book?> GetBookByIdAsync(int id);

    // Locations
    Task<int> AddLocationAsync(Location location);
    Task UpdateLocationAsync(Location location);
    Task DeleteLocationAsync(int locationId);
    Task<List<Location>> GetLocationsAsync();

    // Scans
    Task<int> AddScanAsync(Scan scan);
    Task<List<Scan>> GetScansAsync();

    // Image hashes (dedup)
    Task<bool> ImageHashExistsAsync(string hash);
    Task AddImageHashAsync(string hash, string filePath);
}
