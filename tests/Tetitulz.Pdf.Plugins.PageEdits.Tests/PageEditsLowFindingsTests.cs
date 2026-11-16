// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The LOW assembly-scan findings for this plugin: an output file destroyed before the operation
/// could fail, a subtree dropped where a legal <c>null</c> hides it, and option values written into
/// PDF operators whose range ISO 32000-1 fixes.
/// </summary>
public class PageEditsLowFindingsTests
{
    // ------------------------------------------------------------------
    //  PdfMerger.Merge(outputPath, …) — FileMode.Create truncates
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_ToPaths_WhenAnInputCannotBeOpened_LeavesTheExistingOutputIntact()
    {
        string good = TempFile(OnePagePdf());
        string missing = Path.Combine(Path.GetTempPath(), "absent_" + Path.GetRandomFileName() + ".pdf");
        string output = TempFile(Encoding.ASCII.GetBytes("PREVIOUS OUTPUT"));

        try
        {
            Assert.ThrowsAny<Exception>(() => PdfMerger.Merge(output, good, missing));

            // The output was created (and therefore truncated) before any input was opened, so a
            // failure destroyed a file the call never wrote to.
            Assert.Equal("PREVIOUS OUTPUT", File.ReadAllText(output));
        }
        finally
        {
            Delete(good, output);
        }
    }

    [Fact]
    public void Merge_ToPaths_RefusesAnInputThatIsAlsoTheOutput()
    {
        string path = TempFile(OnePagePdf());
        long before = new FileInfo(path).Length;

        try
        {
            var ex = Assert.Throws<ArgumentException>(() => PdfMerger.Merge(path, path));
            Assert.Contains("output path", ex.Message, StringComparison.Ordinal);

            // Refused, and the input is still there: truncating it first would have guaranteed the
            // failure it then reported.
            Assert.Equal(before, new FileInfo(path).Length);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Merge_ToPaths_WithGoodInputs_StillWritesTheOutput()
    {
        // The control: opening the inputs first must not stop the merge happening.
        string a = TempFile(OnePagePdf());
        string b = TempFile(OnePagePdf());
        string output = Path.Combine(Path.GetTempPath(), "merged_" + Path.GetRandomFileName() + ".pdf");

        try
        {
            PdfMerger.Merge(output, a, b);

            using var doc = PdfDocument.Open(output);
            Assert.Equal(2, doc.Pages.Count);
        }
        finally
        {
            Delete(a, b, output);
        }
    }

    // ------------------------------------------------------------------
    //  The depth cap substitutes a legal null
    // ------------------------------------------------------------------

    /// <remarks>
    /// The chain is of INDIRECT references, and that is the whole point.
    /// <c>CollectReferencedObjects</c> spent a level per reference it followed, so a chain of 300
    /// reached its cap and its tail was written as <c>null</c> (and reported). A reference is now a
    /// step on the walk's work list, and the cap is each object's own nesting — which
    /// <c>PdfParser.MaxParseDepth</c> (the same <c>PdfLimits.MaxObjectGraphDepth</c>) refuses first,
    /// as it does for <c>PdfObjectHelper.DeepClone</c>, which maps a reference instead of following it.
    /// </remarks>
    [Fact]
    public void SplitRange_AChainOfReferencesLongerThanTheDepthCap_IsCopiedWhole()
    {
        using var input = new MemoryStream(DeepIndirectChainPdf());
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);

        Assert.Null(PdfSplitter.LastCloneWarning);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var link = doc.ResolveObject(doc.Pages[0].Resources[new PdfName("Deep")]) as PdfDictionary;
        int links = 0;
        while (link?[PdfName.Next] != null && links < 1000)
        {
            link = doc.ResolveObject(link[PdfName.Next]) as PdfDictionary;
            links++;
        }
        Assert.Equal(300, links);
        Assert.Equal("deep", (link?[new PdfName("Tail")] as PdfString)?.GetText());
    }

    [Fact]
    public void SplitRange_OrdinaryDocument_ReportsNoCloneWarning()
    {
        using var input = new MemoryStream(OnePagePdf());
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);

        Assert.Null(PdfSplitter.LastCloneWarning);
    }

    // ------------------------------------------------------------------
    //  WatermarkOptions values land in range-fixed operators
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(3.5)]     // a percentage control bound to a 0..1 property
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void AddWatermark_OpacityOutsideZeroToOne_IsRefused(double opacity)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Watermark(
            new WatermarkOptions { Text = "X", Opacity = opacity }));
        Assert.Contains("between 0 and 1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-finite angle is refused: its cosine and sine were NaN, every page's text matrix
    /// <c>0 0 0 0 cx cy Tm</c>, and the stamp invisible while the call returned.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AddWatermark_NonFiniteAngle_IsRefused(double angle)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Watermark(
            new WatermarkOptions { Text = "X", AngleDegrees = angle }));
        Assert.Equal("AngleDegrees", ex.ParamName);
    }

    /// <summary>
    /// A cover region with a non-finite number is refused: the writer put it as 0, so a NaN width
    /// drew a box of no width and covered nothing. A finite region is the control.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, 10, 50, 20, "X")]
    [InlineData(10, 10, double.NaN, 20, "Width")]
    [InlineData(10, 10, 50, double.PositiveInfinity, "Height")]
    public void CoverRegions_NonFiniteRegion_IsRefusedNamingIt(double x, double y, double w, double h, string member)
    {
        using var input = new MemoryStream(OnePagePdf());
        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfRedactor.CoverRegions(input, output,
            new[] { new RedactionRegion { PageIndex = 0, X = x, Y = y, Width = w, Height = h } }));
        Assert.Equal("redactions", ex.ParamName);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);

        using var control = new MemoryStream();
        PdfRedactor.CoverRegions(new MemoryStream(OnePagePdf()), control,
            new[] { new RedactionRegion { PageIndex = 0, X = 10, Y = 10, Width = 50, Height = 20 } });
        using var doc = PdfDocument.Open(new MemoryStream(control.ToArray()));
        Assert.Contains(Tetitulz.Pdf.Core.Text.ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData()),
                        o => o.Operator == "re");
    }

    [Fact]
    public void AddWatermark_ColourComponentOutsideZeroToOne_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Watermark(
            new WatermarkOptions { Text = "X", ColorG = 255 }));   // 0..255 mistaken for 0..1
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void AddWatermark_NonPositiveFontSize_IsRefused(double fontSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Watermark(
            new WatermarkOptions { Text = "X", FontSize = fontSize }));
    }

    [Fact]
    public void AddWatermark_FontOutsideTheStandardFourteen_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Watermark(
            new WatermarkOptions { Text = "X", FontName = "Comic Sans MS" }));
        Assert.Contains("standard 14", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddWatermark_ValuesAtTheEdgeOfTheirRange_AreAccepted()
    {
        // The control, and it pins the boundaries: 0 and 1 are legal /ca values, and Symbol is one
        // of the fourteen. A validator that rejected these would pass every test above.
        byte[] output = Watermark(new WatermarkOptions
        {
            Text = "X",
            Opacity = 1,
            ColorR = 0,
            ColorG = 1,
            ColorB = 0,
            FontSize = 0.5,
            FontName = "Symbol",
        });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Contains("Tj", Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] Watermark(WatermarkOptions options)
    {
        using var input = new MemoryStream(OnePagePdf());
        using var output = new MemoryStream();
        PdfWatermarker.AddWatermark(input, output, options);
        return output.ToArray();
    }

    private static PdfDocument Merge(byte[] a, byte[] b)
    {
        using var ma = new MemoryStream(a);
        using var mb = new MemoryStream(b);
        using var output = new MemoryStream();
        PdfMerger.Merge(output, ma, mb);
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static string TempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), "pe_" + Path.GetRandomFileName() + ".pdf");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void Delete(params string[] paths)
    {
        foreach (string path in paths)
            if (File.Exists(path)) File.Delete(path);
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

    private static byte[] OnePagePdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
    });

    /// <summary>
    /// A page whose <c>/Resources</c> begins a chain of 300 indirect references, each object naming
    /// the next. Every hop costs the collect walk a level, so the chain runs past
    /// <c>PdfLimits.MaxObjectGraphDepth</c> (256) and the tail is dropped — as a legal <c>null</c>.
    /// </summary>
    private static byte[] DeepIndirectChainPdf()
    {
        const int links = 300;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Deep 5 0 R >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        };
        for (int i = 0; i < links; i++)
            bodies.Add($"<< /Next {6 + i} 0 R >>");
        bodies.Add("<< /Tail (deep) >>");
        return Assemble(bodies);
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
