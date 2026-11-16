// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tetitulz.Pdf.WpfViewer.Services;

/// <summary>A picture file as the samples a page is drawn with — one decoder for every app that places a picture.</summary>
public static class Pictures
{
    /// <summary>The most pixels a picture placed on a page may have.</summary>
    public const long MaximumPixels = 100_000_000;

    /// <summary>
    /// A picture's straight RGB samples and alpha (null when fully opaque), rows from the top —
    /// any picture Windows reads: PNG, JPEG, BMP, GIF, TIFF, and what its installed codecs add.
    /// </summary>
    /// <exception cref="ArgumentException">The bytes are not a picture Windows can read, or it is too large.</exception>
    public static (byte[] Rgb, byte[]? Alpha, int Width, int Height) Decode(byte[] image)
    {
        BitmapSource source;
        try
        {
            using var ms = new MemoryStream(image, writable: false);
            source = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            throw new ArgumentException("The file is not a picture Windows can read.", nameof(image), ex);
        }
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        if ((long)w * h > MaximumPixels) throw new ArgumentException("The picture is too large to place on a page.", nameof(image));
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        bool opaque = true;
        for (int p = 0; p < w * h; p++)
        {
            rgb[p * 3] = px[p * 4 + 2];
            rgb[p * 3 + 1] = px[p * 4 + 1];
            rgb[p * 3 + 2] = px[p * 4];
            alpha[p] = px[p * 4 + 3];
            opaque &= alpha[p] == 255;
        }
        return (rgb, opaque ? null : alpha, w, h);
    }

    /// <summary>
    /// Straight RGB samples and an alpha (or null for opaque), rows from the top, as a PNG file.
    /// </summary>
    public static byte[] EncodePng(byte[] rgb, byte[]? alpha, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (int p = 0; p < width * height; p++)
        {
            bgra[p * 4] = rgb[p * 3 + 2];
            bgra[p * 4 + 1] = rgb[p * 3 + 1];
            bgra[p * 4 + 2] = rgb[p * 3];
            bgra[p * 4 + 3] = alpha?[p] ?? 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
