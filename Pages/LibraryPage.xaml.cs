using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AIDevGallery.Sample.Models;
using AIDevGallery.Sample.Persistence;

namespace AIDevGallery.Sample.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly ObservableCollection<BookDisplay> _books = new();
    private readonly ObservableCollection<Location> _locations = new();
    private string _searchQuery = string.Empty;
    private int? _selectedLocationId;

    public LibraryPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        BookList.ItemsSource = _books;
        LocationFilter.ItemsSource = _locations;
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

        var books = await repo.GetBooksAsync(_searchQuery, _selectedLocationId);
        var locations = await repo.GetLocationsAsync();

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

    private void BookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteBookBtn.IsEnabled = BookList.SelectedItem != null;
    }

    private async void DeleteBookBtn_Click(object sender, RoutedEventArgs e)
    {
        if (BookList.SelectedItem is not BookDisplay selected) return;

        await DeleteBookAsync(selected);
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

        var current = locations.FirstOrDefault(l => l.Name == book.LocationName);
        if (current != null)
        {
            combo.SelectedItem = current;
        }

        var dialog = new ContentDialog
        {
            Title = "Change Location",
            Content = combo,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || combo.SelectedItem is not Location loc)
        {
            return;
        }

        var dbBook = await repo.GetBookByIdAsync(book.Id);
        if (dbBook == null) return;

        dbBook.LocationId = loc.Id;
        await repo.UpdateBookAsync(dbBook);
        await RefreshAsync();
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

    private static bool TryGetBook(object sender, out BookDisplay book)
    {
        book = null!;
        if (sender is MenuFlyoutItem item)
        {
            var resolvedBook = item.Tag as BookDisplay ?? item.DataContext as BookDisplay;
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
        var dialog = new ContentDialog
        {
            Title = "Delete Book",
            Content = $"Are you sure you want to delete \"{book.Title}\"?",
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

        await repo.DeleteBookAsync(book.Id);
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
        SpineImagePath = book.SpineImagePath;
        LocationName = locationName ?? "";
        CreatedAtDisplay = book.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public int Id { get; }
    public string Title { get; }
    public string Author { get; }
    public string? SpineImagePath { get; }
    public string LocationName { get; }
    public string CreatedAtDisplay { get; }
}
