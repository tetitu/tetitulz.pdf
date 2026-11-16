// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// What a Word document printed through the Microsoft PS Class Driver needed: an image fed through
/// <c>{ … } /FlateDecode filter</c> — a filter whose data source is a procedure cycling through the
/// strings it was given (PLRM §3.13.1) — in the DeviceRGB space <c>setrgbcolor</c> established (PLRM
/// §4.8.2), and a landscape page drawn turned on portrait paper, turned back by its text
/// (<see cref="PostScriptAutoRotate.PageByPage"/>, Distiller's <c>AutoRotatePages</c>).
/// </summary>
public sealed class PrintDriverOutputTests
{
    private static PostScriptConversionResult Convert(string ps, PostScriptConversionOptions? options = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps), options);

    private static List<PdfStream> Images(PostScriptConversionResult r)
    {
        var images = new List<PdfStream>();
        if (r.Resources?[PdfName.XObject] is PdfDictionary xobjects)
            foreach (var kv in xobjects)
                if (kv.Value is PdfStream stream) images.Add(stream);
        return images;
    }

    private static readonly byte[] Samples = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120 };   // 4 × 1 RGB

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal)) z.Write(data);
        return output.ToArray();
    }

    private static string Hex(byte[] bytes) => "<" + System.Convert.ToHexString(bytes) + ">";

    // The driver's shape: the compressed data split into strings held in an array, and a procedure
    // that answers them in turn, round and round — it never answers an empty string.
    private static string CyclingImage(byte[] compressed, int pieces)
    {
        int size = (compressed.Length + pieces - 1) / pieces;
        var strings = Enumerable.Range(0, pieces).Select(k => compressed.Skip(k * size).Take(size).ToArray()).Where(s => s.Length > 0);
        return "/chunks [" + string.Join(" ", strings.Select(Hex)) + "] def /n 0 def\n" +
               "0 0 0 setrgbcolor\n" +
               "<< /ImageType 1 /Width 4 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [4 0 0 -1 0 1]\n" +
               "   /DataSource { chunks n get /n n 1 add chunks length mod def } /FlateDecode filter >> image\n";
    }

    /// <summary>
    /// A filter a cycling procedure feeds gives the image its samples: the procedure's strings
    /// decoded through Flate, gathering stopped once the samples are all there — in DeviceRGB, the
    /// space <c>setrgbcolor</c> set — and nothing is said.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AFilterAProcedureFeeds_GivesTheImageItsSamples(int pieces)
    {
        var r = Convert(CyclingImage(Zlib(Samples), pieces));
        PdfStream image = Assert.Single(Images(r));
        Assert.Equal(Samples, image.RawData);
        Assert.Equal("DeviceRGB", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// Gathering stops once the samples are all there — here the whole deflate data, its Adler-32
    /// trailer in a later string never asked for — and a trailer not reached is not a truncation:
    /// nothing is said.
    /// </summary>
    [Fact]
    public void SamplesAllThereBeforeTheTrailer_AreNotATruncation()
    {
        byte[] compressed = Zlib(Samples);
        string strings = Hex(compressed[..^4]) + " " + Hex(compressed[^4..]);
        var r = Convert("/chunks [" + strings + "] def /n 0 def 0 0 0 setrgbcolor\n" +
                        "<< /ImageType 1 /Width 4 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [4 0 0 -1 0 1]\n" +
                        "   /DataSource { chunks n get /n n 1 add chunks length mod def } /FlateDecode filter >> image\n");
        Assert.Equal(Samples, Assert.Single(Images(r)).RawData);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>A procedure that ends — an empty string — ends the data too; a short image is padded and said.</summary>
    [Fact]
    public void AProcedureThatEnds_EndsTheData_AShortImageSaid()
    {
        byte[] compressed = Zlib(Samples.Take(6).ToArray());
        var r = Convert("/given false def 0 0 0 setrgbcolor\n" +
                        "<< /ImageType 1 /Width 4 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [4 0 0 -1 0 1]\n" +
                        $"   /DataSource {{ given {{ () }} {{ /given true def {Hex(compressed)} }} ifelse }} /FlateDecode filter >> image\n");
        PdfStream image = Assert.Single(Images(r));
        Assert.Equal(Samples.Take(6).Concat(new byte[6]), image.RawData);
        Assert.Contains("the remainder was padded", r.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Hostile: a procedure cycling through data that never decodes is stopped by the allocation
    /// budget — the gathering is charged — and the conversion returns.
    /// </summary>
    [Fact]
    public void ACyclingProcedureThatNeverDecodes_IsStoppedByTheBudget_HostileInput()
    {
        var r = Convert("0 0 0 setrgbcolor\n" +
                        "<< /ImageType 1 /Width 4 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [4 0 0 -1 0 1]\n" +
                        "   /DataSource { (not a zlib stream at all) } /FlateDecode filter >> image\n",
                        new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1 << 20 });
        Assert.True(r.BudgetExhausted);
        Assert.Empty(Images(r));
    }

    /// <summary>A string is a filter's data source too: its bytes, decoded.</summary>
    [Fact]
    public void AStringSource_IsDecodedThroughTheFilter()
    {
        var r = Convert("0 0 0 setrgbcolor\n<< /ImageType 1 /Width 4 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] " +
                        $"/ImageMatrix [4 0 0 -1 0 1] /DataSource {Hex(Zlib(Samples))} /FlateDecode filter >> image\n");
        Assert.Equal(Samples, Assert.Single(Images(r)).RawData);
    }

    /// <summary>A minimal JPEG codestream: SOI, an APP1 holding the bytes of an EOI, the scan with a stuffed byte and a restart marker, EOI.</summary>
    private static readonly byte[] Jpeg =
    {
        0xFF, 0xD8,
        0xFF, 0xE1, 0x00, 0x0C, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0xFF, 0xD9, 0x00, 0x00,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
        0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0x78,
        0xFF, 0xD9,
    };

    private static string ImageThrough(string dataSource)
        => "0 0 0 setrgbcolor\n<< /ImageType 1 /Width 4 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] " +
           $"/ImageMatrix [4 0 0 -1 0 1] /DataSource {dataSource} >> image\n";

    /// <summary>
    /// A JPEG a cycling procedure feeds — the driver's shape — is carried as the image's
    /// <c>/DCTDecode</c> stream: the codestream through its EOI marker, as a JPEG read from the file
    /// is, the procedure's strings after it (it starts over) dropped; an EOI inside a marker segment
    /// is not the end. Nothing is said.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void AJpegAProcedureFeeds_IsCarriedThroughItsEoi(int pieces)
    {
        var r = Convert(CyclingImage(Jpeg, pieces).Replace("/FlateDecode filter", "/DCTDecode filter"));
        PdfStream image = Assert.Single(Images(r));
        Assert.Equal("DCTDecode", ((PdfName)image[PdfName.Filter]).Value);
        Assert.Equal(Jpeg, image.RawData);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// A JPEG is far smaller than its samples — 36 bytes for a 64 × 64 RGB image's 12 288 — so its
    /// gathering ends at its EOI, not when as many bytes as the samples have come: the cycling
    /// procedure is not run on to the budget.
    /// </summary>
    [Fact]
    public void AJpegSmallerThanItsSamples_EndsAtItsEoi()
    {
        string ps = CyclingImage(Jpeg, 3).Replace("/FlateDecode filter", "/DCTDecode filter")
                                         .Replace("/Width 4 /Height 1", "/Width 64 /Height 64");
        var r = Convert(ps, new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1 << 20 });
        Assert.False(r.BudgetExhausted);
        Assert.Equal(Jpeg, Assert.Single(Images(r)).RawData);
    }

    /// <summary>
    /// A JPEG under another filter, fed three characters at a time by a cycling procedure, is decoded
    /// whole: a trial over a partial chunk — <c>FFD</c>, which the hex filter pads to <c>FF D0</c> —
    /// is no verdict on the SOI marker, since below the JPEG's stage the trial's first bytes are the
    /// filter's padding, not the data's; the verdict waits for the whole data.
    /// </summary>
    [Fact]
    public void AJpegUnderAnotherFilter_FedThreeCharactersAtATime_IsDecodedToItsCodestream()
    {
        string hex = System.Convert.ToHexString(Jpeg);
        var pieces = Enumerable.Range(0, (hex.Length + 2) / 3)
                               .Select(k => "(" + hex.Substring(k * 3, Math.Min(3, hex.Length - k * 3)) + ")");
        var r = Convert("/chunks [" + string.Join(" ", pieces) + "] def /n 0 def\n" +
                        ImageThrough("{ chunks n get /n n 1 add chunks length mod def } /ASCIIHexDecode filter /DCTDecode filter"));

        Assert.Equal(Jpeg, Assert.Single(Images(r)).RawData);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// Data that is no JPEG under another filter, fed by a procedure that never ends, is known for it
    /// once the decoded data runs past any partial chunk's padding — it ends there and is said, rather
    /// than being gathered until the allocation budget runs out.
    /// </summary>
    [Fact]
    public void NoJpegUnderAnotherFilter_FromACyclingProcedure_EndsEarly()
    {
        var r = Convert(ImageThrough("{ (41424344) } /ASCIIHexDecode filter /DCTDecode filter"),
                        new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1 << 20 });

        Assert.False(r.BudgetExhausted);
        Assert.Empty(Images(r));
        Assert.Contains("does not begin with a JPEG's SOI marker", r.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>The filters below the JPEG's are decoded first: a hex-encoded codestream a procedure feeds.</summary>
    [Fact]
    public void AJpegUnderAnotherFilter_IsDecodedToItsCodestream()
    {
        string hex = "(" + System.Convert.ToHexString(Jpeg) + ">)";
        var r = Convert(ImageThrough("{ " + hex + " } /ASCIIHexDecode filter /DCTDecode filter"));
        Assert.Equal(Jpeg, Assert.Single(Images(r)).RawData);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// What gives no whole codestream draws nothing and says why: data that does not begin with the
    /// SOI marker — known from its first two bytes, so a procedure that never ends is not run on to
    /// the budget — a JPEG the procedure ends before its EOI, and a filter applied over the JPEG.
    /// </summary>
    [Theory]
    [InlineData("{ (x) } /DCTDecode filter", "does not begin with a JPEG's SOI marker")]
    [InlineData("{ given { () } { /given true def <FFD8FFDA0008010100003F001234> } ifelse } /DCTDecode filter", "ended before its EOI marker")]
    [InlineData("{ (x) } /DCTDecode filter /FlateDecode filter", "over a /DCTDecode data source")]
    public void DataThatIsNoWholeJpeg_DrawsNothing_AndSaysWhy(string dataSource, string why)
    {
        var r = Convert("/given false def\n" + ImageThrough(dataSource));
        Assert.Empty(Images(r));
        Assert.False(r.BudgetExhausted);
        string said = (r.LastError ?? "") + string.Join("|", r.UnimplementedOperators);
        Assert.Contains(why, said, StringComparison.Ordinal);
    }

    /// <summary>Read other than by an image — <c>readstring</c> — a procedure-fed filter is refused by name, and reads nothing.</summary>
    [Fact]
    public void AProcedureFedFilter_ReadByAnythingButAnImage_IsRefusedByName()
    {
        var r = Convert("/f { (abc) } /ASCIIHexDecode filter def f 3 string readstring pop length 0 eq " +
                        "{ newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill } if");
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
        Assert.Contains(r.UnimplementedOperators, name => name.StartsWith("a filter whose data source is a procedure", StringComparison.Ordinal));
    }

    // ==================================================================
    //  The colour operators set the colour space (PLRM §4.8.2)
    // ==================================================================

    /// <summary><c>setcmykcolor</c> makes an image dictionary's samples CMYK; <c>setgray</c> grey — each the operator's own family.</summary>
    [Theory]
    [InlineData("0 0 0 1 setcmykcolor", 4, "DeviceCMYK")]
    [InlineData("0 setgray", 1, "DeviceGray")]
    public void AColourOperator_SetsTheImagesSpace(string setColour, int components, string space)
    {
        byte[] samples = Enumerable.Range(1, 4 * components).Select(b => (byte)b).ToArray();
        string decode = string.Join(" ", Enumerable.Repeat("0 1", components));
        var r = Convert(setColour + $"\n<< /ImageType 1 /Width 4 /Height 1 /BitsPerComponent 8 /Decode [{decode}] " +
                        $"/ImageMatrix [4 0 0 -1 0 1] /DataSource {Hex(samples)} >> image\n");
        PdfStream image = Assert.Single(Images(r));
        Assert.Equal(space, ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Equal(samples, image.RawData);
    }

    /// <summary>
    /// After <c>setgray</c> the space a <c>setcolorspace</c> named is gone: <c>currentcolorspace</c>
    /// answers DeviceGray, not the palette, and <c>setcolor</c> after <c>setrgbcolor</c> takes three.
    /// </summary>
    [Fact]
    public void AColourOperator_ReplacesTheSpaceSetcolorspaceNamed()
    {
        var gray = Convert("[/Indexed /DeviceRGB 1 <000000FFFFFF>] setcolorspace 1 setcolor 0.5 setgray " +
                           "currentcolorspace 0 get /DeviceGray eq currentcolor 0.5 eq and " +
                           "{ newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill } if");
        Assert.Contains("f", ContentStreamAssert.Operators(gray.Content));

        var rgb = Convert("0 0 0 setrgbcolor 1 0 0 setcolor newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill");
        Assert.Contains("1 0 0 rg", ContentStreamAssert.Parse(rgb.Content).Select(o => o.ToString()));
        Assert.True(rgb.IsClean, rgb.LastError);
    }

    // ==================================================================
    //  AutoRotatePages
    // ==================================================================

    private static (PdfDocument Doc, PostScriptDocumentResult Result) Document(string ps, PostScriptAutoRotate rotate)
    {
        using var source = new MemoryStream(Encoding.Latin1.GetBytes(ps));
        var output = new MemoryStream();
        var result = PostScriptConversion.ConvertDocument(source, output, new PostScriptConversionOptions { AutoRotatePages = rotate });
        return (PdfDocument.Open(new MemoryStream(output.ToArray())), result);
    }

    private static (double Width, double Height) Box(PdfDocument doc)
    {
        var box = doc.Pages[0].MediaBox!;
        double N(int i) => ((PdfNumber)box[i]).DoubleValue;
        return (N(2) - N(0), N(3) - N(1));
    }

    private static string FirstLine(PdfDocument doc)
        => Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData() ?? Array.Empty<byte>()).Split('\n')[0];

    // A portrait A4 page, as a driver sets it, and a line of text under a turn of the page.
    private static string TurnedPage(string turn)
        => "<< /PageSize [595 842] >> setpagedevice /Helvetica findfont 20 scalefont setfont\n" +
           $"gsave 300 400 translate {turn} rotate 0 0 moveto (Landscape text on a portrait page) show grestore showpage\n";

    /// <summary>
    /// Text running up the page — a driver's landscape page — is turned clockwise: the box becomes
    /// 842 × 595 and the content is drawn under <c>0 -1 1 0 0 595 cm</c>; running down, the other way;
    /// upside down, half a turn on the same box.
    /// </summary>
    [Theory]
    [InlineData(90, 842, 595, "0 -1 1 0 0 595 cm")]
    [InlineData(-90, 842, 595, "0 1 -1 0 842 0 cm")]
    [InlineData(180, 595, 842, "-1 0 0 -1 595 842 cm")]
    public void PageByPage_TurnsThePageByItsText(int turn, double width, double height, string cm)
    {
        var (doc, _) = Document(TurnedPage(turn.ToString()), PostScriptAutoRotate.PageByPage);
        using (doc)
        {
            Assert.Equal((width, height), Box(doc));
            Assert.Equal(cm, FirstLine(doc));
        }
    }

    /// <summary>The controls: upright text is not turned, nor is anything with <see cref="PostScriptAutoRotate.None"/>, the default.</summary>
    [Fact]
    public void UprightText_AndTheDefault_LeaveThePageAsDrawn()
    {
        var (upright, _) = Document(TurnedPage("0"), PostScriptAutoRotate.PageByPage);
        using (upright) Assert.Equal((595.0, 842.0), Box(upright));

        var (none, _) = Document(TurnedPage("90"), PostScriptAutoRotate.None);
        using (none)
        {
            Assert.Equal((595.0, 842.0), Box(none));
            Assert.DoesNotContain(" cm", FirstLine(none), StringComparison.Ordinal);
        }

        // The last page, ended by the program's end rather than showpage, follows the option too.
        var (trailing, _) = Document(TurnedPage("90").Replace(" showpage", ""), PostScriptAutoRotate.None);
        using (trailing) Assert.Equal((595.0, 842.0), Box(trailing));
        var (turned, _) = Document(TurnedPage("90").Replace(" showpage", ""), PostScriptAutoRotate.PageByPage);
        using (turned) Assert.Equal((842.0, 595.0), Box(turned));
    }

    /// <summary>
    /// The document writer deflates what it built raw — the page's content and an image's samples,
    /// each under <c>/FlateDecode</c> and smaller than its samples — and a reader decodes them back.
    /// </summary>
    [Fact]
    public void TheDocumentWriter_DeflatesContentAndImages()
    {
        string ps = "<< /PageSize [200 200] >> setpagedevice 0 0 0 setrgbcolor 40 40 scale\n" +
                    "<< /ImageType 1 /Width 64 /Height 64 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] " +
                    "/ImageMatrix [64 0 0 -64 0 64] /DataSource " + Hex(new byte[64 * 64 * 3]) + " >> image showpage\n";
        var (doc, _) = Document(ps, PostScriptAutoRotate.None);
        using (doc)
        {
            var page = doc.Pages[0];
            var image = (PdfStream)((PdfDictionary)page.Resources![PdfName.XObject]!).Select(kv => doc.ResolveObject(kv.Value)).Single();
            Assert.Equal("FlateDecode", ((PdfName)image[PdfName.Filter]).Value);
            Assert.True(image.RawData.Length < 64 * 64 * 3 / 10, $"{image.RawData.Length} bytes");
            Assert.Equal(new byte[64 * 64 * 3], image.GetDecodedData());
            Assert.Contains(" Do", Encoding.ASCII.GetString(page.GetContentStreamData() ?? Array.Empty<byte>()), StringComparison.Ordinal);
        }
    }

    /// <summary>A JPEG keeps its own filter and its bytes: a stream with a <c>/Filter</c> is not deflated again.</summary>
    [Fact]
    public void AJpeg_IsWrittenAsItCame()
    {
        byte[] jpeg =
        {
            0xFF, 0xD8,
            0xFF, 0xE1, 0x00, 0x0C, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0xFF, 0xD9, 0x00, 0x00,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
            0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0x78,
            0xFF, 0xFF, 0xD9,
        };
        // Long enough that deflating it would be smaller, so only the /Filter keeps it as it came.
        byte[] padded = jpeg[..^2].Concat(new byte[256]).Concat(jpeg[^2..]).ToArray();
        string ps = "<< /PageSize [100 100] >> setpagedevice 50 50 scale 1 1 8 [1 0 0 -1 0 1] currentfile /DCTDecode filter image\n" +
                    Encoding.Latin1.GetString(padded) + "\nshowpage\n";
        var (doc, _) = Document(ps, PostScriptAutoRotate.None);
        using (doc)
        {
            var image = (PdfStream)((PdfDictionary)doc.Pages[0].Resources![PdfName.XObject]!).Select(kv => doc.ResolveObject(kv.Value)).Single();
            Assert.Equal("DCTDecode", ((PdfName)image[PdfName.Filter]).Value);
            Assert.Equal(padded, image.RawData);
        }
    }

    /// <summary>A page with no text keeps its orientation: a picture drawn turned is not judged.</summary>
    [Fact]
    public void APageWithNoText_IsNotTurned()
    {
        var (doc, _) = Document("<< /PageSize [595 842] >> setpagedevice 90 rotate 0 -100 100 100 rectfill showpage",
                                PostScriptAutoRotate.PageByPage);
        using (doc) Assert.Equal((595.0, 842.0), Box(doc));
    }
}
