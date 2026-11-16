// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="OptimizationOptions.RemoveUnusedXObjectResources"/>: an <c>/XObject</c> entry no
/// content stream invokes is removed, and the object it named is then pruned as unreachable. Each
/// fixture draws a 2×2 image (<c>/Im0</c>), and lists a 3×3 one (<c>/Im1</c>) that nothing draws,
/// so the images can be told apart in the output by width.
/// </summary>
public class UnusedXObjectResourceTests
{
    private const string Im0 = "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8";
    private const string Im1 = "/Type /XObject /Subtype /Image /Width 3 /Height 3 /ColorSpace /DeviceRGB /BitsPerComponent 8";
    private const string Im2 = "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8";
    private const string DrawIm0 = "q 50 0 0 50 100 100 cm /Im0 Do Q";

    private static byte[] Red => Samples(2, 255, 0, 0);
    private static byte[] Blue => Samples(3, 0, 0, 255);
    private static byte[] Green => Samples(4, 0, 255, 0);

    [Fact]
    public void UnusedImageOnAPage_IsRemoved_AndTheDrawnImageIsKept()
    {
        var result = Optimize(OnePage(DrawIm0), Options(), out byte[] optimized);

        Assert.Equal(1, result.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0" }, PageXObjectNames(optimized, 0));
        Assert.Equal(new[] { 2 }, ImageWidths(optimized));
    }

    [Fact]
    public void OptionOff_ChangesNothing()
    {
        var result = Optimize(OnePage(DrawIm0), Options(on: false), out byte[] optimized);

        Assert.Equal(0, result.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0", "Im1" }, PageXObjectNames(optimized, 0));
        Assert.Equal(new[] { 2, 3 }, ImageWidths(optimized));
    }

    [Fact]
    public void LosslessPreset_EnablesThePass_AndDefaultDoesNot()
    {
        Assert.True(OptimizationOptions.Lossless.RemoveUnusedXObjectResources);
        Assert.True(OptimizationOptions.Balanced.RemoveUnusedXObjectResources);
        Assert.True(OptimizationOptions.Aggressive.RemoveUnusedXObjectResources);
        Assert.False(OptimizationOptions.Default.RemoveUnusedXObjectResources);
    }

    /// <summary>The form has no <c>/Resources</c> of its own, so its <c>Do</c> uses the page's.</summary>
    [Fact]
    public void ImageDrawnOnlyInsideAFormWithoutResources_IsKept()
    {
        byte[] pdf = Build(
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /XObject << /Fm0 7 0 R /Im0 5 0 R /Im1 6 0 R >> >>", null),
            (4, "", Ascii("q 1 0 0 1 0 0 cm /Fm0 Do Q")),
            (5, Im0, Red),
            (6, Im1, Blue),
            (7, "/Type /XObject /Subtype /Form /BBox [0 0 200 200]", Ascii(DrawIm0)));

        var result = Optimize(pdf, Options(), out byte[] optimized);

        Assert.Equal(1, result.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Fm0", "Im0" }, PageXObjectNames(optimized, 0));
        Assert.Equal(new[] { 2 }, ImageWidths(optimized));
    }

    /// <summary>
    /// One indirect resource dictionary serves two pages; the first draws <c>/Im0</c>, the second
    /// <c>/Im1</c>. The union is kept and only <c>/Im2</c>, which neither draws, goes.
    /// </summary>
    [Fact]
    public void ResourceDictionarySharedByTwoPages_KeepsAnImageOnlyTheSecondPageDraws()
    {
        byte[] pdf = Build(
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R 9 0 R] /Count 2", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources 8 0 R", null),
            (4, "", Ascii(DrawIm0)),
            (5, Im0, Red),
            (6, Im1, Blue),
            (7, Im2, Green),
            (8, "/XObject << /Im0 5 0 R /Im1 6 0 R /Im2 7 0 R >>", null),
            (9, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 10 0 R /Resources 8 0 R", null),
            (10, "", Ascii("q 50 0 0 50 10 10 cm /Im1 Do Q")));

        var result = Optimize(pdf, Options(), out byte[] optimized);

        Assert.Equal(1, result.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0", "Im1" }, PageXObjectNames(optimized, 1));
        Assert.Equal(new[] { 2, 3 }, ImageWidths(optimized));
    }

    /// <summary>
    /// Two pages with resource dictionaries of their own that share one indirect <c>/XObject</c>
    /// dictionary: it keeps every name either page draws, and loses only <c>/Im2</c>.
    /// </summary>
    [Fact]
    public void XObjectDictionarySharedByTwoResourceDictionaries_KeepsTheUnionOfTheirNames()
    {
        byte[] pdf = Build(
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R 9 0 R] /Count 2", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /XObject 8 0 R >>", null),
            (4, "", Ascii(DrawIm0)),
            (5, Im0, Red),
            (6, Im1, Blue),
            (7, Im2, Green),
            (8, "/Im0 5 0 R /Im1 6 0 R /Im2 7 0 R", null),
            (9, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 10 0 R /Resources << /XObject 8 0 R >>", null),
            (10, "", Ascii("q 50 0 0 50 10 10 cm /Im1 Do Q")));

        var result = Optimize(pdf, Options(), out byte[] optimized);

        Assert.Equal(1, result.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0", "Im1" }, PageXObjectNames(optimized, 0));
        Assert.Equal(new[] { 2, 3 }, ImageWidths(optimized));
    }

    /// <summary>
    /// The page's <c>/XObject</c> dictionary is shared with the resource dictionary of a form the
    /// walk never reads, so it keeps every name. Control: the form with an <c>/XObject</c>
    /// dictionary of its own.
    /// </summary>
    [Fact]
    public void XObjectDictionaryAlsoHeldByAnUnwalkedResourceDictionary_KeepsEveryEntry()
    {
        byte[] Fixture(string formXObjects) => Build(
            (1, "/Type /Catalog /Pages 2 0 R /Keep 7 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /XObject 8 0 R >>", null),
            (4, "", Ascii(DrawIm0)),
            (5, Im0, Red),
            (6, Im1, Blue),
            (7, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject " + formXObjects + " >>",
                Ascii("/Im1 Do")),
            (8, "/Im0 5 0 R /Im1 6 0 R", null));

        var kept = Optimize(Fixture("8 0 R"), Options(), out byte[] optimized);
        Assert.Equal(0, kept.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0", "Im1" }, PageXObjectNames(optimized, 0));

        var control = Optimize(Fixture("<< /Im1 6 0 R >>"), Options(), out byte[] reduced);
        Assert.Equal(1, control.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0" }, PageXObjectNames(reduced, 0));
    }

    /// <summary>
    /// A content stream that will not decode is a page the walk could not read: nothing is removed,
    /// and the refusal names the reason. The control is the same page with content that decodes.
    /// </summary>
    [Fact]
    public void PageWhoseContentCannotBeDecoded_KeepsEverything_RefusedForThatReason()
    {
        byte[] pdf = Build(
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >>", null),
            (4, "/Filter /FlateDecode", Ascii("this is not a zlib stream at all")),
            (5, Im0, Red),
            (6, Im1, Blue));

        var refused = Optimize(pdf, Options(), out byte[] optimized);

        Assert.Equal(0, refused.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0", "Im1" }, PageXObjectNames(optimized, 0));
        Assert.Equal(new[] { 2, 3 }, ImageWidths(optimized));
        Assert.Contains("could not be decoded", DocumentEditDiagnostics.LastError, StringComparison.Ordinal);

        var control = Optimize(OnePage(DrawIm0), Options(), out _);
        Assert.Equal(1, control.UnusedXObjectResourcesRemoved);
    }

    /// <summary>
    /// The page draws nothing; an annotation's appearance stream shares the page's resource
    /// dictionary and draws <c>/Im0</c>.
    /// </summary>
    [Fact]
    public void ImageDrawnByAnAnnotationAppearance_IsKept()
    {
        byte[] pdf = Build(
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources 8 0 R "
                + "/Annots [9 0 R]", null),
            (4, "", Ascii("q Q")),
            (5, Im0, Red),
            (6, Im1, Blue),
            (7, "/Type /XObject /Subtype /Form /BBox [0 0 50 50] /Resources 8 0 R", Ascii("q 50 0 0 50 0 0 cm /Im0 Do Q")),
            (8, "/XObject << /Im0 5 0 R /Im1 6 0 R >>", null),
            (9, "/Type /Annot /Subtype /Stamp /Rect [10 10 60 60] /AP << /N 7 0 R >>", null));

        var result = Optimize(pdf, Options(), out byte[] optimized);

        Assert.Equal(1, result.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0" }, PageXObjectNames(optimized, 0));
        Assert.Equal(new[] { 2 }, ImageWidths(optimized));
    }

    /// <summary>
    /// A tiling pattern's own resources keep what the pattern draws and lose what it does not;
    /// the page's own unused entry goes too.
    /// </summary>
    [Fact]
    public void TilingPatternResources_KeepWhatThePatternDraws()
    {
        byte[] pdf = Build(
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Pattern << /P0 7 0 R >> /XObject << /Im1 6 0 R >> >>", null),
            (4, "", Ascii("/Pattern cs /P0 scn 0 0 100 100 re f")),
            (5, Im0, Red),
            (6, Im1, Blue),
            (7, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] "
                + "/XStep 10 /YStep 10 /Resources << /XObject << /Im0 5 0 R /Im2 8 0 R >> >>",
                Ascii("q 10 0 0 10 0 0 cm /Im0 Do Q")),
            (8, Im2, Green));

        var result = Optimize(pdf, Options(), out byte[] optimized);

        Assert.Equal(2, result.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0" }, PatternXObjectNames(optimized, "P0"));
        Assert.Empty(PageXObjectNames(optimized, 0));
        Assert.Equal(new[] { 2 }, ImageWidths(optimized));
    }

    [Fact]
    public void ResourcesInheritedFromThePagesNode_AreReduced()
    {
        byte[] pdf = Build(
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1 /Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >>", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R", null),
            (4, "", Ascii(DrawIm0)),
            (5, Im0, Red),
            (6, Im1, Blue));

        var result = Optimize(pdf, Options(), out byte[] optimized);

        Assert.Equal(1, result.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0" }, PageXObjectNames(optimized, 0));
        Assert.Equal(new[] { 2 }, ImageWidths(optimized));
    }

    /// <summary>
    /// The page's resource dictionary is also the <c>/Resources</c> of a form nothing invokes but
    /// the catalog still reaches — a holder the walk never read, so the dictionary keeps every entry.
    /// The control is the same file with the form holding a resource dictionary of its own.
    /// </summary>
    [Fact]
    public void ResourceDictionaryAlsoHeldByAnUnwalkedStream_KeepsEveryEntry()
    {
        byte[] Fixture(string formResources) => Build(
            (1, "/Type /Catalog /Pages 2 0 R /Keep 7 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources 8 0 R", null),
            (4, "", Ascii(DrawIm0)),
            (5, Im0, Red),
            (6, Im1, Blue),
            (7, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources " + formResources,
                Ascii("/Im1 Do")),
            (8, "/XObject << /Im0 5 0 R /Im1 6 0 R >>", null));

        var kept = Optimize(Fixture("8 0 R"), Options(), out byte[] optimized);
        Assert.Equal(0, kept.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0", "Im1" }, PageXObjectNames(optimized, 0));

        var control = Optimize(Fixture("<< /XObject << /Im1 6 0 R >> >>"), Options(), out byte[] reduced);
        Assert.Equal(1, control.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0" }, PageXObjectNames(reduced, 0));
    }

    /// <summary>
    /// A resource dictionary some other object refers to under a key that is not
    /// <c>/Resources</c> has a user the walk cannot see; it keeps every entry. Control: the same
    /// file without the extra reference.
    /// </summary>
    [Fact]
    public void ResourceDictionaryReferencedFromElsewhere_KeepsEveryEntry()
    {
        byte[] Fixture(string catalogExtra) => Build(
            (1, "/Type /Catalog /Pages 2 0 R " + catalogExtra, null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources 7 0 R", null),
            (4, "", Ascii(DrawIm0)),
            (5, Im0, Red),
            (6, Im1, Blue),
            (7, "/XObject << /Im0 5 0 R /Im1 6 0 R >>", null));

        var kept = Optimize(Fixture("/Extra 7 0 R"), Options(), out byte[] optimized);
        Assert.Equal(0, kept.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0", "Im1" }, PageXObjectNames(optimized, 0));

        var control = Optimize(Fixture(""), Options(), out byte[] reduced);
        Assert.Equal(1, control.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0" }, PageXObjectNames(reduced, 0));
    }

    /// <summary>
    /// A page that inherits <c>/Resources</c> but has no <c>/Parent</c> to inherit them through:
    /// what a renderer resolves is not what the tree walk handed down, so nothing is removed.
    /// <see cref="ResourcesInheritedFromThePagesNode_AreReduced"/> is the control.
    /// </summary>
    [Fact]
    public void PageInheritingResourcesWithoutAParent_KeepsEverything_RefusedForThatReason()
    {
        byte[] pdf = Build(
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1 /Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >>", null),
            (3, "/Type /Page /MediaBox [0 0 200 200] /Contents 4 0 R", null),
            (4, "", Ascii(DrawIm0)),
            (5, Im0, Red),
            (6, Im1, Blue));

        var refused = Optimize(pdf, Options(), out byte[] optimized);

        Assert.Equal(0, refused.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { 2, 3 }, ImageWidths(optimized));
        Assert.Contains("/Parent", DocumentEditDiagnostics.LastError, StringComparison.Ordinal);
    }

    /// <summary>A <c>Do</c> naming nothing the resources hold is a hole in the walk: nothing changes.</summary>
    [Fact]
    public void DoNamingAnAbsentXObject_ChangesNothing()
    {
        var refused = Optimize(OnePage(DrawIm0 + " /Missing Do"), Options(), out byte[] optimized);

        Assert.Equal(0, refused.UnusedXObjectResourcesRemoved);
        Assert.Equal(new[] { "Im0", "Im1" }, PageXObjectNames(optimized, 0));

        var control = Optimize(OnePage(DrawIm0), Options(), out _);
        Assert.Equal(1, control.UnusedXObjectResourcesRemoved);
    }

    /// <summary>
    /// Removing the unused image does not change what the page renders. Potency: the drawn image's
    /// red is where it is placed, so a render that lost it would differ.
    /// </summary>
    [Fact]
    public void PageRendersTheSame_AfterTheUnusedImageIsRemoved()
    {
        byte[] input = OnePage(DrawIm0);
        var result = Optimize(input, Options(), out byte[] optimized);
        Assert.Equal(1, result.UnusedXObjectResourcesRemoved);

        byte[] before = PdfPageRenderer.RenderPage(new MemoryStream(input), 0, 72);
        byte[] after = PdfPageRenderer.RenderPage(new MemoryStream(optimized), 0, 72);

        using var bitmapBefore = SKBitmap.Decode(before);
        using var bitmapAfter = SKBitmap.Decode(after);
        Assert.Equal(bitmapBefore.Width, bitmapAfter.Width);
        Assert.Equal(bitmapBefore.Height, bitmapAfter.Height);

        SKColor drawn = bitmapAfter.GetPixel(125, 75);
        Assert.True(drawn.Red > 200 && drawn.Green < 60 && drawn.Blue < 60, $"expected the red image, got {drawn}");

        for (int y = 0; y < bitmapBefore.Height; y++)
        for (int x = 0; x < bitmapBefore.Width; x++)
            Assert.Equal(bitmapBefore.GetPixel(x, y), bitmapAfter.GetPixel(x, y));
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    private static OptimizationOptions Options(bool on = true) => new OptimizationOptions
    {
        RemoveUnusedXObjectResources = on,
        RemoveUnusedObjects = true,
        CompressStreams = false,
        RemoveDuplicates = false,
    };

    private static byte[] OnePage(string content) => Build(
        (1, "/Type /Catalog /Pages 2 0 R", null),
        (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
        (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
            + "/Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >>", null),
        (4, "", Ascii(content)),
        (5, Im0, Red),
        (6, Im1, Blue));

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Samples(int side, byte r, byte g, byte b)
    {
        var samples = new byte[side * side * 3];
        for (int i = 0; i < samples.Length; i += 3)
        {
            samples[i] = r;
            samples[i + 1] = g;
            samples[i + 2] = b;
        }
        return samples;
    }

    /// <summary>Objects numbered 1..n in order; a non-null payload makes the object a stream.</summary>
    private static byte[] Build(params (int Number, string Dict, byte[] Stream)[] objects)
    {
        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.7\n");
        var offsets = new long[objects.Length + 1];
        foreach (var (number, dict, stream) in objects)
        {
            offsets[number] = ms.Position;
            if (stream == null)
            {
                Write($"{number} 0 obj\n<< {dict} >>\nendobj\n");
                continue;
            }
            Write($"{number} 0 obj\n<< {dict} /Length {stream.Length} >>\nstream\n");
            ms.Write(stream, 0, stream.Length);
            Write("\nendstream\nendobj\n");
        }

        long xref = ms.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static OptimizationResult Optimize(byte[] input, OptimizationOptions options, out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject obj)
    {
        int guard = 0;
        while (obj is PdfReference reference && guard++ < 32)
            obj = document.GetObject(reference.ReferenceObjectNumber);
        return obj;
    }

    private static PdfDictionary PageResources(PdfDocument document, int pageIndex)
    {
        var catalog = (PdfDictionary)Resolve(document, document.Trailer[PdfName.Root]);
        var pages = (PdfDictionary)Resolve(document, catalog[PdfName.Pages]);
        var kids = (PdfArray)Resolve(document, pages[PdfName.Kids]);
        var page = (PdfDictionary)Resolve(document, kids[pageIndex]);
        return Resolve(document, page[PdfName.Resources]) as PdfDictionary
               ?? (PdfDictionary)Resolve(document, pages[PdfName.Resources]);
    }

    private static string[] Names(PdfDocument document, PdfDictionary resources)
    {
        var xobjects = Resolve(document, resources[PdfName.XObject]) as PdfDictionary;
        return xobjects == null
            ? new string[0]
            : xobjects.Keys.Select(k => k.Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static string[] PageXObjectNames(byte[] pdf, int pageIndex)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        return Names(document, PageResources(document, pageIndex));
    }

    private static string[] PatternXObjectNames(byte[] pdf, string pattern)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var patterns = (PdfDictionary)Resolve(document, PageResources(document, 0)[PdfName.Pattern]);
        var stream = (PdfDictionary)Resolve(document, patterns[new PdfName(pattern)]);
        return Names(document, (PdfDictionary)Resolve(document, stream[PdfName.Resources]));
    }

    /// <summary>The <c>/Width</c> of every image object the output holds, ascending.</summary>
    private static int[] ImageWidths(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        int size = ((PdfNumber)document.Trailer[PdfName.Size]).IntValue;
        var widths = new List<int>();
        for (int n = 1; n < size; n++)
        {
            if (document.GetObject(n) is PdfStream stream &&
                stream[PdfName.Subtype] is PdfName subtype && subtype.Value == "Image")
                widths.Add(((PdfNumber)stream[PdfName.Width]).IntValue);
        }
        widths.Sort();
        return widths.ToArray();
    }
}
