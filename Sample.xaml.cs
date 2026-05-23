using BiblioText.Models;
using BiblioText.Services;
using BiblioText.Utils;
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

namespace BiblioText;

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
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

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

        ConfidenceSlider.Value = 0.20;

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

        // Skip auto-detection if checkbox is unchecked (user will use Refresh button)
        if (!forceRerun && AutoDetectCheck.IsChecked != true)
        {
            ExtractButton.IsEnabled = false;
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
        // Check if camera is enabled in settings
        var appSettings = App.SettingsStore?.Load();
        if (appSettings != null && !appSettings.UseCameraCapture)
        {
            var disabledDialog = new ContentDialog
            {
                Title = "Camera disabled",
                Content = "Camera capture is disabled. Enable it in Settings.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            };
            await disabledDialog.ShowAsync();
            return;
        }

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

        var dialog = new ContentDialog
        {
            Title = "Capture photo",
            CloseButtonText = "Cancel",
            PrimaryButtonText = "Take Photo",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary,
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
            dialog.IsPrimaryButtonEnabled = false;

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
            dialog.IsPrimaryButtonEnabled = false;
            statusText.Text = $"Starting {device.Name}...";

            MediaCapture? nextCapture = null;
            MediaFrameReader? nextFrameReader = null;
            try
            {
                await CleanupCameraAsync();

                // Find a SourceGroup that includes this device
                var sourceGroups = await MediaFrameSourceGroup.FindAllAsync();
                var matchingGroup = sourceGroups.FirstOrDefault(g =>
                    g.SourceInfos.Any(si => si.DeviceInformation?.Id == device.Id && si.SourceKind == MediaFrameSourceKind.Color));

                nextCapture = new MediaCapture();
                var initSettings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = device.Id,
                    PhotoCaptureSource = PhotoCaptureSource.Auto,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                };
                if (matchingGroup != null)
                {
                    initSettings.SourceGroup = matchingGroup;
                }
                await nextCapture.InitializeAsync(initSettings);

                // Accept any color frame source
                var frameSource = nextCapture.FrameSources.Values.FirstOrDefault(source =>
                    source.Info.SourceKind == MediaFrameSourceKind.Color);
                if (frameSource == null)
                {
                    throw new InvalidOperationException("Selected camera does not expose a color frame source.");
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
                dialog.IsPrimaryButtonEnabled = true;
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
                statusText.Text = $"Camera failed: {ex.Message}";
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

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            // Defer closing so we can do async work
            var deferral = args.GetDeferral();
            args.Cancel = true; // prevent auto-close; we'll call dialog.Hide() on success

            if (mediaCapture == null || isCapturingPhoto)
            {
                deferral.Complete();
                return;
            }

            isCapturingPhoto = true;
            dialog.IsPrimaryButtonEnabled = false;
            statusText.Text = "Capturing photo...";

            try
            {
                using var photoStream = new InMemoryRandomAccessStream();
                await mediaCapture.CapturePhotoToStreamAsync(
                    ImageEncodingProperties.CreateJpeg(), photoStream);
                photoStream.Seek(0);

                string captureFolderPath = Path.Combine(Path.GetTempPath(), "YOLO_Captures");
                Directory.CreateDirectory(captureFolderPath);

                string fileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                string filePath = Path.Combine(captureFolderPath, fileName);

                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    await photoStream.AsStreamForRead().CopyToAsync(fileStream);
                }

                await CleanupCameraAsync();
                deferral.Complete();
                dialog.Hide();
                await AddImageAsync(filePath, activate: true);
            }
            catch (Exception ex)
            {
                statusText.Text = $"Capture failed: {ex.Message}";
                dialog.IsPrimaryButtonEnabled = mediaCapture != null && isPreviewing;
                deferral.Complete();
            }
            finally
            {
                isCapturingPhoto = false;
            }
        };

        dialog.Opened += async (s, args) =>
        {
            if (cameraPicker.SelectedItem is DeviceInformation initialCamera)
            {
                await InitializeCameraAsync(initialCamera);
            }
        };

        try
        {
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

    private async Task RemoveActiveImageAsync()
    {
        if (_activeImage == null) return;
        var doomed = _activeImage;
        int index = _images.IndexOf(doomed);
        if (index < 0) return;
        _images.RemoveAt(index);
        doomed.Dispose();
        _activeImage = null;

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

        // Capture what we need from the image before clearing the screen
        var sourceBitmap = item.SourceBitmap;
        var maskedPredictions = cached.MaskedPredictions;
        var boxPredictions = cached.BoxPredictions;
        string displayName = item.DisplayName;
        string? filePath = item.FilePath;

        // Clear the screen immediately so user can load next scan
        await RemoveActiveImageAsync();
        ScanStatusText.Text = "Extracting titles (processing in background)...";

        try
        {
            var opts = new CropExtractorOptions
            {
                ApplyMaskAlpha = maskedPredictions != null,
                PaddingPx = 8,
                MaxLongEdgePx = 1024,
                JpegQuality = 85,
                FillColor = Color.White,
            };

            var crops = await Task.Run(() => CropExtractor.Extract(
                sourceBitmap, maskedPredictions, boxPredictions, opts));

            string stem = SanitizeForPath(displayName);
            string folder = Path.Combine(
                Path.GetTempPath(),
                "YOLO_Crops",
                stem + "_" + DateTime.Now.ToString("HHmmss"));
            await Task.Run(() => CropExtractor.SaveAll(crops, folder));

            // Pipe each crop through the title extractor
            var extracted = new List<(BookCrop Crop, string Title)>(crops.Count);
            for (int ci = 0; ci < crops.Count; ci++)
            {
                var c = crops[ci];
                ScanStatusText.Text = $"Extracting title {ci + 1} of {crops.Count}...";
                string title = await _titleExtractor.ExtractAsync(c);
                extracted.Add((c, title));
            }

            // Build candidates and send to review
            var candidates = new List<Models.ReviewCandidate>();
            for (int i = 0; i < extracted.Count; i++)
            {
                var (crop, title) = extracted[i];
                string detectedTitle = title;
                string detectedAuthor = "";
                if (title.Contains(" - "))
                {
                    var parts = title.Split(" - ", 2);
                    detectedTitle = parts[0].Trim();
                    detectedAuthor = parts[1].Trim();
                }
                else if (title.Contains(" by "))
                {
                    var parts = title.Split(" by ", 2);
                    detectedTitle = parts[0].Trim();
                    detectedAuthor = parts[1].Trim();
                }

                candidates.Add(new Models.ReviewCandidate
                {
                    Index = i + 1,
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

            ScanStatusText.Text = $"Sent {candidates.Count} book(s) to Review.";
            if (App.Window is MainWindow mw)
            {
                mw.NavigateToReview(candidates, filePath);
            }
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = $"Extraction failed: {ex.Message}";
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

        // Capture image data before clearing the screen
        byte[] imageJpeg;
        List<BookCrop>? cropsList = null;
        string filePath = _activeImage.FilePath ?? _activeImage.DisplayName;

        if (_activeImage.Outputs.TryGetValue(CacheKey(), out var cached) && cached.BoxPredictions.Count > 0)
        {
            imageJpeg = BitmapFunctions.RenderAnnotatedJpeg(_activeImage.SourceBitmap, cached.BoxPredictions);
            var opts = new CropExtractorOptions { PaddingPx = 4, MaxLongEdgePx = 512, JpegQuality = 80, FillColor = Color.White };
            cropsList = await Task.Run(() => CropExtractor.Extract(
                _activeImage.SourceBitmap, cached.MaskedPredictions, cached.BoxPredictions, opts));
        }
        else
        {
            using var ms = new MemoryStream();
            _activeImage.SourceBitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            imageJpeg = ms.ToArray();
        }

        // Clear screen immediately so user can load another scan
        await RemoveActiveImageAsync();
        ScanStatusText.Text = "Running AI image analysis...";

        try
        {
            var candidates = await workflow.AnalyzeFullImageAsync(imageJpeg, filePath);

            if (candidates.Count == 0)
            {
                ScanStatusText.Text = "AI analysis returned no books.";
                return;
            }

            // Attach crop images if available
            if (cropsList != null)
            {
                for (int i = 0; i < candidates.Count && i < cropsList.Count; i++)
                {
                    candidates[i].CropJpeg = cropsList[i].Jpeg;
                    candidates[i].PixelWidth = cropsList[i].PixelWidth;
                    candidates[i].PixelHeight = cropsList[i].PixelHeight;
                    candidates[i].Index = i + 1;
                }
            }

            _latestReviewCandidates = candidates;
            ScanStatusText.Text = $"AI analysis complete — {candidates.Count} book(s) sent to Review.";
            if (App.Window is MainWindow mw)
            {
                mw.NavigateToReview(candidates, filePath);
            }
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = $"AI analysis failed: {ex.Message}";
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

    private async void RefreshDetectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeImage != null)
        {
            await ActivateImageAsync(_activeImage, forceRerun: true);
        }
    }

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
