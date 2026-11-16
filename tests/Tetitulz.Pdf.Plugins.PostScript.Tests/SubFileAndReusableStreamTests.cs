// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>SubFileDecode</c> — data that ends at a byte count or at an end-of-data string, how InDesign and
/// Illustrator delimit an EPS's image and pattern data — and <c>ReusableStreamDecode</c>, which reads
/// its source whole when it is made and is read again after <c>resetfile</c> or
/// <c>setfileposition</c>. Both were refused: <c>SubFileDecode</c>'s string was taken for the
/// source, and the image's bytes were scanned as PostScript.
/// </summary>
public sealed class SubFileAndReusableStreamTests
{
    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    private static List<byte[]> ImageBytes(PostScriptConversionResult r)
    {
        var images = new List<byte[]>();
        if (r.Resources?[PdfName.XObject] is PdfDictionary xobjects)
            foreach (var kv in xobjects)
                if (kv.Value is PdfStream stream) images.Add(stream.RawData);
        return images;
    }

    private const string Image = "4 1 8 [4 0 0 -1 0 1] ";
    private const string After = "newpath 0 0 moveto 9 0 lineto 9 9 lineto fill";

    /// <summary>
    /// The data ends before the end-of-data string, which is consumed: the image is the four bytes and
    /// the program continues after the string.
    /// </summary>
    [Fact]
    public void AnEodString_EndsTheData_AndIsConsumed()
    {
        var r = Convert(Image + "currentfile 0 (%%EndData) /SubFileDecode filter image\nABCD%%EndData\n" + After);
        Assert.Equal(new[] { Encoding.ASCII.GetBytes("ABCD") }, ImageBytes(r));
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>With a count of 1 the first occurrence of the string is data: the second ends it.</summary>
    [Fact]
    public void AnEodCount_PassesThatManyOccurrences()
    {
        var r = Convert("6 1 8 [6 0 0 -1 0 1] currentfile 1 (|) /SubFileDecode filter image\nAB|CDE|" + After);
        Assert.Equal(new[] { Encoding.ASCII.GetBytes("AB|CDE") }, ImageBytes(r));
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
        Assert.True(r.IsClean, r.LastError);   // the string consumed: a '|' left behind scans as an undefined name
    }

    /// <summary>With no string the count is a byte count; the dictionary form says the same as the operands.</summary>
    [Fact]
    public void AnEmptyString_MakesTheCountAByteCount()
    {
        var positional = Convert(Image + "currentfile 4 () /SubFileDecode filter image\nWXYZ" + After);
        Assert.Equal(new[] { Encoding.ASCII.GetBytes("WXYZ") }, ImageBytes(positional));
        Assert.Contains("f", ContentStreamAssert.Operators(positional.Content));

        var dictionary = Convert(Image + "currentfile << /EODCount 4 /EODString () >> /SubFileDecode filter image\nWXYZ" + After);
        Assert.Equal(new[] { Encoding.ASCII.GetBytes("WXYZ") }, ImageBytes(dictionary));
    }

    /// <summary>A filter over the sub-file decodes what it passes: hex inside the delimited block.</summary>
    [Fact]
    public void AFilterOverASubFile_DecodesItsData()
    {
        var r = Convert(Image + "currentfile 0 (%EOD) /SubFileDecode filter /ASCIIHexDecode filter image\n00FF7F40>%EOD\n" + After);
        Assert.Equal(new[] { new byte[] { 0x00, 0xFF, 0x7F, 0x40 } }, ImageBytes(r));
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }

    /// <summary>Above another filter the sub-file passes what its count allows of that filter's output: two of the four hex bytes.</summary>
    [Fact]
    public void ASubFileOverAFilter_PassesItsCount()
    {
        var r = Convert("2 1 8 [2 0 0 -1 0 1] currentfile /ASCIIHexDecode filter 2 () /SubFileDecode filter image\n00FF7F40>\n" + After);
        Assert.Equal(new[] { new byte[] { 0x00, 0xFF } }, ImageBytes(r));
    }

    /// <summary>A string the data never reaches leaves the image undrawn and said, the program's bytes consumed as for any unterminated block.</summary>
    [Fact]
    public void AMissingEodString_IsReported()
    {
        var r = Convert(Image + "currentfile 0 (%%EndData) /SubFileDecode filter image\nABCD");
        Assert.Empty(ImageBytes(r));
        Assert.Contains("end-of-data string", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>A negative count is a <c>rangecheck</c>.</summary>
    [Fact]
    public void ANegativeCount_IsARangecheck()
    {
        var r = Convert("{ currentfile -1 (x) /SubFileDecode filter } stopped { $error /errorname get /rangecheck eq { " + After + " } if } if");
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }

    /// <summary>
    /// The string is found in time linear in the data: a string that almost matches at every byte
    /// (a run of 'a' before a 'b', in a megabyte of 'a', the string twice at the end) falls back at
    /// most once a byte, where comparing again from each position would compare four thousand times
    /// a byte. The run is no multiple of the string's: a search that starts again from nothing after a
    /// near miss lines up with the first occurrence only when it is.
    /// </summary>
    [Fact]
    public void AnAlmostMatchingString_IsFoundInLinearTime_HostileInput()
    {
        string eod = new string('a', 4000) + "b";
        string data = new string('a', 999_999) + "b" + eod;
        Interpreter.PsFile.SubFileFallbacks = 0;
        byte[] passed = Interpreter.PsFile.SubFile(Encoding.ASCII.GetBytes(data), 0, data.Length, 1,
                                                   Encoding.ASCII.GetBytes(eod), sourceEndsHere: true, out int end);
        Assert.Equal(data.Length, end);
        Assert.Equal(data.Length - eod.Length, passed.Length);
        Assert.InRange(Interpreter.PsFile.SubFileFallbacks, 1, data.Length);
    }

    // ==================================================================
    //  ReusableStreamDecode
    // ==================================================================

    // The data follows the filter that reads it, and the def comes after the data.
    private const string Reusable =
        "/data currentfile << /Filter /SubFileDecode /DecodeParms << /EODCount 0 /EODString (%EndData) >> >> " +
        "/ReusableStreamDecode filter\nABCD%EndData\ndef\n";

    /// <summary>
    /// The data is read when the filter is made — InDesign's idiom, a sub-file under its own name —
    /// an image reads it, and after <c>resetfile</c> a second image reads it again.
    /// </summary>
    [Fact]
    public void AReusableStream_IsReadWhenMade_AndReadAgainAfterResetfile()
    {
        var r = Convert(Reusable + Image + "data image data resetfile " + Image + "data image " + After);
        Assert.Equal(2, ImageBytes(r).Count);
        Assert.All(ImageBytes(r), bytes => Assert.Equal(Encoding.ASCII.GetBytes("ABCD"), bytes));
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }

    /// <summary>
    /// Without <c>resetfile</c> the second image finds the stream at its end — a file's position is
    /// where the last read left it — and is reported short (the control for the case above).
    /// </summary>
    [Fact]
    public void AReusableStream_IsNotRewoundByAnImage()
    {
        var r = Convert(Reusable + Image + "data image " + Image + "data image " + After);
        Assert.Equal(Encoding.ASCII.GetBytes("ABCD"), ImageBytes(r)[0]);
        Assert.DoesNotContain(ImageBytes(r).Skip(1), bytes => bytes.SequenceEqual(Encoding.ASCII.GetBytes("ABCD")));
        Assert.NotNull(r.LastError);
    }

    private static List<byte[]> ShadingBytes(PostScriptConversionResult r)
    {
        var data = new List<byte[]>();
        if (r.Resources?[PdfName.Shading] is PdfDictionary shadings)
            foreach (var kv in shadings)
            {
                if (kv.Value is PdfStream mesh) data.Add(mesh.RawData);
                else if (kv.Value is PdfDictionary axial && axial[PdfName.Function] is PdfStream sampled) data.Add(sampled.RawData);
            }
        return data;
    }

    private static readonly byte[] MeshData = { 0, 0, 0, 0, 0, 0xFF, 0, 0x80, 0, 0, 0xFF, 0xFF };

    /// <summary>
    /// A mesh's reusable data source is read from position 0 whatever position the program left it
    /// at — a shading pattern reads its data again (PLRM §4.9.3) — so the second <c>shfill</c>, after the first left
    /// the stream at its end, reads the whole mesh too.
    /// </summary>
    [Fact]
    public void AMeshFromAReusableStream_IsReadFromItsStart_EveryTime()
    {
        var r = Convert(
            "/mesh <00000000 00FF0080 0000FFFF> /ReusableStreamDecode filter def " +
            "/sh << /ShadingType 4 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8" +
            " /Decode [0 255 0 255 0 1] /DataSource mesh >> def " +
            "mesh 5 setfileposition sh shfill sh shfill");
        List<byte[]> meshes = ShadingBytes(r);
        Assert.NotEmpty(meshes);
        Assert.All(meshes, bytes => Assert.Equal(MeshData, bytes));
        Assert.Equal(2, ContentStreamAssert.Operators(r.Content).Count(op => op == "sh"));
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>A sampled function's reusable data source begins at position 0 (PLRM §3.10.1).</summary>
    [Fact]
    public void ASampledFunctionFromAReusableStream_IsReadFromItsStart()
    {
        var r = Convert(
            "/samples <00FF> /ReusableStreamDecode filter def samples 1 setfileposition " +
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 100 0] /Function << /FunctionType 0 /Domain [0 1]" +
            " /Range [0 1] /Size [2] /BitsPerSample 8 /DataSource samples >> >> shfill");
        Assert.Equal(new[] { new byte[] { 0x00, 0xFF } }, ShadingBytes(r));
    }

    /// <summary><c>fileposition</c> and <c>setfileposition</c> move about a reusable file; past its end is a <c>rangecheck</c>.</summary>
    [Fact]
    public void FilePositions_MoveAboutAReusableStream()
    {
        var r = Convert(Reusable + "data 2 setfileposition data read pop 67 eq data fileposition 3 eq and { " + After + " } if");
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));

        var past = Convert(Reusable + "{ data 9 setfileposition } stopped { $error /errorname get /rangecheck eq { " + After + " } if } if");
        Assert.Contains("f", ContentStreamAssert.Operators(past.Content));
    }

    /// <summary>A string source is held as it is: <c>(…) /ReusableStreamDecode filter</c> reads the string's bytes.</summary>
    [Fact]
    public void AStringSource_IsHeldWhole()
    {
        var r = Convert("/data (WXYZ) /ReusableStreamDecode filter def " + Image + "data image");
        Assert.Equal(new[] { Encoding.ASCII.GetBytes("WXYZ") }, ImageBytes(r));
    }

    /// <summary>The <c>Filter</c> resource category answers the filters a data source may be read through, these two among them.</summary>
    [Theory]
    [InlineData("SubFileDecode")]
    [InlineData("ReusableStreamDecode")]
    [InlineData("CCITTFaxDecode")]
    public void TheFilterCategory_ListsTheFilter(string filter)
    {
        var r = Convert($"/{filter} /Filter resourcestatus {{ pop pop {After} }} if");
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
        Assert.DoesNotContain("f", ContentStreamAssert.Operators(Convert($"/NoSuchDecode /Filter resourcestatus {{ pop pop {After} }} if").Content));
    }
}
