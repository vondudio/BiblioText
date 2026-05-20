using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AIDevGallery.Sample.Models;

namespace AIDevGallery.Sample.Pages;

public sealed partial class ReviewPage : Page
{
    private readonly ObservableCollection<ReviewCandidate> _candidates = new();

    public ReviewPage()
    {
        this.InitializeComponent();
        ReviewList.ItemsSource = _candidates;
    }

    /// <summary>
    /// Called externally (e.g., from ScanWorkflowService) to populate review candidates.
    /// </summary>
    public void SetCandidates(IEnumerable<ReviewCandidate> candidates)
    {
        _candidates.Clear();
        foreach (var c in candidates)
        {
            _candidates.Add(c);
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

        int saved = 0;
        foreach (var candidate in accepted)
        {
            var book = new Book
            {
                Title = string.IsNullOrWhiteSpace(candidate.EditedTitle) ? candidate.DetectedTitle : candidate.EditedTitle,
                Author = string.IsNullOrWhiteSpace(candidate.EditedAuthor) ? candidate.DetectedAuthor : candidate.EditedAuthor,
                CreatedAt = DateTime.UtcNow
            };
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
}
