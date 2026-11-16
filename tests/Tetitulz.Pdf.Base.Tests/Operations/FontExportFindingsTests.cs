// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The Fonts sweep, batch 2: what reading <see cref="PdfFontExporter"/> found, and the two shared
/// readers it led to — the overflow in Core's <see cref="PfbAssembler"/>, and
/// <c>PdfResolve.Int</c>'s wrap-around.
/// </summary>
public class FontExportFindingsTests
{
    // ---------------------------------------------------------------- PdfResolve.Int

    /// <summary>
    /// Outside the <c>int</c> range a file's number saturates. It was <c>(int)</c> of
    /// <c>LongValue</c>, which keeps the low 32 bits: 4294967297 read as 1, 1e30 as -1.
    /// </summary>
    [Theory]
    [InlineData(4294967297d, int.MaxValue)]
    [InlineData(1e30, int.MaxValue)]
    [InlineData(-1e30, int.MinValue)]
    [InlineData(-4294967297d, int.MinValue)]
    [InlineData(7.9, 7)]
    [InlineData(-7.9, -7)]
    [InlineData(2147483647d, int.MaxValue)]
    [InlineData(40d, 40)]
    public void Saturate_KeepsTheMagnitudeOfAnOutOfRangeNumber(double value, int expected)
        => Assert.Equal(expected, PdfResolve.Saturate(value, fallback: -99));

    [Fact]
    public void Saturate_NaN_IsTheFallback()
        => Assert.Equal(-99, PdfResolve.Saturate(double.NaN, fallback: -99));

    // ---------------------------------------------------------------- the PFB assembler

    /// <summary>
    /// Hostile lengths: <c>2147483647 + 1</c> overflowed an <c>int</c> sum to a negative that
    /// passed the bound, and the copy then threw. Taken in <c>long</c>, the lengths are refused.
    /// </summary>
    [Fact]
    public void PfbAssembler_OverflowingLengths_AreRefusedNotThrown()
    {
        var raw = new byte[82];
        Assert.Null(PfbAssembler.Build(raw, int.MaxValue, 1, 0));
        // The trailing segment's sum overflowed the same way; it is dropped, and the rest is built.
        byte[] pfb = PfbAssembler.Build(raw, 40, 30, int.MaxValue);
        Assert.NotNull(pfb);
        Assert.Equal(6 + 40 + 6 + 30 + 2, pfb.Length);
    }

    // ---------------------------------------------------------------- the exporter

    /// <summary>
    /// Hostile lengths through the exporter: refused and reported, not thrown out of the call.
    /// </summary>
    [Fact]
    public void ExtractFonts_OverflowingType1Lengths_AreReportedNotThrown()
    {
        var fonts = PdfFontExporter.ExtractFonts(new MemoryStream(Type1Font("2147483647", "1", "0")));

        Assert.Empty(fonts);
        Assert.Contains("could not be rebuilt", PdfFontExporter.LastError);
    }

    /// <summary>
    /// A length past the <c>int</c> range read as its low 32 bits: 2^32 + 40 became 40, a
    /// plausible length, and a PFB was built from lengths the file never stated. Saturated, the
    /// length is too large and the font is reported.
    /// </summary>
    [Fact]
    public void ExtractFonts_ALengthPastTheIntRange_IsNotReadAsItsLowBits()
    {
        var fonts = PdfFontExporter.ExtractFonts(new MemoryStream(Type1Font("4294967336", "30", "12")));

        Assert.Empty(fonts);
        Assert.Contains("could not be rebuilt", PdfFontExporter.LastError);
    }

    /// <summary>The control: the same font with lengths 40, 30 and 12 is extracted as a PFB.</summary>
    [Fact]
    public void ExtractFonts_TheSameFontWithItsRealLengths_IsExtracted()
    {
        var font = Assert.Single(PdfFontExporter.ExtractFonts(new MemoryStream(Type1Font("40", "30", "12"))));
        Assert.Equal(".pfb", font.FileExtension);
        Assert.Null(PdfFontExporter.LastError);
    }

    /// <summary>
    /// A descriptor written directly inside its font dictionary has no object number, and only
    /// numbered ones were de-duplicated: a font two pages share came back twice.
    /// </summary>
    [Fact]
    public void ExtractFonts_ADirectDescriptorSharedByTwoPages_IsReturnedOnce()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream(""),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont " +
            "/FontDescriptor << /Type /FontDescriptor /FontName /TestFont /FontFile2 6 0 R >> >>",
            Stream(Encoding.Latin1.GetString(MinimalTrueType.Build("AB"))),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");

        Assert.Single(PdfFontExporter.ExtractFonts(new MemoryStream(pdf)));
    }

    // ---------------------------------------------------------------- the renderer's font loader

    /// <summary>
    /// Hostile lengths through the renderer's Type 1 loader, which kept its own copy of the
    /// bound summed in <c>int</c>: the page renders, and the font's program is refused rather than
    /// crashing the load.
    /// </summary>
    /// <remarks>
    /// The load's exception was caught and reported as a <c>FontLoadFailed</c> carrying it, and the
    /// page fell back to a system font — so the page drew either way, and only the report tells the
    /// refusal from the crash. Read on this thread alone: <c>OnEvent</c> is process-wide.
    /// </remarks>
    [Fact]
    public void Render_OverflowingType1Lengths_AreRefusedAndThePageDraws()
    {
        var crashes = new List<string>();
        int ownThread = System.Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            if (System.Environment.CurrentManagedThreadId == ownThread
                && kind == RenderDiagnosticKind.FontLoadFailed && ex != null)
                crashes.Add(detail + " — " + ex.GetType().Name);
        }

        RenderedPage page;
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            page = PdfPageRenderer.RenderPageToPixels(
                new MemoryStream(Type1Font("2147483647", "1", "0", "BT /F1 24 Tf 10 10 Td (A) Tj ET")), 0, 36);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Empty(crashes);
        Assert.Equal(306, page.Width);
        Assert.Equal(396, page.Height);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// One page drawing with a Type 1 font whose <c>/FontFile</c> is 82 letters, with
    /// <c>/Length1..3</c> written as indirect numbers (8, 9 and 10 0 R), as producers write them.
    /// </summary>
    private static byte[] Type1Font(string l1, string l2, string l3, string content = "")
    {
        var program = new StringBuilder();
        for (int i = 0; i < 82; i++) program.Append((char)('A' + (i % 26)));
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /TestFont /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /FontFile 7 0 R >>",
            Stream(program.ToString(), "/Length1 8 0 R /Length2 9 0 R /Length3 10 0 R"),
            l1, l2, l3);
    }

    private static string Stream(string data, string extraKeys = "")
        => $"<< {extraKeys} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
