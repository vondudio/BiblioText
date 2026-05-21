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
using AIDevGallery.Sample.Models;

namespace AIDevGallery.Sample.Pages;

public sealed partial class ReviewPage : Page
{
    private readonly ObservableCollection<ReviewCandidate> _candidates = new();
    private readonly ObservableCollection<Location> _locations = new();
    private string? _sourceImagePath;

    public ReviewPage()
    {
        this.InitializeComponent();
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
    /// Called externally (e.g., from ScanWorkflowService) to populate review candidates.
    /// </summary>
    public void SetCandidates(IEnumerable<ReviewCandidate> candidates, string? sourceImagePath = null)
    {
        _sourceImagePath = sourceImagePath;
        _candidates.Clear();
        foreach (var c in candidates)
        {
            _candidates.Add(c);
        }

        SourceThumbnail.Source = null;
        OverlayImage.Source = null;
        SourceImageBorder.Visibility = Visibility.Collapsed;
        ImageOverlay.Visibility = Visibility.Collapsed;
        OverlayScroller.ChangeView(0, 0, 1.0f, disableAnimation: true);

        if (!string.IsNullOrWhiteSpace(_sourceImagePath) && File.Exists(_sourceImagePath))
        {
            var imageUri = new Uri(_sourceImagePath);
            var thumbnailBitmap = new BitmapImage(imageUri);
            var overlayBitmap = new BitmapImage(imageUri);
            SourceThumbnail.Source = thumbnailBitmap;
            OverlayImage.Source = overlayBitmap;
            SourceImageBorder.Visibility = Visibility.Visible;
        }

        if (_candidates.Count > 0)
        {
            ReviewStatus.Text = $"{_candidates.Count} book(s) detected. Review and edit titles below.";
            ReviewList.Visibility = Visibility.Visible;
        }
        else
        {
            ReviewStatus.Text = "No scan results to review. Run a scan first from the Scan tab.";
            ReviewList.Visibility = Visibility.Collapsed;
        }
    }

    private void SourceImage_Click(object sender, PointerRoutedEventArgs e)
    {
        if (SourceThumbnail.Source == null)
        {
            return;
        }

        OverlayScroller.ChangeView(0, 0, 1.0f, disableAnimation: true);
        ImageOverlay.Visibility = Visibility.Visible;
    }

    private void ImageOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ImageOverlay) || e.OriginalSource is Grid { Name: nameof(ImageOverlay) })
        {
            ImageOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OverlayScroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        OverlayScroller.ChangeView(0, 0, 1.0f);
    }

    private void CloseOverlay_Click(object sender, RoutedEventArgs e)
    {
        ImageOverlay.Visibility = Visibility.Collapsed;
    }

    private void AcceptAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _candidates)
        {
            c.IsAccepted = true;
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

        // Save accepted books to library via repository
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

        // Get selected location
        int? locationId = (LocationDropdown.SelectedItem as Location)?.Id;

        int saved = 0;
        foreach (var candidate in accepted)
        {
            var book = new Book
            {
                Title = string.IsNullOrWhiteSpace(candidate.EditedTitle) ? candidate.DetectedTitle : candidate.EditedTitle,
                Author = string.IsNullOrWhiteSpace(candidate.EditedAuthor) ? candidate.DetectedAuthor : candidate.EditedAuthor,
                LocationId = locationId,
                CreatedAt = DateTime.UtcNow
            };

            // Save spine image if crop is available
            if (candidate.CropJpeg != null && candidate.CropJpeg.Length > 0)
            {
                var spinesFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "YOLO_Object_DetectionSample", "spines");
                Directory.CreateDirectory(spinesFolder);
                var spinePath = Path.Combine(spinesFolder, $"spine_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{saved}.jpg");
                await File.WriteAllBytesAsync(spinePath, candidate.CropJpeg);
                book.SpineImagePath = spinePath;
            }

            await repo.AddBookAsync(book);
            saved++;
        }

        // Remove saved candidates from the list
        foreach (var candidate in accepted)
        {
            _candidates.Remove(candidate);
        }

        ReviewStatus.Text = $"Saved {saved} book(s) to library. {_candidates.Count} remaining.";
        if (_candidates.Count == 0)
        {
            ReviewList.Visibility = Visibility.Collapsed;
            ReviewStatus.Text = "All books saved! Go to the Library tab to view them.";
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
                // Auto-select the new location
                LocationDropdown.SelectedItem = _locations.FirstOrDefault(l => l.Id == newLoc.Id);
            }
        }
    }
}
