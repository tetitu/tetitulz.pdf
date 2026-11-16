// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §12.4.4 page transitions — <c>PdfPageManipulator.SetPageTransition</c>.
/// </summary>
/// <remarks>
/// <para>The last piece of the initial-view row, and separated from it on purpose: <c>/Trans</c> and
/// <c>/Dur</c> are <b>page</b> attributes while <c>/PageMode</c> and friends are catalog ones, so
/// they belong beside <c>RotatePage</c> rather than in a catalog writer.</para>
///
/// <para>Assertions read the page dictionary out of the produced bytes — the transition is invisible
/// to a renderer, which draws a page identically whether it animates or not, so there is nothing
/// else that could be asserted honestly.</para>
/// </remarks>
public class PageTransitionTests
{
    [Fact]
    public void ATransition_IsWrittenWithItsStyleAndDuration()
    {
        byte[] result = Write(OnePage(), new PageTransitionSettings { Style = "Wipe", Duration = 1.5 });

        var trans = Trans(result, 0);
        Assert.Equal("Trans", ((PdfName)trans[PdfName.Type]).Value);
        Assert.Equal("Wipe", ((PdfName)trans[PdfName.S]).Value);
        Assert.Equal(1.5, ((PdfNumber)trans[PdfName.D]).DoubleValue);
    }

    /// <summary>
    /// Settings with nothing set write the spec's own default style, <c>/S /R</c>, explicitly —
    /// the one entry this type does default.
    /// </summary>
    [Fact]
    public void UnsetSettings_WriteTheReplaceStyle()
    {
        byte[] result = Write(OnePage(), new PageTransitionSettings());
        Assert.Equal("R", ((PdfName)Trans(result, 0)[PdfName.S]).Value);
    }

    /// <summary>Optional entries are written only when set — not as spec defaults.</summary>
    [Fact]
    public void OptionalEntriesAreAbsentWhenNotSet()
    {
        byte[] result = Write(OnePage(), new PageTransitionSettings { Style = "Fade" });

        var trans = Trans(result, 0);
        Assert.Null(trans[PdfName.D]);
        Assert.Null(trans[PdfName.Dm]);
        Assert.Null(trans[PdfName.Di]);
    }

    [Fact]
    public void DimensionMotionAndDirection_AreWritten()
    {
        byte[] result = Write(OnePage(), new PageTransitionSettings
        {
            Style = "Split", Dimension = "V", Motion = "O", Direction = 270,
        });

        var trans = Trans(result, 0);
        Assert.Equal("V", ((PdfName)trans[PdfName.Dm]).Value);
        Assert.Equal("O", ((PdfName)trans[PdfName.M]).Value);
        Assert.Equal(270, ((PdfNumber)trans[PdfName.Di]).IntValue);
    }

    /// <summary>
    /// <c>/Di</c> is a union of an angle and the name <c>/None</c>, so <c>None</c> gets its own flag
    /// rather than being spelled as a magic angle this API invented.
    /// </summary>
    [Fact]
    public void DirectionIsNone_WritesTheNameRatherThanANumber()
    {
        byte[] result = Write(OnePage(), new PageTransitionSettings
        {
            Style = "Fly", DirectionIsNone = true,
        });

        Assert.Equal("None", ((PdfName)Trans(result, 0)[PdfName.Di]).Value);
    }

    /// <summary>
    /// <b>Unrecognised names are refused, not written</b> — a viewer falls back to its default, so a
    /// typo is a silent no-op rather than an error the caller can see.
    /// </summary>
    [Theory]
    [InlineData("Wype")]        // the plausible typo
    [InlineData("wipe")]        // PDF names are case-sensitive (§7.3.5)
    [InlineData("Checkerboard")] // a real transition in other formats, not in Table 161
    public void AnUnrecognisedStyle_IsRefused(string style)
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageTransition(input, output, 0,
                new PageTransitionSettings { Style = style }));

        Assert.Contains("/S", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// <c>/Dm</c> and <c>/M</c> are enumerated too, and are refused on the same terms as
    /// <c>/S</c> — each has its own <c>RequireTransitionName</c> call, so each needs its own case.
    /// </summary>
    [Theory]
    [InlineData("Horizontal", null)]   // the word rather than the letter
    [InlineData("h", null)]            // §7.3.5 again
    [InlineData(null, "In")]
    [InlineData(null, "o")]
    public void AnUnrecognisedDimensionOrMotion_IsRefused(string dimension, string motion)
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageTransition(input, output, 0,
                new PageTransitionSettings { Style = "Split", Dimension = dimension, Motion = motion }));

        Assert.Contains(dimension != null ? "/Dm" : "/M", ex.Message);
    }

    /// <summary>
    /// A null style is refused rather than written — <c>/S</c> is the one entry §12.4.4 requires,
    /// so defaulting it would silently substitute a transition the caller did not ask for.
    /// </summary>
    [Fact]
    public void ANullStyle_IsRefused()
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageTransition(input, output, 0,
                new PageTransitionSettings { Style = null }));

        Assert.Contains("/S", ex.Message);
    }

    /// <summary>The two <c>Fly</c>-only entries, which nothing else in Table 161 uses.</summary>
    [Fact]
    public void TheFlyScaleAndOpacityAreWritten()
    {
        byte[] result = Write(OnePage(), new PageTransitionSettings
        {
            Style = "Fly", Scale = 0.25, IsOpaque = false,
        });

        var trans = Trans(result, 0);
        Assert.Equal(0.25, ((PdfNumber)trans[PdfName.SS]).DoubleValue);
        Assert.False(((PdfBoolean)trans[PdfName.B]).Value);
    }

    /// <summary><c>/Di</c> is a closed set of angles, not a free number.</summary>
    [Theory]
    [InlineData(45)]
    [InlineData(-90)]
    [InlineData(360)]
    public void AnAngleTable161DoesNotDefine_IsRefused(int direction)
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();

        Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageTransition(input, output, 0,
                new PageTransitionSettings { Style = "Glitter", Direction = direction }));
    }

    /// <summary>315 is in the set — it is there only because <c>Glitter</c> uses it.</summary>
    [Fact]
    public void TheGlitterAngleIsAccepted()
    {
        byte[] result = Write(OnePage(),
            new PageTransitionSettings { Style = "Glitter", Direction = 315 });

        Assert.Equal(315, ((PdfNumber)Trans(result, 0)[PdfName.Di]).IntValue);
    }

    [Fact]
    public void ANegativeDuration_IsRefused()
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();

        Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageTransition(input, output, 0,
                new PageTransitionSettings { Style = "Wipe", Duration = -1 }));
    }

    // ------------------------------------------------------------------
    //  /Dur
    // ------------------------------------------------------------------

    [Fact]
    public void TheAdvanceTimeIsWrittenBesideTheTransition()
    {
        byte[] result = Write(OnePage(), new PageTransitionSettings { Style = "Fade" }, displayDuration: 5);

        Assert.Equal(5d, ((PdfNumber)PageEntry(result, 0, "Dur")).DoubleValue);
    }

    /// <summary>A null advance time leaves an existing one alone rather than clearing it.</summary>
    [Fact]
    public void ANullAdvanceTimeLeavesTheExistingOne()
    {
        byte[] withDur = Write(OnePage(), new PageTransitionSettings { Style = "Fade" }, displayDuration: 5);
        byte[] changed = Write(withDur, new PageTransitionSettings { Style = "Wipe" });

        Assert.Equal("Wipe", ((PdfName)Trans(changed, 0)[PdfName.S]).Value);
        Assert.Equal(5d, ((PdfNumber)PageEntry(changed, 0, "Dur")).DoubleValue);
    }

    /// <summary>A negative advance time removes it — the only way to take one away.</summary>
    [Fact]
    public void ANegativeAdvanceTimeRemovesIt()
    {
        byte[] withDur = Write(OnePage(), new PageTransitionSettings { Style = "Fade" }, displayDuration: 5);
        byte[] cleared = Write(withDur, new PageTransitionSettings { Style = "Fade" }, displayDuration: -1);

        Assert.Null(PageEntry(cleared, 0, "Dur"));
    }

    /// <summary>A null transition removes the entry, so the page reverts to an instant replace.</summary>
    [Fact]
    public void ANullTransitionRemovesTheEntry()
    {
        byte[] withTrans = Write(OnePage(), new PageTransitionSettings { Style = "Wipe" });
        Assert.NotNull(PageEntry(withTrans, 0, "Trans"));

        byte[] cleared = Write(withTrans, null);
        Assert.Null(PageEntry(cleared, 0, "Trans"));
    }

    // ------------------------------------------------------------------

    /// <summary>The multi-page overload writes one revision for all the pages named.</summary>
    [Fact]
    public void SeveralPagesAreWrittenInOneRevision()
    {
        byte[] source = ThreePages();
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfPageManipulator.SetPageTransition(input, output, new[] { 0, 1, 2 },
            new PageTransitionSettings { Style = "Fade" }, 3);
        byte[] result = output.ToArray();

        for (int page = 0; page < 3; page++)
            Assert.Equal("Fade", ((PdfName)Trans(result, page)[PdfName.S]).Value);

        Assert.Equal(CountEofMarkers(source) + 1, CountEofMarkers(result));
    }

    /// <summary>The façade reaches it.</summary>
    [Fact]
    public void TheFacadeReachesTheWriter()
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();
        PdfOperations.SetPageTransition(input, output, 0,
            new PageTransitionSettings { Style = "Box", Motion = "I" });

        Assert.Equal("Box", ((PdfName)Trans(output.ToArray(), 0)[PdfName.S]).Value);
    }

    // ------------------------------------------------------------------

    private static byte[] Write(byte[] pdf, PageTransitionSettings transition, double? displayDuration = null)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPageManipulator.SetPageTransition(input, output, 0, transition, displayDuration);
        return output.ToArray();
    }

    private static PdfObject PageEntry(byte[] pdf, int pageIndex, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return doc.ResolveObject(doc.Pages[pageIndex].Dictionary[new PdfName(key)]);
    }

    private static PdfDictionary Trans(byte[] pdf, int pageIndex)
    {
        var dict = PageEntry(pdf, pageIndex, "Trans") as PdfDictionary;
        Assert.NotNull(dict);
        return dict!;
    }

    private static int CountEofMarkers(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int count = 0, at = 0;
        while ((at = text.IndexOf("%%EOF", at, System.StringComparison.Ordinal)) >= 0) { count++; at += 5; }
        return count;
    }

    private static byte[] OnePage() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""));

    private static byte[] ThreePages() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R 6 0 R] /Count 3 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
