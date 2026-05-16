using AIDevGallery.Sample.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AIDevGallery.Sample.Services;

internal sealed class CropExtractorOptions
{
    /// <summary>If true and the prediction has a per-pixel mask, replace background pixels with FillColor.</summary>
    public bool ApplyMaskAlpha { get; set; } = true;

    /// <summary>Pixels added on each side of the bounding box (clipped to image edges).</summary>
    public int PaddingPx { get; set; } = 8;

    /// <summary>Cap the long edge of the output crop. Down-sized via bicubic if exceeded. 0 = no cap.</summary>
    public int MaxLongEdgePx { get; set; } = 1024;

    /// <summary>JPEG quality (0-100). 85 is a good balance for vision models.</summary>
    public long JpegQuality { get; set; } = 85;

    /// <summary>If non-null, only predictions whose label equals this value are extracted.</summary>
    public string? OnlyClass { get; set; }

    /// <summary>Background color when ApplyMaskAlpha=true. White produces clean OCR / vision-model input.</summary>
    public Color FillColor { get; set; } = Color.White;
}

/// <summary>
/// Pre-processes an image + its YOLO26 detections (with optional masks) into a
/// list of <see cref="BookCrop"/>s ready to submit to an Azure OpenAI vision API
/// (GPT-5.4) or other vision model. No network calls happen here - the API call
/// site is in <see cref="IBookTitleExtractor"/>.
/// </summary>
internal static class CropExtractor
{
    public static List<BookCrop> Extract(
        Bitmap source,
        IReadOnlyList<MaskedPrediction>? masked,
        IReadOnlyList<Prediction> boxPredictions,
        CropExtractorOptions opts)
    {
        var results = new List<BookCrop>();

        // Walk masked predictions when available (so we can cut backgrounds out);
        // otherwise fall back to box-only crops from regular predictions.
        if (masked != null && masked.Count > 0)
        {
            foreach (var p in masked)
            {
                if (opts.OnlyClass != null && !string.Equals(opts.OnlyClass, p.Label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var crop = ExtractOne(source, p, opts);
                if (crop != null)
                {
                    results.Add(crop);
                }
            }
        }
        else
        {
            foreach (var p in boxPredictions)
            {
                if (opts.OnlyClass != null && !string.Equals(opts.OnlyClass, p.Label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var crop = ExtractOne(source, p, opts);
                if (crop != null)
                {
                    results.Add(crop);
                }
            }
        }

        return results;
    }

    /// <summary>Saves each crop's JPEG bytes into a folder for human inspection. Returns the folder.</summary>
    public static string SaveAll(IEnumerable<BookCrop> crops, string folder)
    {
        Directory.CreateDirectory(folder);
        int i = 0;
        foreach (var c in crops)
        {
            string filename = $"{i:D3}_{Sanitize(c.Label)}_{c.Confidence:0.00}.jpg";
            string path = Path.Combine(folder, filename);
            File.WriteAllBytes(path, c.Jpeg);
            c.SavedPath = path;
            i++;
        }
        return folder;
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static BookCrop? ExtractOne(Bitmap source, MaskedPrediction p, CropExtractorOptions opts)
    {
        var (rect, w, h) = PaddedClipBox(source, p.Box, opts.PaddingPx);
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        Bitmap crop;
        if (opts.ApplyMaskAlpha)
        {
            crop = ApplyMaskFill(source, rect, p, opts.FillColor);
        }
        else
        {
            crop = source.Clone(rect, source.PixelFormat);
        }

        crop = MaybeDownscale(crop, opts.MaxLongEdgePx);
        try
        {
            byte[] jpeg = EncodeJpeg(crop, opts.JpegQuality);
            return new BookCrop
            {
                Label = p.Label,
                Confidence = p.Confidence,
                Box = p.Box,
                PixelWidth = crop.Width,
                PixelHeight = crop.Height,
                Jpeg = jpeg,
            };
        }
        finally
        {
            crop.Dispose();
        }
    }

    private static BookCrop? ExtractOne(Bitmap source, Prediction p, CropExtractorOptions opts)
    {
        var (rect, w, h) = PaddedClipBox(source, p.Box!, opts.PaddingPx);
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        Bitmap crop = source.Clone(rect, source.PixelFormat);
        crop = MaybeDownscale(crop, opts.MaxLongEdgePx);
        try
        {
            byte[] jpeg = EncodeJpeg(crop, opts.JpegQuality);
            return new BookCrop
            {
                Label = p.Label,
                Confidence = p.Confidence,
                Box = p.Box!,
                PixelWidth = crop.Width,
                PixelHeight = crop.Height,
                Jpeg = jpeg,
            };
        }
        finally
        {
            crop.Dispose();
        }
    }

    private static (Rectangle Rect, int Width, int Height) PaddedClipBox(Bitmap source, Box box, int pad)
    {
        int x0 = Math.Clamp((int)MathF.Floor(box.Xmin) - pad, 0, source.Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(box.Ymin) - pad, 0, source.Height - 1);
        int x1 = Math.Clamp((int)MathF.Ceiling(box.Xmax) + pad, x0 + 1, source.Width);
        int y1 = Math.Clamp((int)MathF.Ceiling(box.Ymax) + pad, y0 + 1, source.Height);
        int w = x1 - x0;
        int h = y1 - y0;
        return (new Rectangle(x0, y0, w, h), w, h);
    }

    /// <summary>
    /// Composes a copy of the source's bounding-box region with background pixels
    /// replaced by <paramref name="fillColor"/> wherever the per-pixel mask is below
    /// 0.5. Output is 24bpp RGB so JPEG encoding works cleanly.
    /// </summary>
    private static Bitmap ApplyMaskFill(Bitmap source, Rectangle rect, MaskedPrediction p, Color fillColor)
    {
        int w = rect.Width;
        int h = rect.Height;
        Bitmap result = new(w, h, PixelFormat.Format24bppRgb);

        BitmapData srcData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        BitmapData dstData = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int srcStride = srcData.Stride;
            int dstStride = dstData.Stride;
            byte[] src = new byte[Math.Abs(srcStride) * h];
            byte[] dst = new byte[Math.Abs(dstStride) * h];
            Marshal.Copy(srcData.Scan0, src, 0, src.Length);

            int maskOriginX = (int)MathF.Floor(p.Box.Xmin);
            int maskOriginY = (int)MathF.Floor(p.Box.Ymin);
            byte fr = fillColor.R, fg = fillColor.G, fb = fillColor.B;

            for (int y = 0; y < h; y++)
            {
                int origY = rect.Y + y;
                int my = origY - maskOriginY;
                bool maskRowValid = my >= 0 && my < p.Height;

                for (int x = 0; x < w; x++)
                {
                    int sIdx = y * srcStride + x * 3;
                    int dIdx = y * dstStride + x * 3;

                    bool inside = false;
                    if (maskRowValid)
                    {
                        int origX = rect.X + x;
                        int mx = origX - maskOriginX;
                        if (mx >= 0 && mx < p.Width && p.Mask[my * p.Width + mx] >= 128)
                        {
                            inside = true;
                        }
                    }

                    if (inside)
                    {
                        dst[dIdx] = src[sIdx];
                        dst[dIdx + 1] = src[sIdx + 1];
                        dst[dIdx + 2] = src[sIdx + 2];
                    }
                    else
                    {
                        dst[dIdx] = fb;
                        dst[dIdx + 1] = fg;
                        dst[dIdx + 2] = fr;
                    }
                }
            }

            Marshal.Copy(dst, 0, dstData.Scan0, dst.Length);
        }
        finally
        {
            source.UnlockBits(srcData);
            result.UnlockBits(dstData);
        }
        return result;
    }

    private static Bitmap MaybeDownscale(Bitmap input, int maxLongEdge)
    {
        if (maxLongEdge <= 0)
        {
            return input;
        }
        int longEdge = Math.Max(input.Width, input.Height);
        if (longEdge <= maxLongEdge)
        {
            return input;
        }

        float scale = (float)maxLongEdge / longEdge;
        int nw = Math.Max(1, (int)(input.Width * scale));
        int nh = Math.Max(1, (int)(input.Height * scale));

        Bitmap scaled = new(nw, nh);
        using (Graphics g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(input, 0, 0, nw, nh);
        }
        input.Dispose();
        return scaled;
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        ImageCodecInfo? jpegCodec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
        if (jpegCodec == null)
        {
            using var msFallback = new MemoryStream();
            bitmap.Save(msFallback, ImageFormat.Jpeg);
            return msFallback.ToArray();
        }

        using var ms = new MemoryStream();
        var encoderParams = new EncoderParameters(1)
        {
            Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality) }
        };
        bitmap.Save(ms, jpegCodec, encoderParams);
        return ms.ToArray();
    }
}
