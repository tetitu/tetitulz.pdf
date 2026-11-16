// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="SimpleFontWidthProbe"/> — the narrow public façade
/// <c>Tetitulz.Pdf.Plugins.Conformance</c> uses for PDF/A's glyph-width consistency check (ISO
/// 19005-1:2005 Technical Corrigendum 1, §6.3.6), since Core's font classes are <c>internal</c> and
/// <c>InternalsVisibleTo</c> deliberately names no plugin. Verified here, against a real embedded
/// TrueType program, before anything in Conformance is built on top of it.
/// </summary>
public class SimpleFontWidthProbeTests
{
    /// <summary>
    /// The one case <see cref="SimpleFontWidthProbe.TryGetEmbeddedAdvance"/> can answer
    /// unambiguously: a symbolic font with no <c>/Encoding</c>, resolved through the embedded
    /// program's own <c>(3,0)</c> cmap via the <c>0xF000|code</c> convention —
    /// <see cref="MinimalTrueType.BuildSymbolic"/> builds exactly this shape, the same fixture the
    /// subsetter tests already trust.
    /// </summary>
    [Fact]
    public void TryGetEmbeddedAdvance_SymbolicFontWithBuiltInEncoding_ReturnsTheHmtxAdvance()
    {
        // Chars sort as 'A' < 'B', so glyph 1 is 'A' (MappedGlyphAdvance = 610) and glyph 2 is
        // 'B' (MappedGlyphAdvance + 10 = 620) — MinimalTrueType's own documented shape.
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        using var doc = OpenFixture(font, declaredWidthOfA: 700);

        var fontDict = doc.ResolveObject(new PdfReference(FontObject, 0)) as PdfDictionary;
        var probe = new SimpleFontWidthProbe(fontDict, doc);

        Assert.True(probe.IsTrueType);
        Assert.True(probe.TryGetEmbeddedAdvance('A', out int advance));
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, advance);
    }

    [Fact]
    public void TryGetEmbeddedAdvance_DistinguishesGlyphsByAdvance()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        using var doc = OpenFixture(font, declaredWidthOfA: 700);

        var fontDict = doc.ResolveObject(new PdfReference(FontObject, 0)) as PdfDictionary;
        var probe = new SimpleFontWidthProbe(fontDict, doc);

        Assert.True(probe.TryGetEmbeddedAdvance('B', out int advanceB));
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance + 10, advanceB);
    }

    /// <summary>
    /// The declared side reads the PDF's own <c>/Widths</c>, independent of what the embedded
    /// program actually says — the two are deliberately different values in this fixture so a test
    /// mixing them up would be caught immediately.
    /// </summary>
    [Fact]
    public void GetWidth_ReadsThePdfsOwnWidthsArray_IndependentlyOfTheEmbeddedProgram()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        using var doc = OpenFixture(font, declaredWidthOfA: 700);

        var fontDict = doc.ResolveObject(new PdfReference(FontObject, 0)) as PdfDictionary;
        var probe = new SimpleFontWidthProbe(fontDict, doc);

        Assert.True(probe.HasExplicitWidth('A'));
        Assert.Equal(700, probe.GetWidth('A'));
        Assert.True(probe.TryGetEmbeddedAdvance('A', out int embedded));
        Assert.NotEqual(700, embedded); // 610 — proves the two sides are genuinely independent reads
    }

    /// <summary>
    /// The common case this probe deliberately declines (see its class remarks): a code with no
    /// resolvable glyph id through the built-in ladder returns false rather than a guess.
    /// </summary>
    [Fact]
    public void TryGetEmbeddedAdvance_CodeWithNoBuiltInMapping_ReturnsFalse()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        using var doc = OpenFixture(font, declaredWidthOfA: 700);

        var fontDict = doc.ResolveObject(new PdfReference(FontObject, 0)) as PdfDictionary;
        var probe = new SimpleFontWidthProbe(fontDict, doc);

        // 'Z' (0x5A) is not one of the font's two mapped characters.
        Assert.False(probe.TryGetEmbeddedAdvance('Z', out int advance));
        Assert.Equal(0, advance);
    }

    [Fact]
    public void IsTrueType_IsFalseForAType1Font()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var fontDict = doc.ResolveObject(new PdfReference(4, 0)) as PdfDictionary;
        var probe = new SimpleFontWidthProbe(fontDict, doc);

        Assert.True(probe.IsSimpleFont);
        Assert.False(probe.IsTrueType);
        Assert.False(probe.TryGetEmbeddedAdvance('A', out _));
    }

    [Fact]
    public void TryGetEmbeddedWidth_ForATrueTypeFont_IsTheEmbeddedAdvance()
    {
        using var doc = OpenFixture(MinimalTrueType.BuildSymbolic("AB"), declaredWidthOfA: 700);
        var probe = new SimpleFontWidthProbe(doc.ResolveObject(new PdfReference(FontObject, 0)) as PdfDictionary, doc);

        Assert.False(probe.IsType1);
        Assert.True(probe.TryGetEmbeddedWidth('A', out double width));
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, width);
    }

    // ---- Type 1 fonts: /FontFile3 (CFF) and /FontFile (Type 1) ----

    private static byte[] CffWithGlyphA(int widthOperand, double[] fontMatrix = null, int secondAWidth = 0)
    {
        var font = new Tetitulz.Pdf.Core.Fonts.Cff.CffFontWriter.Font { NominalWidthX = 0, DefaultWidthX = 0, FontMatrix = fontMatrix };
        font.GlyphNames.Add(".notdef"); font.CharStrings.Add(new byte[] { 14 });
        font.GlyphNames.Add("A");
        font.CharStrings.Add(Concat(MinimalType1.Num(widthOperand), MinimalType1.Num(0), new byte[] { 22, 14 }));
        if (secondAWidth > 0)
        {
            // A second glyph under the same name: the charset no longer names every glyph apart.
            font.GlyphNames.Add("A");
            font.CharStrings.Add(Concat(MinimalType1.Num(secondAWidth), MinimalType1.Num(0), new byte[] { 22, 14 }));
        }
        return Tetitulz.Pdf.Core.Fonts.Cff.CffFontWriter.Build(font);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new System.Collections.Generic.List<byte>();
        foreach (byte[] part in parts) all.AddRange(part);
        return all.ToArray();
    }

    private static SimpleFontWidthProbe Type1Probe(PdfDocument doc) =>
        new SimpleFontWidthProbe(doc.ResolveObject(new PdfReference(FontObject, 0)) as PdfDictionary, doc);

    /// <summary>
    /// A <paramref name="subtype"/> font (object 4) whose <c>/Encoding</c> is <paramref name="encoding"/>,
    /// declaring code 65 500 wide, over a descriptor (5) embedding <paramref name="program"/> (6) as
    /// <c>/FontFile3</c> when <paramref name="length1"/> is null, else as <c>/FontFile</c>.
    /// </summary>
    private static PdfDocument OpenType1Fixture(byte[] program, int? length1 = null, int? length2 = null,
        string encoding = "<< /Differences [65 /A] >>", string subtype = "Type1")
    {
        string fontFile = length1 == null ? "/FontFile3" : "/FontFile";
        string streamExtras = length1 == null
            ? " /Subtype /Type1C"
            : $" /Length1 {length1.Value.ToString(CultureInfo.InvariantCulture)} /Length2 {length2.Value.ToString(CultureInfo.InvariantCulture)}";
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            $"<< /Type /Font /Subtype /{subtype} /BaseFont /TestFont /FirstChar 65 /Widths [500] /Encoding {encoding} " +
                "/FontDescriptor 5 0 R >>",
            $"<< /Type /FontDescriptor /FontName /TestFont /Flags 4 {fontFile} 6 0 R >>",
            "<< /Length " + program.Length.ToString(CultureInfo.InvariantCulture) + streamExtras +
                " >>\nstream\n" + Encoding.Latin1.GetString(program) + "\nendstream");
        return PdfDocument.Open(new MemoryStream(pdf));
    }

    [Theory]
    [InlineData("Type1")]
    [InlineData("MMType1")]
    public void TryGetEmbeddedWidth_Type1CGlyphNamedByDifferences_IsTheCffAdvance(string subtype)
    {
        using var doc = OpenType1Fixture(CffWithGlyphA(560), subtype: subtype);
        var probe = Type1Probe(doc);

        Assert.True(probe.IsType1);
        Assert.False(probe.IsTrueType);
        Assert.True(probe.TryGetEmbeddedWidth(65, out double width));
        Assert.Equal(560, width, 3);
    }

    [Fact]
    public void TryGetEmbeddedWidth_Type1CAdvance_IsScaledByTheFontMatrix()
    {
        using var doc = OpenType1Fixture(CffWithGlyphA(560, new[] { 0.0005, 0, 0, 0.0005, 0, 0 }));
        Assert.True(Type1Probe(doc).TryGetEmbeddedWidth(65, out double width));
        Assert.Equal(280, width, 3);
    }

    [Fact]
    public void TryGetEmbeddedWidth_CodeWithNoDifferencesName_IsDeclined()
    {
        // WinAnsi's 65 is "A" too, but a base-encoding name is not the unambiguous selection a
        // /Differences name is (see the class remarks).
        using var doc = OpenType1Fixture(CffWithGlyphA(560), encoding: "/WinAnsiEncoding");
        Assert.False(Type1Probe(doc).TryGetEmbeddedWidth(65, out _));
    }

    [Fact]
    public void TryGetEmbeddedWidth_DifferencesNameTheCffDoesNotDefine_IsDeclined()
    {
        using var doc = OpenType1Fixture(CffWithGlyphA(560), encoding: "<< /Differences [65 /B] >>");
        var probe = Type1Probe(doc);

        Assert.False(probe.TryGetEmbeddedWidth(65, out _));
        Assert.False(probe.EmbeddedProgramDefines("B"));
        Assert.True(probe.EmbeddedProgramDefines("A"));
    }

    [Fact]
    public void TryGetEmbeddedWidth_CffWhoseCharsetNamesTwoGlyphsAlike_IsDeclined()
    {
        using var doc = OpenType1Fixture(CffWithGlyphA(560, secondAWidth: 900));
        var probe = Type1Probe(doc);

        Assert.False(probe.TryGetEmbeddedWidth(65, out _));
        Assert.Null(probe.EmbeddedProgramDefines("B"));
    }

    [Fact]
    public void TryGetEmbeddedWidth_Type1ProgramGlyphNamedByDifferences_IsItsHsbwWidth()
    {
        var (program, length1, length2) = MinimalType1.Build(new[]
        {
            (".notdef", MinimalType1.AdvanceOnly(0)),
            ("A", MinimalType1.AdvanceOnly(610)),
        });
        using var doc = OpenType1Fixture(program, length1, length2);
        var probe = Type1Probe(doc);

        Assert.True(probe.TryGetEmbeddedWidth(65, out double width));
        Assert.Equal(610, width, 3);
        Assert.True(probe.EmbeddedProgramDefines("A"));
        Assert.False(probe.EmbeddedProgramDefines("B"));
    }

    [Fact]
    public void TryGetEmbeddedWidth_Type1ProgramWithHalfSizeFontMatrix_IsScaled()
    {
        var (program, length1, length2) = MinimalType1.Build(
            new[] { (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(1220)) },
            fontMatrix: "[0.0005 0 0 0.0005 0 0]");
        using var doc = OpenType1Fixture(program, length1, length2);

        Assert.True(Type1Probe(doc).TryGetEmbeddedWidth(65, out double width));
        Assert.Equal(610, width, 1);
    }

    [Fact]
    public void TryGetEmbeddedWidth_Type1ProgramWhoseFontMatrixIsRejected_IsDeclined()
    {
        var (program, length1, length2) = MinimalType1.Build(
            new[] { (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(610)) },
            fontMatrix: "[1 0 0 1 0 0]");
        using var doc = OpenType1Fixture(program, length1, length2);

        Assert.False(Type1Probe(doc).TryGetEmbeddedWidth(65, out _));
    }

    [Fact]
    public void EmbeddedProgramDefines_Type1ProgramWithASkippedCharStringsEntry_CannotTell()
    {
        var (program, length1, length2) = MinimalType1.Build(
            new[] { (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(610)) },
            extraCharStringsText: "/B notALength RD ND\n");
        using var doc = OpenType1Fixture(program, length1, length2);
        var probe = Type1Probe(doc);

        Assert.Null(probe.EmbeddedProgramDefines("B"));
        Assert.True(probe.TryGetEmbeddedWidth(65, out _));   // what was read is still usable for a width
    }

    [Fact]
    public void IsType1_IsFalseForAType3Font()
    {
        using var doc = OpenType1Fixture(CffWithGlyphA(560), subtype: "Type3");
        var probe = Type1Probe(doc);

        Assert.False(probe.IsType1);
        Assert.False(probe.TryGetEmbeddedWidth(65, out _));
        Assert.Null(probe.EmbeddedProgramDefines("A"));
    }

    // ------------------------------------------------------------------

    private const int FontObject = 4;
    private const int FontDescriptorObject = 5;
    private const int FontFileObject = 6;

    /// <summary>
    /// A one-page document with a symbolic, no-<c>/Encoding</c> TrueType font (object 4) embedding
    /// <paramref name="fontProgramBytes"/> as <c>/FontFile2</c>, with a single-entry
    /// <c>/Widths</c> array declaring code 65 ('A') as <paramref name="declaredWidthOfA"/>.
    /// </summary>
    private static PdfDocument OpenFixture(byte[] fontProgramBytes, int declaredWidthOfA)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Font << /F1 " + FontObject + " 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont "
                + "/FirstChar 65 /Widths [" + declaredWidthOfA.ToString(CultureInfo.InvariantCulture) + "] "
                + "/FontDescriptor " + FontDescriptorObject + " 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 4 " // bit 3 = Symbolic
                + "/FontFile2 " + FontFileObject + " 0 R >>",
            StreamObj(fontProgramBytes));

        return PdfDocument.Open(new MemoryStream(pdf));
    }

    private static string StreamObj(byte[] data)
    {
        // Latin-1 round-trips every byte value 0-255 through a char 1:1, which is what lets a
        // binary font program travel through this string-based PDF builder unmodified.
        string raw = Encoding.Latin1.GetString(data);
        return "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + raw + "\nendstream";
    }

    private static byte[] Build(params string[] bodies)
    {
        // sb.Length is used directly as a byte offset: Latin-1 maps every char 0-255 to exactly
        // one byte, so char count and byte count agree throughout this builder.
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
