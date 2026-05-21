using AIDevGallery.Sample.Models;
using AIDevGallery.Sample.Services;
using AIDevGallery.Sample.Utils;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace AIDevGallery.Sample;

internal sealed partial class Sample : Microsoft.UI.Xaml.Controls.Page
{
    private static readonly List<(float Width, float Height)> Yolov4Anchors =
    [
        (12, 16), (19, 36), (40, 28),     // Small grid (52x52)
        (36, 75), (76, 55), (72, 146),    // Medium grid (26x26)
        (142, 110), (192, 243), (459, 401) // Large grid (13x13)
    ];

    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private InferenceSession? _inferenceSession;
    private ModelInfo? _currentModel;
    private string _modelsDir = string.Empty;
    private string _assetsDir = string.Empty;
    private bool _suppressModelChange;

    // The strip of loaded images and the one currently displayed in the main viewer.
    private readonly ObservableCollection<ImageItem> _images = new();
    private ImageItem? _activeImage;

    // Stub Azure OpenAI extractor; swap implementation when wiring the real API.
    private IBookTitleExtractor _titleExtractor = App.TitleExtractor ?? new StubBookTitleExtractor();

    // Confidence slider state. We re-run inference only when the user has
    // released the slider thumb (mouse up / pointer capture lost). Keyboard
    // arrow nudges and TextBox edits don't go through the pointer capture
    // path, so they fall back to a short debounce to coalesce auto-repeat.
    private bool _sliderPointerActive;
    private bool _sliderValueChangedSincePress;
    private bool _syncingConfidenceControls;
    private Microsoft.UI.Xaml.DispatcherTimer? _confidenceDebounce;

    // Left-button pan state. ScrollViewer only pans with middle-mouse / touch by default;
    // we capture the pointer here and drive HorizontalOffset/VerticalOffset directly.
    private bool _isPanning;
    private Windows.Foundation.Point _panStartPointer;
    private double _panStartOffsetX;
    private double _panStartOffsetY;
    private uint _panPointerId;

    public Sample()
    {
        this.Unloaded += (s, e) =>
        {
            _confidenceDebounce?.Stop();
            _inferenceSession?.Dispose();
            foreach (var img in _images)
            {
                img.Dispose();
            }
            _images.Clear();
        };
        this.InitializeComponent();

        ThumbnailRepeater.ItemsSource = _images;
        _images.CollectionChanged += (s, e) => UpdateItemsCountLabel();
        UpdateItemsCountLabel();

        _confidenceDebounce = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _confidenceDebounce.Tick += async (s, e) =>
        {
            _confidenceDebounce.Stop();
            if (_inferenceSession == null || _activeImage == null)
            {
                return;
            }
            await ActivateImageAsync(_activeImage, forceRerun: true);
        };

        // Slider release detection. Slider captures the pointer while the user
        // drags the thumb (or holds the track); on release we fire one inference.
        ConfidenceSlider.AddHandler(
            PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ConfidenceSlider_PointerPressed),
            handledEventsToo: true);
        ConfidenceSlider.AddHandler(
            PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ConfidenceSlider_PointerReleased),
            handledEventsToo: true);
        ConfidenceSlider.AddHandler(
            PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ConfidenceSlider_PointerReleased),
            handledEventsToo: true);

        // Plain mouse wheel = zoom (no Ctrl required) and left-drag = pan when zoomed.
        // ScrollViewer marks pointer events handled internally for its own scroll/zoom
        // logic, so we have to use AddHandler with handledEventsToo:true.
        ImageScroller.AddHandler(
            PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ImageScroller_PointerWheelChanged),
            handledEventsToo: true);
        ImageScroller.AddHandler(
            DoubleTappedEvent,
            new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(ImageScroller_DoubleTapped),
            handledEventsToo: true);
        ImageScroller.AddHandler(
            PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ImageScroller_PointerPressed),
            handledEventsToo: true);
        ImageScroller.AddHandler(
            PointerMovedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ImageScroller_PointerMoved),
            handledEventsToo: true);
        ImageScroller.AddHandler(
            PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ImageScroller_PointerReleased),
            handledEventsToo: true);
        ImageScroller.AddHandler(
            PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ImageScroller_PointerCaptureLost),
            handledEventsToo: true);
    }

    private void Page_Loaded()
    {
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _modelsDir = Path.Join(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "Models");
        _assetsDir = Path.Join(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "Assets");

        var available = ModelRegistry.Available(_modelsDir);
        if (available.Count == 0)
        {
            App.Window?.ShowException(null,
                $"No ONNX models found in '{_modelsDir}'. Run scripts\\download-models.ps1 to fetch YOLO26 weights, or rebuild with yolov4.onnx in Models\\.");
            return;
        }

        _suppressModelChange = true;
        ModelPicker.ItemsSource = available;
        var initial = ModelRegistry.Default(_modelsDir) ?? available[0];
        ModelPicker.SelectedItem = initial;
        _suppressModelChange = false;

        ConfidenceSlider.Value = initial.DefaultConfidence;

        try
        {
            await LoadModel(initial);
            App.Window?.ModelLoaded();
        }
        catch (Exception ex)
        {
            App.Window?.ShowException(ex, "Failed to load model.");
            return;
        }

        // Seed the strip with the bundled default photo so it's never empty on launch.
        string defaultPath = Path.Join(_assetsDir, "team.jpg");
        if (File.Exists(defaultPath))
        {
            await AddImageAsync(defaultPath, activate: true);
        }
    }

    private async Task LoadModel(ModelInfo model)
    {
        _inferenceSession?.Dispose();
        _inferenceSession = null;
        _currentModel = model;

        string modelPath = model.GetFullPath(_modelsDir);

        await Task.Run(async () =>
        {
            var catalog = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
            try
            {
                await catalog.EnsureAndRegisterCertifiedAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WARNING: Failed to install packages: {ex.Message}");
            }

            SessionOptions sessionOptions = new();
            sessionOptions.RegisterOrtExtensions();
            _inferenceSession = new InferenceSession(modelPath, sessionOptions);
        });
    }

    // ---------------- Image collection ----------------

    private void UpdateItemsCountLabel()
    {
        int n = _images.Count;
        ItemsCountLabel.Text = $"{n} ITEM{(n == 1 ? string.Empty : "S")}";
    }

    private async Task<ImageItem?> AddImageAsync(string filePath, bool activate)
    {
        // Skip duplicates (re-importing the same path just re-selects it).
        var existing = _images.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (activate)
            {
                await ActivateImageAsync(existing);
            }
            return existing;
        }

        ImageItem item;
        try
        {
            item = await ImageItem.LoadAsync(filePath);
        }
        catch (Exception ex)
        {
            App.Window?.ShowException(ex, $"Failed to load '{Path.GetFileName(filePath)}'.");
            return null;
        }

        _images.Add(item);
        if (activate)
        {
            await ActivateImageAsync(item);
        }
        return item;
    }

    private async Task ActivateImageAsync(ImageItem item, bool forceRerun = false)
    {
        // Selected-state flags.
        if (_activeImage != item)
        {
            if (_activeImage != null)
            {
                _activeImage.IsSelected = false;
            }
            _activeImage = item;
            item.IsSelected = true;
            RemoveButton.IsEnabled = true;
            AiAnalyzeButton.IsEnabled = true;
        }

        // Always swap to the pristine source first so any stale boxes from a previous
        // render disappear instantly.
        DefaultImage.Source = item.SourceImage;

        if (_inferenceSession == null || _currentModel == null)
        {
            ExtractButton.IsEnabled = false;
            return;
        }

        string cacheKey = CacheKey();
        if (!forceRerun && item.Outputs.TryGetValue(cacheKey, out CachedOutput? cached))
        {
            DefaultImage.Source = cached.Image;
            StatusText.Text = $"(cached) model={_currentModel.Id}  conf={ConfidenceSlider.Value:F2}  detections={cached.BoxPredictions.Count}";
            StatusBar.Visibility = Visibility.Visible;
            ExtractButton.IsEnabled = cached.BoxPredictions.Count > 0;
            return;
        }

        ExtractButton.IsEnabled = false;
        await DetectObjects(item);
    }

    private string CacheKey()
    {
        if (_currentModel == null)
        {
            return string.Empty;
        }
        double conf = Math.Round(ConfidenceSlider.Value, 2);
        return $"{_currentModel.Id}|{conf:F2}";
    }

    // ---------------- Strip event handlers ----------------

    private async void Thumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ImageItem item)
        {
            await ActivateImageAsync(item);
        }
    }

    private async void ImportTile_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = App.Window is null
            ? IntPtr.Zero
            : WinRT.Interop.WindowNative.GetWindowHandle(App.Window);

        var picker = new FileOpenPicker();
        if (hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        foreach (var ext in SupportedExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }
        picker.ViewMode = PickerViewMode.Thumbnail;

        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0)
        {
            return;
        }

        bool first = true;
        foreach (var f in files)
        {
            await AddImageAsync(f.Path, activate: first);
            first = false;
        }
    }

    private async void CaptureTile_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<DeviceInformation> cameras;
        try
        {
            cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        }
        catch (Exception ex)
        {
            App.Window?.ShowException(ex, "Failed to enumerate cameras.");
            return;
        }

        if (cameras.Count == 0)
        {
            var unavailableDialog = new ContentDialog
            {
                Title = "Camera unavailable",
                Content = "No cameras were found on this device.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            };
            await unavailableDialog.ShowAsync();
            return;
        }

        MediaCapture? mediaCapture = null;
        MediaFrameReader? frameReader = null;
        SoftwareBitmapSource? previewSource = null;
        bool isPreviewing = false;
        bool isInitializingCamera = false;
        bool isCapturingPhoto = false;
        bool isUpdatingPreview = false;

        var cameraPicker = new ComboBox
        {
            ItemsSource = cameras,
            DisplayMemberPath = nameof(DeviceInformation.Name),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var previewImage = new Microsoft.UI.Xaml.Controls.Image
        {
            Width = 640,
            Height = 480,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };

        var statusText = new TextBlock
        {
            Text = "Starting camera preview...",
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };

        var takePhotoButton = new Button
        {
            Content = "Take Photo",
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false,
        };

        var dialog = new ContentDialog
        {
            Title = "Capture photo",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Camera",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    cameraPicker,
                    previewImage,
                    statusText,
                    takePhotoButton,
                },
            },
        };

        void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (isUpdatingPreview || previewSource == null)
            {
                return;
            }

            using var frame = sender.TryAcquireLatestFrame();
            var softwareBitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (softwareBitmap == null)
            {
                return;
            }

            SoftwareBitmap displayBitmap;
            try
            {
                displayBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to convert preview frame: {ex.Message}");
                return;
            }

            isUpdatingPreview = true;
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (previewSource != null)
                    {
                        await previewSource.SetBitmapAsync(displayBitmap);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to update preview frame: {ex.Message}");
                }
                finally
                {
                    displayBitmap.Dispose();
                    isUpdatingPreview = false;
                }
            }))
            {
                displayBitmap.Dispose();
                isUpdatingPreview = false;
            }
        }

        async Task CleanupCameraAsync()
        {
            takePhotoButton.IsEnabled = false;

            if (frameReader != null)
            {
                frameReader.FrameArrived -= FrameReader_FrameArrived;
                try
                {
                    await frameReader.StopAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to stop frame reader: {ex.Message}");
                }
                finally
                {
                    frameReader.Dispose();
                    frameReader = null;
                }
            }

            previewSource = null;
            previewImage.Source = null;
            isPreviewing = false;
            isUpdatingPreview = false;

            mediaCapture?.Dispose();
            mediaCapture = null;
        }

        async Task<bool> InitializeCameraAsync(DeviceInformation device)
        {
            if (isInitializingCamera)
            {
                return false;
            }

            isInitializingCamera = true;
            cameraPicker.IsEnabled = false;
            takePhotoButton.IsEnabled = false;
            statusText.Text = $"Starting {device.Name}...";

            MediaCapture? nextCapture = null;
            MediaFrameReader? nextFrameReader = null;
            try
            {
                await CleanupCameraAsync();

                nextCapture = new MediaCapture();
                await nextCapture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = device.Id,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                });

                var frameSource = nextCapture.FrameSources.Values.FirstOrDefault(source =>
                    source.Info.SourceKind == MediaFrameSourceKind.Color &&
                    (source.Info.MediaStreamType == MediaStreamType.VideoPreview ||
                     source.Info.MediaStreamType == MediaStreamType.VideoRecord));
                if (frameSource == null)
                {
                    throw new InvalidOperationException("Selected camera does not expose a preview stream.");
                }

                nextFrameReader = await nextCapture.CreateFrameReaderAsync(frameSource, MediaEncodingSubtypes.Bgra8);
                nextFrameReader.FrameArrived += FrameReader_FrameArrived;
                nextFrameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                var startStatus = await nextFrameReader.StartAsync();
                if (startStatus != MediaFrameReaderStartStatus.Success)
                {
                    throw new InvalidOperationException($"Preview frame reader could not start ({startStatus}).");
                }

                previewSource = new SoftwareBitmapSource();
                previewImage.Source = previewSource;
                mediaCapture = nextCapture;
                frameReader = nextFrameReader;
                isPreviewing = true;
                statusText.Text = $"Previewing {device.Name}";
                takePhotoButton.IsEnabled = true;
                return true;
            }
            catch (Exception ex)
            {
                if (nextFrameReader != null)
                {
                    nextFrameReader.FrameArrived -= FrameReader_FrameArrived;
                    try
                    {
                        await nextFrameReader.StopAsync();
                    }
                    catch (Exception stopEx)
                    {
                        Debug.WriteLine($"Failed to stop frame reader after init failure: {stopEx.Message}");
                    }
                    nextFrameReader.Dispose();
                }

                nextCapture?.Dispose();
                previewSource = null;
                previewImage.Source = null;
                statusText.Text = $"Unable to preview {device.Name}.";
                Debug.WriteLine($"Camera init failed for '{device.Name}': {ex.Message}");
                return false;
            }
            finally
            {
                cameraPicker.IsEnabled = true;
                isInitializingCamera = false;
            }
        }

        cameraPicker.SelectionChanged += async (_, _) =>
        {
            if (cameraPicker.SelectedItem is DeviceInformation selectedCamera && !isCapturingPhoto)
            {
                await InitializeCameraAsync(selectedCamera);
            }
        };

        takePhotoButton.Click += async (_, _) =>
        {
            if (frameReader == null || isCapturingPhoto)
            {
                return;
            }

            isCapturingPhoto = true;
            takePhotoButton.IsEnabled = false;
            statusText.Text = "Capturing photo...";

            try
            {
                // Grab the latest frame from the reader instead of CapturePhotoToStreamAsync
                // which is unreliable with StreamingCaptureMode.Video on ARM64.
                using var frame = frameReader.TryAcquireLatestFrame();
                var softwareBitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (softwareBitmap == null)
                {
                    statusText.Text = "No frame available. Try again.";
                    takePhotoButton.IsEnabled = true;
                    isCapturingPhoto = false;
                    return;
                }

                // Convert to Bgra8 for encoding
                using var convertedBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                string captureFolderPath = Path.Combine(Path.GetTempPath(), "YOLO_Captures");
                Directory.CreateDirectory(captureFolderPath);

                string fileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                string filePath = Path.Combine(captureFolderPath, fileName);

                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, fileStream.AsRandomAccessStream());
                    encoder.SetSoftwareBitmap(convertedBitmap);
                    await encoder.FlushAsync();
                }

                await CleanupCameraAsync();
                await AddImageAsync(filePath, activate: true);
                dialog.Hide();
            }
            catch (Exception ex)
            {
                statusText.Text = "Capture failed. Try again.";
                App.Window?.ShowException(ex, "Failed to capture photo.");
                takePhotoButton.IsEnabled = frameReader != null && isPreviewing;
            }
            finally
            {
                isCapturingPhoto = false;
            }
        };

        try
        {
            bool cameraReady = false;
            if (cameraPicker.SelectedItem is DeviceInformation initialCamera)
            {
                cameraReady = await InitializeCameraAsync(initialCamera);
            }

            if (!cameraReady && cameras.Count <= 1)
            {
                // Camera init failed and no alternatives — don't show dialog.
                return;
            }

            await dialog.ShowAsync();
        }
        finally
        {
            await CleanupCameraAsync();
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeImage == null)
        {
            return;
        }
        var doomed = _activeImage;
        int index = _images.IndexOf(doomed);
        _images.RemoveAt(index);
        doomed.Dispose();
        _activeImage = null;
        ExtractButton.IsEnabled = false;
        AiAnalyzeButton.IsEnabled = false;

        if (_images.Count == 0)
        {
            DefaultImage.Source = null;
            StatusText.Text = string.Empty;
            StatusBar.Visibility = Visibility.Collapsed;
            RemoveButton.IsEnabled = false;
            ExtractButton.IsEnabled = false;
            AiAnalyzeButton.IsEnabled = false;
            return;
        }

        // Activate the neighbor at the same index (or the new last item).
        int next = Math.Min(index, _images.Count - 1);
        await ActivateImageAsync(_images[next]);
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeImage == null || _currentModel == null)
        {
            return;
        }
        var item = _activeImage;
        if (!item.Outputs.TryGetValue(CacheKey(), out var cached) || cached.BoxPredictions.Count == 0)
        {
            return;
        }

        ExtractButton.IsEnabled = false;
        Loader.IsActive = true;
        Loader.Visibility = Visibility.Visible;

        try
        {
            var opts = new CropExtractorOptions
            {
                ApplyMaskAlpha = cached.MaskedPredictions != null,
                PaddingPx = 8,
                MaxLongEdgePx = 1024,
                JpegQuality = 85,
                FillColor = Color.White,
            };

            // Crop on a background thread (GDI+ encode is CPU-bound).
            var crops = await Task.Run(() => CropExtractor.Extract(
                item.SourceBitmap, cached.MaskedPredictions, cached.BoxPredictions, opts));

            // Save JPEGs to %TEMP%\YOLO_Crops\<image-stem>\ for human inspection -
            // useful while pre-processing is being tuned.
            string stem = SanitizeForPath(item.DisplayName);
            string folder = Path.Combine(
                Path.GetTempPath(),
                "YOLO_Crops",
                stem + "_" + DateTime.Now.ToString("HHmmss"));
            await Task.Run(() => CropExtractor.SaveAll(crops, folder));

            // Pipe each crop through the stub extractor.
            var extracted = new List<(BookCrop Crop, string Title)>(crops.Count);
            foreach (var c in crops)
            {
                string title = await _titleExtractor.ExtractAsync(c);
                extracted.Add((c, title));
            }

            await ShowResultsDialog(item, extracted, folder);
        }
        catch (Exception ex)
        {
            App.Window?.ShowException(ex, "Failed to extract titles.");
        }
        finally
        {
            Loader.IsActive = false;
            Loader.Visibility = Visibility.Collapsed;
            ExtractButton.IsEnabled = cached.BoxPredictions.Count > 0;
        }
    }

    private async Task ShowResultsDialog(ImageItem item, List<(BookCrop Crop, string Title)> results, string folder)
    {
        // Build ReviewCandidates from the extraction results
        var candidates = new List<Models.ReviewCandidate>();
        for (int i = 0; i < results.Count; i++)
        {
            var (crop, title) = results[i];
            string detectedTitle = title;
            string? detectedAuthor = null;
            if (title.Contains(',') && !title.StartsWith("("))
            {
                var parts = title.Split(',', 2);
                detectedTitle = parts[0].Trim();
                detectedAuthor = parts.Length > 1 ? parts[1].Trim() : null;
            }
            candidates.Add(new Models.ReviewCandidate
            {
                Index = i,
                DetectedTitle = detectedTitle,
                DetectedAuthor = detectedAuthor,
                EditedTitle = detectedTitle,
                EditedAuthor = detectedAuthor,
                IsAccepted = true,
                CropJpeg = crop.Jpeg,
                PixelWidth = crop.PixelWidth,
                PixelHeight = crop.PixelHeight,
                Confidence = crop.Confidence
            });
        }
        _latestReviewCandidates = candidates;

        var sb = new StringBuilder();
        sb.AppendLine($"Extracted {results.Count} crop{(results.Count == 1 ? string.Empty : "s")} from '{item.DisplayName}'.");
        sb.AppendLine($"JPEGs saved to: {folder}");
        sb.AppendLine();
        foreach (var (crop, title) in results)
        {
            sb.AppendLine($"  [{crop.Label} {crop.Confidence:0.00}, {crop.PixelWidth}x{crop.PixelHeight}, {crop.Jpeg.Length / 1024} KB]");
            sb.AppendLine($"    -> {title}");
        }

        var dialog = new ContentDialog
        {
            Title = "Title Extraction",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = sb.ToString(),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                },
                MaxHeight = 480,
                MaxWidth = 720,
            },
            PrimaryButtonText = "Send to Review",
            SecondaryButtonText = "Open crop folder",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (App.Window is MainWindow mw)
            {
                mw.NavigateToReview(candidates, _activeImage?.FilePath);
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open folder: {ex.Message}");
            }
        }
    }

    private static string SanitizeForPath(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private async void AiAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeImage == null) return;

        var workflow = App.WorkflowService;
        if (workflow == null)
        {
            App.Window?.ShowException(null, "Workflow service not initialized.");
            return;
        }

        AiAnalyzeButton.IsEnabled = false;
        Loader.IsActive = true;
        Loader.Visibility = Visibility.Visible;

        try
        {
            // Encode the source bitmap to JPEG for the AI
            byte[] imageJpeg;
            using (var ms = new MemoryStream())
            {
                _activeImage.SourceBitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                imageJpeg = ms.ToArray();
            }

            var candidates = await workflow.AnalyzeFullImageAsync(
                imageJpeg, _activeImage.FilePath ?? _activeImage.DisplayName);

            if (candidates.Count == 0)
            {
                App.Window?.ShowException(null, "AI analysis returned no books. Try adjusting the image or check Settings.");
                return;
            }

            // Store candidates for the Review page to pick up
            _latestReviewCandidates = candidates;

            var dialog = new ContentDialog
            {
                Title = "AI Analysis Complete",
                Content = $"Detected {candidates.Count} book(s). Navigate to the Review tab to review and save them.",
                PrimaryButtonText = "Go to Review",
                CloseButtonText = "Stay here",
                XamlRoot = this.XamlRoot,
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Navigate to review page
                if (App.Window is MainWindow mw)
                {
                    mw.NavigateToReview(candidates, _activeImage?.FilePath);
                }
            }
        }
        catch (Exception ex)
        {
            App.Window?.ShowException(ex, "Full-image AI analysis failed.");
        }
        finally
        {
            Loader.IsActive = false;
            Loader.Visibility = Visibility.Collapsed;
            AiAnalyzeButton.IsEnabled = _activeImage != null;
        }
    }

    /// <summary>Latest review candidates from AI analysis, available for the Review page.</summary>
    internal static List<Models.ReviewCandidate>? _latestReviewCandidates;

    private void ViewerRoot_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to queue";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void ViewerRoot_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        bool first = true;
        foreach (var raw in items)
        {
            if (raw is not StorageFile sf)
            {
                continue;
            }
            string ext = Path.GetExtension(sf.Path);
            if (Array.IndexOf(SupportedExtensions, ext.ToLowerInvariant()) < 0)
            {
                continue;
            }
            await AddImageAsync(sf.Path, activate: first);
            first = false;
        }
    }

    // ---------------- Model / confidence handlers ----------------

    private async void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelChange)
        {
            return;
        }

        if (ModelPicker.SelectedItem is not ModelInfo model || model == _currentModel)
        {
            return;
        }

        Loader.IsActive = true;
        Loader.Visibility = Visibility.Visible;

        try
        {
            await LoadModel(model);
            ConfidenceSlider.Value = model.DefaultConfidence;
            if (_activeImage != null)
            {
                await ActivateImageAsync(_activeImage);
            }
        }
        catch (Exception ex)
        {
            App.Window?.ShowException(ex, $"Failed to load model '{model.DisplayName}'.");
        }
        finally
        {
            Loader.IsActive = false;
            Loader.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfidenceSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingConfidenceControls && ConfidenceText != null)
        {
            _syncingConfidenceControls = true;
            try
            {
                ConfidenceText.Text = e.NewValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                _syncingConfidenceControls = false;
            }
        }

        if (_inferenceSession == null || _activeImage == null)
        {
            return;
        }

        if (_sliderPointerActive)
        {
            _sliderValueChangedSincePress = true;
            _confidenceDebounce?.Stop();
            return;
        }

        _confidenceDebounce?.Stop();
        _confidenceDebounce?.Start();
    }

    private void ConfidenceSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _sliderPointerActive = true;
        _sliderValueChangedSincePress = false;
        _confidenceDebounce?.Stop();
    }

    private async void ConfidenceSlider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_sliderPointerActive)
        {
            return;
        }
        _sliderPointerActive = false;

        if (!_sliderValueChangedSincePress)
        {
            return;
        }
        _sliderValueChangedSincePress = false;

        if (_inferenceSession == null || _activeImage == null)
        {
            return;
        }

        _confidenceDebounce?.Stop();
        await ActivateImageAsync(_activeImage);
    }

    private void ConfidenceUp_Click(object sender, RoutedEventArgs e)
    {
        SetConfidence(ConfidenceSlider.Value + 0.05);
    }

    private void ConfidenceDown_Click(object sender, RoutedEventArgs e)
    {
        SetConfidence(ConfidenceSlider.Value - 0.05);
    }

    private void ConfidenceText_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitConfidenceText();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ConfidenceText.Text = ConfidenceSlider.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            e.Handled = true;
        }
    }

    private void ConfidenceText_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitConfidenceText();
    }

    private void CommitConfidenceText()
    {
        if (ConfidenceText == null)
        {
            return;
        }
        if (double.TryParse(ConfidenceText.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed))
        {
            SetConfidence(parsed);
        }
        else
        {
            ConfidenceText.Text = ConfidenceSlider.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void SetConfidence(double v)
    {
        v = Math.Clamp(v, ConfidenceSlider.Minimum, ConfidenceSlider.Maximum);
        v = Math.Round(v / ConfidenceSlider.StepFrequency) * ConfidenceSlider.StepFrequency;

        if (Math.Abs(v - ConfidenceSlider.Value) < 0.0001)
        {
            ConfidenceText.Text = v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        ConfidenceSlider.Value = v;
    }

    // ---------------- Viewer zoom / pan handlers ----------------

    private void ImageScroller_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ImageScroller);
        int delta = pt.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        float currentZoom = ImageScroller.ZoomFactor;
        float step = delta > 0 ? 1.10f : 1f / 1.10f;
        float newZoom = Math.Clamp(currentZoom * step, ImageScroller.MinZoomFactor, ImageScroller.MaxZoomFactor);

        if (Math.Abs(newZoom - currentZoom) < 0.001f)
        {
            e.Handled = true;
            return;
        }

        double contentX = pt.Position.X + ImageScroller.HorizontalOffset;
        double contentY = pt.Position.Y + ImageScroller.VerticalOffset;
        double offsetX = contentX * (newZoom / currentZoom) - pt.Position.X;
        double offsetY = contentY * (newZoom / currentZoom) - pt.Position.Y;

        ImageScroller.ChangeView(offsetX, offsetY, newZoom, disableAnimation: true);
        e.Handled = true;
    }

    private void ImageScroller_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        ImageScroller.ChangeView(0, 0, 1.0f, disableAnimation: false);
        e.Handled = true;
    }

    private void ImageScroller_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ImageScroller.ZoomFactor <= 1.001f)
        {
            return;
        }
        var pt = e.GetCurrentPoint(ImageScroller);
        if (!pt.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!ImageScroller.CapturePointer(e.Pointer))
        {
            return;
        }

        _isPanning = true;
        _panStartPointer = pt.Position;
        _panStartOffsetX = ImageScroller.HorizontalOffset;
        _panStartOffsetY = ImageScroller.VerticalOffset;
        _panPointerId = e.Pointer.PointerId;
        e.Handled = true;
    }

    private void ImageScroller_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPanning || e.Pointer.PointerId != _panPointerId)
        {
            return;
        }

        var pos = e.GetCurrentPoint(ImageScroller).Position;
        double dx = pos.X - _panStartPointer.X;
        double dy = pos.Y - _panStartPointer.Y;
        ImageScroller.ChangeView(_panStartOffsetX - dx, _panStartOffsetY - dy, null, disableAnimation: true);
        e.Handled = true;
    }

    private void ImageScroller_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPanning || e.Pointer.PointerId != _panPointerId)
        {
            return;
        }
        _isPanning = false;
        ImageScroller.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void ImageScroller_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPanning = false;
    }

    // ---------------- Detection ----------------

    private async Task DetectObjects(ImageItem item)
    {
        if (_inferenceSession == null || _currentModel == null)
        {
            return;
        }

        Loader.IsActive = true;
        Loader.Visibility = Visibility.Visible;

        var model = _currentModel;
        float confidence = (float)ConfidenceSlider.Value;
        string cacheKey = CacheKey();

        // Deep-copy the cached pristine source so RenderPredictions always draws boxes
        // on a fresh canvas. Bitmap.Clone() (parameterless) is a shallow clone that
        // shares the underlying pixel buffer with the source - using it here would let
        // the Graphics.FromImage draw calls bake boxes into item.SourceBitmap.
        Bitmap image = new Bitmap(item.SourceBitmap);
        int originalWidth = image.Width;
        int originalHeight = image.Height;

        var swTotal = Stopwatch.StartNew();
        long preprocessMs = 0;
        long inferenceMs = 0;
        long postprocessMs = 0;

        var detectionResult = await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            using var resizedImage = BitmapFunctions.ResizeWithPadding(
                image, model.InputWidth, model.InputHeight, out var letterbox);

            List<NamedOnnxValue> inputs;
            string inputName = _inferenceSession!.InputNames[0];

            switch (model.Layout)
            {
                case TensorLayout.Nchw:
                {
                    var tensor = BitmapFunctions.PreprocessBitmapForYolo26(resizedImage);
                    inputs = [NamedOnnxValue.CreateFromTensor(inputName, tensor)];
                    break;
                }
                case TensorLayout.Nhwc:
                default:
                {
                    var dims = _inferenceSession.InputMetadata[inputName].Dimensions;
                    dims[0] = 1;
                    Tensor<float> input = new DenseTensor<float>(dims);
                    input = BitmapFunctions.PreprocessBitmapForYOLO(resizedImage, input);
                    inputs = [NamedOnnxValue.CreateFromTensor(inputName, input)];
                    break;
                }
            }

            preprocessMs = sw.ElapsedMilliseconds;
            sw.Restart();

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _inferenceSession!.Run(inputs);
            inferenceMs = sw.ElapsedMilliseconds;
            sw.Restart();

            List<Prediction> boxOutput;
            List<MaskedPrediction>? maskOutput = null;
            switch (model.Head)
            {
                case ModelHead.Yolo26EndToEnd:
                {
                    var t = results[0].AsTensor<float>();
                    boxOutput = YOLOHelpers.ExtractYolo26EndToEnd(t, letterbox, model.Labels, confidence);
                    break;
                }
                case ModelHead.Yolo26OneToMany:
                {
                    var t = results[0].AsTensor<float>();
                    boxOutput = YOLOHelpers.ExtractYolo26OneToMany(t, letterbox, model.Labels, confidence);
                    break;
                }
                case ModelHead.Yolo26Segmentation:
                {
                    var detTensor = results[0].AsTensor<float>();
                    var protoTensor = results[1].AsTensor<float>();
                    maskOutput = YOLOHelpers.ExtractYolo26Segmentation(
                        detTensor, protoTensor, letterbox,
                        originalWidth, originalHeight,
                        model.InputWidth, model.InputHeight,
                        model.Labels, confidence);
                    boxOutput = maskOutput.ConvertAll(m => m.ToPrediction());
                    break;
                }
                case ModelHead.Yolov4Anchor:
                default:
                {
                    var grids = new List<Tensor<float>>
                    {
                        results[0].AsTensor<float>(),
                        results[1].AsTensor<float>(),
                        results[2].AsTensor<float>(),
                    };
                    var raw = YOLOHelpers.ExtractPredictions(
                        grids, Yolov4Anchors,
                        model.InputWidth, model.InputHeight,
                        originalWidth, originalHeight,
                        model.Labels, confidence);
                    boxOutput = YOLOHelpers.ApplyNms(raw, 0.4f);
                    break;
                }
            }

            postprocessMs = sw.ElapsedMilliseconds;
            return (Box: boxOutput, Mask: maskOutput);
        });

        int detectionCount = detectionResult.Box.Count;
        BitmapImage outputImage = detectionResult.Mask is { Count: > 0 }
            ? BitmapFunctions.RenderMaskedPredictions(image, detectionResult.Mask)
            : BitmapFunctions.RenderPredictions(image, detectionResult.Box);

        item.Outputs[cacheKey] = new CachedOutput
        {
            Image = outputImage,
            BoxPredictions = detectionResult.Box,
            MaskedPredictions = detectionResult.Mask,
        };

        DispatcherQueue.TryEnqueue(() =>
        {
            // Only swap if the user hasn't switched to a different image meanwhile.
            if (_activeImage == item)
            {
                DefaultImage.Source = outputImage;
                string segNote = detectionResult.Mask is { Count: > 0 } ? "  (seg)" : string.Empty;
                StatusText.Text =
                    $"{detectionCount} detection{(detectionCount == 1 ? string.Empty : "s")}{segNote}  •  " +
                    $"pre {preprocessMs} ms  •  infer {inferenceMs} ms  •  post {postprocessMs} ms  •  total {swTotal.ElapsedMilliseconds} ms";
                StatusBar.Visibility = Visibility.Visible;
                ExtractButton.IsEnabled = detectionCount > 0;
            }
            Loader.IsActive = false;
            Loader.Visibility = Visibility.Collapsed;
        });

        image.Dispose();
    }
}
