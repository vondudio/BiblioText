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
        _candidates.CollectionChanged += (_, _) => UpdateSelectionButtons();
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
        UpdateSelectionButtons();
    }

    private void UpdateSelectionButtons()
    {
        int total = _candidates.Count;
        int selected = ReviewList.SelectedItems.Count;
        DeleteSelectedButton.IsEnabled = selected > 0;
        SendToLibraryButton.IsEnabled = selected > 0;
        SelectAllButton.IsEnabled = total > 0;
        SelectAllLabel.Text = (total > 0 && selected == total) ? "Deselect All" : "Select All";
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_candidates.Count == 0) return;
        if (ReviewList.SelectedItems.Count == _candidates.Count)
        {
            ReviewList.SelectedItems.Clear();
        }
        else
        {
            ReviewList.SelectAll();
        }
    }

    private void ReviewList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && ReviewList.SelectedItems.Count > 0)
        {
            DeleteSelectedButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var toRemove = ReviewList.SelectedItems.OfType<ReviewCandidate>().ToList();
        if (toRemove.Count == 0) return;
        foreach (var c in toRemove)
        {
            _candidates.Remove(c);
        }
        ReviewStatus.Text = $"{_candidates.Count} book(s) remaining.";
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

    private bool _isSaving;

    private async void SendToLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        _isSaving = true;
        SendToLibraryButton.IsEnabled = false;
        try
        {
            var selectedAll = ReviewList.SelectedItems.OfType<ReviewCandidate>().ToList();
            // Belt-and-suspenders: never save unknown/illegible spines even if user selected one.
            var selected = selectedAll
                .Where(c => !IsUnknownTitle(StringOr(c.EditedTitle, c.DetectedTitle))
                         || !string.IsNullOrWhiteSpace(StringOr(c.EditedAuthor, c.DetectedAuthor)))
                .ToList();

            int skippedUnknown = selectedAll.Count - selected.Count;
            if (selected.Count == 0)
            {
                ReviewStatus.Text = skippedUnknown > 0
                    ? $"Skipped {skippedUnknown} unknown spine(s). Nothing to save."
                    : "Select at least one book to send to the library.";
                return;
            }

            var repo = App.LibraryRepository;
            var saveService = App.Services?.GetService(typeof(IReviewApplicationService)) as IReviewApplicationService;
            if (repo == null || saveService == null)
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

            ReviewStatusText.Text = $"Saving {selected.Count} book(s)...";

            ReviewSaveResult result;
            try
            {
                result = await saveService.SaveAcceptedAsync(selected, locationId, sourceImagePath);
            }
            catch (Exception saveEx)
            {
                ReviewStatusText.Text = $"Save failed: {saveEx.Message}";
                return;
            }

            // Remove saved candidates from the list (use the actually-saved subset)
            foreach (var candidate in result.SavedCandidates)
            {
                _candidates.Remove(candidate);
            }

            // Hand description generation to the app-wide background queue so it
            // survives navigation away from Review and the nav-rail badge can
            // show how many are still pending. The save above is already done.
            App.BackgroundDescriptions?.Enqueue(result.SavedBooks);
            ReviewStatusText.Text = $"Queued {result.SavedBooks.Count} book(s) for descriptions — generating in the background.";

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
                var dupNote = result.DuplicateCount > 0 ? $" ({result.DuplicateCount} flagged as duplicate)" : "";
                var skipNote = skippedUnknown > 0 ? $" (skipped {skippedUnknown} unknown)" : "";
                ReviewStatus.Text = $"Saved {result.SavedBooks.Count} book(s) to library{dupNote}{skipNote}. {_candidates.Count} remaining.";
            }
        }
        finally
        {
            _isSaving = false;
            SendToLibraryButton.IsEnabled = true;
        }
    }

    private static string StringOr(string? a, string? b) => string.IsNullOrWhiteSpace(a) ? (b ?? "") : a;


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
