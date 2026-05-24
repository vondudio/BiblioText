using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;

namespace BiblioText;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        // Start maximized with nav pane collapsed
        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        NavView.IsPaneOpen = false;

        this.RootFrame.Loaded += (sender, args) =>
        {
            // Select the Scan tab by default
            NavView.SelectedItem = NavView.MenuItems[0];
        };
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            RootFrame.Navigate(typeof(Pages.SettingsPage));
            return;
        }

        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            switch (tag)
            {
                case "scan":
                    RootFrame.Navigate(typeof(Sample));
                    break;
                case "review":
                    RootFrame.Navigate(typeof(Pages.ReviewPage));
                    break;
                case "library":
                    RootFrame.Navigate(typeof(Pages.LibraryPage));
                    break;
            }
        }
    }

    internal void ModelLoaded()
    {
        ProgressRingGrid.Visibility = Visibility.Collapsed;
    }

    internal async void ShowException(Exception? ex, string? optionalMessage = null)
    {
        var msg = optionalMessage ?? ex switch
        {
            COMException
                when ex.Message.Contains("the rpc server is unavailable", StringComparison.CurrentCultureIgnoreCase) =>
                    "The WCL is in an unstable state.\nRebooting the machine will restart the WCL.",
            _ => $"Error:\n{ex?.Message ?? string.Empty}{(optionalMessage != null ? "\n" + optionalMessage : string.Empty)}"
        };

        var errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = msg,
            IsTextSelectionEnabled = true,
        };

        ContentDialog exceptionDialog = new()
        {
            Title = "Something went wrong",
            Content = errorText,
            PrimaryButtonText = "Copy error details",
            SecondaryButtonText = "Reload",
            XamlRoot = Content.XamlRoot,
            CloseButtonText = "Close",
            PrimaryButtonStyle = (Style)App.Current.Resources["AccentButtonStyle"],
        };

        var result = await exceptionDialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            CopyExceptionToClipboard(ex, optionalMessage);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            RootFrame.Navigate(typeof(Sample));
        }
    }

    public static void CopyExceptionToClipboard(Exception? ex, string? optionalMessage)
    {
        string exceptionDetails = string.IsNullOrWhiteSpace(optionalMessage) ? string.Empty : optionalMessage + "\n";

        if (ex != null)
        {
            exceptionDetails += GetExceptionDetails(ex);
        }

        DataPackage dataPackage = new DataPackage();
        dataPackage.SetText(exceptionDetails);
        Clipboard.SetContent(dataPackage);
    }

    private static string GetExceptionDetails(Exception ex)
    {
        var innerExceptionData = ex.InnerException == null ? "" :
            $"Inner Exception:\n{GetExceptionDetails(ex.InnerException)}";
        string details = $@"Message: {ex.Message}
StackTrace: {ex.StackTrace}
{innerExceptionData}";
        return details;
    }

    /// <summary>
    /// Navigate to the Review page and pass in candidates from an AI analysis or crop extraction.
    /// </summary>
    internal void NavigateToReview(System.Collections.Generic.List<Models.ReviewCandidate> candidates, string? sourceImagePath)
    {
        // Select the Review nav item
        NavView.SelectedItem = NavView.MenuItems[1];
        RootFrame.Navigate(typeof(Pages.ReviewPage));

        // Pass candidates to the page after navigation
        if (RootFrame.Content is Pages.ReviewPage reviewPage)
        {
            reviewPage.SetCandidates(candidates, sourceImagePath);
        }
    }
}