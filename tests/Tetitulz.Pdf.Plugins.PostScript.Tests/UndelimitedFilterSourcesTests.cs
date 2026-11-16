// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>currentfile /FlateDecode filter</c> and its binary siblings as image data sources — how a
/// binary EPS embeds its images. They were refused by name: nothing in the byte stream marks where
/// the block ends, and a guessed extent would feed the rest of the program into the image and lose
/// it from the program. Now Core's filters decode the block and report where it ended, and the
/// scanner resumes <b>exactly</b> there.
///
/// <para>So every test here draws something after the block and asserts it drew as written: a grey
/// of 0.25 set from the bytes right after the data, then a fill. A resume point one byte off reads
/// the grey out of the image data, or the image out of the grey.</para>
/// </summary>
public sealed class UndelimitedFilterSourcesTests
{
    private static readonly byte[] Samples = { 0x00, 0xFF, 0x7F, 0x40 };

    private const string FourGrey  = "4 1 8 [4 0 0 -1 0 1] currentfile ";

    /// <summary>
    /// A 100×100 image — ten thousand sample bytes, far more than the JPEG fixture's 37 — so that a
    /// JPEG padded to the sample count, as raw samples are, would come out as something other than
    /// the codestream.
    /// </summary>
    private const string JpegGrey  = "100 100 8 [100 0 0 -100 0 100] currentfile ";
    private const string Afterwards = " 0.25 setgray newpath 0 0 moveto 9 0 lineto 9 9 lineto fill\n";

    private static byte[] Program(string header, string filters, byte[] block,
                                  string after = Afterwards, string lineEnd = "\n")
        => Bytes(header + filters + " image" + lineEnd).Concat(block).Concat(Bytes(after)).ToArray();

    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    private static PostScriptConversionResult Convert(byte[] program)
        => PostScriptConversion.ConvertFragment(program);

    private static PdfStream Image(PostScriptConversionResult r)
    {
        var xobjects = Assert.IsType<PdfDictionary>(r.Resources?[PdfName.XObject]);
        return Assert.IsType<PdfStream>(xobjects.Single().Value);
    }

    /// <summary>The image was drawn, and the program after its block ran as written.</summary>
    private static void AssertDrawnAsWritten(PostScriptConversionResult r)
    {
        var ops = ContentStreamAssert.Operators(r.Content);
        Assert.Contains("Do", ops);
        Assert.Contains("f", ops);
        Assert.Equal(0.25, ContentStreamAssert.FirstOperandsOf(r.Content, "g")[0], 6);
        Assert.True(r.IsClean, r.LastError);
        Assert.Empty(r.UnimplementedOperators);
    }

    /// <summary>The image was skipped, and the program after its block still ran as written.</summary>
    private static void AssertSkippedButResumed(PostScriptConversionResult r)
    {
        var ops = ContentStreamAssert.Operators(r.Content);
        Assert.DoesNotContain("Do", ops);
        Assert.Contains("f", ops);
        Assert.Equal(0.25, ContentStreamAssert.FirstOperandsOf(r.Content, "g")[0], 6);
    }

    public static IEnumerable<object[]> BinaryFilters => new[]
    {
        new object[] { "/RunLengthDecode filter", RunLengthEncodeFilter.Encode(Samples) },
        new object[] { "/LZWDecode filter",       LzwEncodeFilter.Encode(Samples) },
        new object[] { "/FlateDecode filter",     FlateEncodeFilter.Encode(Samples) },
    };

    [Theory]
    [MemberData(nameof(BinaryFilters))]
    public void ABinaryFilterSource_DecodesToItsBytes_AndTheProgramResumesAfterIt(string filters, byte[] block)
    {
        var r = Convert(Program(FourGrey, filters, block));

        Assert.Equal(Samples, Image(r).RawData);
        AssertDrawnAsWritten(r);
    }

    /// <summary>
    /// Nothing — not even white space — between the block and the next token: the resume point is
    /// the byte after the data's own end, not the next line. The next token is <c>10 40 div</c>
    /// rather than <c>0.25</c>: a resume one byte late would read <c>.25</c> from the latter and
    /// still set the right grey, which is how the first sweep's plugin-side RunLength row survived.
    /// </summary>
    [Theory]
    [MemberData(nameof(BinaryFilters))]
    public void TheNextTokenMayFollowTheBlockWithoutASeparator(string filters, byte[] block)
    {
        var r = Convert(Program(FourGrey, filters, block,
                                after: "10 40 div setgray newpath 0 0 moveto 9 0 lineto 9 9 lineto fill\n"));

        Assert.Equal(Samples, Image(r).RawData);
        AssertDrawnAsWritten(r);
    }

    /// <summary>
    /// A stream longer than the 4 096 bytes the measuring pass hands the inflater at a time — random
    /// samples do not compress — so the end lies in a later chunk.
    /// </summary>
    [Fact]
    public void AFlateBlockLongerThanTheMeasuringChunk_IsConsumedExactly()
    {
        var samples = new byte[6000];
        new Random(7).NextBytes(samples);
        byte[] block = FlateEncodeFilter.Encode(samples);
        Assert.True(block.Length > 4096, "fixture: the stream must span several measuring chunks");

        var r = Convert(Program("6000 1 8 [6000 0 0 -1 0 1] currentfile ", "/FlateDecode filter", block));

        Assert.Equal(samples, Image(r).RawData);
        AssertDrawnAsWritten(r);
    }

    /// <summary>
    /// A block the data never ends — a Flate stream cut before its final block, with nothing after
    /// it — is reported and skipped, as an unterminated <c>~&gt;</c> block is: the bytes it had are
    /// not a picture, and where the program would resume is unknown.
    /// </summary>
    [Fact]
    public void ATruncatedBinaryBlock_IsReportedAndSkipped()
    {
        byte[] block = FlateEncodeFilter.Encode(Samples);
        byte[] cut = block.Take(block.Length - 6).ToArray();

        var r = Convert(Program(FourGrey, "/FlateDecode filter", cut, after: ""));

        Assert.False(r.IsClean);
        Assert.Contains("FlateDecode", r.LastError, StringComparison.Ordinal);
        Assert.Contains("end-of-data marker", r.LastError, StringComparison.Ordinal);
        ContentStreamAssert.DoesNotContainOperator(r.Content, "Do");
    }

    /// <summary>
    /// A zlib trailer that disagrees with the bytes produced is corruption the inflater cannot see.
    /// The end is still where the framing puts it, so the image is drawn and the program resumes —
    /// and the disagreement is reported, as a truncated base-85-carried Flate source is.
    /// </summary>
    [Fact]
    public void AFlateBlockWithACorruptTrailer_IsDrawnAndReported()
    {
        byte[] block = FlateEncodeFilter.Encode(Samples);
        block[block.Length - 1] ^= 0xFF;

        var r = Convert(Program(FourGrey, "/FlateDecode filter", block));

        Assert.Equal(Samples, Image(r).RawData);
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
        Assert.Equal(0.25, ContentStreamAssert.FirstOperandsOf(r.Content, "g")[0], 6);
        Assert.False(r.IsClean);
        Assert.Contains("a /FlateDecode data source was truncated", r.LastError, StringComparison.Ordinal);
        Assert.Contains("disagrees", r.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A filter whose end this converter cannot find is refused by name. <c>/CCITTFaxDecode</c> was
    /// that filter until its end was measured (<c>CcittInlineTests</c>); <c>/JBIG2Decode</c>, whose
    /// data is a segment stream with no end of its own here, is one still.
    /// </summary>
    [Fact]
    public void AFilterWhoseEndIsNotFound_IsRefusedByName()
    {
        var r = Convert(Program(FourGrey, "/JBIG2Decode filter", new byte[] { 0x00, 0x10, 0x01 }, after: ""));

        Assert.Contains("currentfile /JBIG2Decode filter (no end-of-data marker in the byte stream)",
                        r.UnimplementedOperators);
        ContentStreamAssert.DoesNotContainOperator(r.Content, "Do");
    }

    // ------------------------------------------------------------------ DCTDecode

    /// <summary>
    /// A one-component JPEG codestream: SOI; an APP1 whose payload holds a thumbnail's
    /// <c>FF D9</c>; a scan header; entropy-coded data with a stuffed <c>FF 00</c> and a restart
    /// marker; <c>FF</c> fill; EOI.
    /// </summary>
    private static byte[] Jpeg() => new byte[]
    {
        0xFF, 0xD8,
        0xFF, 0xE1, 0x00, 0x0C, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0xFF, 0xD9, 0x00, 0x00,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
        0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0x78,
        0xFF, 0xFF, 0xD9,
    };

    /// <summary>
    /// A separation image whose samples are a JPEG is carried in its own space — a JPEG's lossy
    /// samples are tints, though they are no palette indices — its codestream under its own filter and
    /// the program resuming after it; the same image as raw samples is carried as an indexed one.
    /// </summary>
    [Fact]
    public void ASeparationImageWithADctSource_IsCarriedAsASeparation_AndTheProgramResumes()
    {
        const string separation = "[/Separation /Spot /DeviceGray {1 exch sub}] setcolorspace ";
        // The dictionary form, which paints in the current colour space: the operand form is DeviceGray (PLRM §8.2).
        var r = Convert(Program(separation + "<< /ImageType 1 /Width 100 /Height 100 /BitsPerComponent 8 " +
                                "/ImageMatrix [100 0 0 -100 0 100] /DataSource currentfile ", "/DCTDecode filter >>", Jpeg()));
        var image = (PdfStream)((PdfDictionary)r.Resources[PdfName.XObject])[new PdfName("Im1")];
        Assert.Equal("DCTDecode", ((PdfName)image[PdfName.Filter]).Value);
        var space = (PdfArray)image[PdfName.ColorSpace];
        Assert.Equal("Separation", ((PdfName)space[0]).Value);
        Assert.Equal("Spot", ((PdfName)space[1]).Value);
        Assert.Contains("0.25 g", Encoding.Latin1.GetString(r.Content), StringComparison.Ordinal);

        var raw = PostScriptConversion.ConvertFragment(Bytes(separation + "<< /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <80> >> image"));
        var rawImage = (PdfStream)((PdfDictionary)raw.Resources[PdfName.XObject])[new PdfName("Im1")];
        Assert.Equal("Indexed", ((PdfName)((PdfArray)rawImage[PdfName.ColorSpace])[0]).Value);
    }

    /// <summary>
    /// An image in a space this converter refuses consumes its inline block, so the program resumes
    /// after it rather than reading the data as program — a CIE-based image, with a fill after it.
    /// </summary>
    [Fact]
    public void ARefusedSpacesInlineImage_ConsumesItsBlock_AndTheProgramResumes()
    {
        var r = PostScriptConversion.ConvertFragment(Bytes(
            "[/CIEBasedA << >>] setcolorspace << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1] /DataSource currentfile /ASCIIHexDecode filter >> image\n" +
            "00FF>\n0.25 setgray newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill"));
        Assert.False(r.Resources?[PdfName.XObject] is PdfDictionary xobjects && xobjects.Count > 0);
        Assert.Contains("CIEBasedA", r.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("0.25 g", Encoding.Latin1.GetString(r.Content), StringComparison.Ordinal);
    }

    /// <summary>
    /// A JPEG is carried, not decoded: the image's bytes are the codestream and its <c>/Filter</c>
    /// is <c>/DCTDecode</c>, as a PDF writer embeds a JPEG — and the program resumes after the EOI.
    /// </summary>
    [Fact]
    public void ADctSource_IsCarriedAsAJpeg_UnderItsOwnFilter()
    {
        var r = Convert(Program(JpegGrey, "/DCTDecode filter", Jpeg()));

        var image = Image(r);
        Assert.Equal(Jpeg(), image.RawData);
        Assert.Equal(PdfName.DCTDecode, image[PdfName.Filter]);
        Assert.Equal(8, ((PdfNumber)image[PdfName.BitsPerComponent]).IntValue);
        Assert.Equal(100, ((PdfNumber)image[PdfName.Width]).IntValue);
        AssertDrawnAsWritten(r);
    }

    /// <summary>The shape Photoshop writes: base-85 outside, JPEG inside.</summary>
    [Fact]
    public void ADctSourceUnderAscii85_IsCarriedAsAJpeg()
    {
        var r = Convert(Program(JpegGrey, "/ASCII85Decode filter /DCTDecode filter", Bytes(Base85(Jpeg()) + "~>")));

        var image = Image(r);
        Assert.Equal(Jpeg(), image.RawData);
        Assert.Equal(PdfName.DCTDecode, image[PdfName.Filter]);
        AssertDrawnAsWritten(r);
    }

    /// <summary>
    /// Nothing can be applied to a JPEG's samples here, so a stage over <c>/DCTDecode</c> is
    /// refused by name — with the block consumed, so the program still resumes after it.
    /// </summary>
    [Fact]
    public void AStageOverADctSource_IsRefused_AndTheBlockConsumed()
    {
        var r = Convert(Program(JpegGrey, "/DCTDecode filter /FlateDecode filter", Jpeg()));

        Assert.Contains("filter /FlateDecode (over a /DCTDecode data source)", r.UnimplementedOperators);
        AssertSkippedButResumed(r);
    }

    /// <summary>A JPEG cannot be a stencil mask; the request is refused, the block consumed.</summary>
    [Fact]
    public void ADctSourceForAStencilMask_IsRefused_AndTheBlockConsumed()
    {
        byte[] program = Bytes("1 1 true [1 0 0 -1 0 1] currentfile /DCTDecode filter imagemask\n")
            .Concat(Jpeg()).Concat(Bytes(Afterwards)).ToArray();

        var r = Convert(program);

        Assert.False(r.IsClean);
        Assert.Contains("which a JPEG cannot carry", r.LastError, StringComparison.Ordinal);
        AssertSkippedButResumed(r);
    }

    /// <summary>Adobe base-85 (no <c>z</c> shorthand), without its <c>~&gt;</c>.</summary>
    private static string Base85(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (k < n ? data[i + k] : (byte)0);
            var digits = new char[5];
            for (int k = 4; k >= 0; k--) { digits[k] = (char)('!' + v % 85); v /= 85; }
            sb.Append(digits, 0, n + 1);
        }
        return sb.ToString();
    }
}
