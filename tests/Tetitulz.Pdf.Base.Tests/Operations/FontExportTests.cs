// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfFontExporter"/> — getting an embedded font program back out as a <b>file</b>.
/// </summary>
/// <remarks>
/// <para><b>The point of the tests is that two of the four flavours are not files.</b>
/// <c>/FontFile2</c> is a bare sfnt and comes out byte-for-byte; <c>/FontFile</c> needs PFB sector
/// headers rebuilt from <c>/Length1..3</c>, and a bare CFF needs an OpenType envelope. So every
/// assertion here checks the <b>signature of the produced file</b>, not that bytes came back —
/// returning the raw stream would satisfy anything weaker and produce a file no font tool opens.
/// </para>
/// </remarks>
public class FontExportTests
{
    /// <summary>A TrueType program is already a <c>.ttf</c> and must come back untouched.</summary>
    [Fact]
    public void ATrueTypeProgram_ComesBackByteForByte()
    {
        byte[] program = MinimalTrueType.Build("AB");

        var font = Assert.Single(PdfOperations.ExtractFonts(new MemoryStream(WithFontFile2(program))));

        Assert.Equal(program, font.Data);
        Assert.Equal(".ttf", font.FileExtension);
        Assert.Equal("FontFile2", font.ProgramKey);
    }

    /// <summary>
    /// <b>A Type 1 program is not a file.</b> §9.9 stores the three segments raw; a <c>.pfb</c>
    /// interleaves them with six-byte sector headers, and every font tool reads the latter.
    /// </summary>
    [Fact]
    public void AType1Program_GetsItsPfbSectorHeaders()
    {
        var font = Assert.Single(PdfOperations.ExtractFonts(new MemoryStream(WithType1FontFile())));

        Assert.Equal(".pfb", font.FileExtension);
        Assert.Equal("FontFile", font.ProgramKey);

        // 0x80 0x01 opens the first (ASCII) sector, and 0x80 0x03 closes the file.
        Assert.Equal(0x80, font.Data[0]);
        Assert.Equal(0x01, font.Data[1]);
        Assert.Equal(0x80, font.Data[font.Data.Length - 2]);
        Assert.Equal(0x03, font.Data[font.Data.Length - 1]);
        Assert.True(font.Data.Length > 20, "the segments must still be in there");
    }

    /// <summary>
    /// A <c>/FontFile3</c> whose own <c>/Subtype</c> is <c>/OpenType</c> is <b>already</b> an sfnt.
    /// Wrapping it again — which "FontFile3 means CFF" would do — makes a corrupt double envelope.
    /// </summary>
    [Fact]
    public void AnOpenTypeFontFile3_IsNotWrappedAgain()
    {
        byte[] program = MinimalOpenTypeCff.Build("AB");

        var font = Assert.Single(PdfOperations.ExtractFonts(
            new MemoryStream(WithFontFile3(program, "/OpenType"))));

        Assert.Equal(program, font.Data);
        Assert.Equal(".otf", font.FileExtension);
    }

    /// <summary>
    /// …and a <c>/Type1C</c> one is a <b>bare CFF</b> that must gain an envelope. The produced file
    /// starts with the <c>OTTO</c> sfnt tag, which a bare CFF does not.
    /// </summary>
    [Fact]
    public void ABareCffFontFile3_GainsAnOpenTypeEnvelope()
    {
        byte[] bareCff = MinimalOpenTypeCff.ExtractCff(MinimalOpenTypeCff.Build("AB"));

        var font = Assert.Single(PdfOperations.ExtractFonts(
            new MemoryStream(WithFontFile3(bareCff, "/Type1C"))));

        Assert.Equal(".otf", font.FileExtension);
        Assert.NotEqual(bareCff, font.Data);
        Assert.Equal(new byte[] { 0x4F, 0x54, 0x54, 0x4F }, font.Data.Take(4).ToArray());   // 'OTTO'
    }

    // ------------------------------------------------------------------

    /// <summary>A font the document does not embed yields nothing — and is not an error.</summary>
    [Fact]
    public void ANonEmbeddedFont_YieldsNothing()
    {
        Assert.Empty(PdfOperations.ExtractFonts(new MemoryStream(StandardFourteenOnly())));
        Assert.Null(PdfFontExporter.LastError);
    }

    /// <summary>
    /// One font referenced from several pages comes back once. A forty-page document with one font
    /// must not yield forty copies of it.
    /// </summary>
    [Fact]
    public void AFontSharedAcrossPages_IsReturnedOnce()
    {
        Assert.Single(PdfOperations.ExtractFonts(new MemoryStream(SharedFontTwoPages())));
    }

    /// <summary>
    /// A subset is flagged. It is not the foundry's font — most of its glyphs are absent — and
    /// installing or redistributing it is neither useful nor generally permitted.
    /// </summary>
    [Fact]
    public void ASubsetTaggedFont_IsFlaggedAsASubset()
    {
        var subset = Assert.Single(PdfOperations.ExtractFonts(
            new MemoryStream(WithFontFile2(MinimalTrueType.Build("AB"), "/ABCDEF+TestFont"))));
        Assert.True(subset.IsSubset);

        var whole = Assert.Single(PdfOperations.ExtractFonts(
            new MemoryStream(WithFontFile2(MinimalTrueType.Build("AB"), "/TestFont"))));
        Assert.False(whole.IsSubset);
    }

    /// <summary>
    /// The program's own <c>fsType</c> travels with it — extraction is the operation most likely to
    /// redistribute a font, and the field is the only statement the file makes about that.
    /// </summary>
    [Fact]
    public void TheEmbeddingLicenceTravelsWithTheProgram()
    {
        var restricted = Assert.Single(PdfOperations.ExtractFonts(
            new MemoryStream(WithFontFile2(MinimalTrueType.BuildWithFsType("AB", 0x0002)))));
        Assert.True(restricted.License.RestrictedLicense);

        var installable = Assert.Single(PdfOperations.ExtractFonts(
            new MemoryStream(WithFontFile2(MinimalTrueType.BuildWithFsType("AB", 0x0000)))));
        Assert.False(installable.License.RestrictedLicense);
    }

    /// <summary>
    /// A program that will not decode is <b>reported</b>, not silently omitted: an empty list is the
    /// right answer for a document that embeds nothing, so silence would be indistinguishable.
    /// </summary>
    [Fact]
    public void AProgramThatWillNotDecode_IsReported()
    {
        var fonts = PdfOperations.ExtractFonts(new MemoryStream(UndecodableFontFile()));

        Assert.Empty(fonts);
        Assert.NotNull(PdfFontExporter.LastError);
    }

    /// <summary>The error slot is cleared by a call that has nothing to report.</summary>
    [Fact]
    public void TheErrorSlotIsClearedByACleanCall()
    {
        PdfOperations.ExtractFonts(new MemoryStream(UndecodableFontFile()));
        Assert.NotNull(PdfFontExporter.LastError);

        PdfOperations.ExtractFonts(new MemoryStream(WithFontFile2(MinimalTrueType.Build("AB"))));
        Assert.Null(PdfFontExporter.LastError);
    }

    /// <summary>
    /// A font used only inside a form XObject — an imported page, a stamp, a letterhead — is
    /// extracted: the walk goes through the page's forms, not just its own <c>/Font</c>.
    /// </summary>
    [Fact]
    public void AFontUsedOnlyInsideAFormXObject_IsExtracted()
    {
        var font = Assert.Single(PdfOperations.ExtractFonts(new MemoryStream(FontInsideAForm())));
        Assert.Equal("TestFont", font.BaseFont);
        Assert.Null(PdfFontExporter.LastError);
    }

    /// <summary>The page draws a form; only the form's resources name the font.</summary>
    private static byte[] FontInsideAForm() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /XObject << /Fm0 8 0 R >> >> >>",
        StreamObj("/Fm0 Do"),
        "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FontDescriptor 6 0 R >>",
        "<< /Type /FontDescriptor /FontName /TestFont /FontFile2 7 0 R >>",
        BinaryStreamObj(MinimalTrueType.Build("AB"), null),
        "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >> /Length 0 >>\nstream\n\nendstream");

    /// <summary>
    /// A Type 0 font's program is on its <b>descendant</b> CIDFont's descriptor — the Type 0 has
    /// none of its own.
    /// </summary>
    [Fact]
    public void ATypeZeroFontsProgram_IsFoundOnItsDescendant()
    {
        var font = Assert.Single(PdfOperations.ExtractFonts(new MemoryStream(TypeZeroFont())));
        Assert.Equal(("Type0", "FontFile2"), (font.Subtype, font.ProgramKey));
    }

    private static byte[] TypeZeroFont() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj(""),
        "<< /Type /Font /Subtype /Type0 /BaseFont /TestFont /Encoding /Identity-H /DescendantFonts [8 0 R] >>",
        "<< /Type /FontDescriptor /FontName /TestFont /FontFile2 7 0 R >>",
        BinaryStreamObj(MinimalTrueType.Build("AB"), null),
        "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 6 0 R >>");

    /// <summary>
    /// A program too short to be any font file (under four bytes — not even an sfnt's version tag)
    /// is reported and left out, not returned as a file no tool opens.
    /// </summary>
    [Fact]
    public void AProgramTooShortToBeAFont_IsReported()
    {
        Assert.Empty(PdfOperations.ExtractFonts(new MemoryStream(WithFontFile2(new byte[] { 0, 1, 0 }))));
        Assert.Contains("nothing usable", PdfFontExporter.LastError);
    }

    // ------------------------------------------------------------------

    private static byte[] WithFontFile2(byte[] program, string baseFont = "/TestFont") => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj(""),
        "<< /Type /Font /Subtype /TrueType /BaseFont " + baseFont + " /FontDescriptor 6 0 R >>",
        "<< /Type /FontDescriptor /FontName " + baseFont + " /FontFile2 7 0 R >>",
        BinaryStreamObj(program, null));

    private static byte[] WithFontFile3(byte[] program, string programSubtype) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj(""),
        "<< /Type /Font /Subtype /Type1 /BaseFont /TestFont /FontDescriptor 6 0 R >>",
        "<< /Type /FontDescriptor /FontName /TestFont /FontFile3 7 0 R >>",
        BinaryStreamObj(program, "/Subtype " + programSubtype));

    /// <summary>
    /// A Type 1 program: 40 bytes of clear text, 30 of "binary", 12 of trailer — the three segments
    /// §9.9 names, with the lengths written as <b>indirect</b> references, which is what real
    /// producers do and what a raw read loses.
    /// </summary>
    private static byte[] WithType1FontFile()
    {
        var program = new byte[82];
        for (int i = 0; i < program.Length; i++) program[i] = (byte)('A' + (i % 26));

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
                + " /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObj(""),
            "<< /Type /Font /Subtype /Type1 /BaseFont /TestFont /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /FontFile 7 0 R >>",
            BinaryStreamObj(program, "/Length1 8 0 R /Length2 9 0 R /Length3 10 0 R"),
            "40", "30", "12");
    }

    private static byte[] StandardFourteenOnly() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj(""),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    private static byte[] SharedFontTwoPages() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj(""),
        "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FontDescriptor 6 0 R >>",
        "<< /Type /FontDescriptor /FontName /TestFont /FontFile2 7 0 R >>",
        BinaryStreamObj(MinimalTrueType.Build("AB"), null),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>");

    /// <summary>A <c>/FontFile2</c> claiming <c>/FlateDecode</c> over bytes that are not one.</summary>
    private static byte[] UndecodableFontFile() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj(""),
        "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FontDescriptor 6 0 R >>",
        "<< /Type /FontDescriptor /FontName /TestFont /FontFile2 7 0 R >>",
        BinaryStreamObj(Encoding.Latin1.GetBytes("not a flate stream at all"), "/Filter /FlateDecode"));

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    /// <summary>A stream object carrying arbitrary bytes, marked so <see cref="Build"/> splices them.</summary>
    private static string BinaryStreamObj(byte[] data, string extraKeys) =>
        " BIN " + System.Convert.ToBase64String(data) + " " + (extraKeys ?? "");

    private static byte[] Build(params string[] bodies)
    {
        var output = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); output.Write(b, 0, b.Length); }

        Ascii("%PDF-1.7\n");
        var offsets = new int[bodies.Length];

        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = (int)output.Length;
            Ascii((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");

            if (bodies[i].StartsWith(" BIN ", System.StringComparison.Ordinal))
            {
                string[] parts = bodies[i].Substring(5).Split(' ');
                byte[] data = System.Convert.FromBase64String(parts[0]);
                string extra = parts.Length > 1 ? parts[1] : "";

                Ascii("<< " + (extra.Length > 0 ? extra + " " : "") + "/Length " +
                      data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
                output.Write(data, 0, data.Length);
                Ascii("\nendstream");
            }
            else Ascii(bodies[i]);

            Ascii("\nendobj\n");
        }

        int xref = (int)output.Length;
        Ascii("xref\n0 " + (bodies.Length + 1).ToString(CultureInfo.InvariantCulture) +
              "\n0000000000 65535 f \n");
        foreach (int o in offsets)
            Ascii(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Ascii("trailer\n<< /Size " + (bodies.Length + 1).ToString(CultureInfo.InvariantCulture) +
              " /Root 1 0 R >>\n");
        Ascii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return output.ToArray();
    }
}
