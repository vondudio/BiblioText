using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.UI;
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

    private async void DescriptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBook(sender, out var book) is false)
        {
            return;
        }

        var stack = new StackPanel { Spacing = 12 };

        // Author subhead (title goes in dialog Title)
        if (!string.IsNullOrWhiteSpace(book.Author))
        {
            stack.Children.Add(new TextBlock
            {
                Text = book.Author,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                FontSize = 13,
                Opacity = 0.75,
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        // Scanned date
        if (!string.IsNullOrWhiteSpace(book.ScannedDisplay))
        {
            stack.Children.Add(new TextBlock
            {
                Text = book.ScannedDisplay,
                FontSize = 12,
                Opacity = 0.55
            });
        }

        // Long description as paragraphs
        var rich = new RichTextBlock
        {
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
            LineHeight = 22
        };

        var body = string.IsNullOrWhiteSpace(book.LongDescription)
            ? "No description available"
            : book.LongDescription;

        foreach (var paragraphText in SplitIntoParagraphs(body))
        {
            var p = new Microsoft.UI.Xaml.Documents.Paragraph
            {
                Margin = new Thickness(0, 0, 0, 10)
            };
            p.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = paragraphText });
            rich.Blocks.Add(p);
        }

        stack.Children.Add(rich);

        // Sources footer (if any)
        var sourceDisplay = book.DescriptionSourceDisplayForDialog;
        if (!string.IsNullOrWhiteSpace(sourceDisplay))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Sources",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 6, 0, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = sourceDisplay,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        // Size the scroller to the current window so the full text is always
        // reachable, regardless of how long the description is.
        var window = App.Window;
        var bounds = window?.Bounds;
        var width = Math.Min(720.0, Math.Max(420.0, (bounds?.Width ?? 900) * 0.6));
        var height = Math.Min(720.0, Math.Max(360.0, (bounds?.Height ?? 700) * 0.7));

        var scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = width,
            Height = height,
            Padding = new Thickness(4, 0, 12, 0),
            Content = stack
        };

        var dialog = new ContentDialog
        {
            Title = book.Title,
            Content = scroller,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private static IEnumerable<string> SplitIntoParagraphs(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            yield break;
        }

        // Prefer explicit blank-line breaks if the AI emitted them.
        var blocks = body
            .Replace("\r\n", "\n")
            .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Replace("\n", " ").Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (blocks.Count > 1)
        {
            foreach (var b in blocks) yield return b;
            yield break;
        }

        // Otherwise group every 3 sentences into a paragraph.
        var flat = body.Replace("\r", " ").Replace("\n", " ").Trim();
        var sentences = System.Text.RegularExpressions.Regex
            .Split(flat, @"(?<=[\.\?\!])\s+(?=[A-Z""'\u201c])")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        if (sentences.Count == 0)
        {
            yield return flat;
            yield break;
        }

        const int sentencesPerParagraph = 3;
        for (int i = 0; i < sentences.Count; i += sentencesPerParagraph)
        {
            yield return string.Join(" ", sentences.Skip(i).Take(sentencesPerParagraph));
        }
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
        IsDescriptionGrounded = book.IsDescriptionGrounded;
        DescriptionSourcesJson = book.DescriptionSourcesJson;
        CreatedAtDisplay = book.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");
        ScannedDisplay = $"Scanned {book.CreatedAt.ToLocalTime():yyyy-M-d}";

        if (!string.IsNullOrEmpty(SpineImagePath) && File.Exists(SpineImagePath))
        {
            SpineImage = new BitmapImage(new Uri(SpineImagePath));
        }

        CoverImageUri = ExtractCoverUri(DescriptionSourcesJson);
        if (CoverImageUri != null)
        {
            CoverImage = new BitmapImage(CoverImageUri);
        }

        ProviderBadges = BuildProviderBadges(DescriptionSourcesJson);
    }

    public int Id { get; }
    public string Title { get; }
    public string Author { get; }
    public string ShortDescription { get; }
    public string LongDescription { get; }
    public string LongDescriptionDisplay
    {
        get
        {
            var description = string.IsNullOrWhiteSpace(LongDescription) ? "No description available" : LongDescription;
            var sourceDisplay = DescriptionSourceDisplay;
            return string.IsNullOrWhiteSpace(sourceDisplay)
                ? description
                : $"{description}\n\nSources:\n{sourceDisplay}";
        }
    }
    public Visibility HasDescription => string.IsNullOrEmpty(ShortDescription) ? Visibility.Collapsed : Visibility.Visible;
    public bool HasBookshelfImage => !string.IsNullOrWhiteSpace(BookshelfImagePath) && File.Exists(BookshelfImagePath);
    public string? SpineImagePath { get; }
    public string? BookshelfImagePath { get; }
    public int? DetectionIndex { get; }
    public BitmapImage? SpineImage { get; }
    public Uri? CoverImageUri { get; }
    public BitmapImage? CoverImage { get; }
    public Visibility CoverVisibility => CoverImage != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SpineVisibility => CoverImage == null ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<ProviderBadge> ProviderBadges { get; }
    public string LocationName { get; }
    public string LocationDisplay => string.IsNullOrEmpty(LocationName) ? "" : $"📍 {LocationName}";
    public Visibility LocationVisibility => string.IsNullOrEmpty(LocationName) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsDuplicate { get; }
    public Visibility DuplicateVisibility => IsDuplicate ? Visibility.Visible : Visibility.Collapsed;
    public bool IsDescriptionGrounded { get; }
    public string? DescriptionSourcesJson { get; }
    public Visibility GroundedVisibility => IsDescriptionGrounded ? Visibility.Visible : Visibility.Collapsed;
    public string CreatedAtDisplay { get; }
    public string ScannedDisplay { get; }
    public string DetectionLabel => DetectionIndex.HasValue ? $"#{DetectionIndex}" : "";

    public string DescriptionSourceDisplayForDialog => DescriptionSourceDisplay;

    private string DescriptionSourceDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DescriptionSourcesJson))
            {
                return string.Empty;
            }

            try
            {
                var sources = JsonSerializer.Deserialize<List<DescriptionSourceDisplay>>(DescriptionSourcesJson);
                if (sources == null || sources.Count == 0)
                {
                    return string.Empty;
                }

                return string.Join(
                    "\n",
                    sources
                        .Where(source => !string.IsNullOrWhiteSpace(source.Url))
                        .Select(source => $"- {source.Provider}: {source.Url}"));
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }
    }

    private static Uri? ExtractCoverUri(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
        {
            return null;
        }

        try
        {
            var sources = JsonSerializer.Deserialize<List<DescriptionSourceDisplay>>(sourcesJson);
            var coverUrl = sources?
                .Select(s => s.CoverUrl)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

            return Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri) ? uri : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ProviderBadge> BuildProviderBadges(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
        {
            return Array.Empty<ProviderBadge>();
        }

        try
        {
            var sources = JsonSerializer.Deserialize<List<DescriptionSourceDisplay>>(sourcesJson);
            if (sources == null || sources.Count == 0)
            {
                return Array.Empty<ProviderBadge>();
            }

            // Distinct by provider name, preserving first-seen order, so each provider only renders one chip.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var badges = new List<ProviderBadge>();
            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source.Provider)) continue;
                if (!seen.Add(source.Provider)) continue;

                var badge = ProviderBadge.ForProvider(source.Provider);
                if (badge != null)
                {
                    badges.Add(badge);
                }
            }

            return badges;
        }
        catch (JsonException)
        {
            return Array.Empty<ProviderBadge>();
        }
    }
}

internal sealed class ProviderBadge
{
    private ProviderBadge(string code, string tooltip, Color background)
    {
        Code = code;
        ToolTip = tooltip;
        Background = new SolidColorBrush(background);
    }

    public string Code { get; }
    public string ToolTip { get; }
    public SolidColorBrush Background { get; }

    public static ProviderBadge? ForProvider(string provider) => provider switch
    {
        var p when p.Equals("Google Books", StringComparison.OrdinalIgnoreCase) =>
            new ProviderBadge("G", "Google Books", Color.FromArgb(0xFF, 0x1A, 0x73, 0xE8)),
        var p when p.Equals("Wikipedia", StringComparison.OrdinalIgnoreCase) =>
            new ProviderBadge("W", "Wikipedia", Color.FromArgb(0xFF, 0x4B, 0x55, 0x63)),
        var p when p.Equals("Open Library", StringComparison.OrdinalIgnoreCase) =>
            new ProviderBadge("OL", "Open Library", Color.FromArgb(0xFF, 0xA4, 0x75, 0x51)),
        var p when p.Equals("AI", StringComparison.OrdinalIgnoreCase) =>
            new ProviderBadge("AI", "Generated from model knowledge (no external source)", Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6)),
        _ => null
    };
}

internal sealed class DescriptionSourceDisplay
{
    public string? Provider { get; set; }
    public string? Url { get; set; }
    public string? CoverUrl { get; set; }
}
