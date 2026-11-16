// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>ImageType 4</c> (a colour key) and <c>ImageType 3</c> (an explicit mask, its three interleave
/// types) — PLRM §4.10.6 — as PDF's colour-key <c>/Mask</c> ranges and stencil <c>/Mask</c> image
/// (ISO 32000-2 §8.9.6.3, §8.9.6.4). Both were reported and skipped, so a masked picture was missing.
/// </summary>
public sealed class ImageMaskTypeTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private static PdfStream OnlyImage(PostScriptConversionResult r)
        => r.Resources?[PdfName.XObject] is PdfDictionary x && x.Count == 1 ? (PdfStream)x[new PdfName("Im1")] : null;

    private static string[] Ops(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Select(o => o.ToString()).ToArray();

    private static double[] Numbers(PdfObject array)
        => ((PdfArray)array).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray();

    private const string GrayData =
        " /DataDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2] /DataSource <10203040> >>";

    // ------------------------------------------------------------------
    //  ImageType 4
    // ------------------------------------------------------------------

    /// <summary>One value a component is the range from it to itself.</summary>
    [Fact]
    public void AMaskColorValue_IsAColourKeyRangeOfOne()
    {
        var image = OnlyImage(Convert("<< /ImageType 4 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1]" +
                                      " /DataSource <00FF> /MaskColor [255] >> image"));
        Assert.Equal(new double[] { 255, 255 }, Numbers(image[PdfName.Mask]));
        Assert.Equal(new byte[] { 0x00, 0xFF }, image.RawData);
    }

    /// <summary>Ranges are kept, each value held to the samples' range: below 0 to 0, above the depth's maximum to it.</summary>
    [Fact]
    public void MaskColorRanges_AreKept_HeldToTheSampleRange()
    {
        var image = OnlyImage(Convert("/DeviceRGB setcolorspace << /ImageType 4 /Width 1 /Height 1 /BitsPerComponent 8" +
                                      " /ImageMatrix [1 0 0 -1 0 1] /DataSource <010203> /MaskColor [-5 10 0 300 4 4] >> image"));
        Assert.Equal(new double[] { 0, 10, 0, 255, 4, 4 }, Numbers(image[PdfName.Mask]));
        Assert.Equal("DeviceRGB", ((PdfName)image[PdfName.ColorSpace]).Value);
    }

    /// <summary>At four bits the maximum is 15, not 255: the depth sets the range.</summary>
    [Fact]
    public void MaskColorAtFourBits_IsHeldToFifteen()
    {
        var image = OnlyImage(Convert("<< /ImageType 4 /Width 2 /Height 1 /BitsPerComponent 4 /ImageMatrix [2 0 0 -1 0 1]" +
                                      " /DataSource <0F> /MaskColor [3 99] >> image"));
        Assert.Equal(new double[] { 3, 15 }, Numbers(image[PdfName.Mask]));
    }

    /// <summary>
    /// A <c>/MaskColor</c> of the wrong arity skips the image with its inline block consumed, so the
    /// program resumes after it; a right one is the control.
    /// </summary>
    [Fact]
    public void AMaskColorOfTheWrongArity_SkipsTheImage_AndConsumesItsBlock()
    {
        const string Head = "<< /ImageType 4 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1]" +
                            " /DataSource currentfile /ASCIIHexDecode filter /MaskColor ";
        var refused = Convert(Head + "[1 2 3] >> image\n7F>\n0.25 setgray" + Marker);
        Assert.Null(refused.Resources?[PdfName.XObject]);
        Assert.Contains("0.25 g", Ops(refused));
        // Still the last thing said: the block read as program would have said more after it.
        Assert.Contains("/MaskColor has 3 entries", refused.LastError, System.StringComparison.Ordinal);

        var drawn = Convert(Head + "[1] >> image\n7F>\n0.25 setgray" + Marker);
        Assert.Equal(new byte[] { 0x7F }, OnlyImage(drawn).RawData);
        Assert.Contains("0.25 g", Ops(drawn));
    }

    /// <summary>An ImageType 4 dictionary with no <c>/MaskColor</c> is skipped, not drawn unmasked.</summary>
    [Fact]
    public void AnImageType4WithNoMaskColor_IsSkipped()
    {
        var r = Convert("<< /ImageType 4 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <7F> >> image" +
                        " 0.25 setgray" + Marker);
        Assert.Null(r.Resources?[PdfName.XObject]);
        Assert.Contains("0.25 g", Ops(r));
    }

    // ------------------------------------------------------------------
    //  ImageType 3
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>InterleaveType 3</c>: the mask has its own source and size; it becomes the image's stencil
    /// <c>/Mask</c>, its <c>/Decode</c> kept, the data's samples untouched.
    /// </summary>
    [Fact]
    public void ASeparateSourceMask_IsTheImagesStencil()
    {
        var image = OnlyImage(Convert("<< /ImageType 3 /InterleaveType 3" + GrayData +
                                      " /MaskDict << /ImageType 1 /Width 4 /Height 4 /BitsPerComponent 1 /ImageMatrix [4 0 0 -4 0 4]" +
                                      " /DataSource <F0F00F0F> /Decode [1 0] >> >> image"));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, image.RawData);
        var mask = (PdfStream)image[PdfName.Mask];
        Assert.Equal(PdfBoolean.True, mask[PdfName.ImageMask]);
        Assert.Equal(4, ((PdfNumber)mask[PdfName.Width]).IntValue);
        Assert.Equal(4, ((PdfNumber)mask[PdfName.Height]).IntValue);
        Assert.Equal(new byte[] { 0xF0, 0xF0, 0x0F, 0x0F }, mask.RawData);
        Assert.Equal(new double[] { 1, 0 }, Numbers(mask[PdfName.Decode]));
    }

    /// <summary>A mask with no <c>/Decode</c> keeps PDF's default, which is imagemask's: no entry.</summary>
    [Fact]
    public void AMaskWithNoDecode_WritesNone()
    {
        var image = OnlyImage(Convert("<< /ImageType 3 /InterleaveType 3" + GrayData +
                                      " /MaskDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 1 /ImageMatrix [2 0 0 -2 0 2]" +
                                      " /DataSource <8040> >> >> image"));
        var mask = (PdfStream)image[PdfName.Mask];
        Assert.Null(mask[PdfName.Decode]);
        Assert.Equal(new byte[] { 0x80, 0x40 }, mask.RawData);
    }

    /// <summary>Procedure sources for the mask and the data run in turn, mask first, each reading its row from the program.</summary>
    [Fact]
    public void ProcedureSources_ForTheMaskAndTheData_RunInTurn()
    {
        var image = OnlyImage(Convert("/m 1 string def /d 2 string def << /ImageType 3 /InterleaveType 3" +
                                      " /DataDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /ImageMatrix [2 0 0 -2 0 2]" +
                                      " /DataSource { currentfile d readhexstring pop } >>" +
                                      " /MaskDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 1 /ImageMatrix [2 0 0 -2 0 2]" +
                                      " /DataSource { currentfile m readhexstring pop } >> >> image\n80\n1020\n40\n3040\n"));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, image.RawData);
        Assert.Equal(new byte[] { 0x80, 0x40 }, ((PdfStream)image[PdfName.Mask]).RawData);
    }

    /// <summary>
    /// <c>InterleaveType 1</c>: each pixel's first sample is the mask's — any value but 0 sets the bit,
    /// as the reference renderer reads it — and the rest the data's.
    /// </summary>
    [Fact]
    public void AChunkyMask_IsEachPixelsFirstSample()
    {
        var image = OnlyImage(Convert("<< /ImageType 3 /InterleaveType 1" +
                                      " /DataDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1] /DataSource <01100020> >>" +
                                      " /MaskDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1] >> >> image"));
        Assert.Equal(new byte[] { 0x10, 0x20 }, image.RawData);
        Assert.Equal(new byte[] { 0x80 }, ((PdfStream)image[PdfName.Mask]).RawData);
        Assert.Equal(1, ((PdfNumber)((PdfStream)image[PdfName.Mask])[PdfName.BitsPerComponent]).IntValue);
    }

    /// <summary>
    /// Below a byte the samples are taken apart bit by bit: at four bits, mask 2 (its high bit clear)
    /// still sets the bit, and the data's nibbles close up into one byte.
    /// </summary>
    [Fact]
    public void AChunkyMaskBelowAByte_IsTakenApartBitByBit()
    {
        var image = OnlyImage(Convert("<< /ImageType 3 /InterleaveType 1" +
                                      " /DataDict << /ImageType 1 /Width 3 /Height 1 /BitsPerComponent 4 /ImageMatrix [3 0 0 -1 0 1] /DataSource <2A0B1C> >>" +
                                      " /MaskDict << /ImageType 1 /Width 3 /Height 1 /BitsPerComponent 4 /ImageMatrix [3 0 0 -1 0 1] >> >> image"));
        Assert.Equal(new byte[] { 0xAB, 0xC0 }, image.RawData);
        Assert.Equal(new byte[] { 0xA0 }, ((PdfStream)image[PdfName.Mask]).RawData);
    }

    /// <summary><c>InterleaveType 2</c> with twice the data's height: two mask rows, then a data row.</summary>
    [Fact]
    public void ScanLineMaskRows_LeadTheDataRows_InProportion()
    {
        var image = OnlyImage(Convert("<< /ImageType 3 /InterleaveType 2" +
                                      " /DataDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1] /DataSource <AA551020> >>" +
                                      " /MaskDict << /ImageType 1 /Width 8 /Height 2 /BitsPerComponent 1 /ImageMatrix [8 0 0 -2 0 2] >> >> image"));
        Assert.Equal(new byte[] { 0x10, 0x20 }, image.RawData);
        Assert.Equal(new byte[] { 0xAA, 0x55 }, ((PdfStream)image[PdfName.Mask]).RawData);
    }

    /// <summary><c>InterleaveType 2</c> with half the data's height: a mask row, then two data rows, twice.</summary>
    [Fact]
    public void ScanLineDataRows_FollowTheirMaskRow_InProportion()
    {
        var image = OnlyImage(Convert("<< /ImageType 3 /InterleaveType 2" +
                                      " /DataDict << /ImageType 1 /Width 1 /Height 4 /BitsPerComponent 8 /ImageMatrix [1 0 0 -4 0 4] /DataSource <801020403020> >>" +
                                      " /MaskDict << /ImageType 1 /Width 1 /Height 2 /BitsPerComponent 1 /ImageMatrix [1 0 0 -2 0 2] >> >> image"));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x20 }, image.RawData);
        Assert.Equal(new byte[] { 0x80, 0x40 }, ((PdfStream)image[PdfName.Mask]).RawData);
    }

    /// <summary>Under <c>InterleaveType 1</c> a mask of another size is a <c>rangecheck</c>; the data's size is the control.</summary>
    [Fact]
    public void AChunkyMaskOfAnotherSize_IsARangecheck()
    {
        static string Program(int maskWidth) =>
            "{ << /ImageType 3 /InterleaveType 1" +
            " /DataDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1] /DataSource <01100020> >>" +
            $" /MaskDict << /ImageType 1 /Width {maskWidth} /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 -1 0 1] >> >> image }} stopped {{" +
            " $error /errorname get /rangecheck eq {" + Marker + "} if } if";
        Assert.Contains("f", Ops(Convert(Program(3))));
        var drawn = Convert(Program(2));
        Assert.DoesNotContain("f", Ops(drawn));
        Assert.NotNull(OnlyImage(drawn));
    }

    /// <summary>Under <c>InterleaveType 2</c> heights neither of which divides the other are a <c>rangecheck</c>.</summary>
    [Fact]
    public void ScanLineHeightsThatDoNotDivide_AreARangecheck()
    {
        static string Program(int maskHeight) =>
            "{ << /ImageType 3 /InterleaveType 2" +
            " /DataDict << /ImageType 1 /Width 1 /Height 2 /BitsPerComponent 8 /ImageMatrix [1 0 0 -2 0 2] /DataSource <0000000000000000> >>" +
            $" /MaskDict << /ImageType 1 /Width 1 /Height {maskHeight} /BitsPerComponent 1 /ImageMatrix [1 0 0 -1 0 1] >> >> image }} stopped {{" +
            " $error /errorname get /rangecheck eq {" + Marker + "} if } if";
        Assert.Contains("f", Ops(Convert(Program(3))));
        Assert.DoesNotContain("f", Ops(Convert(Program(4))));
    }

    /// <summary>
    /// A refused mask's own inline block is consumed with the data's, so the program resumes after
    /// both: a two-bit mask under <c>InterleaveType 3</c> is a <c>rangecheck</c>.
    /// </summary>
    [Fact]
    public void ARefusedMask_ConsumesItsOwnBlock()
    {
        var r = Convert("{ << /ImageType 3 /InterleaveType 3" + GrayData +
                        " /MaskDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 2 /ImageMatrix [2 0 0 -2 0 2]" +
                        " /DataSource currentfile /ASCIIHexDecode filter >> >> image } stopped\nF0F0>\npop 0.25 setgray" + Marker);
        Assert.Null(r.Resources?[PdfName.XObject]);
        Assert.Contains("0.25 g", Ops(r));
        Assert.Contains("f", Ops(r));

        // It says what the same refusal of a mask in a string says, and nothing more: the block read
        // as program would have been reported too.
        var control = Convert("{ << /ImageType 3 /InterleaveType 3" + GrayData +
                              " /MaskDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 2 /ImageMatrix [2 0 0 -2 0 2]" +
                              " /DataSource <F0F0> >> >> image } stopped\npop 0.25 setgray" + Marker);
        Assert.Equal(control.DiagnosticCount, r.DiagnosticCount);
        Assert.Equal(control.LastError, r.LastError);
    }

    /// <summary>The marker is drawn when <paramref name="body"/> stops with a <c>rangecheck</c> — that error, not another.</summary>
    private static string IfRangecheck(string body) =>
        "{ " + body + " } stopped { $error /errorname get /rangecheck eq {" + Marker + "} if } if";

    private static string SeparateMask(string maskEntries, int interleave = 3) =>
        $"<< /ImageType 3 /InterleaveType {interleave}" + GrayData +
        " /MaskDict << /ImageType 1 /BitsPerComponent 1 /ImageMatrix [2 0 0 -2 0 2] " + maskEntries + " >> >> image";

    /// <summary>An interleave type other than 1, 2 or 3 is a <c>rangecheck</c>, and nothing is drawn.</summary>
    [Fact]
    public void AnInterleaveTypeOfFour_IsARangecheck()
    {
        var r = Convert(IfRangecheck(SeparateMask("/Width 2 /Height 2 /DataSource <8040>", interleave: 4)));
        Assert.Contains("f", Ops(r));
        Assert.Null(r.Resources?[PdfName.XObject]);
    }

    /// <summary>Under <c>InterleaveType 3</c> a mask with no source of its own is a <c>rangecheck</c>; one with a source is the control.</summary>
    [Fact]
    public void ASeparateMaskWithNoSource_IsARangecheck()
    {
        Assert.Contains("f", Ops(Convert(IfRangecheck(SeparateMask("/Width 2 /Height 2")))));
        var drawn = Convert(IfRangecheck(SeparateMask("/Width 2 /Height 2 /DataSource <8040>")));
        Assert.DoesNotContain("f", Ops(drawn));
        Assert.NotNull(OnlyImage(drawn));
    }

    /// <summary>A mask of no size, or past the image size limits, is a <c>rangecheck</c> before anything is allocated.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(10_000_000, 1)]
    [InlineData(1, 10_000_000)]
    public void AMaskOfNoSizeOrPastTheLimits_IsARangecheck(int width, int height)
    {
        var r = Convert(IfRangecheck(SeparateMask($"/Width {width} /Height {height} /DataSource <8040>")));
        Assert.Contains("f", Ops(r));
        Assert.Null(r.Resources?[PdfName.XObject]);
    }

    /// <summary>
    /// The data's one source may be a JPEG: carried under <c>/DCTDecode</c>, its stencil beside it,
    /// and the program resuming after the codestream.
    /// </summary>
    [Fact]
    public void AJpegDataSource_IsCarriedWithItsMask()
    {
        byte[] head = Encoding.ASCII.GetBytes("<< /ImageType 3 /InterleaveType 3" +
            " /DataDict << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource currentfile /DCTDecode filter >>" +
            " /MaskDict << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 1 /ImageMatrix [1 0 0 -1 0 1] /DataSource <80> >> >> image\n");
        var r = PostScriptConversion.ConvertFragment(head.Concat(Jpeg).Concat(Encoding.ASCII.GetBytes("\n0.25 setgray" + Marker)).ToArray());
        var image = OnlyImage(r);
        Assert.Equal(PdfName.DCTDecode, image[PdfName.Filter]);
        Assert.Equal(Jpeg, image.RawData);
        Assert.Equal(new byte[] { 0x80 }, ((PdfStream)image[PdfName.Mask]).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary>A mask source that is a JPEG is refused — a stencil is no JPEG — the program resuming after it.</summary>
    [Fact]
    public void AJpegMaskSource_IsRefused()
    {
        byte[] head = Encoding.ASCII.GetBytes("<< /ImageType 3 /InterleaveType 3" +
            " /DataDict << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /DataSource <7F> >>" +
            " /MaskDict << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 1 /ImageMatrix [1 0 0 -1 0 1] /DataSource currentfile /DCTDecode filter >> >> image\n");
        var r = PostScriptConversion.ConvertFragment(head.Concat(Jpeg).Concat(Encoding.ASCII.GetBytes("\n0.25 setgray" + Marker)).ToArray());
        Assert.Null(r.Resources?[PdfName.XObject]);
        Assert.Contains("0.25 g", Ops(r));
        Assert.Contains("mask source", r.LastError, System.StringComparison.Ordinal);
    }

    /// <summary>An ImageType 4 dictionary that says it is a stencil is skipped, its block consumed; without <c>/ImageMask</c> it is drawn.</summary>
    [Fact]
    public void AnImageType4Stencil_IsSkipped_AndConsumesItsBlock()
    {
        const string Head = "<< /ImageType 4 /Width 1 /Height 1 /BitsPerComponent 8 /ImageMatrix [1 0 0 -1 0 1] /MaskColor [0]" +
                            " /DataSource currentfile /ASCIIHexDecode filter ";
        var refused = Convert(Head + "/ImageMask true >> image\n7F>\n0.25 setgray" + Marker);
        Assert.Null(refused.Resources?[PdfName.XObject]);
        Assert.Contains("0.25 g", Ops(refused));
        Assert.NotNull(OnlyImage(Convert(Head + ">> image\n7F>\n0.25 setgray" + Marker)));
    }

    /// <summary>A data dictionary that cannot be used leaves no mask block behind to be read as program.</summary>
    [Fact]
    public void AnUnusableDataDictionary_ConsumesTheMasksBlock()
    {
        var r = Convert("<< /ImageType 3 /InterleaveType 3" +
                        " /DataDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /DataSource <10203040> >>" +
                        " /MaskDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 1 /ImageMatrix [2 0 0 -2 0 2]" +
                        " /DataSource currentfile /ASCIIHexDecode filter >> >> image\nF0F0>\n0.25 setgray" + Marker);
        Assert.Null(r.Resources?[PdfName.XObject]);
        Assert.Contains("0.25 g", Ops(r));
        Assert.Contains("/ImageMatrix", r.LastError, System.StringComparison.Ordinal);
    }

    private static readonly byte[] Jpeg =
    {
        0xFF, 0xD8,
        0xFF, 0xE1, 0x00, 0x0C, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0xFF, 0xD9, 0x00, 0x00,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
        0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0x78,
        0xFF, 0xFF, 0xD9,
    };

    /// <summary>
    /// In a document the stencil is an indirect stream of its own (§7.3.8), which the reader resolves
    /// from the image's <c>/Mask</c>.
    /// </summary>
    [Fact]
    public void InADocument_TheStencilIsAnIndirectStream()
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(
            "%!PS\n<< /ImageType 3 /InterleaveType 3" + GrayData +
            " /MaskDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 1 /ImageMatrix [2 0 0 -2 0 2] /DataSource <8040> >> >> image showpage\n"));
        using var output = new MemoryStream();
        Assert.Equal(1, PostScriptConversion.ConvertDocument(input, output).PageCount);
        var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var xobjects = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]);
        var image = (PdfStream)doc.ResolveObject(xobjects.Single().Value);
        Assert.IsType<PdfReference>(image[PdfName.Mask]);
        var mask = (PdfStream)doc.ResolveObject(image[PdfName.Mask]);
        Assert.Equal(PdfBoolean.True, mask[PdfName.ImageMask]);
        Assert.Equal(2, ((PdfNumber)mask[PdfName.Width]).IntValue);
    }
}
