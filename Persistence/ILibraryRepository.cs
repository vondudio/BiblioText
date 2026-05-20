#nullable enable

using AIDevGallery.Sample.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AIDevGallery.Sample.Persistence;

public interface ILibraryRepository
{
    Task InitializeAsync();

    // Books
    Task<int> AddBookAsync(Book book);
    Task UpdateBookAsync(Book book);
    Task DeleteBookAsync(int bookId);
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
}
