using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BiblioText.Models;
using BiblioText.Persistence;

namespace BiblioText.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly ObservableCollection<BookDisplay> _books = new();
    private readonly ObservableCollection<Location> _locations = new();
    private string _searchQuery = string.Empty;
    private int? _selectedLocationId;
    private string? _sortOption;
    private string? _statusFilter;

    public LibraryPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        BookList.ItemsSource = _books;
        LocationFilter.ItemsSource = _locations;
        StatusFilter.SelectedIndex = 0;
        this.Loaded += LibraryPage_Loaded;
    }

    private async void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var repo = App.LibraryRepository;
        if (repo == null) return;

        List<Book> books;

        // Try semantic search if query is non-trivial and service is available
        var searchService = App.SemanticSearchService;
        if (!string.IsNullOrWhiteSpace(_searchQuery) && searchService != null && searchService.IsAvailable)
        {
            var bookIds = await searchService.SearchAsync(_searchQuery);
            if (bookIds.Count > 0)
            {
                var allBooks = await repo.GetBooksAsync(locationId: _selectedLocationId);
                var idSet = new HashSet<int>(bookIds);
                books = allBooks.Where(b => idSet.Contains(b.Id)).ToList();
            }
            else
            {
                books = await repo.GetBooksAsync(_searchQuery, _selectedLocationId);
            }
        }
        else
        {
            books = await repo.GetBooksAsync(_searchQuery, _selectedLocationId);
        }

        var locations = await repo.GetLocationsAsync();

        books = ApplyStatusFilter(books);
        books = ApplySort(books);

        _books.Clear();
        foreach (var b in books)
        {
            _books.Add(new BookDisplay(b, locations.FirstOrDefault(l => l.Id == b.LocationId)?.Name));
        }

        _locations.Clear();
        _locations.Add(new Location { Id = 0, Name = "All Locations" });
        foreach (var loc in locations)
        {
            _locations.Add(loc);
        }

        EmptyState.Visibility = _books.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BookList.Visibility = _books.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionActions();
    }

    private List<Book> ApplySort(List<Book> books)
    {
        return _sortOption switch
        {
            "Title A→Z" => books.OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            "Title Z→A" => books.OrderByDescending(b => b.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            "Author A→Z" => books.OrderBy(b => b.Author ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            "Author Z→A" => books.OrderByDescending(b => b.Author ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            "Newest First" => books.OrderByDescending(b => b.CreatedAt).ToList(),
            "Oldest First" => books.OrderBy(b => b.CreatedAt).ToList(),
            _ => books
        };
    }

    private List<Book> ApplyStatusFilter(List<Book> books)
    {
        return _statusFilter switch
        {
            "Duplicates" => books.Where(b => b.IsDuplicate).ToList(),
            "Missing Descriptions" => books.Where(b => string.IsNullOrWhiteSpace(b.ShortDescription) && string.IsNullOrWhiteSpace(b.LongDescription)).ToList(),
            _ => books
        };
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _searchQuery = sender.Text;
            await RefreshAsync();
        }
    }

    private async void LocationFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocationFilter.SelectedItem is Location loc)
        {
            _selectedLocationId = loc.Id == 0 ? null : loc.Id;
            await RefreshAsync();
        }
    }

    private async void SortDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sortOption = SortDropdown.SelectedItem as string;
        await RefreshAsync();
    }

    private async void StatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _statusFilter = StatusFilter.SelectedItem as string;
        await RefreshAsync();
    }

    private void StartScanningButton_Click(object sender, RoutedEventArgs e)
    {
        App.Window?.NavigateToScan();
    }

    private void BookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionActions();
    }

    private async void DeleteBookBtn_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBooks();
        if (selectedBooks.Count == 0) return;

        await DeleteBooksAsync(selectedBooks);
    }

    private async void ChangeLocationBtn_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBooks();
        if (selectedBooks.Count == 0) return;

        await ChangeLocationForBooksAsync(selectedBooks);
    }

    private async void EditTitle_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBook(sender, out var book) is false) return;

        var input = new TextBox { Text = book.Title, PlaceholderText = "Book title" };
        var dialog = new ContentDialog
        {
            Title = "Edit Title",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }

        var repo = App.LibraryRepository;
        if (repo == null) return;

        var dbBook = await repo.GetBookByIdAsync(book.Id);
        if (dbBook == null) return;

        dbBook.Title = input.Text.Trim();
        await repo.UpdateBookAsync(dbBook);
        await RefreshAsync();
    }

    private async void EditAuthor_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBook(sender, out var book) is false) return;

        var input = new TextBox { Text = book.Author, PlaceholderText = "Author name" };
        var dialog = new ContentDialog
        {
            Title = "Edit Author",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var repo = App.LibraryRepository;
        if (repo == null) return;

        var dbBook = await repo.GetBookByIdAsync(book.Id);
        if (dbBook == null) return;

        dbBook.Author = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
        await repo.UpdateBookAsync(dbBook);
        await RefreshAsync();
    }

    private async void ChangeLocation_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBook(sender, out var book) is false) return;

        await ChangeLocationForBooksAsync(new[] { book });
    }

    private async void DeleteFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBook(sender, out var book) is false) return;

        await DeleteBookAsync(book);
    }

    private async void AddLocationBtn_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "Location name (e.g., Living Room Shelf)" };
        var dialog = new ContentDialog
        {
            Title = "Add Location",
            Content = input,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            var repo = App.LibraryRepository;
            if (repo != null)
            {
                await repo.AddLocationAsync(new Location { Name = input.Text.Trim(), CreatedAt = DateTime.UtcNow });
                await RefreshAsync();
            }
        }
    }

    private void SpineImage_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBook(sender, out var book) is false || string.IsNullOrWhiteSpace(book.BookshelfImagePath) || !File.Exists(book.BookshelfImagePath))
        {
            return;
        }

        BookshelfOverlayImage.Source = new BitmapImage(new Uri(book.BookshelfImagePath));

        // Overlay the spine scan on top of the bookshelf image
        if (!string.IsNullOrWhiteSpace(book.SpineImagePath) && File.Exists(book.SpineImagePath))
        {
            SpineOverlayImage.Source = new BitmapImage(new Uri(book.SpineImagePath));
            SpineOverlayBorder.Visibility = Visibility.Visible;
        }
        else
        {
            SpineOverlayImage.Source = null;
            SpineOverlayBorder.Visibility = Visibility.Collapsed;
        }

        BookshelfOverlay.Visibility = Visibility.Visible;
        BookshelfOverlayScroller.ChangeView(0, 0, 1.0f, disableAnimation: true);
    }

    private void DescriptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBook(sender, out var book) is false || sender is not FrameworkElement target)
        {
            return;
        }

        var flyout = new Flyout
        {
            Content = new ScrollViewer
            {
                MaxWidth = 420,
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = book.LongDescriptionDisplay,
                    TextWrapping = TextWrapping.WrapWholeWords
                }
            }
        };

        flyout.ShowAt(target);
    }

    private void CloseBookshelfOverlay_Click(object sender, RoutedEventArgs e)
    {
        CloseBookshelfOverlay();
    }

    private void BookshelfOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CloseBookshelfOverlay();
    }

    private void BookshelfOverlayContent_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Click anywhere (including on the image) closes the overlay
        CloseBookshelfOverlay();
    }

    private void CloseBookshelfOverlay()
    {
        BookshelfOverlay.Visibility = Visibility.Collapsed;
        BookshelfOverlayImage.Source = null;
        SpineOverlayImage.Source = null;
        SpineOverlayBorder.Visibility = Visibility.Collapsed;
    }

    private static bool TryGetBook(object sender, out BookDisplay book)
    {
        book = null!;
        if (sender is FrameworkElement element)
        {
            var resolvedBook = element.Tag as BookDisplay ?? element.DataContext as BookDisplay;
            if (resolvedBook != null)
            {
                book = resolvedBook;
                return true;
            }
        }

        return false;
    }

    private async Task DeleteBookAsync(BookDisplay book)
    {
        await DeleteBooksAsync(new[] { book });
    }

    private void UpdateSelectionActions()
    {
        var hasSelection = BookList.SelectedItems.Count > 0;
        DeleteBookBtn.IsEnabled = hasSelection;
        ChangeLocationBtn.IsEnabled = hasSelection;
    }

    private List<BookDisplay> GetSelectedBooks()
    {
        return BookList.SelectedItems.Cast<BookDisplay>().ToList();
    }

    private async Task DeleteBooksAsync(IReadOnlyList<BookDisplay> books)
    {
        if (books.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = books.Count == 1 ? "Delete Book" : "Delete Books",
            Content = books.Count == 1
                ? $"Are you sure you want to delete \"{books[0].Title}\"?"
                : $"Are you sure you want to delete {books.Count} selected books?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            PrimaryButtonStyle = (Style)App.Current.Resources["AccentButtonStyle"]
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var repo = App.LibraryRepository;
        if (repo == null) return;

        foreach (var book in books)
        {
            await repo.DeleteBookAsync(book.Id);
        }

        await RefreshAsync();
    }

    private async Task ChangeLocationForBooksAsync(IReadOnlyList<BookDisplay> books)
    {
        if (books.Count == 0)
        {
            return;
        }

        var repo = App.LibraryRepository;
        if (repo == null) return;

        var locations = await repo.GetLocationsAsync();
        var combo = new ComboBox
        {
            ItemsSource = locations,
            DisplayMemberPath = nameof(Location.Name),
            PlaceholderText = "Select location",
            Width = 300
        };

        if (books.Count == 1)
        {
            var current = locations.FirstOrDefault(l => l.Name == books[0].LocationName);
            if (current != null)
            {
                combo.SelectedItem = current;
            }
        }

        var dialog = new ContentDialog
        {
            Title = books.Count == 1 ? "Change Location" : $"Change Location for {books.Count} Books",
            Content = combo,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || combo.SelectedItem is not Location loc)
        {
            return;
        }

        foreach (var book in books)
        {
            var dbBook = await repo.GetBookByIdAsync(book.Id);
            if (dbBook == null)
            {
                continue;
            }

            dbBook.LocationId = loc.Id;
            await repo.UpdateBookAsync(dbBook);
        }

        await RefreshAsync();
    }
}

/// <summary>Display-friendly wrapper around Book for the ListView.</summary>
internal sealed class BookDisplay
{
    public BookDisplay(Book book, string? locationName)
    {
        Id = book.Id;
        Title = book.Title;
        Author = book.Author ?? "";
        ShortDescription = book.ShortDescription ?? "";
        LongDescription = book.LongDescription ?? "";
        SpineImagePath = book.SpineImagePath;
        BookshelfImagePath = book.BookshelfImagePath;
        DetectionIndex = book.DetectionIndex;
        LocationName = locationName ?? "";
        IsDuplicate = book.IsDuplicate;
        CreatedAtDisplay = book.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

        if (!string.IsNullOrEmpty(SpineImagePath) && File.Exists(SpineImagePath))
        {
            SpineImage = new BitmapImage(new Uri(SpineImagePath));
        }
    }

    public int Id { get; }
    public string Title { get; }
    public string Author { get; }
    public string ShortDescription { get; }
    public string LongDescription { get; }
    public string LongDescriptionDisplay => string.IsNullOrWhiteSpace(LongDescription) ? "No description available" : LongDescription;
    public Visibility HasDescription => string.IsNullOrEmpty(ShortDescription) ? Visibility.Collapsed : Visibility.Visible;
    public bool HasBookshelfImage => !string.IsNullOrWhiteSpace(BookshelfImagePath) && File.Exists(BookshelfImagePath);
    public string? SpineImagePath { get; }
    public string? BookshelfImagePath { get; }
    public int? DetectionIndex { get; }
    public BitmapImage? SpineImage { get; }
    public string LocationName { get; }
    public string LocationDisplay => string.IsNullOrEmpty(LocationName) ? "" : $"📍 {LocationName}";
    public bool IsDuplicate { get; }
    public Visibility DuplicateVisibility => IsDuplicate ? Visibility.Visible : Visibility.Collapsed;
    public string CreatedAtDisplay { get; }
    public string DetectionLabel => DetectionIndex.HasValue ? $"#{DetectionIndex}" : "";
}
