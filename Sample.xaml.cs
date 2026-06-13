using BiblioText.Models;
using BiblioText.Services;
using BiblioText.Utils;
using Microsoft.Graphics.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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
    private bool _detecting; // true while DetectObjects Task.Run is in-flight

    // Secondary model for clipping (bounding boxes) — loaded on first extract/AI call
    private InferenceSession? _clipSession;
    private ModelInfo? _clipModel;

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

    // ---------------- Box overlay (clickable detections + user-drawn box) ----------------
    //
    // BoxOverlay is a Canvas sized to source-image pixel dimensions, sitting in
    // the same Viewbox as DefaultImage so it scales with zoom/pan. Each
    // detection becomes a Rectangle child whose Tag points back to its
    // Prediction; tapping toggles IsExcluded. When DrawBoxButton is checked,
    // the Canvas captures pointer events to rubber-band a new Rectangle, which
    // on release becomes a Prediction with IsManual=true.
    private bool _drawMode;
    private bool _isDrawing;
    private uint _drawPointerId;
    private Windows.Foundation.Point _drawStart;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _drawRect;
    private CachedOutput? _overlayCache; // the cache entry currently rendered in BoxOverlay
    private double _overlayImageWidth;
    private double _overlayImageHeight;
    private DrawSource _drawSource = DrawSource.None;
    // Pen-barrel hover tracking: hover-barrel may not produce PointerPressed, so
    // we drive draw start/commit off rising/falling edges of IsBarrelButtonPressed
    // observed in PointerMoved.
    private bool _penBarrelPressed;
    private uint _penPointerId;

    private enum DrawSource { None, Toggle, PenBarrel }

    public Sample()
    {
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

        // BoxOverlay receives draw-mode pointer events directly. ScrollViewer
        // marks pointer events handled internally for its own scroll/zoom logic,
        // so we have to use AddHandler with handledEventsToo:true (the same
        // pattern the pan handlers below use) — otherwise the overlay's regular
        // PointerPressed never fires and Draw mode silently does nothing.
        BoxOverlay.AddHandler(
            PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(BoxOverlay_PointerPressed),
            handledEventsToo: true);
        BoxOverlay.AddHandler(
            PointerMovedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(BoxOverlay_PointerMoved),
            handledEventsToo: true);
        BoxOverlay.AddHandler(
            PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(BoxOverlay_PointerReleased),
            handledEventsToo: true);
        BoxOverlay.AddHandler(
            PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(BoxOverlay_PointerCaptureLost),
            handledEventsToo: true);
        BoxOverlay.AddHandler(
            PointerExitedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(BoxOverlay_PointerExited),
            handledEventsToo: true);
        BoxOverlay.AddHandler(
            PointerCanceledEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(BoxOverlay_PointerCanceled),
            handledEventsToo: true);
    }

    private void Page_Loaded()
    {
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Only initialize once — page is cached, don't reset on re-navigation
        if (_currentModel != null) return;

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
        var initial = ModelRegistry.DefaultForViewing(_modelsDir) ?? available[0];
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
    }

    private async Task LoadModel(ModelInfo model)
    {
        // If detection is running, keep old session alive until it finishes
        var oldSession = _inferenceSession;
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

        // Dispose old session only after new one is ready and detection has finished
        if (oldSession != null && !_detecting)
        {
            oldSession.Dispose();
        }
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

        // Check image hash against DB for previously imported images
        var repo = App.LibraryRepository;
        if (repo != null)
        {
            string hash = await Task.Run(() => ComputeFileHash(filePath));
            bool alreadyImported = await repo.ImageHashExistsAsync(hash);
            if (alreadyImported)
            {
                var dialog = new ContentDialog
                {
                    Title = "Duplicate Image",
                    Content = $"'{Path.GetFileName(filePath)}' has already been imported. Import again?",
                    PrimaryButtonText = "Import Anyway",
                    CloseButtonText = "Skip",
                    XamlRoot = this.XamlRoot,
                    DefaultButton = ContentDialogButton.Close,
                };
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    return null;
                }
            }
            else
            {
                await repo.AddImageHashAsync(hash, filePath);
            }
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

    private static string ComputeFileHash(string filePath)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hashBytes = sha.ComputeHash(stream);
        return Convert.ToHexString(hashBytes);
    }

    private bool _activating;

    private async Task ActivateImageAsync(ImageItem item, bool forceRerun = false)
    {
        if (_activating) return;
        _activating = true;
        try
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
            // render disappear instantly. Clear the overlay too — a fresh cache hit /
            // detection will rebuild it.
            if (item.SourceImage != null)
            {
                DefaultImage.Source = item.SourceImage;
            }
            ClearBoxOverlay();

            if (_inferenceSession == null || _currentModel == null)
            {
                ExtractButton.IsEnabled = false;
                return;
            }

            string cacheKey = CacheKey();
            if (!forceRerun && item.Outputs.TryGetValue(cacheKey, out CachedOutput? cached))
            {
                DefaultImage.Source = cached.Image;
                RefreshBoxOverlay(cached);
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
        catch (Exception ex)
        {
            _detecting = false;
            Debug.WriteLine($"ActivateImageAsync failed: {ex}");
            Loader.IsActive = false;
            Loader.Visibility = Visibility.Collapsed;
            // Show method name from stack trace for easier debugging
            string location = ex.TargetSite?.Name ?? "unknown";
            StatusText.Text = $"Detection failed in {location}: {ex.Message}";
            StatusBar.Visibility = Visibility.Visible;
        }
        finally
        {
            _activating = false;
        }
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
            ClearBoxOverlay();
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

    private int _isExtracting;
    private int _isAnalyzing;

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (System.Threading.Interlocked.Exchange(ref _isExtracting, 1) == 1) return;
        ExtractButton.IsEnabled = false;
        try
        {
        if (_activeImage == null || _currentModel == null)
        {
            return;
        }
        var item = _activeImage;

        // Use the bounding boxes the user actually sees in the overlay — including
        // their manual additions and minus anything they deselected — instead of
        // re-running a separate clip model. Falls back to the clip model only if
        // detection hasn't been cached yet.
        ScanStatusText.Text = "Preparing crops for extraction...";
        var sourceBitmap = item.SourceBitmap;
        float confidence = (float)ConfidenceSlider.Value;

        List<Prediction> boxPredictions;
        if (item.Outputs.TryGetValue(CacheKey(), out CachedOutput? cached) && cached.BoxPredictions.Count > 0)
        {
            // Snapshot to avoid mutation while the async crop runs in the background.
            boxPredictions = cached.BoxPredictions
                .Where(p => !p.IsExcluded && p.Box != null)
                .Select(p => new Prediction { Box = p.Box, Label = p.Label, Confidence = p.Confidence, IsManual = p.IsManual })
                .ToList();
        }
        else
        {
            try
            {
                boxPredictions = await GetClipPredictionsAsync(sourceBitmap, confidence);
            }
            catch (Exception ex)
            {
                ScanStatusText.Text = $"Clip model error: {ex.Message}";
                return;
            }
        }

        if (boxPredictions.Count == 0)
        {
            ScanStatusText.Text = "No boxes selected for extraction.";
            return;
        }

        string displayName = item.DisplayName;
        string? filePath = item.FilePath;

        // Remove from UI without disposing the bitmap (we still need it for cropping)
        int index = _images.IndexOf(item);
        if (index >= 0) _images.RemoveAt(index);
        _activeImage = null;

        if (_images.Count == 0)
        {
            DefaultImage.Source = null;
            ClearBoxOverlay();
            StatusText.Text = string.Empty;
            StatusBar.Visibility = Visibility.Collapsed;
            RemoveButton.IsEnabled = false;
            ExtractButton.IsEnabled = false;
            AiAnalyzeButton.IsEnabled = false;
        }
        else
        {
            int next = Math.Min(index, _images.Count - 1);
            await ActivateImageAsync(_images[next]);
        }

        ScanStatusText.Text = "Extracting titles (processing in background)...";

        try
        {
            var opts = new CropExtractorOptions
            {
                ApplyMaskAlpha = false, // bounding box model — no masks
                PaddingPx = 8,
                MaxLongEdgePx = 1024,
                JpegQuality = 85,
                FillColor = Color.White,
            };

            var crops = await Task.Run(() => CropExtractor.Extract(
                sourceBitmap, null, boxPredictions, opts));

            string stem = SanitizeForPath(displayName);
            string folder = Path.Combine(
                Path.GetTempPath(),
                "YOLO_Crops",
                stem + "_" + DateTime.Now.ToString("HHmmss"));
            await Task.Run(() => CropExtractor.SaveAll(crops, folder));

            // Pipe each crop through the title extractor
            var extracted = new List<(BookCrop Crop, ExtractionResult Result)>(crops.Count);
            for (int ci = 0; ci < crops.Count; ci++)
            {
                var c = crops[ci];
                ScanStatusText.Text = $"Extracting title {ci + 1} of {crops.Count}...";
                var result = await _titleExtractor.ExtractAsync(c);
                extracted.Add((c, result));
            }

            // Build candidates and send to review
            var candidates = new List<Models.ReviewCandidate>();
            for (int i = 0; i < extracted.Count; i++)
            {
                var (crop, result) = extracted[i];

                candidates.Add(new Models.ReviewCandidate
                {
                    Index = i + 1,
                    DetectedTitle = result.Title,
                    DetectedAuthor = result.Author,
                    EditedTitle = result.Title,
                    EditedAuthor = result.Author,
                    IsAccepted = true,
                    CropJpeg = crop.Jpeg,
                    PixelWidth = crop.PixelWidth,
                    PixelHeight = crop.PixelHeight,
                    Confidence = result.Confidence
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
        finally
        {
            item.Dispose();
        }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isExtracting, 0);
            ExtractButton.IsEnabled = _activeImage != null;
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
        if (System.Threading.Interlocked.Exchange(ref _isAnalyzing, 1) == 1) return;
        AiAnalyzeButton.IsEnabled = false;
        try
        {
        if (_activeImage == null) return;

        var workflow = App.WorkflowService;
        if (workflow == null)
        {
            App.Window?.ShowException(null, "Workflow service not initialized.");
            return;
        }

        // Use the bounding boxes the user actually sees in the overlay (minus
        // deselected ones, plus manual ones) so the AI submission matches what
        // the user reviewed. Fall back to clip model only if detection hasn't run.
        ScanStatusText.Text = "Preparing image for AI analysis...";
        var sourceBitmap = _activeImage.SourceBitmap;
        float confidence = (float)ConfidenceSlider.Value;
        string filePath = _activeImage.FilePath ?? _activeImage.DisplayName;

        byte[] imageJpeg;
        List<BookCrop>? cropsList = null;

        List<Prediction> clipBoxes;
        if (_activeImage.Outputs.TryGetValue(CacheKey(), out CachedOutput? aiCached) && aiCached.BoxPredictions.Count > 0)
        {
            clipBoxes = aiCached.BoxPredictions
                .Where(p => !p.IsExcluded && p.Box != null)
                .Select(p => new Prediction { Box = p.Box, Label = p.Label, Confidence = p.Confidence, IsManual = p.IsManual })
                .ToList();
        }
        else
        {
            try
            {
                clipBoxes = await GetClipPredictionsAsync(sourceBitmap, confidence);
            }
            catch
            {
                clipBoxes = [];
            }
        }

        if (clipBoxes.Count > 0)
        {
            imageJpeg = BitmapFunctions.RenderAnnotatedJpeg(sourceBitmap, clipBoxes);
            var opts = new CropExtractorOptions { PaddingPx = 4, MaxLongEdgePx = 512, JpegQuality = 80, FillColor = Color.White };
            cropsList = await Task.Run(() => CropExtractor.Extract(
                sourceBitmap, null, clipBoxes, opts));
        }
        else
        {
            using var ms = new MemoryStream();
            sourceBitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
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
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isAnalyzing, 0);
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

    private async void RefreshDetectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeImage != null)
        {
            await ActivateImageAsync(_activeImage, forceRerun: true);
        }
    }

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeImage == null) return;

        OcrButton.IsEnabled = false;
        StatusText.Text = "Running OCR...";
        StatusBar.Visibility = Visibility.Visible;

        try
        {
            var item = _activeImage;
            var sourceBitmap = item.SourceBitmap;
            int w = sourceBitmap.Width;
            int h = sourceBitmap.Height;

            // Deep-copy on UI thread where we have exclusive GDI+ access,
            // then encode to PNG off-thread
            Bitmap copy;
            try
            {
                copy = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(copy))
                {
                    g.DrawImage(sourceBitmap, 0, 0, w, h);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"OCR: bitmap copy failed — {ex.Message}";
                return;
            }

            byte[] pngBytes = await Task.Run(() =>
            {
                using (copy)
                using (var ms = new MemoryStream())
                {
                    copy.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            });

            // Decode to SoftwareBitmap on UI thread
            SoftwareBitmap softwareBitmap;
            using (var ras = new InMemoryRandomAccessStream())
            {
                await ras.WriteAsync(pngBytes.AsBuffer());
                ras.Seek(0);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
                softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            }

            // Ensure TextRecognizer is ready
            var readyState = TextRecognizer.GetReadyState();
            if (readyState == AIFeatureReadyState.NotReady)
            {
                var op = await TextRecognizer.EnsureReadyAsync();
                if (op.Status != AIFeatureReadyResultState.Success)
                {
                    StatusText.Text = "OCR not available on this system.";
                    softwareBitmap.Dispose();
                    return;
                }
            }
            else if (readyState != AIFeatureReadyState.Ready)
            {
                StatusText.Text = $"OCR not available: {readyState}";
                softwareBitmap.Dispose();
                return;
            }

            var recognizer = await TextRecognizer.CreateAsync();
            RecognizedText? result;
            using (var imageBuffer = ImageBuffer.CreateForSoftwareBitmap(softwareBitmap))
            {
                result = recognizer.RecognizeTextFromImage(imageBuffer);
            }

            if (result?.Lines == null || !result.Lines.Any())
            {
                StatusText.Text = "OCR: no text found.";
                softwareBitmap.Dispose();
                return;
            }

            // Render OCR text overlay on the canvas
            OcrOverlay.Children.Clear();
            OcrOverlay.Width = softwareBitmap.PixelWidth;
            OcrOverlay.Height = softwareBitmap.PixelHeight;
            softwareBitmap.Dispose();

            foreach (var line in result.Lines)
            {
                try
                {
                    var bgBrush = new SolidColorBrush { Color = Colors.Black, Opacity = 0.6 };
                    double height = Math.Abs(line.BoundingBox.TopRight.Y - line.BoundingBox.BottomRight.Y) * 0.85;

                    var block = new TextBlock
                    {
                        Text = line.Text,
                        Foreground = new SolidColorBrush(Colors.White),
                        FontSize = height > 0 ? height : 1,
                    };

                    var grid = new Grid
                    {
                        Background = bgBrush,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 2, 4, 2),
                    };
                    grid.Children.Add(block);

                    OcrOverlay.Children.Add(grid);
                    Canvas.SetLeft(grid, line.BoundingBox.TopLeft.X);
                    Canvas.SetTop(grid, line.BoundingBox.TopLeft.Y);
                }
                catch { }
            }

            int lineCount = result.Lines.Count();
            OcrOverlay.Visibility = Visibility.Visible;
            StatusText.Text = $"OCR: {lineCount} line(s) detected. Clearing in 10s...";

            // Auto-hide after 10 seconds
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (s, _) =>
            {
                timer.Stop();
                OcrOverlay.Visibility = Visibility.Collapsed;
                OcrOverlay.Children.Clear();
                StatusText.Text = "OCR overlay cleared.";
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"OCR failed: {ex.Message}";
            Debug.WriteLine($"OCR failed: {ex}");
        }
        finally
        {
            OcrButton.IsEnabled = true;
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
            if (_activeImage != null && AutoDetectCheck.IsChecked == true)
            {
                await ActivateImageAsync(_activeImage, forceRerun: true);
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
        // Don't capture the pointer when the user is interacting with the box
        // overlay — either to tap a detection rectangle or to rubber-band a new
        // one. Without this guard, ScrollViewer pan capture (at zoom > 1) would
        // swallow the press before the Rectangle's PointerPressed could fire.
        if (_drawMode || IsInsideBoxOverlay(e.OriginalSource as DependencyObject))
        {
            return;
        }

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

    private bool IsInsideBoxOverlay(DependencyObject? src)
    {
        while (src != null)
        {
            if (src == BoxOverlay)
            {
                return true;
            }
            src = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(src);
        }
        return false;
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
        var session = _inferenceSession;
        float confidence = (float)ConfidenceSlider.Value;
        string cacheKey = CacheKey();

        // Deep-copy the cached pristine source so RenderPredictions always draws boxes
        // on a fresh canvas.
        Bitmap image;
        try
        {
            image = new Bitmap(item.SourceBitmap);
        }
        catch
        {
            Loader.IsActive = false;
            Loader.Visibility = Visibility.Collapsed;
            return;
        }
        int originalWidth = image.Width;
        int originalHeight = image.Height;

        var swTotal = Stopwatch.StartNew();
        long preprocessMs = 0;
        long inferenceMs = 0;
        long postprocessMs = 0;

        _detecting = true;
        var detectionResult = await Task.Run(() =>
        {
            try
            {
            var sw = Stopwatch.StartNew();
            using var resizedImage = BitmapFunctions.ResizeWithPadding(
                image, model.InputWidth, model.InputHeight, out var letterbox);

            List<NamedOnnxValue> inputs;
            string inputName = session.InputNames[0];

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
                    var dims = session.InputMetadata[inputName].Dimensions;
                    dims[0] = 1;
                    Tensor<float> input = new DenseTensor<float>(dims);
                    input = BitmapFunctions.PreprocessBitmapForYOLO(resizedImage, input);
                    inputs = [NamedOnnxValue.CreateFromTensor(inputName, input)];
                    break;
                }
            }

            preprocessMs = sw.ElapsedMilliseconds;
            sw.Restart();

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);
            if (results == null || results.Count == 0)
            {
                return (Box: new List<Prediction>(), Mask: (List<MaskedPrediction>?)null);
            }
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
                    if (results.Count < 2)
                    {
                        var t = results[0].AsTensor<float>();
                        boxOutput = YOLOHelpers.ExtractYolo26EndToEnd(t, letterbox, model.Labels, confidence);
                        break;
                    }
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
                case ModelHead.RfDetr:
                {
                    boxOutput = YOLOHelpers.ExtractRfDetr(
                        results, letterbox,
                        originalWidth, originalHeight,
                        model.InputWidth, model.InputHeight,
                        model.Labels, confidence);
                    break;
                }
                case ModelHead.Yolov4Anchor:
                default:
                {
                    if (results.Count < 3)
                    {
                        boxOutput = new List<Prediction>();
                        break;
                    }
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Inference Task.Run failed: {ex}");
                return (Box: (List<Prediction>?)null, Mask: (List<MaskedPrediction>?)null);
            }
        });
        _detecting = false;

        // If inference failed (null Box), don't cache — allow retry
        if (detectionResult.Box == null)
        {
            image.Dispose();
            DispatcherQueue.TryEnqueue(() =>
            {
                Loader.IsActive = false;
                Loader.Visibility = Visibility.Collapsed;
                StatusText.Text = "Detection failed — click Refresh to retry.";
                StatusBar.Visibility = Visibility.Visible;
            });
            return;
        }

        int detectionCount = detectionResult.Box.Count;
        BitmapImage outputImage;
        try
        {
            outputImage = detectionResult.Mask is { Count: > 0 }
                ? BitmapFunctions.RenderMaskedPredictions(image, detectionResult.Mask)
                : BitmapFunctions.RenderPredictions(image, detectionResult.Box);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RenderPredictions failed: {ex}");
            image.Dispose();
            throw;
        }

        var cachedOutput = new CachedOutput
        {
            Image = outputImage,
            BoxPredictions = detectionResult.Box,
            MaskedPredictions = detectionResult.Mask,
        };
        item.Outputs[cacheKey] = cachedOutput;

        DispatcherQueue.TryEnqueue(() =>
        {
            // Only swap if the user hasn't switched to a different image meanwhile
            // and the active cache key still matches (guard against a stale
            // late-arriving detection from a previous model/confidence).
            if (_activeImage == item && CacheKey() == cacheKey)
            {
                DefaultImage.Source = outputImage;
                RefreshBoxOverlay(cachedOutput);
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

    /// <summary>
    /// Ensures the clipping model (yolo26m bounding boxes) is loaded.
    /// Used by Extract and AI Analyze to get clean bounding box crops
    /// regardless of which model is selected for viewing.
    /// </summary>
    private async Task EnsureClipModelAsync()
    {
        if (_clipSession != null) return;

        _clipModel = ModelRegistry.DefaultForClipping(_modelsDir);
        if (_clipModel == null) return;

        string modelPath = _clipModel.GetFullPath(_modelsDir);
        await Task.Run(async () =>
        {
            var catalog = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
            try { await catalog.EnsureAndRegisterCertifiedAsync(); } catch { }
            SessionOptions opts = new();
            opts.RegisterOrtExtensions();
            _clipSession = new InferenceSession(modelPath, opts);
        });
    }

    /// <summary>
    /// Runs the clip model on the given bitmap and returns bounding box predictions.
    /// </summary>
    private async Task<List<Prediction>> GetClipPredictionsAsync(Bitmap sourceBitmap, float confidence)
    {
        await EnsureClipModelAsync();
        if (_clipSession == null || _clipModel == null) return [];

        var model = _clipModel;
        var session = _clipSession;
        int originalWidth = sourceBitmap.Width;
        int originalHeight = sourceBitmap.Height;

        // Clone bitmap data on UI thread to avoid GDI+ threading issues
        Bitmap cloned;
        try
        {
            cloned = new Bitmap(originalWidth, originalHeight, sourceBitmap.PixelFormat);
            using (var g = Graphics.FromImage(cloned))
            {
                g.DrawImage(sourceBitmap, 0, 0, originalWidth, originalHeight);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Bitmap clone failed: {ex.Message}");
            return [];
        }

        return await Task.Run(() =>
        {
            using (cloned)
            using (var resized = BitmapFunctions.ResizeWithPadding(
                cloned, model.InputWidth, model.InputHeight, out var letterbox))
            {
                string inputName = session.InputNames[0];
                var tensor = BitmapFunctions.PreprocessBitmapForYolo26(resized);
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

                using var results = session.Run(inputs);

                return model.Head switch
                {
                    ModelHead.Yolo26EndToEnd =>
                        YOLOHelpers.ExtractYolo26EndToEnd(results[0].AsTensor<float>(), letterbox, model.Labels, confidence),
                    ModelHead.Yolo26OneToMany =>
                        YOLOHelpers.ExtractYolo26OneToMany(results[0].AsTensor<float>(), letterbox, model.Labels, confidence),
                    _ => YOLOHelpers.ExtractYolo26EndToEnd(results[0].AsTensor<float>(), letterbox, model.Labels, confidence),
                };
            }
        });
    }

    // ---------------- Box overlay rendering ----------------

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush BoxStrokeBrush =
        new(Windows.UI.Color.FromArgb(255, 230, 60, 60));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush ManualStrokeBrush =
        new(Windows.UI.Color.FromArgb(255, 60, 180, 230));

    private void ClearBoxOverlay()
    {
        BoxOverlay.Children.Clear();
        _overlayCache = null;
        _overlayImageWidth = 0;
        _overlayImageHeight = 0;
        // Cancel any in-progress draw regardless of source — the underlying
        // image is gone, so neither toggle-mode nor pen-barrel mode can recover.
        if (_isDrawing)
        {
            _isDrawing = false;
            _drawRect = null;
            if (_drawSource == DrawSource.Toggle)
            {
                DrawBoxButton.IsChecked = false;
            }
            _drawSource = DrawSource.None;
        }
        _penBarrelPressed = false;
        _penPointerId = 0;
        UpdateDetectionCountLabel();
    }

    private void RefreshBoxOverlay(CachedOutput cached)
    {
        _overlayCache = cached;
        // Read the overlay's working dimensions from the underlying GDI bitmap —
        // BitmapImage.PixelWidth/PixelHeight is 0 until the async PNG decode
        // completes, and we'd otherwise clamp draw-mode coords to (0,0) and
        // discard the rubber-band as "too small" on release.
        var item = _activeImage;
        double w = item?.SourceBitmap?.Width ?? 0;
        double h = item?.SourceBitmap?.Height ?? 0;
        if (w <= 0 || h <= 0)
        {
            var size = GetSourcePixelSize(cached.Image);
            w = size.Width;
            h = size.Height;
        }
        _overlayImageWidth = w;
        _overlayImageHeight = h;
        BoxOverlay.Width = _overlayImageWidth;
        BoxOverlay.Height = _overlayImageHeight;
        BoxOverlay.Children.Clear();

        foreach (var p in cached.BoxPredictions)
        {
            if (p?.Box == null) continue;
            var rect = BuildBoxRectangle(p);
            BoxOverlay.Children.Add(rect);
        }

        UpdateDetectionCountLabel();
    }

    private void UpdateDetectionCountLabel()
    {
        if (_overlayCache == null || _overlayCache.BoxPredictions.Count == 0)
        {
            DetectionCountText.Text = string.Empty;
            return;
        }
        int total = _overlayCache.BoxPredictions.Count(p => p?.Box != null);
        int selected = _overlayCache.BoxPredictions.Count(p => p?.Box != null && !p.IsExcluded);
        DetectionCountText.Text = $"Detections: {total}  \u2022  Selected: {selected}";
    }

    private static (double Width, double Height) GetSourcePixelSize(BitmapImage img)
    {
        // BitmapImage exposes PixelWidth / PixelHeight after the source is decoded.
        double w = img.PixelWidth > 0 ? img.PixelWidth : 0;
        double h = img.PixelHeight > 0 ? img.PixelHeight : 0;
        return (w, h);
    }

    private Microsoft.UI.Xaml.Shapes.Rectangle BuildBoxRectangle(Prediction p)
    {
        var box = p.Box!;
        // Stroke thickness scales with image size so it stays visible at any zoom.
        double thickness = Math.Max(2.0, (_overlayImageWidth + _overlayImageHeight) * 0.0015);
        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = Math.Max(1, box.Xmax - box.Xmin),
            Height = Math.Max(1, box.Ymax - box.Ymin),
            Stroke = p.IsManual ? ManualStrokeBrush : BoxStrokeBrush,
            StrokeThickness = thickness,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Tag = p,
        };
        ApplyExclusionVisual(rect, p.IsExcluded);
        Canvas.SetLeft(rect, box.Xmin);
        Canvas.SetTop(rect, box.Ymin);
        // Use AddHandler(handledEventsToo:true) so ScrollViewer's internal
        // pointer-event handling doesn't swallow the tap before it reaches us.
        rect.AddHandler(
            PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(Box_PointerPressed),
            handledEventsToo: true);
        return rect;
    }

    private static void ApplyExclusionVisual(Microsoft.UI.Xaml.Shapes.Rectangle rect, bool excluded)
    {
        if (excluded)
        {
            rect.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 2 };
            rect.Opacity = 0.3;
        }
        else
        {
            rect.StrokeDashArray = null;
            rect.Opacity = 1.0;
        }
    }

    private void Box_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Pen with barrel held: skip toggle-exclusion so the event can bubble
        // to BoxOverlay_PointerPressed (or the next PointerMoved transition)
        // and start a new draw at the pen location.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
        {
            var penProps = e.GetCurrentPoint((UIElement)sender).Properties;
            if (penProps.IsBarrelButtonPressed)
            {
                return;
            }
        }

        // Tap-to-toggle only — draw mode owns pointer interaction on the Canvas.
        if (_drawMode) return;
        if (sender is not Microsoft.UI.Xaml.Shapes.Rectangle rect) return;
        if (rect.Tag is not Prediction p) return;
        p.IsExcluded = !p.IsExcluded;
        ApplyExclusionVisual(rect, p.IsExcluded);
        UpdateDetectionCountLabel();
        e.Handled = true;
    }

    // ---------------- Draw-new-box mode ----------------

    private void DrawBoxButton_Checked(object sender, RoutedEventArgs e)
    {
        _drawMode = true;
        ScanStatusText.Text = "Draw mode: drag on the image to add a bounding box.";
    }

    private void DrawBoxButton_Unchecked(object sender, RoutedEventArgs e)
    {
        _drawMode = false;
        // Only tear down state if THIS source owns the in-progress draw —
        // pen-barrel draws must survive the user toggling the button.
        if (_isDrawing && _drawSource == DrawSource.Toggle && _drawRect != null)
        {
            BoxOverlay.Children.Remove(_drawRect);
            _isDrawing = false;
            _drawRect = null;
            _drawSource = DrawSource.None;
        }
        ScanStatusText.Text = "Draw mode off.";
    }

    private void StartDrawRect(Windows.Foundation.Point start)
    {
        double thickness = Math.Max(2.0, (_overlayImageWidth + _overlayImageHeight) * 0.0015);
        _drawRect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Stroke = ManualStrokeBrush,
            StrokeThickness = thickness,
            StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 6, 3 },
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 60, 180, 230)),
            Width = 1,
            Height = 1,
        };
        Canvas.SetLeft(_drawRect, start.X);
        Canvas.SetTop(_drawRect, start.Y);
        BoxOverlay.Children.Add(_drawRect);
    }

    private bool TryStartPenBarrelDraw(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_isDrawing) return false;
        if (_overlayCache == null) return false;
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen) return false;

        var pt = e.GetCurrentPoint(BoxOverlay);
        if (!pt.Properties.IsBarrelButtonPressed) return false;
        // Pen tip touching screen is a normal click — don't piggyback a draw on it.
        if (pt.Properties.IsLeftButtonPressed) return false;

        _isDrawing = true;
        _drawSource = DrawSource.PenBarrel;
        _drawPointerId = e.Pointer.PointerId;
        _penPointerId = e.Pointer.PointerId;
        _penBarrelPressed = true;
        _drawStart = pt.Position;
        StartDrawRect(pt.Position);
        // Capture is opportunistic for hover-only pen input — don't fail if it
        // doesn't take. Hover PointerMoved keeps firing on the element below
        // the pen anyway.
        BoxOverlay.CapturePointer(e.Pointer);
        ScanStatusText.Text = "Drawing box (release barrel button to commit)...";
        e.Handled = true;
        return true;
    }

    private void BoxOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_overlayCache == null) return;

        // Re-entry guard — a second pointer press while a draw is in progress
        // is ignored. Prevents tip-down during a pen-barrel hover-draw (or any
        // secondary pointer) from corrupting _drawRect / _drawPointerId.
        if (_isDrawing)
        {
            e.Handled = true;
            return;
        }

        // Pen-barrel fast path: try this BEFORE the originalSource gate so a
        // barrel press over an existing box starts a new draw rather than
        // toggling exclusion (Box_PointerPressed already skips on barrel).
        if (TryStartPenBarrelDraw(e)) return;

        if (!_drawMode) return;

        // Ignore clicks that originate on an existing box — let Box_PointerPressed
        // run instead (which no-ops in draw mode, but we still don't want to start
        // a draw with a rubber-band that includes their click).
        if (e.OriginalSource is Microsoft.UI.Xaml.Shapes.Rectangle r && r != _drawRect)
        {
            return;
        }

        var pt = e.GetCurrentPoint(BoxOverlay);
        if (!pt.Properties.IsLeftButtonPressed) return;

        if (!BoxOverlay.CapturePointer(e.Pointer)) return;

        _isDrawing = true;
        _drawSource = DrawSource.Toggle;
        _drawPointerId = e.Pointer.PointerId;
        _drawStart = pt.Position;
        StartDrawRect(pt.Position);
        e.Handled = true;
    }

    private void BoxOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Pen barrel state machine — drives both START (rising edge) and COMMIT
        // (falling edge) for pen-barrel mode, because hover-barrel doesn't
        // reliably produce PointerPressed/Released on Surface Slim Pen.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen && _overlayCache != null)
        {
            var ppen = e.GetCurrentPoint(BoxOverlay);
            bool barrel = ppen.Properties.IsBarrelButtonPressed;
            bool tipDown = ppen.Properties.IsLeftButtonPressed;

            if (!_isDrawing && barrel && !tipDown && !_penBarrelPressed)
            {
                // Rising edge of barrel button while not drawing → start at current pen pos.
                _isDrawing = true;
                _drawSource = DrawSource.PenBarrel;
                _drawPointerId = e.Pointer.PointerId;
                _penPointerId = e.Pointer.PointerId;
                _drawStart = ppen.Position;
                StartDrawRect(ppen.Position);
                BoxOverlay.CapturePointer(e.Pointer);
                _penBarrelPressed = true;
                ScanStatusText.Text = "Drawing box (release barrel button to commit)...";
                e.Handled = true;
                return;
            }

            if (_isDrawing && _drawSource == DrawSource.PenBarrel && e.Pointer.PointerId == _drawPointerId)
            {
                if (!barrel)
                {
                    // Falling edge mid-move → commit.
                    _penBarrelPressed = false;
                    FinishDrawing(commit: true);
                    e.Handled = true;
                    return;
                }
                // Fall through to common rubber-band update below.
            }

            _penBarrelPressed = barrel;
        }

        if (!_isDrawing || _drawRect == null) return;
        if (e.Pointer.PointerId != _drawPointerId) return;

        var pos = e.GetCurrentPoint(BoxOverlay).Position;
        double x = Math.Max(0, Math.Min(_overlayImageWidth, pos.X));
        double y = Math.Max(0, Math.Min(_overlayImageHeight, pos.Y));
        double left = Math.Min(_drawStart.X, x);
        double top = Math.Min(_drawStart.Y, y);
        double width = Math.Abs(x - _drawStart.X);
        double height = Math.Abs(y - _drawStart.Y);

        Canvas.SetLeft(_drawRect, left);
        Canvas.SetTop(_drawRect, top);
        _drawRect.Width = Math.Max(1, width);
        _drawRect.Height = Math.Max(1, height);
        e.Handled = true;
    }

    private void BoxOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDrawing || _drawRect == null) return;
        if (e.Pointer.PointerId != _drawPointerId) return;

        // For pen-barrel draws, PointerReleased also fires when the pen TIP
        // lifts off the screen. Don't commit while the barrel button is still
        // held — PointerMoved's falling-edge path or a later release will do it.
        if (_drawSource == DrawSource.PenBarrel)
        {
            var props = e.GetCurrentPoint(BoxOverlay).Properties;
            if (props.IsBarrelButtonPressed)
            {
                return;
            }
            _penBarrelPressed = false;
        }

        // Commit BEFORE the system tears the pointer capture down — calling
        // ReleasePointerCapture here would fire PointerCaptureLost synchronously,
        // which would treat this as a cancellation and discard the box. The
        // system auto-releases capture on pointer-up; the late CaptureLost
        // handler sees _isDrawing == false and no-ops.
        FinishDrawing(commit: true);
        e.Handled = true;
    }

    private void BoxOverlay_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;
        // Pen-barrel capture is opportunistic — hover PointerMoved keeps
        // firing on the overlay even without capture, so don't treat capture
        // loss as cancellation. PointerExited / PointerCanceled handle real
        // pen-departed-overlay cases.
        if (_drawSource == DrawSource.PenBarrel) return;
        FinishDrawing(commit: false);
    }

    private void BoxOverlay_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Pen left hover range mid pen-barrel draw → cancel (we can't track
        // barrel-release once the pen is out of range).
        if (_isDrawing && _drawSource == DrawSource.PenBarrel && e.Pointer.PointerId == _drawPointerId)
        {
            FinishDrawing(commit: false);
        }
        if (e.Pointer.PointerId == _penPointerId)
        {
            _penPointerId = 0;
            _penBarrelPressed = false;
        }
    }

    private void BoxOverlay_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_isDrawing && e.Pointer.PointerId == _drawPointerId)
        {
            FinishDrawing(commit: false);
        }
        if (e.Pointer.PointerId == _penPointerId)
        {
            _penPointerId = 0;
            _penBarrelPressed = false;
        }
    }

    private void FinishDrawing(bool commit)
    {
        var draft = _drawRect;
        var source = _drawSource;
        _isDrawing = false;
        _drawRect = null;
        _drawSource = DrawSource.None;

        if (draft == null) return;
        BoxOverlay.Children.Remove(draft);

        if (!commit || _overlayCache == null)
        {
            if (source == DrawSource.Toggle) DrawBoxButton.IsChecked = false;
            return;
        }

        double left = Canvas.GetLeft(draft);
        double top = Canvas.GetTop(draft);
        double right = left + draft.Width;
        double bottom = top + draft.Height;

        // Require at least an 8x8 box to count as a real draw, otherwise discard
        // — a stray click shouldn't become a permanent invisible prediction.
        if (draft.Width < 8 || draft.Height < 8)
        {
            ScanStatusText.Text = "Draw cancelled (box too small).";
            if (source == DrawSource.Toggle) DrawBoxButton.IsChecked = false;
            return;
        }

        var manual = new Prediction
        {
            Box = new Box((float)left, (float)top, (float)right, (float)bottom),
            Label = "manual",
            Confidence = 1.0f,
            IsManual = true,
        };
        _overlayCache.BoxPredictions.Add(manual);
        var rect = BuildBoxRectangle(manual);
        BoxOverlay.Children.Add(rect);
        ExtractButton.IsEnabled = _overlayCache.BoxPredictions.Count(p => !p.IsExcluded) > 0;
        ScanStatusText.Text = $"Added manual box. Detections: {_overlayCache.BoxPredictions.Count}.";
        UpdateDetectionCountLabel();
        if (source == DrawSource.Toggle) DrawBoxButton.IsChecked = false; // one-shot
    }
}
