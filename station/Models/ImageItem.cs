using BiblioText.Utils;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace BiblioText.Models;

/// <summary>
/// One loaded image in the bottom thumbnail strip. Holds the EXIF-normalized
/// pristine source bitmap, a pre-decoded BitmapImage for the main viewer, a
/// small thumbnail BitmapImage for the tile, and a per-(modelId, confidence)
/// cache of already-rendered prediction outputs so flipping back to a previously
/// processed image is instant.
/// </summary>
internal sealed class ImageItem : INotifyPropertyChanged, IDisposable
{
    private const int ThumbnailSize = 240;

    private bool _isSelected;
    private bool _disposed;

    public string FilePath { get; }
    public string DisplayName { get; }

    /// <summary>EXIF-normalized pristine pixels. Never drawn on; deep-copied for inference.</summary>
    public Bitmap SourceBitmap { get; private set; }

    /// <summary>Pre-decoded BitmapImage of the pristine source. Assigned to the main Image to clear any rendered boxes.</summary>
    public BitmapImage SourceImage { get; private set; }

    /// <summary>Center-cropped square BitmapImage for the strip tile.</summary>
    public BitmapImage Thumbnail { get; private set; }

    /// <summary>
    /// Per-(modelId, conf2dp) cache of rendered prediction outputs. Key built by
    /// Sample.CacheKey() so confidence rounding stays in one place. Each entry
    /// holds the displayed BitmapImage plus the underlying predictions (and masks
    /// when the model is a -seg variant) so the EXTRACT TITLES button can crop
    /// without re-running inference.
    /// </summary>
    public Dictionary<string, CachedOutput> Outputs { get; } = new(StringComparer.Ordinal);

    /// <summary>Driven by Sample to update the tile's selected-state border.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private ImageItem(string filePath, Bitmap source, BitmapImage sourceImage, BitmapImage thumbnail)
    {
        FilePath = filePath;
        DisplayName = Path.GetFileNameWithoutExtension(filePath);
        SourceBitmap = source;
        SourceImage = sourceImage;
        Thumbnail = thumbnail;
    }

    public static async Task<ImageItem> LoadAsync(string filePath)
    {
        // Off-thread: load JPEG, EXIF-normalize, encode to PNG byte arrays.
        // BitmapImage is a UI-thread-only WinUI type, so we decode on the
        // calling thread (which must be the UI thread).
        var (source, mainBytes, thumbBytes) = await Task.Run(() =>
        {
            var bmp = new Bitmap(filePath);
            BitmapFunctions.NormalizeOrientation(bmp);

            byte[] mainPng;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                mainPng = ms.ToArray();
            }

            byte[] thumbPng;
            using (Bitmap square = CenterCropToSquare(bmp))
            using (Bitmap scaled = BitmapFunctions.ResizeBitmap(square, ThumbnailSize, ThumbnailSize))
            using (var ts = new MemoryStream())
            {
                scaled.Save(ts, ImageFormat.Png);
                thumbPng = ts.ToArray();
            }

            return (bmp, mainPng, thumbPng);
        });

        // Back on the UI thread: create BitmapImage from byte arrays.
        var sourceImage = new BitmapImage();
        using (var ms = new InMemoryRandomAccessStream())
        {
            await ms.WriteAsync(mainBytes.AsBuffer());
            ms.Seek(0);
            await sourceImage.SetSourceAsync(ms);
        }

        var thumb = new BitmapImage();
        using (var ts = new InMemoryRandomAccessStream())
        {
            await ts.WriteAsync(thumbBytes.AsBuffer());
            ts.Seek(0);
            await thumb.SetSourceAsync(ts);
        }

        return new ImageItem(filePath, source, sourceImage, thumb);
    }

    private static Bitmap CenterCropToSquare(Bitmap source)
    {
        int side = Math.Min(source.Width, source.Height);
        int x = (source.Width - side) / 2;
        int y = (source.Height - side) / 2;
        // Use Graphics.DrawImage for a true deep copy — Bitmap.Clone(Rectangle)
        // can share the underlying pixel buffer and produce corrupt results
        // when the source is accessed concurrently.
        var cropped = new Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.DrawImage(source,
                new Rectangle(0, 0, side, side),
                new Rectangle(x, y, side, side),
                GraphicsUnit.Pixel);
        }
        return cropped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SourceBitmap.Dispose();
        Outputs.Clear();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
