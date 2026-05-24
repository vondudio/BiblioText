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

namespace BiblioText.Pages;

public sealed partial class ReviewPage : Page
{
    private readonly ObservableCollection<ReviewCandidate> _candidates = new();
    private readonly ObservableCollection<Location> _locations = new();
    private readonly List<ScanResultSet> _scanQueue = new();
    private int _currentScanIndex = -1;

    public ReviewPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ReviewList.ItemsSource = _candidates;
        LocationDropdown.ItemsSource = _locations;
        this.Loaded += ReviewPage_Loaded;
        this.Unloaded += ReviewPage_Unloaded;
    }

    private async void ReviewPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadLocationsAsync();
    }

    private async void ReviewPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _dictation?.Dispose();
        _dictation = null;
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
        CloseSpineOverlay();

        if (_currentScanIndex < 0 || _currentScanIndex >= _scanQueue.Count)
        {
            ReviewStatus.Text = "No scan results to review. Run a scan first from the Scan tab.";
            ReviewList.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            return;
        }

        var set = _scanQueue[_currentScanIndex];
        var sorted = set.Candidates.OrderBy(c => c.Confidence).ToList();
        foreach (var c in sorted)
        {
            // Auto-reject entries with unknown/empty title and no author
            if (IsUnknownTitle(c.EditedTitle) && string.IsNullOrWhiteSpace(c.EditedAuthor))
            {
                c.IsAccepted = false;
            }
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

    private static bool IsUnknownTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        return title.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);
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

    private void CropThumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ReviewCandidate candidate && candidate.CropJpeg != null)
        {
            ShowSpineImage(candidate.CropJpeg);
        }
    }

    private void ShowSpineImage(byte[] jpegData)
    {
        var bitmapImage = new BitmapImage();
        using var stream = new MemoryStream(jpegData);
        bitmapImage.SetSource(stream.AsRandomAccessStream());
        SpineOverlayImage.Source = bitmapImage;
        SpineOverlayScroller.ChangeView(0, 0, 1.0f, disableAnimation: true);
    }

    private void CloseSpineOverlay()
    {
        SpineOverlayImage.Source = null;
    }

    private void ReviewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Multi-select: update reject button state, etc.
        int selected = ReviewList.SelectedItems.Count;
        RejectSelectedButton.IsEnabled = selected > 0;
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
            // Navigate: Grid > Button (col 0) > Border > Grid > Image (named CropImage)
            var rootGrid = args.ItemContainer.ContentTemplateRoot as Grid;
            var button = rootGrid?.Children.OfType<Button>().FirstOrDefault();
            if (button?.Content is Microsoft.UI.Xaml.Controls.Border border
                && border.Child is Grid innerGrid)
            {
                var cropImage = innerGrid.Children.OfType<Image>().FirstOrDefault();
                if (cropImage != null)
                {
                    var bitmapImage = new BitmapImage();
                    using var stream = new MemoryStream(candidate.CropJpeg);
                    bitmapImage.SetSource(stream.AsRandomAccessStream());
                    cropImage.Source = bitmapImage;
                }
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

    private void RejectSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ReviewList.SelectedItems)
        {
            if (item is ReviewCandidate c)
            {
                c.IsAccepted = false;
            }
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
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

        int saved = 0;
        var savedBooks = new List<Book>();
        ReviewStatusText.Text = $"Saving {accepted.Count} book(s)...";
        foreach (var candidate in accepted)
        {
            var book = new Book
            {
                Title = string.IsNullOrWhiteSpace(candidate.EditedTitle) ? candidate.DetectedTitle : candidate.EditedTitle,
                Author = string.IsNullOrWhiteSpace(candidate.EditedAuthor) ? candidate.DetectedAuthor : candidate.EditedAuthor,
                LocationId = locationId,
                DetectionIndex = candidate.Index > 0 ? candidate.Index : null,
                BookshelfImagePath = sourceImagePath,
                CreatedAt = DateTime.UtcNow
            };

            // Save spine image if crop is available
            if (candidate.CropJpeg != null && candidate.CropJpeg.Length > 0)
            {
                var spinesFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BiblioText", "spines");
                Directory.CreateDirectory(spinesFolder);
                var spinePath = Path.Combine(spinesFolder, $"spine_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{saved}.jpg");
                await File.WriteAllBytesAsync(spinePath, candidate.CropJpeg);
                book.SpineImagePath = spinePath;
            }

            await repo.AddBookAsync(book);
            savedBooks.Add(book);
            saved++;
        }

        // Remove saved candidates from the list
        foreach (var candidate in accepted)
        {
            _candidates.Remove(candidate);
        }

        // Batch fetch descriptions from AI
        await FetchAndStoreDescriptionsAsync(savedBooks, repo);

        // Remove this scan set if all candidates are processed
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
            ReviewStatus.Text = $"Saved {saved} book(s) to library. {_candidates.Count} remaining.";
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

            var descService = new BookDescriptionService(settingsStore);
            var bookList = books.Select(b => (b.Id, b.Title, b.Author)).ToList();
            var descriptions = await descService.GetDescriptionsAsync(bookList);

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

    private DictationService? _dictation;
    private Button? _activeRecordButton;

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        // Initialize service on first use
        if (_dictation == null)
        {
            _dictation = new DictationService();
            _dictation.StatusChanged += msg => ReviewStatusText.Text = msg ?? "";
            try
            {
                await _dictation.InitializeAsync(DispatcherQueue);
            }
            catch (Exception ex)
            {
                ReviewStatusText.Text = $"Speech model error: {ex.Message}";
                _dictation = null;
                return;
            }
        }

        // If this button is already recording, stop and transcribe
        if (_dictation.IsRecording && _activeRecordButton == btn)
        {
            string text = await _dictation.StopAndTranscribeAsync();
            InsertTextIntoField(btn, text);
            SetMicIcon(btn, false);
            _activeRecordButton = null;
            return;
        }

        // If another button was recording, stop it first
        if (_dictation.IsRecording && _activeRecordButton != null)
        {
            string text = await _dictation.StopAndTranscribeAsync();
            InsertTextIntoField(_activeRecordButton, text);
            SetMicIcon(_activeRecordButton, false);
        }

        // Start recording for this button
        _dictation.StartRecording();
        SetMicIcon(btn, true);
        _activeRecordButton = btn;
    }

    private void InsertTextIntoField(Button btn, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Find the sibling TextBox in the same Grid
        if (btn.Parent is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is TextBox tb)
                {
                    string existing = tb.Text ?? "";
                    if (!string.IsNullOrEmpty(existing) && !existing.EndsWith(" "))
                        existing += " ";
                    tb.Text = existing + text;
                    break;
                }
            }
        }
    }

    private static void SetMicIcon(Button btn, bool recording)
    {
        if (btn.Content is FontIcon icon)
        {
            // Stop icon vs mic icon
            icon.Glyph = recording ? "\uE71A" : "\uE720";
        }
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
