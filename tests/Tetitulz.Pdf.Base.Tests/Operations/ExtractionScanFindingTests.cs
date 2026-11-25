// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Regression tests for the <c>Operations/Extraction/</c> findings of the 2026-08 assembly scan.
/// Every PDF is built in memory; every assertion is on parsed output, extracted coordinates or a
/// reported reason — never on a byte count or a wall clock.
/// </summary>
public class ExtractionScanFindingTests
{
    // ------------------------------------------------------------------
    //  In-memory PDF construction
    // ------------------------------------------------------------------

    /// <summary>
    /// Assembles a PDF from pre-rendered object bodies (object 1 = catalog, 2 = pages).
    /// Bodies are 1-based: <c>bodies[0]</c> becomes <c>1 0 obj</c>.
    /// </summary>
    private static byte[] Assemble(IReadOnlyList<string> bodies)
    {
        var head = Encoding.ASCII.GetBytes("%PDF-1.7\n");
        using var ms = new MemoryStream();
        ms.Write(head, 0, head.Length);

        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add((int)ms.Length);
            var b = Latin1(bodies[i]);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        var tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int n, string dictExtras, string content)
        => $"{n} 0 obj\n<< /Length {Latin1(content).Length}{dictExtras} >>\nstream\n{content}\nendstream\nendobj\n";

    /// <summary>A one-page document whose page dictionary body the caller supplies.</summary>
    private static byte[] OnePage(string pageDictBody, params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, pageDictBody),
        };
        bodies.AddRange(extraObjects);
        return Assemble(bodies);
    }

    // ==================================================================
    //  Pdf3DExtractor — listing must not inflate every embedded stream
    // ==================================================================

    /// <summary>
    /// The listing API is a metadata query. Calling <c>GetDecodedData()</c> on every /3DD stream
    /// purely to read <c>.Length</c> would let ten Flate bombs allocate tens of gigabytes from a call
    /// that returns three ints per annotation.
    /// </summary>
    [Fact]
    public void List3DAnnotations_OversizedStream_ReportsRefusalInsteadOfDecodingIt()
    {
        // 4 KB of uncompressed payload, listed under a 64-byte ceiling.
        string payload = new string('A', 4096);
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /3D /Rect [0 0 10 10] /3DD 5 0 R >>"),
            StreamObj(5, " /Type /3D /Subtype /U3D", payload));

        var listed = Pdf3DExtractor.List3DAnnotations(new MemoryStream(pdf), maxStreamBytes: 64);

        var only = Assert.Single(listed);
        Assert.Equal("U3D", only.StreamType);
        // The guard's own evidence: a reason, not a length, and not a partial count.
        Assert.NotNull(only.DataLengthUnavailableReason);
        Assert.Equal(0, only.DataLength);
        Assert.NotNull(Pdf3DExtractor.LastError);
    }

    /// <summary>Under a ceiling the stream fits, the reported length is still exact.</summary>
    [Fact]
    public void List3DAnnotations_StreamWithinCeiling_ReportsExactDecodedLength()
    {
        string payload = new string('A', 4096);
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /3D /Rect [0 0 10 10] /3DD 5 0 R >>"),
            StreamObj(5, " /Type /3D /Subtype /U3D", payload));

        var listed = Pdf3DExtractor.List3DAnnotations(new MemoryStream(pdf));

        var only = Assert.Single(listed);
        Assert.Null(only.DataLengthUnavailableReason);
        Assert.Equal(4096, only.DataLength);
        Assert.Null(Pdf3DExtractor.LastError);
    }

    // ==================================================================
    //  PdfRichMediaExtractor — /Sound was listed and never extractable
    // ==================================================================

    /// <summary>
    /// Both filters admitted /Sound and neither branch handled it, so a Sound annotation listed
    /// with DataLength 0 and extracted as <c>byte[0]</c> — indistinguishable from an empty asset.
    /// </summary>
    [Fact]
    public void SoundAnnotation_IsBothListedWithItsLengthAndExtractable()
    {
        const string audio = "RIFFfake-wave-payload";
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /Sound /Rect [0 0 10 10] /Sound 5 0 R >>"),
            StreamObj(5, " /Type /Sound /R 22050 /E /Raw", audio));

        var listed = PdfRichMediaExtractor.ListRichMedia(new MemoryStream(pdf));
        var only = Assert.Single(listed);
        Assert.Equal(PdfAnnotationSubtypes.Sound, only.Subtype);
        Assert.Equal(audio.Length, only.DataLength);
        Assert.Null(only.DataLengthUnavailableReason);

        byte[] extracted = PdfRichMediaExtractor.ExtractMedia(new MemoryStream(pdf), 0);
        Assert.Equal(audio, Encoding.ASCII.GetString(extracted));
        Assert.Null(PdfRichMediaExtractor.LastError);
    }

    /// <summary>
    /// An annotation whose asset really is missing still returns an empty array — but now says so,
    /// which is the whole point: the caller can tell the two cases apart.
    /// </summary>
    [Fact]
    public void ExtractMedia_WithNoAssetStream_ExplainsTheEmptyResult()
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /Sound /Rect [0 0 10 10] >>"));

        byte[] extracted = PdfRichMediaExtractor.ExtractMedia(new MemoryStream(pdf), 0);

        Assert.Empty(extracted);
        Assert.NotNull(PdfRichMediaExtractor.LastError);
        Assert.Contains("/Sound", PdfRichMediaExtractor.LastError, StringComparison.Ordinal);
    }

    // ==================================================================
    //  PdfGraphicsExtractor — one bbox over the whole path lost every rule
    // ==================================================================

    /// <summary>
    /// Three thin rules painted by one <c>f</c>. The combined box is 400 x 40.6, which failed the
    /// <c>thin &lt; 3</c> test, so the method emitted nothing and PdfTableExtractor's Lines
    /// strategy reported no table at all.
    /// </summary>
    [Fact]
    public void MultipleThinRectanglesInOneFill_YieldOneRulingEach()
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << >> >>",
            StreamObj(4, "",
                "0 100 400 0.6 re\n0 120 400 0.6 re\n0 140 400 0.6 re\nf\n"));

        var g = PdfGraphicsExtractor.ExtractPage(new MemoryStream(pdf), 0);

        Assert.Equal(3, g.HorizontalRulings.Count);
        // Assert on the geometry, not the count alone: each rule sits at its own band centre and
        // spans the full 400 pt width.
        var ys = g.HorizontalRulings.Select(r => r.Y).OrderBy(y => y).ToArray();
        Assert.Equal(100.3f, ys[0], 1);
        Assert.Equal(120.3f, ys[1], 1);
        Assert.Equal(140.3f, ys[2], 1);
        Assert.All(g.HorizontalRulings, r => Assert.Equal(400f, r.X2 - r.X1, 1));
    }

    /// <summary>A genuine background fill is still not a ruling — the thin test still applies.</summary>
    [Fact]
    public void SingleLargeFilledRectangle_IsNotReportedAsARuling()
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << >> >>",
            StreamObj(4, "", "50 50 400 300 re\nf\n"));

        var g = PdfGraphicsExtractor.ExtractPage(new MemoryStream(pdf), 0);

        Assert.Empty(g.HorizontalRulings);
        Assert.Empty(g.VerticalRulings);
    }

    // ==================================================================
    //  PdfTextSearch — negative contextChars
    // ==================================================================

    [Fact]
    public void Search_NegativeContextChars_ThrowsNamingTheParameter()
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            StreamObj(4, "", "BT /F1 12 Tf 10 100 Td (abc) Tj ET\n"));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfTextSearch.Search(new MemoryStream(pdf), "a", true, -10));

        // The old failure surfaced from inside StringBuilder.Append and named its parameter.
        Assert.Equal("contextChars", ex.ParamName);
    }

    [Fact]
    public void Search_NegativeContextChars_FilePathOverloadAlsoThrows()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfTextSearch.Search("no-such-file.pdf", "a", true, -1));

        // Validated before the file is opened, so the argument error is not masked by IO.
        Assert.Equal("contextChars", ex.ParamName);
    }

    // ==================================================================
    //  PdfStructuredTextExtractor — aggregate form-invocation budget
    // ==================================================================

    /// <summary>
    /// A doubling chain defeats both existing guards: no form draws itself (so the branch-scoped
    /// visited set never fires) and no path is deep (so the depth cap never fires), yet the total
    /// invocation count is 2^(n-1). This asserts the budget's own evidence — the reported refusal —
    /// not elapsed time.
    /// </summary>
    [Fact]
    public void FormXObjectDoublingChain_IsRefusedByTheAggregateBudget()
    {
        const int levels = 18; // 2^17 = 131,072 invocations > the 100,000 budget

        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };

        // Page resources name every form, so a form with no /Resources of its own inherits them.
        string res = "<< /XObject << " +
            string.Join(" ", Enumerable.Range(1, levels).Select(i => $"/F{i} {3 + i} 0 R")) +
            " >> >>";
        bodies.Add(Obj(3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents " +
            $"{3 + levels + 1} 0 R /Resources {res} >>"));

        for (int i = 1; i <= levels; i++)
        {
            string content = i < levels ? $"/F{i + 1} Do /F{i + 1} Do\n" : "BT ET\n";
            bodies.Add(StreamObj(3 + i, " /Type /XObject /Subtype /Form /BBox [0 0 10 10]", content));
        }
        bodies.Add(StreamObj(3 + levels + 1, "", "/F1 Do\n"));

        byte[] pdf = Assemble(bodies);

        var pages = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf));

        Assert.Single(pages);
        Assert.NotNull(PdfStructuredTextExtractor.LastError);
        Assert.Contains(
            PdfLimits.MaxFormXObjectInvocationsPerPage.ToString("N0"),
            PdfStructuredTextExtractor.LastError,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The budget must not fire on an ordinary page — a guard that refuses legitimate input is a
    /// worse defect than the one it closes.
    /// </summary>
    [Fact]
    public void OrdinaryFormInvocation_IsNotRefusedByTheBudget()
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
            "/Resources << /XObject << /F1 5 0 R >> /Font << /F1 6 0 R >> >> >>",
            StreamObj(4, "", "/F1 Do\n"),
            StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 200 200]",
                "BT /F1 12 Tf 10 100 Td (Hi) Tj ET\n"),
            Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        var pages = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf));

        Assert.Null(PdfStructuredTextExtractor.LastError);
        // The form's text really was reached, so the fixture exercises the recursion it claims to.
        Assert.Contains("Hi", string.Join(" ", pages[0].Lines.Select(l => l.Text)),
            StringComparison.Ordinal);
    }

    // ==================================================================
    //  PdfLayoutAnalyzer — the coverage grid is filled, not painted
    // ==================================================================

    /// <summary>
    /// Glyphs whose advance width and height span the page: marking the entire clamped grid for each
    /// — ~337M cell marks/s — 50 000 of them would cost ~148 s for one page, on the default extraction
    /// path. A difference array bounds that by construction, so the input is analysed rather than
    /// refused or truncated.
    /// </summary>
    [Fact]
    public void FullPageSpanningGlyphs_AreAnalysedRatherThanRefused()
    {
        // Extents large enough to reach both grid clamps (5000 x-buckets, 200 y-bands), so each
        // glyph covers the full 1e6-cell grid: 100 of them was 1e8 marks against the old 5e7 cap.
        var chars = new List<PdfTextChar>();
        for (int i = 0; i < 100; i++)
            chars.Add(new PdfTextChar("X", 0f, 0f, 6000f, 1000f));

        var layout = new PdfPageTextLayout(0, Array.Empty<PdfTextLine>(), chars);

        // No warning: one zone here is the ANSWER (ink everywhere leaves no river), not a
        // give-up. The slot is what tells those two apart, which is why it is asserted null
        // rather than merely ignored.
        Assert.Null(PdfLayoutAnalyzer.LastAnalysisWarning);
        Assert.Single(layout.Columns);
    }

    /// <summary>
    /// The half the ceiling could not give: a page whose glyph coverage exceeds the old budget
    /// <b>and</b> has a real column river is now split correctly instead of collapsing to one
    /// whole-page zone.
    /// </summary>
    /// <remarks>
    /// This is the test that distinguishes "cheaper" from "cheaper by being wrong". Each glyph
    /// spans every y-band (200) and ~84 x-buckets, so 4 000 of them is ~6.7e7 cell marks —
    /// comfortably past the 5e7 cap the painting fill needed. Under that fill the analysis stopped
    /// mid-page and returned one zone with a warning; the river between x=2000 and x=4000 was
    /// invisible.
    /// </remarks>
    [Fact]
    public void CoverageBeyondTheOldCeiling_StillFindsTheColumnRiver()
    {
        var chars = new List<PdfTextChar>();
        for (int i = 0; i < 2000; i++)
        {
            chars.Add(new PdfTextChar("x", (i % 20) * 100f, 0f, 100f, 1000f));          // 0..2000
            chars.Add(new PdfTextChar("x", 4000f + (i % 20) * 100f, 0f, 100f, 1000f));  // 4000..6000
        }

        var columns = PdfLayoutAnalyzer.DetectColumns(
            new PdfPageTextLayout(0, Array.Empty<PdfTextLine>(), chars));

        Assert.Null(PdfLayoutAnalyzer.LastAnalysisWarning);
        Assert.Equal(2, columns.Count);
        Assert.True(columns[0].X + columns[0].Width <= columns[1].X,
            "the two zones overlap, so the river was not where the analysis says it was");
    }

    /// <summary>An ordinary two-column page is still analysed, and still finds two columns.</summary>
    [Fact]
    public void TwoColumnPage_IsStillDetectedWithoutTrippingTheBudget()
    {
        var chars = new List<PdfTextChar>();
        for (int row = 0; row < 40; row++)
        {
            float y = 700f - row * 15f;
            for (int i = 0; i < 20; i++) chars.Add(new PdfTextChar("x", 60f + i * 6f, y, 6f, 10f));
            for (int i = 0; i < 20; i++) chars.Add(new PdfTextChar("x", 330f + i * 6f, y, 6f, 10f));
        }

        var layout = new PdfPageTextLayout(0, Array.Empty<PdfTextLine>(), chars);

        Assert.Null(PdfLayoutAnalyzer.LastAnalysisWarning);
        Assert.Equal(2, layout.Columns.Count);
    }
}
