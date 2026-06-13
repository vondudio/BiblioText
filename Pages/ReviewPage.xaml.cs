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
using BiblioText.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BiblioText.Pages;

public sealed partial class ReviewPage : Page
{
    private readonly ObservableCollection<ReviewCandidate> _candidates = new();
    private readonly ObservableCollection<Location> _locations = new();
    private readonly List<ScanResultSet> _scanQueue = new();
    private int _currentScanIndex = -1;
    private bool _isSaving;

    public ReviewPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ReviewList.ItemsSource = _candidates;
        LocationDropdown.ItemsSource = _locations;
        this.Loaded += ReviewPage_Loaded;
    }

    private async void ReviewPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadLocationsAsync();
    }

    private async Task LoadLocationsAsync()
    {
        var repo = App.LibraryRepository;
        if (repo == null) return;

        var locations = await repo.GetLocationsAsync();
        _locations.Clear();
        foreach (var loc in locations)
        {
            _locations.Add(loc);
        }
    }

    /// <summary>
    /// Called externally to add a new scan result set to the review queue.
    /// </summary>
    public void SetCandidates(IEnumerable<ReviewCandidate> candidates, string? sourceImagePath = null)
    {
        var set = new ScanResultSet
        {
            Candidates = candidates.ToList(),
            SourceImagePath = sourceImagePath,
            Label = $"Scan {_scanQueue.Count + 1}"
        };
        _scanQueue.Add(set);

        // If this is the first/only set, display it
        if (_scanQueue.Count == 1)
        {
            _currentScanIndex = 0;
        }
        else if (_currentScanIndex < 0)
        {
            _currentScanIndex = _scanQueue.Count - 1;
        }

        DisplayCurrentScanSet();
        UpdateScanNavigation();
        ReviewStatusText.Text = $"{_scanQueue.Count} scan(s) in queue — {set.Candidates.Count} book(s) in current set.";
    }

    private void DisplayCurrentScanSet()
    {
        _candidates.Clear();
        CloseImagePanel();

        if (_currentScanIndex < 0 || _currentScanIndex >= _scanQueue.Count)
        {
            ReviewStatus.Text = "No scan results to review. Run a scan first from the Scan tab.";
            ReviewList.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            return;
        }

        var set = _scanQueue[_currentScanIndex];
        foreach (var c in set.Candidates)
        {
            _candidates.Add(c);
        }

        if (_candidates.Count > 0)
        {
            ReviewStatus.Text = $"{_candidates.Count} book(s) detected. Review and edit titles below.";
            ReviewList.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Visible;
        }
        else
        {
            ReviewStatus.Text = "No books in this scan set.";
            ReviewList.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Visible;
        }
    }

    private void UpdateScanNavigation()
    {
        if (_scanQueue.Count > 1)
        {
            ScanNavPanel.Visibility = Visibility.Visible;
            ScanIndexLabel.Text = $"{_currentScanIndex + 1} / {_scanQueue.Count}";
            PrevScanButton.IsEnabled = _currentScanIndex > 0;
            NextScanButton.IsEnabled = _currentScanIndex < _scanQueue.Count - 1;
        }
        else
        {
            ScanNavPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void PrevScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentScanIndex > 0)
        {
            _currentScanIndex--;
            DisplayCurrentScanSet();
            UpdateScanNavigation();
        }
    }

    private void NextScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentScanIndex < _scanQueue.Count - 1)
        {
            _currentScanIndex++;
            DisplayCurrentScanSet();
            UpdateScanNavigation();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentScanIndex >= 0 && _currentScanIndex < _scanQueue.Count)
        {
            _scanQueue.RemoveAt(_currentScanIndex);

            if (_scanQueue.Count == 0)
            {
                _currentScanIndex = -1;
            }
            else if (_currentScanIndex >= _scanQueue.Count)
            {
                _currentScanIndex = _scanQueue.Count - 1;
            }

            DisplayCurrentScanSet();
            UpdateScanNavigation();
        }
    }

    private void CropThumbnail_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ReviewCandidate candidate && candidate.CropJpeg != null)
        {
            ShowImageInPanel(candidate.CropJpeg);
        }
        else if (_currentScanIndex >= 0 && _currentScanIndex < _scanQueue.Count)
        {
            // Show source image if no crop
            var path = _scanQueue[_currentScanIndex].SourceImagePath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                ImagePanelImage.Source = new BitmapImage(new Uri(path));
                ShowImagePanel();
            }
        }
    }

    private void ShowImageInPanel(byte[] jpegData)
    {
        var bitmapImage = new BitmapImage();
        using var stream = new MemoryStream(jpegData);
        bitmapImage.SetSource(stream.AsRandomAccessStream());
        ImagePanelImage.Source = bitmapImage;
        ShowImagePanel();
    }

    private void ShowImagePanel()
    {
        ImagePanelColumn.Width = new GridLength(1, GridUnitType.Star);
        ImagePanel.Visibility = Visibility.Visible;
        ImagePanelScroller.ChangeView(0, 0, 1.0f, disableAnimation: true);
    }

    private void CloseImagePanel()
    {
        ImagePanel.Visibility = Visibility.Collapsed;
        ImagePanelColumn.Width = new GridLength(0);
        ImagePanelImage.Source = null;
    }

    private void CloseImagePanel_Click(object sender, RoutedEventArgs e)
    {
        CloseImagePanel();
    }

    private void ImagePanel_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CloseImagePanel();
    }

    private void AcceptAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _candidates)
        {
            c.IsAccepted = true;
        }
    }

    private void ReviewList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;

        if (args.Item is ReviewCandidate candidate && candidate.CropJpeg is { Length: > 0 })
        {
            var grid = args.ItemContainer.ContentTemplateRoot as Grid;
            var border = grid?.Children.OfType<Microsoft.UI.Xaml.Controls.Border>().FirstOrDefault();
            if (border?.Child is Image cropImage)
            {
                var bitmapImage = new BitmapImage();
                using var stream = new MemoryStream(candidate.CropJpeg);
                bitmapImage.SetSource(stream.AsRandomAccessStream());
                cropImage.Source = bitmapImage;
            }
        }
    }

    private void RejectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _candidates)
        {
            c.IsAccepted = false;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        var accepted = _candidates.Where(c => c.IsAccepted).ToList();
        if (accepted.Count == 0)
        {
            var dialog = new ContentDialog
            {
                Title = "Nothing to save",
                Content = "No books are accepted. Toggle at least one book to 'Accept' before saving.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        var repo = App.LibraryRepository;
        if (repo == null)
        {
            var dialog = new ContentDialog
            {
                Title = "Not configured",
                Content = "Library repository is not initialized. Check app startup.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        int? locationId = (LocationDropdown.SelectedItem as Location)?.Id;
        string? sourceImagePath = _currentScanIndex >= 0 && _currentScanIndex < _scanQueue.Count
            ? _scanQueue[_currentScanIndex].SourceImagePath
            : null;

        var savedBooks = new List<Book>();
        _isSaving = true;
        SaveButton.IsEnabled = false;
        AcceptAllButton.IsEnabled = false;
        RejectAllButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        try
        {
            ReviewStatusText.Text = $"Preparing {accepted.Count} book(s)...";
            var reviewService = App.Services?.GetRequiredService<IReviewApplicationService>()
                ?? new ReviewApplicationService(repo);
            ReviewStatusText.Text = $"Saving {accepted.Count} book(s)...";
            var saveResult = await reviewService.SaveAcceptedAsync(accepted, locationId, sourceImagePath);
            savedBooks.AddRange(saveResult.SavedBooks);

            foreach (var candidate in saveResult.SavedCandidates)
            {
                _candidates.Remove(candidate);
            }

            await FetchAndStoreDescriptionsAsync(savedBooks, repo);

            if (_candidates.Count == 0 && _currentScanIndex >= 0 && _currentScanIndex < _scanQueue.Count)
            {
                _scanQueue.RemoveAt(_currentScanIndex);
                if (_scanQueue.Count == 0)
                {
                    _currentScanIndex = -1;
                    ReviewStatus.Text = "All books saved! Go to the Library tab to view them.";
                    ReviewList.Visibility = Visibility.Collapsed;
                    CancelButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (_currentScanIndex >= _scanQueue.Count)
                        _currentScanIndex = _scanQueue.Count - 1;
                    DisplayCurrentScanSet();
                }
                UpdateScanNavigation();
            }
            else
            {
                ReviewStatus.Text = $"Saved {savedBooks.Count} book(s) to library. {_candidates.Count} remaining.";
            }
        }
        catch (Exception ex)
        {
            ReviewStatusText.Text = $"Save failed: {ex.Message}";
            var dialog = new ContentDialog
            {
                Title = "Save failed",
                Content = $"No books were removed from the review list. {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isSaving = false;
            SaveButton.IsEnabled = true;
            AcceptAllButton.IsEnabled = true;
            RejectAllButton.IsEnabled = true;
            CancelButton.IsEnabled = _currentScanIndex >= 0 && _currentScanIndex < _scanQueue.Count;
        }
    }

    private async Task FetchAndStoreDescriptionsAsync(List<Book> books, Persistence.ILibraryRepository repo)
    {
        try
        {
            var settingsStore = App.SettingsStore;
            if (settingsStore == null) return;

            var settings = settingsStore.Load();
            if (!settings.IsConfigured)
            {
                ReviewStatusText.Text = "Descriptions skipped: Azure OpenAI not configured.";
                return;
            }

            ReviewStatusText.Text = $"Fetching descriptions for {books.Count} book(s)...";

            var descService = App.Services?.GetService<BookDescriptionService>()
                ?? new BookDescriptionService(settingsStore);
            var bookList = books.Select(b => (b.Id, b.Title, b.Author)).ToList();
            var descriptionResult = await descService.GetDescriptionsResultAsync(bookList);
            if (!descriptionResult.IsSuccess)
            {
                ReviewStatusText.Text = $"Descriptions skipped: {descriptionResult.ErrorMessage}";
                return;
            }

            var descriptions = descriptionResult.Descriptions;

            if (descriptions.Count == 0)
            {
                ReviewStatusText.Text = "No descriptions returned from AI.";
                return;
            }

            for (int i = 0; i < descriptions.Count; i++)
            {
                var desc = descriptions[i];
                ReviewStatusText.Text = $"Storing description {i + 1} of {descriptions.Count}...";
                var book = books.FirstOrDefault(b => b.Id == desc.BookId);
                if (book != null)
                {
                    book.ShortDescription = desc.ShortDescription;
                    book.LongDescription = desc.LongDescription;
                    book.IsDescriptionGrounded = desc.IsGrounded;
                    book.DescriptionSourcesJson = desc.SourcesJson;
                    book.DescriptionGeneratedAt = desc.GeneratedAt;
                    await repo.UpdateBookAsync(book);

                    var searchService = App.SemanticSearchService;
                    if (searchService != null)
                    {
                        await searchService.IndexBookAsync(book.Id, book.Title, book.Author, book.LongDescription);
                    }
                }
            }

            ReviewStatusText.Text = $"✓ Saved {books.Count} book(s), {descriptions.Count} description(s) added.";
        }
        catch (Exception ex)
        {
            ReviewStatusText.Text = $"Description error: {ex.Message}";
        }
    }

    private void VoiceTypeButton_Click(object sender, RoutedEventArgs e)
    {
        // Trigger Windows Voice Typing (Win+H) — user should focus desired field first
        LaunchVoiceTyping();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void LaunchVoiceTyping()
    {
        const byte VK_LWIN = 0x5B;
        const byte VK_H = 0x48;
        const uint KEYEVENTF_KEYDOWN = 0x0000;
        const uint KEYEVENTF_KEYUP = 0x0002;

        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        keybd_event(VK_H, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        keybd_event(VK_H, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private async void AddLocationButton_Click(object sender, RoutedEventArgs e)
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
                var newLoc = new Location { Name = input.Text.Trim(), CreatedAt = DateTime.UtcNow };
                await repo.AddLocationAsync(newLoc);
                await LoadLocationsAsync();
                LocationDropdown.SelectedItem = _locations.FirstOrDefault(l => l.Id == newLoc.Id);
            }
        }
    }
}
