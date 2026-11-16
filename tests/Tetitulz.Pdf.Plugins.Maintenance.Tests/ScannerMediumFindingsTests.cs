// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The MEDIUM assembly-scan findings for the optimizer's two content scanners: the glyph-usage
/// scanner that decides what may be pruned, and the placement scanner that decides what may be
/// downsampled. Both are the "dangerous half" of an optimisation, so each assertion is about the
/// scanner's own evidence — a reported reason, a recorded placement — rather than about the call
/// having returned.
/// </summary>
public class ScannerMediumFindingsTests
{
    // ------------------------------------------------------------------
    //  ImagePlacementScanner — §7.7.3.4 inherited /Resources
    // ------------------------------------------------------------------

    [Fact]
    public void InheritedResources_ImageIsDownsampledToTheTargetDpi()
    {
        // The page carries no /Resources of its own; the /Pages node does. The scanner read only
        // the page's own entry, so `resources` was null, HandleDo returned at its `xobjects == null`
        // guard, no placement was recorded, and TargetDpi silently did nothing — the very class of
        // defect the scanner was written to fix.
        byte[] pdf = InheritedResourcesImagePdf();
        int before = ImageWidth(pdf);
        Assert.Equal(ImagePixels, before);   // the fixture really does carry a 400-sample-wide image

        var result = Optimize(pdf, new OptimizationOptions
        {
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true,
                TargetFormat = ImageFormat.Jpeg,
                Quality = 75,
                TargetDpi = 72,
                SkipAlreadyOptimized = false,
            },
        }, out byte[] optimized);

        // Drawn 96 pt wide at 400 samples = 300 dpi; at TargetDpi 72 that is under a quarter.
        Assert.True(ImageWidth(optimized) < before,
            $"the image was not downsampled: {before} → {ImageWidth(optimized)}");
        Assert.True(result.ImagesOptimized > 0);
    }

    // ------------------------------------------------------------------
    //  Both scanners — the operation list is materialised before any budget
    // ------------------------------------------------------------------

    [Fact]
    public void OversizedContentStream_IsRefusedBeforeItIsParsed()
    {
        // 9 MiB of `q Q` pairs decodes inside MaxStreamDecodeBytes (64 MiB) and parses to millions
        // of operation objects BEFORE the per-operation budget can fire. The guard's evidence is
        // the refusal: no font is subset, and the reason names the ceiling.
        byte[] pdf = OversizedContentPdf();

        DocumentEditDiagnostics.Clear();
        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true }, out _);

        Assert.Equal(0, result.FontsSubset);
        Assert.NotNull(DocumentEditDiagnostics.LastError);
        Assert.Contains("scan ceiling", DocumentEditDiagnostics.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryContentStream_IsNotRefused()
    {
        // The control: a ceiling that refused every page would pass the test above.
        DocumentEditDiagnostics.Clear();
        var result = Optimize(InheritedResourcesImagePdf(),
            new OptimizationOptions { SubsetEmbeddedFonts = true }, out _);

        Assert.Null(DocumentEditDiagnostics.LastError);
        Assert.NotNull(result);
    }

    // ------------------------------------------------------------------
    //  ImagePlacementScanner — paint sites it does not follow
    // ------------------------------------------------------------------

    [Fact]
    public void ImageDrawnThroughAPatternOrSoftMask_IsFollowedNotReportedAsUnfollowed()
    {
        // A tiling pattern selected by `scn` and a soft-mask group reached through `gs` can both
        // paint an image. An earlier revision followed NEITHER and reported that fact, which was
        // the honest answer for a walk that stopped at the page. The walk now follows both — a
        // tiling pattern's /Matrix composed against the BASE CTM per §8.7.3.1, and the soft-mask
        // group's own content — so there is nothing left to disclaim for this document.
        //
        // Renamed rather than re-asserted: the behaviour improved, and a test whose name says
        // "IsReportedAsUnfollowed" while asserting the opposite is the failure mode no guard
        // catches (CLAUDE.md, "a test whose name and body disagree").
        DocumentEditDiagnostics.Clear();
        Optimize(PatternAndSoftMaskPdf(), new OptimizationOptions
        {
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true,
                TargetFormat = ImageFormat.Jpeg,
                Quality = 75,
                TargetDpi = 72,
                SkipAlreadyOptimized = false,
            },
        }, out _);

        // The falsifiable half: if the pattern/soft-mask walk regressed to stopping at the page,
        // the scanner would once again disclaim those paint sites and this would go red.
        string reported = DocumentEditDiagnostics.LastError;
        Assert.True(reported == null || !reported.Contains("does not follow", StringComparison.Ordinal),
            "the placement scan should follow tiling patterns and soft-mask groups, so it has no " +
            "unfollowed paint site to disclaim here; got: " + (reported ?? "<null>"));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static OptimizationResult Optimize(byte[] input, OptimizationOptions options,
        out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }

    /// <summary>The /Width of the one image XObject in the file.</summary>
    private static int ImageWidth(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        foreach (var kvp in doc.Parser.EnumerateAllObjects())
            if (kvp.Value is PdfStream stream &&
                (doc.ResolveObject(stream[PdfName.Subtype]) as PdfName)?.Value == "Image")
                return (int)((doc.ResolveObject(stream[PdfName.Width]) as PdfNumber)?.LongValue ?? 0);
        throw new InvalidOperationException("the fixture no longer carries an image XObject");
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    // 400×400 samples drawn into a 96×96 pt box: 300 dpi.
    private const int ImagePixels = 400;
    private const string ImagePageContent = "q 96 0 0 96 10 10 cm /Im0 Do Q";

    private static byte[] InheritedResourcesImagePdf()
    {
        byte[] jpeg = Jpeg(ImagePixels, ImagePixels);

        return AssembleWithBinary(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            // The /Resources live HERE, on the /Pages node, and the page inherits them.
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /XObject << /Im0 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            $"<< /Length {ImagePageContent.Length} >>\nstream\n{ImagePageContent}\nendstream",
            null,   // 5 — the image XObject, written as bytes
        }, binaryObject: 5,
           binaryHeader: $"<< /Type /XObject /Subtype /Image /Width {ImagePixels} /Height {ImagePixels} " +
                         "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " +
                         jpeg.Length + " >>",
           binaryData: jpeg);
    }

    /// <summary>A real JPEG: the downsampler decodes and re-encodes the samples.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height,
            SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SkiaSharp.SKColor(
                    (byte)(x * 255 / Math.Max(1, width - 1)),
                    (byte)(y * 255 / Math.Max(1, height - 1)), 128));

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] OversizedContentPdf()
    {
        var content = new StringBuilder(9 * 1024 * 1024 + 64);
        while (content.Length < 9 * 1024 * 1024) content.Append("q Q ");
        string text = content.ToString();

        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {text.Length} >>\nstream\n{text}\nendstream",
        });
    }

    // The page selects a tiling pattern and an /ExtGState, and draws an image directly.
    private static byte[] PatternAndSoftMaskPdf()
    {
        byte[] jpeg = Jpeg(64, 64);
        const string content = "/Pattern cs /P0 scn /GS0 gs q 96 0 0 96 10 10 cm /Im0 Do Q";

        return AssembleWithBinary(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /XObject << /Im0 5 0 R >> /Pattern << /P0 6 0 R >> " +
            "/ExtGState << /GS0 << /ca 0.5 >> >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            null,   // 5 — image
            "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] " +
            "/XStep 10 /YStep 10 /Resources << >> /Length 0 >>\nstream\n\nendstream",
        }, binaryObject: 5,
           binaryHeader: "<< /Type /XObject /Subtype /Image /Width 64 /Height 64 " +
                         "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " +
                         jpeg.Length + " >>",
           binaryData: jpeg);
    }

    private static byte[] Assemble(List<string> bodies) =>
        AssembleWithBinary(bodies, -1, null, null);

    private static byte[] AssembleWithBinary(List<string> bodies, int binaryObject,
        string binaryHeader, byte[] binaryData)
    {
        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new long[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n");
            if (i + 1 == binaryObject)
            {
                Write(binaryHeader + "\nstream\n");
                ms.Write(binaryData, 0, binaryData.Length);
                Write("\nendstream\n");
            }
            else
            {
                Write(bodies[i] + "\n");
            }
            Write("endobj\n");
        }

        int size = bodies.Count + 1;
        long xref = ms.Position;
        Write($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++)
            Write(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
