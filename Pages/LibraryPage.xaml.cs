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

        var dialog = new ContentDialog
        {
            Title = "Delete Book",
            Content = $"Are you sure you want to delete \"{selected.Title}\"?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            PrimaryButtonStyle = (Style)App.Current.Resources["AccentButtonStyle"]
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var repo = App.LibraryRepository;
            if (repo != null)
            {
                await repo.DeleteBookAsync(selected.Id);
                await RefreshAsync();
            }
        }
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
}

/// <summary>Display-friendly wrapper around Book for the ListView.</summary>
internal sealed class BookDisplay
{
    public BookDisplay(Book book, string? locationName)
    {
        Id = book.Id;
        Title = book.Title;
        Author = book.Author ?? "";
        LocationName = locationName ?? "";
        CreatedAtDisplay = book.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public int Id { get; }
    public string Title { get; }
    public string Author { get; }
    public string LocationName { get; }
    public string CreatedAtDisplay { get; }
}
