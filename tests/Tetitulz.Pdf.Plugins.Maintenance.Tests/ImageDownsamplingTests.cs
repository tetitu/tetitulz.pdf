// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Behavioural cover for <see cref="ImageOptimizationOptions.TargetDpi"/>.
///
/// <para><b>Why this file exists.</b> The feature was inert for years and nothing went red. It read
/// <c>/XResolution</c> from the image dictionary — a key ISO 32000-1 does not define — which always
/// returned null, so the source resolution fell back to 72; every preset's target (150, 200, 300) is
/// greater than 72, the <c>scaleFactor &lt; 1.0</c> test therefore never passed, and no image was
/// ever downsampled by dpi. The only assertions naming <c>TargetDpi</c> checked that the presets held
/// the numbers 150/200/300, which is equally true with the downsampling code deleted.</para>
///
/// <para>So these tests assert the <b>rewritten image dimensions</b>. An image XObject has no
/// resolution of its own: §8.9.5.2 maps its samples onto the unit square of user space, so its
/// effective dpi is a property of the CTM at the <c>Do</c> that paints it —
/// <c>pixels × 72 / points drawn</c>. Every fixture here therefore draws the image at a size that
/// differs from its pixel count, which the default <c>CreatePdfWithImage</c> does not.</para>
/// </summary>
public class ImageDownsamplingTests
{
    // 400 pixels across 96 points = 300 dpi.
    private const int Pixels = 400;
    private const double DrawnPoints = 96;
    private const int SourceDpi = 300;

    private static OptimizationOptions WithTargetDpi(int targetDpi) => new OptimizationOptions
    {
        ImageOptimization = new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = ImageFormat.Jpeg,
            Quality = 75,
            TargetDpi = targetDpi,
            SkipAlreadyOptimized = false,
        }
    };

    [Fact]
    public void TargetDpi_HalvesAnImageDrawnAtTwiceTheTargetResolution()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels, DrawnPoints, DrawnPoints);

        byte[] optimized = Optimize(pdf, WithTargetDpi(SourceDpi / 2));

        var image = FirstImage(optimized, out var document);
        using (document)
        {
            // 300 dpi drawn, 150 dpi asked for → half the samples in each direction.
            Assert.Equal(Pixels / 2, IntOf(image[PdfName.Width]));
            Assert.Equal(Pixels / 2, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>
    /// A JPEG resampled far below its size is decoded no larger than the target needs: 2000 pixels
    /// across 96 points is 1500 dpi, and 150 dpi asks for 200. The decode stops at a DCT scale with
    /// the headroom above 200, not at 2000 (85.7 ms and 57.7 MB on the benchmark's 2900² frame
    /// before). The output cannot tell a reduced decode from a full one, so the counter is the
    /// evidence, beside the size the resample still reaches.
    /// </summary>
    [Fact]
    public void TargetDpi_DecodesALargeJpegNoLargerThanItsResampleTarget()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(2000, 2000, DrawnPoints, DrawnPoints);

        int before = PdfOptimizer.ReducedJpegDecodes;
        byte[] optimized = Optimize(pdf, WithTargetDpi(150));

        Assert.Equal(1, PdfOptimizer.ReducedJpegDecodes - before);
        var image = FirstImage(optimized, out var document);
        using (document)
        {
            Assert.Equal(200, IntOf(image[PdfName.Width]));
            Assert.Equal(200, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>The control: nothing asks for a resample, so the JPEG is decoded at its own size.</summary>
    [Fact]
    public void TargetDpi_DecodesAJpegInFullWhenNoResampleIsAsked()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(2000, 2000, DrawnPoints, DrawnPoints);

        int before = PdfOptimizer.ReducedJpegDecodes;
        byte[] optimized = Optimize(pdf, WithTargetDpi(3000));

        Assert.Equal(0, PdfOptimizer.ReducedJpegDecodes - before);
        var image = FirstImage(optimized, out var document);
        using (document)
            Assert.Equal(2000, IntOf(image[PdfName.Width]));
    }

    /// <summary>
    /// The scale is taken from the ratio, not from a fixed step: a third of the resolution is a
    /// third of the samples. A test at one ratio alone passes for an implementation that always
    /// halves.
    /// </summary>
    [Fact]
    public void TargetDpi_ScalesByTheRatioOfDrawnToRequestedResolution()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels, DrawnPoints, DrawnPoints);

        byte[] optimized = Optimize(pdf, WithTargetDpi(SourceDpi / 3));

        var image = FirstImage(optimized, out var document);
        using (document)
        {
            Assert.Equal(Pixels / 3, IntOf(image[PdfName.Width]));
            Assert.Equal(Pixels / 3, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>
    /// Upsampling is never a saving. An image already coarser than the target keeps every sample.
    /// </summary>
    [Fact]
    public void TargetDpi_LeavesAnImageDrawnBelowTheTargetAtItsOriginalSize()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels, DrawnPoints, DrawnPoints);

        byte[] optimized = Optimize(pdf, WithTargetDpi(SourceDpi * 2));

        var image = FirstImage(optimized, out var document);
        using (document)
        {
            Assert.Equal(Pixels, IntOf(image[PdfName.Width]));
            Assert.Equal(Pixels, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>
    /// At the default drawn size an image sits at exactly 72 dpi, which is below every preset
    /// target — so this is the case that made the broken implementation look like it worked. It must
    /// leave the image alone for the right reason, not because the feature is dead.
    /// </summary>
    [Fact]
    public void TargetDpi_LeavesAnImageDrawnAtSeventyTwoDpiAlone()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels);   // drawn at 400 pt = 72 dpi

        byte[] optimized = Optimize(pdf, WithTargetDpi(150));

        var image = FirstImage(optimized, out var document);
        using (document)
        {
            Assert.Equal(Pixels, IntOf(image[PdfName.Width]));
            Assert.Equal(Pixels, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>
    /// An image no content stream paints has no placement, so there is no evidence about the
    /// resolution it needs and it must not be resampled on a guess.
    /// </summary>
    [Fact]
    public void TargetDpi_LeavesAnImageThatIsNeverDrawnAlone()
    {
        byte[] pdf = BuildPdfWithUndrawnImage(Pixels);

        byte[] optimized = Optimize(pdf, WithTargetDpi(72));

        var image = FirstImage(optimized, out var document);
        using (document)
        {
            Assert.Equal(Pixels, IntOf(image[PdfName.Width]));
            Assert.Equal(Pixels, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>
    /// §7.7.3.2 requires <c>/Type /Pages</c> on an intermediate page-tree node, but producers omit it
    /// and Core reads those files anyway — <c>PdfPageTree</c> recognises a leaf structurally, by the
    /// absence of <c>/Kids</c>. The placement scanner classified every node that did not say
    /// <c>/Type /Pages</c> as a leaf, so an untyped node was "scanned" — it paints nothing — and its
    /// <c>/Kids</c> were never followed. Every page below it then read as a page that draws no image,
    /// which is indistinguishable from an image nothing paints, and <c>reason</c> stayed null.
    ///
    /// <para>The assertion is on the rewritten dimensions rather than on the page count, because that
    /// is the observable consequence: the image is drawn at 300 dpi on the page behind the untyped
    /// node, so a target of 150 must halve it.</para>
    /// </summary>
    [Fact]
    public void TargetDpi_SeesAPlacementOnAPageBehindAnUntypedPageTreeNode()
    {
        byte[] pdf = BuildPdfWithUntypedPageTreeNode();

        // The premise: Core still reaches the page, so anything the scanner misses is its own doing.
        using (var probe = PdfDocument.Open(new MemoryStream(pdf)))
            Assert.Single(probe.Pages);

        byte[] optimized = Optimize(pdf, WithTargetDpi(SourceDpi / 2));

        var image = FirstImage(optimized, out var document);
        using (document)
        {
            Assert.Equal(Pixels / 2, IntOf(image[PdfName.Width]));
            Assert.Equal(Pixels / 2, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>
    /// The scanner's incompleteness reason was collected into an <c>out</c> variable that nothing
    /// read, so a scan that stopped early downsampled from whatever it had managed to see — with
    /// <c>ToleratedFailures</c> at 0 and nothing on <c>DocumentEditDiagnostics</c>, i.e. reported as
    /// a clean run. The same fixture without the unfollowable <c>Do</c> is the halving test at the
    /// top of this file, so the difference asserted here is the reason being honoured and not the
    /// downsampler being broken.
    /// </summary>
    [Fact]
    public void TargetDpi_LeavesEveryImageAloneWhenThePlacementScanWasIncomplete()
    {
        byte[] pdf = BuildPdfWithUnfollowableDo();
        DocumentEditDiagnostics.Clear();

        var result = OptimizeReporting(pdf, WithTargetDpi(SourceDpi / 2), out byte[] optimized);

        Assert.True(result.ToleratedFailures > 0,
            "an incomplete placement scan has to reach the caller, not just change the outcome");
        Assert.Contains("placement scan was incomplete", DocumentEditDiagnostics.LastError);

        var image = FirstImage(optimized, out var document);
        using (document)
        {
            Assert.Equal(Pixels, IntOf(image[PdfName.Width]));
            Assert.Equal(Pixels, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>
    /// A placement's dpi is pixels per inch, and a page's <c>/UserUnit</c> (Table 30) makes each
    /// user-space unit that many points: 400 pixels across 96 units of a <c>/UserUnit 4</c> page is
    /// 75 dpi as printed, below a 150-dpi target, so the image stays. Measured in user units it would
    /// read as 300 dpi and be halved — a loss that cannot be undone. The <c>/UserUnit 1</c> control is
    /// the same file read the same way, halved.
    /// </summary>
    [Theory]
    [InlineData(4, Pixels)]
    [InlineData(1, Pixels / 2)]
    public void TargetDpi_MeasuresAPlacementInPointsThroughThePagesUserUnit(int userUnit, int expectedWidth)
    {
        byte[] pdf = RawRgbImagePdf(Pixels, DrawnPoints, " /UserUnit " + userUnit);

        byte[] optimized = Optimize(pdf, WithPngTargetDpi(SourceDpi / 2));

        var image = FirstImage(optimized, out var document);
        using (document)
            Assert.Equal(expectedWidth, IntOf(image[PdfName.Width]));
    }

    /// <summary>
    /// §11.6.5.3 binds a soft mask carrying <c>/Matte</c> to its image: the same <c>/Width</c> and
    /// <c>/Height</c>, and one <c>/Matte</c> entry per image colour component. The mask has no
    /// placement of its own, so resampling the image alone leaves the two disagreeing; the image is
    /// left as it is and the reason reported. The control is the same pair without <c>/Matte</c>,
    /// whose image is halved.
    /// </summary>
    [Theory]
    [InlineData(" /Matte [1 1 1]", Pixels)]
    [InlineData("", Pixels / 2)]
    public void TargetDpi_LeavesAnImageWhoseSoftMaskCarriesMatteAlone(string matte, int expectedWidth)
    {
        byte[] pdf = RawRgbImagePdf(Pixels, DrawnPoints, "", softMaskExtra: matte);
        DocumentEditDiagnostics.Clear();

        var result = OptimizeReporting(pdf, WithPngTargetDpi(SourceDpi / 2), out byte[] optimized);

        var image = FirstImage(optimized, out var document);
        using (document)
        {
            Assert.Equal(expectedWidth, IntOf(image[PdfName.Width]));
            var mask = document.ResolveObject(image[PdfName.SMask]) as PdfStream;
            Assert.NotNull(mask);
            Assert.Equal(Pixels, IntOf(mask[PdfName.Width]));
        }
        if (matte.Length > 0)
        {
            Assert.True(result.ToleratedFailures > 0, "a refusal to resample has to reach the caller");
            Assert.Contains("/Matte", DocumentEditDiagnostics.LastError, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(0, result.ToleratedFailures);
        }
    }

    private static OptimizationOptions WithPngTargetDpi(int targetDpi) => new OptimizationOptions
    {
        ImageOptimization = new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = ImageFormat.Png,
            TargetDpi = targetDpi,
            SkipAlreadyOptimized = false,
        }
    };

    /// <summary>
    /// A square raw 8-bit RGB image of noise (so no re-encode can shrink it into a skip), drawn across
    /// <paramref name="drawnPoints"/> user units from (100, 600), with <paramref name="pageExtra"/>
    /// in the page dictionary and, when <paramref name="softMaskExtra"/> is not null, a DeviceGray
    /// soft mask of the same size carrying it.
    /// </summary>
    private static byte[] RawRgbImagePdf(int pixels, double drawnPoints, string pageExtra, string softMaskExtra = null)
    {
        var random = new Random(97);
        var rgb = new byte[pixels * pixels * 3];
        random.NextBytes(rgb);
        var grey = new byte[pixels * pixels];
        random.NextBytes(grey);

        string drawn = drawnPoints.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        string content = $"q {drawn} 0 0 {drawn} 100 600 cm /Im0 Do Q";
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) { byte[] b = System.Text.Encoding.ASCII.GetBytes(text); ms.Write(b, 0, b.Length); }
        void Object(string body, byte[] data = null)
        {
            offsets.Add(ms.Position);
            Write((offsets.Count) + " 0 obj\n" + body);
            if (data != null) { Write("\nstream\n"); ms.Write(data, 0, data.Length); Write("\nendstream"); }
            Write("\nendobj\n");
        }

        Write("%PDF-1.6\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
               "/Resources << /XObject << /Im0 5 0 R >> >>" + pageExtra + " >>");
        Object($"<< /Length {content.Length} >>", System.Text.Encoding.ASCII.GetBytes(content));
        Object($"<< /Type /XObject /Subtype /Image /Width {pixels} /Height {pixels} /ColorSpace /DeviceRGB " +
               $"/BitsPerComponent 8 /Length {rgb.Length}" + (softMaskExtra != null ? " /SMask 6 0 R" : "") + " >>", rgb);
        if (softMaskExtra != null)
            Object($"<< /Type /XObject /Subtype /Image /Width {pixels} /Height {pixels} /ColorSpace /DeviceGray " +
                   $"/BitsPerComponent 8 /Length {grey.Length}{softMaskExtra} >>", grey);

        long xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) Write(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// The standard 300-dpi fixture, with the page's trailing text run overwritten by a <c>Do</c>
    /// naming an XObject the page's <c>/Resources</c> does not define — a paint operation the scan
    /// cannot follow. The replacement is the same byte count as what it replaces, so every xref
    /// offset stays valid, and the trailing <c>%</c> run comments out the <c>ET</c> left behind.
    /// </summary>
    private static byte[] BuildPdfWithUnfollowableDo()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels, DrawnPoints, DrawnPoints);

        byte[] needle = System.Text.Encoding.ASCII.GetBytes("(Image Test) Tj");
        int at = IndexOf(pdf, needle);
        Assert.True(at >= 0, "the fixture no longer contains the text run this patch overwrites");

        const string replacement = "/Zz Do %%%%%%%%";
        Assert.Equal(needle.Length, replacement.Length);
        foreach (char c in replacement) pdf[at++] = (byte)c;
        return pdf;
    }

    /// <summary>
    /// The shared builder's page tree, with the root node's <c>/Type</c> key renamed so the node
    /// carries no <c>/Type</c> at all. <c>/Junk</c> is the same length as <c>/Type</c>, so every xref
    /// offset in the fixture stays valid — rewriting the whole file here would duplicate the JPEG
    /// generator for one key's difference.
    /// </summary>
    private static byte[] BuildPdfWithUntypedPageTreeNode()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels, DrawnPoints, DrawnPoints);

        byte[] needle = System.Text.Encoding.ASCII.GetBytes("/Type /Pages");
        int at = IndexOf(pdf, needle);
        Assert.True(at >= 0, "the fixture no longer contains '/Type /Pages'");
        Assert.Equal(-1, IndexOf(pdf, needle, at + 1));   // and only the one node carries it

        foreach (char c in "/Junk") pdf[at++] = (byte)c;
        return pdf;
    }

    /// <summary>
    /// The image is present in <c>/Resources /XObject</c> but the content stream never invokes it.
    /// </summary>
    private static byte[] BuildPdfWithUndrawnImage(int pixels)
    {
        // Reuse the shared builder, then blank the Do out of the content stream. Rewriting the whole
        // file here would duplicate the JPEG generator for one operator's difference.
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(pixels, pixels, 96, 96);

        // "/Im0 Do" -> "/Im0 --", same byte count so every xref offset stays valid.
        byte[] needle = System.Text.Encoding.ASCII.GetBytes("/Im0 Do");
        int at = IndexOf(pdf, needle);
        Assert.True(at >= 0, "the fixture no longer contains '/Im0 Do'");
        pdf[at + 5] = (byte)'-';
        pdf[at + 6] = (byte)'-';
        return pdf;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        for (int i = from; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static PdfStream FirstImage(byte[] pdf, out PdfDocument document)
    {
        var opened = PdfDocument.Open(new MemoryStream(pdf));
        document = opened;

        PdfObject Deref(PdfObject o)
        {
            int guard = 0;
            while (o is PdfReference r && guard++ < 32) o = opened.GetObject(r.ReferenceObjectNumber);
            return o;
        }

        var catalog = Deref(document.Trailer[PdfName.Root]) as PdfDictionary;
        var pages = Deref(catalog[PdfName.Pages]) as PdfDictionary;
        var kids = Deref(pages[PdfName.Kids]) as PdfArray;
        var page = Deref(kids[0]) as PdfDictionary;
        var resources = Deref(page[PdfName.Resources]) as PdfDictionary;
        var xobjects = Deref(resources[PdfName.XObject]) as PdfDictionary;
        var image = Deref(xobjects[new PdfName("Im0")]) as PdfStream;

        Assert.NotNull(image);
        return image;
    }

    private static int IntOf(PdfObject o) => (int)((PdfNumber)o).LongValue;

    private static byte[] Optimize(byte[] input, OptimizationOptions options)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        PdfOptimizer.Optimize(i, o, options);
        return o.ToArray();
    }

    private static OptimizationResult OptimizeReporting(byte[] input, OptimizationOptions options,
        out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }
}
