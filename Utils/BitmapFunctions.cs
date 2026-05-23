using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Storage.Streams;

namespace BiblioText.Utils;

internal class BitmapFunctions
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StdDev = [0.229f, 0.224f, 0.225f];

    // EXIF "Orientation" tag id; see Exif 2.32 spec sec 4.6.4.
    private const int ExifOrientationTag = 0x0112;

    /// <summary>
    /// Applies the JPEG/EXIF orientation tag to the raw pixel buffer so subsequent
    /// inference and rendering see the same upright frame the user sees on screen.
    /// System.Drawing.Bitmap loads pixels as-stored on disk and ignores EXIF, but the
    /// XAML BitmapImage we display honors it - without this call, boxes drawn for a
    /// phone-camera photo would appear rotated 90/180/270 degrees relative to the image.
    /// Returns true if the bitmap was rotated/flipped.
    /// </summary>
    public static bool NormalizeOrientation(Bitmap bitmap)
    {
        try
        {
            if (Array.IndexOf(bitmap.PropertyIdList, ExifOrientationTag) < 0)
            {
                return false;
            }
            var prop = bitmap.GetPropertyItem(ExifOrientationTag);
            if (prop?.Value is null || prop.Value.Length < 2)
            {
                return false;
            }
            int orientation = BitConverter.ToUInt16(prop.Value, 0);
            RotateFlipType rotation = orientation switch
            {
                2 => RotateFlipType.RotateNoneFlipX,
                3 => RotateFlipType.Rotate180FlipNone,
                4 => RotateFlipType.Rotate180FlipX,
                5 => RotateFlipType.Rotate90FlipX,
                6 => RotateFlipType.Rotate90FlipNone,
                7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone
            };
            if (rotation == RotateFlipType.RotateNoneFlipNone)
            {
                return false;
            }
            bitmap.RotateFlip(rotation);
            // Stamp the buffer as upright so a re-encode (RenderPredictions saves PNG)
            // doesn't double-rotate if we ever round-trip the bytes.
            bitmap.RemovePropertyItem(ExifOrientationTag);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Bitmap ResizeBitmap(Bitmap originalBitmap, int newWidth, int newHeight)
    {
        Bitmap resizedBitmap = new(newWidth, newHeight);
        using (Graphics graphics = Graphics.FromImage(resizedBitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(originalBitmap, 0, 0, newWidth, newHeight);
        }

        return resizedBitmap;
    }

    public static Bitmap ResizeWithPadding(Bitmap originalBitmap, int targetWidth, int targetHeight)
    {
        return ResizeWithPadding(originalBitmap, targetWidth, targetHeight, out _);
    }

    public static Bitmap ResizeWithPadding(Bitmap originalBitmap, int targetWidth, int targetHeight, out Letterbox letterbox)
    {
        letterbox = Letterbox.Compute(originalBitmap.Width, originalBitmap.Height, targetWidth, targetHeight);

        int scaledWidth = (int)(originalBitmap.Width * letterbox.Scale);
        int scaledHeight = (int)(originalBitmap.Height * letterbox.Scale);

        Bitmap paddedBitmap = new(targetWidth, targetHeight);
        using (Graphics graphics = Graphics.FromImage(paddedBitmap))
        {
            graphics.Clear(Color.FromArgb(114, 114, 114)); // Ultralytics default letterbox fill
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(originalBitmap, letterbox.PadX, letterbox.PadY, scaledWidth, scaledHeight);
        }

        return paddedBitmap;
    }

    public static async Task<Bitmap> ResizeVideoFrameWithPadding(VideoFrame videoFrame, int targetWidth, int targetHeight)
    {
        // Convert VideoFrame to SoftwareBitmap (RGBA8 for compatibility)
        var softwareBitmap = SoftwareBitmap.Convert(videoFrame.SoftwareBitmap, BitmapPixelFormat.Rgba8);

        using (IRandomAccessStream stream = new InMemoryRandomAccessStream())
        {
            // Create a BitmapEncoder for JPEG or PNG
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            // Set the software bitmap
            encoder.SetSoftwareBitmap(softwareBitmap);

            // Determine the scaling factor
            float scale = Math.Min((float)targetWidth / softwareBitmap.PixelWidth, (float)targetHeight / softwareBitmap.PixelHeight);

            // Calculate new scaled dimensions
            int scaledWidth = (int)(softwareBitmap.PixelWidth * scale);
            int scaledHeight = (int)(softwareBitmap.PixelHeight * scale);

            // Calculate padding offsets (centering the image)
            int offsetX = (targetWidth - scaledWidth) / 2;
            int offsetY = (targetHeight - scaledHeight) / 2;

            // Apply transformations
            encoder.BitmapTransform.ScaledWidth = (uint)scaledWidth;
            encoder.BitmapTransform.ScaledHeight = (uint)scaledHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;

            await encoder.FlushAsync();
            stream.Seek(0); // Reset stream position

            // Convert to System.Drawing.Bitmap
            using var tempBitmap = new Bitmap(stream.AsStream());
            Bitmap paddedBitmap = new(targetWidth, targetHeight);

            using (Graphics graphics = Graphics.FromImage(paddedBitmap))
            {
                graphics.Clear(Color.White); // White padding background
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                // Draw the resized image centered
                graphics.DrawImage(tempBitmap, offsetX, offsetY, scaledWidth, scaledHeight);
            }

            return paddedBitmap;
        }
    }

    public static Tensor<float> PreprocessBitmapForFaceDetection(Bitmap bitmap, Tensor<float> input)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        IntPtr ptr = bmpData.Scan0;
        int bytes = Math.Abs(stride) * height;
        byte[] rgbValues = new byte[bytes];

        Marshal.Copy(ptr, rgbValues, 0, bytes);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * stride) + (x * 3);
                byte blue = rgbValues[index];
                byte green = rgbValues[index + 1];
                byte red = rgbValues[index + 2];

                // Convert to grayscale and normalize to [0,1]
                float gray = (0.299f * red + 0.587f * green + 0.114f * blue) / 255f;

                input[0, 0, y, x] = (gray - .442f) / .28f;
            }
        }

        bitmap.UnlockBits(bmpData);

        return input;
    }

    public static Tensor<float> PreprocessBitmapWithStdDev(Bitmap bitmap, Tensor<float> input)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        IntPtr ptr = bmpData.Scan0;
        int bytes = Math.Abs(stride) * height;
        byte[] rgbValues = new byte[bytes];

        Marshal.Copy(ptr, rgbValues, 0, bytes);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * 3;
                byte blue = rgbValues[index];
                byte green = rgbValues[index + 1];
                byte red = rgbValues[index + 2];

                input[0, 0, y, x] = ((red / 255f) - Mean[0]) / StdDev[0];
                input[0, 1, y, x] = ((green / 255f) - Mean[1]) / StdDev[1];
                input[0, 2, y, x] = ((blue / 255f) - Mean[2]) / StdDev[2];
            }
        }

        bitmap.UnlockBits(bmpData);

        return input;
    }

    public static Tensor<float> PreprocessBitmapWithoutStandardization(Bitmap bitmap, Tensor<float> input)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData bmpData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        IntPtr ptr = bmpData.Scan0;
        int bytes = Math.Abs(stride) * height;
        byte[] rgbValues = new byte[bytes];

        Marshal.Copy(ptr, rgbValues, 0, bytes);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * 3;
                byte blue = rgbValues[index];
                byte green = rgbValues[index + 1];
                byte red = rgbValues[index + 2];

                input[0, 0, y, x] = red / 255f;
                input[0, 1, y, x] = green / 255f;
                input[0, 2, y, x] = blue / 255f;
            }
        }

        bitmap.UnlockBits(bmpData);

        return input;
    }

    /// <summary>
    /// NCHW float32 [0,1] RGB preprocessing for Ultralytics YOLOv8/v11/v26 style models.
    /// Reads the bitmap pixels in one Marshal.Copy and writes channel-planar into the
    /// underlying buffer via Span&lt;float&gt; for ~10-100x speedup vs per-pixel Tensor indexing.
    /// </summary>
    public static DenseTensor<float> PreprocessBitmapForYolo26(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var input = new DenseTensor<float>([1, 3, height, width]);
        Span<float> buffer = input.Buffer.Span;
        int planeSize = width * height;

        BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = bmpData.Stride;
            byte[] rgbValues = new byte[Math.Abs(stride) * height];
            Marshal.Copy(bmpData.Scan0, rgbValues, 0, rgbValues.Length);

            const float Inv255 = 1f / 255f;
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                int dst = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = rowStart + x * 3;
                    byte b = rgbValues[i];
                    byte g = rgbValues[i + 1];
                    byte r = rgbValues[i + 2];
                    buffer[dst + x] = r * Inv255;                         // R plane
                    buffer[planeSize + dst + x] = g * Inv255;             // G plane
                    buffer[2 * planeSize + dst + x] = b * Inv255;         // B plane
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }

        return input;
    }

    public static Tensor<float> PreprocessBitmapForYOLO(Bitmap bitmap, Tensor<float> input)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData bmpData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        IntPtr ptr = bmpData.Scan0;
        int bytes = Math.Abs(stride) * height;
        byte[] rgbValues = new byte[bytes];

        Marshal.Copy(ptr, rgbValues, 0, bytes);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * 3;
                byte blue = rgbValues[index];
                byte green = rgbValues[index + 1];
                byte red = rgbValues[index + 2];

                // The reason this needs its own function is because the variables are in different places in the input
                input[0, y, x, 0] = red / 255f;
                input[0, y, x, 1] = green / 255f;
                input[0, y, x, 2] = blue / 255f;
            }
        }

        bitmap.UnlockBits(bmpData);

        return input;
    }

    public static DenseTensor<float> PreprocessBitmapForObjectDetection(Bitmap bitmap, int paddedHeight, int paddedWidth)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        DenseTensor<float> input = new([3, paddedHeight, paddedWidth]);

        BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        IntPtr ptr = bmpData.Scan0;
        int bytes = Math.Abs(stride) * height;
        byte[] rgbValues = new byte[bytes];

        Marshal.Copy(ptr, rgbValues, 0, bytes);

        for (int y = paddedHeight - height; y < height; y++)
        {
            for (int x = paddedWidth - width; x < width; x++)
            {
                int index = (y - (paddedHeight - height)) * stride + (x - (paddedWidth - width)) * 3;
                byte blue = rgbValues[index];
                byte green = rgbValues[index + 1];
                byte red = rgbValues[index + 2];

                input[0, y, x] = blue - Mean[0];
                input[1, y, x] = green - Mean[1];
                input[2, y, x] = red - Mean[2];
            }
        }

        bitmap.UnlockBits(bmpData);

        return input;
    }

    public static BitmapImage RenderPredictions(Bitmap image, List<Prediction> predictions)
    {
        DrawPredictions(image, predictions);
        return EncodeBitmapToBitmapImage(image);
    }

    /// <summary>
    /// Renders numbered bounding boxes onto a copy of the image and returns JPEG bytes.
    /// The original image is not modified.
    /// </summary>
    public static byte[] RenderAnnotatedJpeg(Bitmap sourceImage, IReadOnlyList<Prediction> predictions)
    {
        using var copy = new Bitmap(sourceImage);
        DrawPredictions(copy, predictions.ToList());
        using var ms = new MemoryStream();
        copy.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    /// <summary>
    /// Renders YOLO26-seg results: paints translucent class-colored masks over the
    /// pixels they cover, then draws the bounding-box rectangles + labels on top.
    /// </summary>
    public static BitmapImage RenderMaskedPredictions(Bitmap image, List<MaskedPrediction> masked, float maskAlpha = 0.4f)
    {
        if (masked.Count > 0)
        {
            PaintMaskOverlay(image, masked, maskAlpha);
        }
        var asPredictions = masked.ConvertAll(m => m.ToPrediction());
        DrawPredictions(image, asPredictions);
        return EncodeBitmapToBitmapImage(image);
    }

    private static void DrawPredictions(Bitmap image, List<Prediction> predictions)
    {
        using Graphics g = Graphics.FromImage(image);
        float markerSize = (image.Width + image.Height) * 0.001f;
        using Pen pen = new(Color.Red, markerSize);
        using Brush brush = new SolidBrush(Color.White);
        using Brush bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using Font font = new("Arial", GetAdjustedFontsize(predictions));
        using Font idFont = new("Arial", GetAdjustedFontsize(predictions) * 1.5f, FontStyle.Bold);
        int index = 1;
        foreach (var p in predictions)
        {
            if (p == null || p.Box == null)
            {
                continue;
            }
            g.DrawLine(pen, p.Box.Xmin, p.Box.Ymin, p.Box.Xmax, p.Box.Ymin);
            g.DrawLine(pen, p.Box.Xmax, p.Box.Ymin, p.Box.Xmax, p.Box.Ymax);
            g.DrawLine(pen, p.Box.Xmax, p.Box.Ymax, p.Box.Xmin, p.Box.Ymax);
            g.DrawLine(pen, p.Box.Xmin, p.Box.Ymax, p.Box.Xmin, p.Box.Ymin);

            // Draw numerical ID badge
            string idText = index.ToString();
            var idSize = g.MeasureString(idText, idFont);
            float badgeX = p.Box.Xmin;
            float badgeY = p.Box.Ymin - idSize.Height - 2;
            if (badgeY < 0) badgeY = p.Box.Ymin + 2;
            g.FillRectangle(bgBrush, badgeX, badgeY, idSize.Width + 4, idSize.Height);
            g.DrawString(idText, idFont, brush, badgeX + 2, badgeY);

            string labelText = $"{p.Label}, {p.Confidence:0.00}";
            g.DrawString(labelText, font, brush, new PointF(p.Box.Xmin, p.Box.Ymax + 2));
            index++;
        }
    }

    private static void PaintMaskOverlay(Bitmap image, List<MaskedPrediction> masked, float maskAlpha)
    {
        int W = image.Width;
        int H = image.Height;
        BitmapData data = image.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            byte[] pixels = new byte[Math.Abs(stride) * H];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            foreach (var p in masked)
            {
                Color color = ColorForLabel(p.Label);
                byte cr = color.R, cg = color.G, cb = color.B;
                int x0 = (int)p.Box.Xmin;
                int y0 = (int)p.Box.Ymin;
                for (int my = 0; my < p.Height; my++)
                {
                    int y = y0 + my;
                    if ((uint)y >= (uint)H)
                    {
                        continue;
                    }
                    int rowBase = y * stride;
                    int maskRow = my * p.Width;
                    for (int mx = 0; mx < p.Width; mx++)
                    {
                        byte mv = p.Mask[maskRow + mx];
                        if (mv < 128)
                        {
                            continue;
                        }
                        int x = x0 + mx;
                        if ((uint)x >= (uint)W)
                        {
                            continue;
                        }
                        float a = (mv / 255f) * maskAlpha;
                        float ia = 1f - a;
                        int idx = rowBase + x * 3;
                        pixels[idx]     = (byte)(pixels[idx]     * ia + cb * a);
                        pixels[idx + 1] = (byte)(pixels[idx + 1] * ia + cg * a);
                        pixels[idx + 2] = (byte)(pixels[idx + 2] * ia + cr * a);
                    }
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    /// <summary>Stable per-label color picker for the mask overlay.</summary>
    private static Color ColorForLabel(string label)
    {
        // Distinct, perceptually-spaced HSV palette deterministically picked from the label hash.
        uint h = 2166136261u;
        foreach (char c in label)
        {
            h = (h ^ c) * 16777619u;
        }
        float hue = (h % 360u) / 360f;
        return HsvToRgb(hue, 0.85f, 1.0f);
    }

    private static Color HsvToRgb(float h, float s, float v)
    {
        int i = (int)(h * 6);
        float f = h * 6 - i;
        float p = v * (1 - s);
        float q = v * (1 - f * s);
        float t = v * (1 - (1 - f) * s);
        float r, g, b;
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return Color.FromArgb(255,
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
    }

    private static BitmapImage EncodeBitmapToBitmapImage(Bitmap image)
    {
        BitmapImage bitmapImage = new();
        using MemoryStream memoryStream = new();
        image.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
        memoryStream.Position = 0;
        bitmapImage.SetSource(memoryStream.AsRandomAccessStream());
        return bitmapImage;
    }

    public static BitmapImage? RenderBackgroundMask(Bitmap image, byte[] backgroundMask, int originalImageWidth, int originalImageHeight)
    {
        if (image == null || backgroundMask == null || backgroundMask.Length == 0)
        {
            return null;
        }

        using Graphics g = Graphics.FromImage(image);

        using SolidBrush semiTransparentRedBrush = new SolidBrush(Color.FromArgb(100, 255, 0, 0));

        for (int y = 0; y < originalImageHeight; y++)
        {
            for (int x = 0; x < originalImageWidth; x++)
            {
                int index = (y * originalImageWidth + x) * 4;
                if (backgroundMask[index + 3] > 128)
                {
                    g.FillRectangle(semiTransparentRedBrush, x, y, 1, 1);
                }
            }
        }

        BitmapImage bitmapImage = new();
        using (MemoryStream memoryStream = new())
        {
            image.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
            memoryStream.Position = 0;
            bitmapImage.SetSource(memoryStream.AsRandomAccessStream());
        }

        return bitmapImage;
    }

    // For super resolution
    public static Bitmap CropAndScale(Bitmap paddedBitmap, int originalWidth, int originalHeight, int modelScalingFactor)
    {
        float scale = Math.Min(128f / originalWidth, 128f / originalHeight);

        // Calculate the dimensions of the cropped area
        int cropWidth = (int)(originalWidth * scale * modelScalingFactor);
        int cropHeight = (int)(originalHeight * scale * modelScalingFactor);

        // Calculate the offset to locate the padded content in the 512x512 image
        int offsetX = (paddedBitmap.Width - cropWidth) / 2;
        int offsetY = (paddedBitmap.Height - cropHeight) / 2;

        // Crop the region containing the actual content
        Rectangle cropArea = new(offsetX, offsetY, cropWidth, cropHeight);
        using Bitmap croppedBitmap = paddedBitmap.Clone(cropArea, paddedBitmap.PixelFormat);

        // Scale the cropped bitmap to {modelScalingFactor} times the original image dimensions
        int finalWidth = originalWidth * modelScalingFactor;
        int finalHeight = originalHeight * modelScalingFactor;
        Bitmap scaledBitmap = new(finalWidth, finalHeight);

        using (Graphics graphics = Graphics.FromImage(scaledBitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(croppedBitmap, 0, 0, finalWidth, finalHeight);
        }

        return scaledBitmap;
    }

    public static Bitmap TensorToBitmap(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> tensor)
    {
        // Assumes output tensor shape [batch, c, w, h]
        var outputTensor = tensor[0].AsTensor<float>();
        int height = outputTensor.Dimensions[2];
        int width = outputTensor.Dimensions[3];

        // Create the bitmap
        Bitmap bitmap = new(width, height, PixelFormat.Format24bppRgb);
        BitmapData bmpData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        IntPtr ptr = bmpData.Scan0;
        byte[] pixelData = new byte[Math.Abs(stride) * height];

        // Fill the pixel data
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * stride) + (x * 3);

                // Extract RGB values from the tensor (assume [0,1] range)
                float rVal = outputTensor[0, 0, y, x];  // Red
                float gVal = outputTensor[0, 1, y, x];  // Green
                float bVal = outputTensor[0, 2, y, x];  // Blue

                // Scale to [0, 255] and clamp
                byte r = (byte)Math.Clamp(rVal * 255, 0, 255);
                byte g = (byte)Math.Clamp(gVal * 255, 0, 255);
                byte b = (byte)Math.Clamp(bVal * 255, 0, 255);

                // Store pixel values in BGR order
                pixelData[index] = b;
                pixelData[index + 1] = g;
                pixelData[index + 2] = r;
            }
        }

        // Copy the pixel data to the bitmap
        Marshal.Copy(pixelData, 0, ptr, pixelData.Length);
        bitmap.UnlockBits(bmpData);

        return bitmap;
    }

    // Crops bitmap given a prediciton box
    public static Bitmap CropImage(Bitmap originalImage, Box box)
    {
        int xmin = Math.Max(0, (int)box.Xmin);
        int ymin = Math.Max(0, (int)box.Ymin);
        int width = Math.Min(originalImage.Width - xmin, (int)(box.Xmax - box.Xmin));
        int height = Math.Min(originalImage.Height - ymin, (int)(box.Ymax - box.Ymin));

        Rectangle cropRectangle = new(xmin, ymin, width, height);
        return originalImage.Clone(cropRectangle, originalImage.PixelFormat);
    }

    // Overlays cropped section a bitmap inside the original image in the Box region
    public static Bitmap OverlayImage(Bitmap originalImage, Bitmap overlay, Box box)
    {
        using Graphics graphics = Graphics.FromImage(originalImage);

        // Scale the overlay to match the bounding box size
        graphics.DrawImage(overlay, new Rectangle(
            (int)box.Xmin,
            (int)box.Ymin,
            (int)(box.Xmax - box.Xmin),
            (int)(box.Ymax - box.Ymin)));

        return originalImage;
    }

    public static BitmapImage ConvertBitmapToBitmapImage(Bitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();

        // Save the bitmap to a stream
        bitmap.Save(stream.AsStream(), ImageFormat.Png);
        stream.Seek(0);

        // Create a BitmapImage from the stream
        BitmapImage bitmapImage = new();
        bitmapImage.SetSource(stream);

        return bitmapImage;
    }

    private static float GetAdjustedFontsize(List<Prediction> predictions)
    {
        float adjustedFontSize = 12;

        if (predictions.Count > 0)
        {
            int maxPredictionTextLength = predictions.Select(p => p.Label.Length).ToList().Max() + 5;
            float minPredictionBoxWidth = predictions.Select(p => p.Box!.Xmax - p.Box!.Xmin).ToList().Min();
            adjustedFontSize = Math.Clamp(minPredictionBoxWidth / ((float)maxPredictionTextLength), 8, 16);
        }

        return adjustedFontSize;
    }
}