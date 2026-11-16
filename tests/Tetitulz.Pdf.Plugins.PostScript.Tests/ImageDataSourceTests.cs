// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Procedure data sources and the reads that feed them (PLRM §4.10.2, §8.2): the EPS idiom
/// <c>{ currentfile picstr readhexstring pop } image</c>, binary <c>readstring</c>, several sources a
/// component, and <c>read</c>/<c>readline</c> on the program's own bytes. A procedure source was
/// refused and every read was an arity-table entry, so such an image was missing and its data was
/// then tokenised as program.
/// </summary>
public sealed class ImageDataSourceTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static PostScriptConversionResult Convert(byte[] program)
        => PostScriptConversion.ConvertFragment(program);

    private static PostScriptConversionResult Convert(string program)
        => Convert(Encoding.ASCII.GetBytes(program));

    private static PdfStream OnlyImage(PostScriptConversionResult r)
        => r.Resources?[PdfName.XObject] is PdfDictionary x && x.Count == 1 ? (PdfStream)x[new PdfName("Im1")] : null;

    private static string[] Ops(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Select(o => o.ToString()).ToArray();

    /// <summary>The EPS idiom: hex samples read a row at a time from the program, which resumes after them.</summary>
    [Fact]
    public void ReadhexstringFromCurrentfile_FeedsTheImage_AndTheProgramResumes()
    {
        var r = Convert("/picstr 2 string def 2 2 8 [2 0 0 -2 0 2] { currentfile picstr readhexstring pop } image\n" +
                        "00FF\n80 40\n0.25 setgray" + Marker);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x80, 0x40 }, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary>
    /// Binary samples read with <c>readstring</c>: the one end of line after <c>image</c> is its
    /// delimiter, and a data byte that is LF or CR is a sample, not skipped.
    /// </summary>
    [Fact]
    public void ReadstringFromCurrentfile_TakesTheBytesAsTheyAre_AfterOneDelimiter()
    {
        byte[] head = Encoding.ASCII.GetBytes("/s 2 string def 2 2 8 [2 0 0 -2 0 2] { currentfile s readstring pop } image\n");
        byte[] data = { 0x0A, 0x0D, 0x20, 0x00 };   // the second read starts at a space, which is a sample
        byte[] tail = Encoding.ASCII.GetBytes("\n0.25 setgray" + Marker);
        var r = Convert(head.Concat(data).Concat(tail).ToArray());
        Assert.Equal(data, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary>After an operator ended by CR LF, the two are one delimiter: the data's first byte is the one after them.</summary>
    [Fact]
    public void ACrLfAfterTheOperator_IsOneDelimiter()
    {
        byte[] head = Encoding.ASCII.GetBytes("/s 1 string def 1 1 8 [1 0 0 -1 0 1] { currentfile s readstring pop } image\r\n");
        byte[] data = { 0x41 };   // not an LF, which a CR taken alone would leave to be read
        var r = Convert(head.Concat(data).Concat(Encoding.ASCII.GetBytes("\n")).ToArray());
        Assert.Equal(data, OnlyImage(r).RawData);
    }

    /// <summary>
    /// Several procedure sources run in turn, each reading its row from the program: R, G and B rows
    /// in order, two pixels by two — so the planes are R0 R1, G0 G1, B0 B1 and each pixel takes its
    /// own row's sample.
    /// </summary>
    [Fact]
    public void SeveralProcedureSources_RunInTurn_EachReadingItsRow()
    {
        var r = Convert("/r 2 string def /g 2 string def /b 2 string def 2 2 8 [2 0 0 -2 0 2]" +
                        " { currentfile r readhexstring pop } { currentfile g readhexstring pop } { currentfile b readhexstring pop }" +
                        " true 3 colorimage\n1011 2021 3031\n1213 2223 3233\n");
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x11, 0x21, 0x31, 0x12, 0x22, 0x32, 0x13, 0x23, 0x33 }, OnlyImage(r).RawData);
    }

    /// <summary>
    /// A file whose block an image has taken is at its end (PLRM §3.8.4): a second read of it finds
    /// nothing, and the program after the block is not taken as its data.
    /// </summary>
    [Fact]
    public void AFileItsImageTook_IsAtItsEnd_AndTheProgramAfterItRuns()
    {
        var r = Convert("/f currentfile /ASCIIHexDecode filter def" +
                        " { 1 1 8 [1 0 0 -1 0 1] f image f token { pop } {" + Marker + "} ifelse } exec\n7F>\n0.25 setgray" + Marker);
        Assert.Equal(new byte[] { 0x7F }, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
        Assert.Equal(2, Ops(r).Count(o => o == "f"));
    }

    /// <summary>A filtered file part-read by <c>readstring</c> and then given to <c>image</c> hands it the rest of its block.</summary>
    [Fact]
    public void AFilteredFilePartRead_GivesAnImageTheRestOfItsBlock()
    {
        // In one procedure, parsed before the data: a read takes what follows the program's tokens.
        var r = Convert("/f currentfile /ASCIIHexDecode filter def" +
                        " { f 1 string readstring pop pop 1 1 8 [1 0 0 -1 0 1] f image } exec\n0A0B>\n");
        Assert.Equal(new byte[] { 0x0B }, OnlyImage(r).RawData);
    }

    /// <summary>A procedure source that answers anything but a string is a <c>typecheck</c>, which <c>stopped</c> catches; one that answers a string is the control above.</summary>
    [Fact]
    public void AProcedureSourceAnsweringANumber_IsATypecheck()
    {
        var r = Convert("{ 1 1 8 [1 0 0 -1 0 1] { 5 } image } stopped {" + Marker + "} if");
        Assert.Contains("f", Ops(r));
        Assert.Null(OnlyImage(r));
    }

    /// <summary>A procedure that answers the same string each time is called until the image is full.</summary>
    [Fact]
    public void AProcedureSource_IsCalledUntilTheImageIsFull()
    {
        var r = Convert("4 1 8 [4 0 0 -1 0 1] { <FF00> } image");
        Assert.Equal(new byte[] { 0xFF, 0x00, 0xFF, 0x00 }, OnlyImage(r).RawData);
    }

    /// <summary>A procedure that answers an empty string ends its data: the rest is padded, and that is said.</summary>
    [Fact]
    public void AnEmptyStringFromASource_EndsIt_ThePaddingSaid()
    {
        PostScriptDiagnostics.Clear();
        var r = Convert("/n 0 def 4 1 8 [4 0 0 -1 0 1] { n 0 eq { /n 1 def <7F> } { () } ifelse } image");
        Assert.Equal(new byte[] { 0x7F, 0, 0, 0 }, OnlyImage(r).RawData);
        Assert.Contains("padded", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary><c>colorimage</c> with one source a component: the planes interleaved, pixel by pixel.</summary>
    [Fact]
    public void ColorimageWithSeveralSources_InterleavesThePlanes()
    {
        PostScriptDiagnostics.Clear();
        var r = Convert("2 1 8 [2 0 0 -1 0 1] <FF00> <00FF> <8080> true 3 colorimage");
        PdfStream image = OnlyImage(r);
        Assert.Null(r.LastError);                                             // each plane complete: nothing padded
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x80, 0x00, 0xFF, 0x80 }, image.RawData);
        Assert.Equal("DeviceRGB", ((PdfName)image[PdfName.ColorSpace]).Value);
    }

    /// <summary>
    /// Several procedure sources of one-bit samples: each called in turn, and every sample keeping its
    /// bit — red 10000000, green 01000000, blue 00100000 give the pixels 100 010 001 000 000 000 000 000.
    /// </summary>
    [Fact]
    public void SeveralOneBitProcedureSources_InterleaveBitByBit()
    {
        var r = Convert("8 1 1 [8 0 0 -1 0 1] {<80>} {<40>} {<20>} true 3 colorimage");
        Assert.Equal(new byte[] { 0b1000_1000, 0b1000_0000, 0b0000_0000 }, OnlyImage(r).RawData);
    }

    /// <summary>The dictionary form's <c>/MultipleDataSources</c>: an array of one source a component.</summary>
    [Fact]
    public void TheDictionaryFormsMultipleDataSources_AreInterleaved()
    {
        var r = Convert("/DeviceRGB setcolorspace << /ImageType 1 /Width 1 /Height 1 /BitsPerComponent 8" +
                        " /ImageMatrix [1 0 0 -1 0 1] /MultipleDataSources true /DataSource [ <11> {<22>} <33> ] >> image");
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, OnlyImage(r).RawData);
    }

    /// <summary>A filtered file read a piece at a time: its block decoded once, each read served from it.</summary>
    [Fact]
    public void AFilteredFile_IsReadAPieceAtATime()
    {
        var r = Convert("/f currentfile /ASCIIHexDecode filter def /s 1 string def" +
                        " 2 2 8 [2 0 0 -2 0 2] { f s readstring pop } image\n0A0B0C0D>\n0.25 setgray" + Marker);
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C, 0x0D }, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary><c>readline</c> reads to the end of the line, which it does not store — LF, or CR LF as one — answering true, and the program goes on after it.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Readline_ReadsOneLine(string end)
    {
        var r = Convert("currentfile 20 string readline\nhello" + end + "{ (hello) eq {" + Marker + "} if } { pop } ifelse");
        Assert.Contains("f", Ops(r));
    }

    /// <summary>A line ended by CR LF takes both: the next <c>readline</c> reads the next line, not an empty one.</summary>
    [Fact]
    public void ALineEndedByCrLf_TakesBoth()
    {
        var r = Convert("{ currentfile 9 string readline pop pop currentfile 9 string readline pop (b) eq {" + Marker + "} if } exec\na\r\nb\n");
        Assert.Contains("f", Ops(r));
    }

    /// <summary>At the end of the program with no end of line, <c>readline</c> answers what it read and false.</summary>
    [Fact]
    public void ReadlineAtTheEnd_AnswersFalse()
    {
        // The test after the read is in the procedure, parsed before the data it reads: a fill for
        // false, a stroke for true.
        const string Branch = "{ currentfile 20 string readline { 0 0 moveto 1 1 lineto stroke } {" + Marker + "} ifelse pop } exec\n";
        string[] atEnd = Ops(Convert(Branch + "end"));
        Assert.Contains("f", atEnd);
        Assert.DoesNotContain("S", atEnd);
        string[] withLineEnd = Ops(Convert(Branch + "end\n"));
        Assert.Contains("S", withLineEnd);
        Assert.DoesNotContain("f", withLineEnd);
    }

    /// <summary>A line longer than the string is a <c>rangecheck</c>, as PLRM has it.</summary>
    [Fact]
    public void ALineLongerThanTheString_IsARangecheck()
    {
        var r = Convert("{ currentfile 2 string readline } stopped\nhello\n {" + Marker + "} if");
        Assert.Contains("f", Ops(r));
    }

    /// <summary><c>read</c> takes the byte after its delimiter: here <c>A</c>.</summary>
    [Fact]
    public void Read_TakesTheNextByte()
    {
        var r = Convert("currentfile read\nA pop 65 eq {" + Marker + "} if");
        Assert.Contains("f", Ops(r));
    }

    /// <summary>A read of anything but a file comes back empty and false — so a reading loop ends — and says so.</summary>
    [Fact]
    public void AReadOfSomethingThatIsNotAFile_IsEmptyAndFalse()
    {
        PostScriptDiagnostics.Clear();
        var r = Convert("5 (ab) readstring not exch length 0 eq and {" + Marker + "} if");
        Assert.Contains("f", Ops(r));
        Assert.Contains("readstring (a file other than currentfile)", PostScriptDiagnostics.UnimplementedOperators);
    }

    /// <summary>
    /// Hostile: a procedure answering one byte at a time for a large image runs once per byte, each
    /// call charged — so a small budget ends the conversion rather than a million calls running.
    /// </summary>
    [Fact]
    public void HostileOneByteProcedureSource_IsBoundedByTheBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 20_000 };
        var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes("1000 1000 8 [1000 0 0 -1000 0 1000] { (x) } image"), options);
        Assert.True(r.BudgetExhausted);
        Assert.Null(OnlyImage(r));
        var calm = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes("10 10 8 [10 0 0 -10 0 10] { (x) } image"), options);
        Assert.False(calm.BudgetExhausted);
        Assert.NotNull(OnlyImage(calm));
    }
}
