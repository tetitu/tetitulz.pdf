// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAValidator"/>'s A19 — ISO 19005-1:2005 Technical Corrigendum 1 §6.3.6:
/// <c>/Widths</c> must agree with the embedded program's own glyph advance. Scoped to where
/// <c>Core.Fonts.SimpleFontWidthProbe</c> selects the glyph without ambiguity (see its remarks): a
/// symbolic TrueType font with its built-in encoding — <see cref="MinimalTrueType.BuildSymbolic"/>
/// builds exactly that shape — or a Type 1 font's <c>/Differences</c> name in a CFF or Type 1 program.
/// </summary>
public class PdfAGlyphWidthConsistencyTests
{
    [Fact]
    public void Validate_WidthsMismatchesEmbeddedAdvance_ReportsA19()
    {
        // MinimalTrueType.BuildSymbolic("AB") gives glyph 1 ('A') an hmtx advance of
        // MappedGlyphAdvance (610). Declaring /Widths[0] (code 65 = 'A') as something else is the
        // mismatch.
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        var result = Validate(FontPdf(font, declaredWidthOfA: 700));

        Assert.Contains(result.Violations, v => v.Code == "A19");
    }

    [Fact]
    public void Validate_WidthsMatchesEmbeddedAdvance_DoesNotReportA19()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        var result = Validate(FontPdf(font, declaredWidthOfA: MinimalTrueType.MappedGlyphAdvance));

        Assert.DoesNotContain(result.Violations, v => v.Code == "A19");
    }

    /// <summary>The common case this check deliberately declines: a nonsymbolic font with a
    /// standard encoding and no <c>/Differences</c> has no code this probe can resolve to a glyph
    /// id, so a genuine mismatch on such a font goes unreported — a known, documented limitation,
    /// not a bug this test is pinning as correct behaviour to keep in mind if the scope ever
    /// widens.</summary>
    [Fact]
    public void Validate_NonsymbolicFontWithNoDifferences_ReportsNothing_EvenWithAMismatch()
    {
        byte[] font = MinimalTrueType.Build("AB"); // (3,1) Unicode cmap, not (3,0) symbol
        var result = Validate(FontPdf(font, declaredWidthOfA: 999, symbolic: false));

        Assert.DoesNotContain(result.Violations, v => v.Code == "A19");
    }

    [Fact]
    public void Validate_NoWidthsArray_ReportsNothing()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        var result = Validate(FontPdfWithNoWidths(font));

        Assert.DoesNotContain(result.Violations, v => v.Code == "A19");
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(590, true)]
    public void Validate_Type1CFontAgainstItsCffAdvance_A19(int declaredWidthOfA, bool reports)
    {
        // MinimalOpenTypeCff's box glyph A advances 500; /Differences names it for code 65.
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("A"));
        var result = Validate(Type1FontPdf(cff, "/FontFile3", " /Subtype /Type1C", declaredWidthOfA));

        if (reports) Assert.Contains(result.Violations, v => v.Code == "A19" && v.Message.Contains("Type 1 program"));
        else Assert.DoesNotContain(result.Violations, v => v.Code == "A19");
    }

    [Theory]
    [InlineData(610, false)]
    [InlineData(700, true)]
    public void Validate_Type1ProgramFontAgainstItsHsbwAdvance_A19(int declaredWidthOfA, bool reports)
    {
        var (program, length1, length2) = MinimalType1.Build(
            new[] { (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(610)) });
        var result = Validate(Type1FontPdf(program, "/FontFile",
            $" /Length1 {length1.ToString(CultureInfo.InvariantCulture)} /Length2 {length2.ToString(CultureInfo.InvariantCulture)}",
            declaredWidthOfA));

        if (reports) Assert.Contains(result.Violations, v => v.Code == "A19" && v.Message.Contains("(ISO 19005-1"));
        else Assert.DoesNotContain(result.Violations, v => v.Code == "A19");
    }

    [Fact]
    public void Validate_NonEmbeddedType1Font_ReportsNothingForA19()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /Widths [999] >>",
            "<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 6 0 R >>",
            "<< /N 3 /Length 3 >>\nstream\nfoo\nendstream");

        var result = PdfAValidator.Validate(new MemoryStream(pdf));

        Assert.DoesNotContain(result.Violations, v => v.Code == "A19");
    }

    /// <summary>
    /// A Type 3 font: <c>/Widths</c> against its glyph procedure's <c>d0</c> width, in every part
    /// (ISO 19005-1 Cor. 1 6.3.6, 19005-2 6.2.11.5, 19005-4 6.2.10.5), each citing its own clause.
    /// </summary>
    [Theory]
    [InlineData(1, 400, "ISO 19005-1:2005 Technical Corrigendum 1 §6.3.6")]
    [InlineData(2, 400, "ISO 19005-2 6.2.11.5")]
    [InlineData(4, 400, "ISO 19005-4 6.2.10.5")]
    [InlineData(2, 500, null)]
    public void Validate_Type3WidthsAgainstTheGlyphProcedure_A19(int part, int d0Width, string? clause)
    {
        string proc = d0Width.ToString(CultureInfo.InvariantCulture) + " 0 d0 0 0 10 10 re f";
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /T3 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
            "/CharProcs << /a 5 0 R >> /Encoding << /Type /Encoding /Differences [65 /a] >> /FirstChar 65 /LastChar 65 /Widths [500] >>",
            "<< /Length " + proc.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + proc + "\nendstream");

        var a19 = PdfAValidator.Validate(new MemoryStream(pdf), (PdfAConformancePart)part).Violations.Where(v => v.Code == "A19").ToList();
        if (clause == null)
            Assert.Empty(a19);
        else
        {
            string message = Assert.Single(a19).Message;
            Assert.Contains("the Type 3 glyph procedure's d0/d1 width is 400", message, StringComparison.Ordinal);
            Assert.Contains(clause, message, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------

    private static PdfAValidationResult Validate(byte[] pdf) => PdfAValidator.Validate(new MemoryStream(pdf));

    private static byte[] FontPdf(byte[] fontProgram, int declaredWidthOfA, bool symbolic = true)
        => Build(
            "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 65 /Widths ["
                + declaredWidthOfA.ToString(CultureInfo.InvariantCulture) + "] /FontDescriptor 7 0 R >>",
            "<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 6 0 R >>",
            "<< /N 3 /Length 3 >>\nstream\nfoo\nendstream",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags " + (symbolic ? "4" : "32")
                + " /FontFile2 8 0 R >>",
            StreamObj(fontProgram));

    private static byte[] Type1FontPdf(byte[] program, string fontFileKey, string streamEntries, int declaredWidthOfA)
        => Build(
            "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /TestFont /FirstChar 65 /Widths ["
                + declaredWidthOfA.ToString(CultureInfo.InvariantCulture)
                + "] /Encoding << /Differences [65 /A] >> /FontDescriptor 7 0 R >>",
            "<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 6 0 R >>",
            "<< /N 3 /Length 3 >>\nstream\nfoo\nendstream",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 4 " + fontFileKey + " 8 0 R >>",
            "<< /Length " + program.Length.ToString(CultureInfo.InvariantCulture) + streamEntries
                + " >>\nstream\n" + Encoding.Latin1.GetString(program) + "\nendstream");

    private static byte[] FontPdfWithNoWidths(byte[] fontProgram)
        => Build(
            "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FontDescriptor 7 0 R >>",
            "<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 6 0 R >>",
            "<< /N 3 /Length 3 >>\nstream\nfoo\nendstream",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 4 /FontFile2 8 0 R >>",
            StreamObj(fontProgram));

    private static string StreamObj(byte[] data)
    {
        // Latin-1 round-trips every byte value 0-255 through a char 1:1.
        string raw = Encoding.Latin1.GetString(data);
        return "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + raw + "\nendstream";
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
