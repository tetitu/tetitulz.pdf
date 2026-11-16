// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfRepair"/> and the parser's own recovery scan must recognise the same
/// <c>N G obj</c> header. They did not, and the repair tool was the <b>stricter</b> of the two.
/// </summary>
/// <remarks>
/// <para>
/// <c>PdfParser.RecoverXrefByScanning</c> had been fixed to accept any §7.2.2 white space before
/// the keyword, a delimiter after it, and a white-space <i>run</i> between the generation number
/// and the keyword. The fix landed one level short: <c>PdfRepair.ScanForObjects</c> kept a second
/// copy that matched a literal <c>(byte)' '</c>, demanded white space (never a delimiter) after the
/// keyword, and required a generation digit immediately before it. So three legal spellings —
/// <c>4 0\nobj</c>, <c>4 0 obj&lt;&lt;…</c> and <c>4 0  obj</c> — were recovered by
/// <see cref="PdfDocument.Open"/> and silently dropped by the tool whose job is rescuing files.
/// </para>
/// <para>
/// <b>Silently is the word.</b> Nothing threw. A file whose every header used one of those
/// spellings came out of repair as a structurally well-formed but EMPTY document — header, binary
/// marker, an xref holding only the free-list head, and a trailer whose <c>/Root 1 0 R</c> names an
/// object that is not in the file — with a <see cref="PdfRepairResult"/> reporting 0 objects and an
/// <b>empty</b> error list.
/// </para>
/// <para>
/// Every case here asserts the recovered object <i>count</i> and reads the recovered object's own
/// payload back out of the repaired bytes. "Repair returned a result" and "the output is non-empty"
/// are both true of the broken build.
/// </para>
/// </remarks>
public class RepairHeaderScanParityTests
{
    /// <summary>The four object headers a fixture may be written with.</summary>
    private const string PlainSpace = "4 0 obj\n";       // the control — both copies always agreed
    private const string Newline    = "4 0\nobj\n";      // §7.2.2 white space, not only 0x20
    private const string Delimiter  = "4 0 obj";         // the dictionary abuts the keyword
    private const string DoubleSpace = "4 0  obj\n";     // a white-space RUN

    // ================================================================
    //  Parity: the two scans must agree, object for object
    // ================================================================

    /// <summary>
    /// The repair tool and the parser recover the same four objects from the same bytes, whichever
    /// legal spelling object 4's header uses.
    /// </summary>
    [Theory]
    [InlineData(PlainSpace)]
    [InlineData(Newline)]
    [InlineData(Delimiter)]
    [InlineData(DoubleSpace)]
    public void RepairAndTheParserRecoverTheSameObjectsFromTheSameBytes(string headerOfObject4)
    {
        byte[] damaged = DamagedPdf(headerOfObject4);

        var result = Repair(damaged, out byte[] repaired);

        using var doc = PdfDocument.Open(new MemoryStream(damaged));
        int parserRecovered = doc.CrossReferenceTable.Count;

        Assert.Equal(4, parserRecovered);
        Assert.Equal(parserRecovered, result.RecoveredObjectCount);
        Assert.Equal(4242, SerialOfObject4(repaired));
    }

    /// <summary>
    /// The payload assertion on its own, so a regression that recovers the right <i>number</i> of
    /// objects at the wrong offsets is not mistaken for a pass. The recovered object 4 must read
    /// back as the dictionary the fixture wrote — an offset one byte out yields a parse failure,
    /// not a different count.
    /// </summary>
    [Theory]
    [InlineData(PlainSpace)]
    [InlineData(Newline)]
    [InlineData(Delimiter)]
    [InlineData(DoubleSpace)]
    public void TheRepairedFileCarriesObject4sOwnPayload(string headerOfObject4)
    {
        Repair(DamagedPdf(headerOfObject4), out byte[] repaired);

        Assert.Equal(4242, SerialOfObject4(repaired));
    }

    // ================================================================
    //  The headline case: every header uses the spelling repair refused
    // ================================================================

    /// <summary>
    /// When <i>every</i> header uses a spelling the old scan refused, repair produced a document
    /// with no objects in it at all. This asserts the opposite end: all four objects present, and
    /// the catalog reachable through the trailer of the repaired file.
    /// </summary>
    [Fact]
    public void ADocumentWrittenEntirelyWithNewlineHeadersIsNotRepairedIntoAnEmptyFile()
    {
        byte[] damaged = AllHeadersOnOwnLine();

        var result = Repair(damaged, out byte[] repaired);

        Assert.Equal(4, result.RecoveredObjectCount);

        using var doc = PdfDocument.Open(new MemoryStream(repaired));
        var catalog = Assert.IsType<PdfDictionary>(doc.GetObject(1));
        Assert.Equal(PdfName.Catalog, catalog.GetName(PdfName.Type));
        Assert.Equal(4242, SerialOfObject4(repaired));
    }

    /// <summary>
    /// The generation the header declares still reaches the rebuilt xref — the capture stayed at
    /// the call site when token recognition moved out, and an xref row whose generation contradicts
    /// the copied header is treated as stale by a strict reader.
    /// </summary>
    [Fact]
    public void ANonZeroGenerationSurvivesAHeaderSpellingTheOldScanRefused()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        sb.Append("2 3  obj\n<< /Serial 4242 >>\nendobj\n");   // two spaces AND generation 3
        sb.Append("%%EOF\n");

        Repair(Encoding.Latin1.GetBytes(sb.ToString()), out byte[] repaired);

        string text = Encoding.Latin1.GetString(repaired);
        Assert.Contains("2 3  obj", text, System.StringComparison.Ordinal);
        Assert.Contains("00003 n", text, System.StringComparison.Ordinal);
    }

    // ================================================================
    //  The guard half — a non-header must still not be recovered
    // ================================================================

    /// <summary>
    /// Widening the rule must not widen it to "any digits near <c>obj</c>". A digit run that is the
    /// tail of something else is not an object header, and <c>endobj</c> is not one either.
    /// </summary>
    [Theory]
    [InlineData("x")]
    [InlineData("/")]
    [InlineData(">")]
    public void ObjectDigitsPrecededByANonWhitespaceByteAreStillNotAHeader(string pad)
    {
        byte[] damaged = DamagedPdf(pad + PlainSpace);

        var result = Repair(damaged, out byte[] repaired);

        Assert.Equal(3, result.RecoveredObjectCount);
        Assert.DoesNotContain("4242", Encoding.Latin1.GetString(repaired), System.StringComparison.Ordinal);
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static PdfRepairResult Repair(byte[] damaged, out byte[] repaired)
    {
        using var input = new MemoryStream(damaged);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        repaired = output.ToArray();
        return result;
    }

    /// <summary>
    /// Reads object 4's <c>/Serial</c> out of a repaired file, or -1 if the object is not there.
    /// This is the payload check: it fails both when the object was dropped and when it was
    /// recorded at an offset that does not parse.
    /// </summary>
    private static int SerialOfObject4(byte[] repaired)
    {
        using var doc = PdfDocument.Open(new MemoryStream(repaired));
        return doc.GetObject(4) is PdfDictionary dict
            ? dict.GetInteger(new PdfName("Serial")) ?? -1
            : -1;
    }

    /// <summary>
    /// A four-object document with <b>no cross-reference table</b>, so both the parser and the
    /// repair tool must find every object by scanning. Object 4 carries the only <c>/Serial</c>, so
    /// reading it back is exactly "was object 4 recovered, at the right offset".
    /// </summary>
    private static byte[] DamagedPdf(string headerOfObject4)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        sb.Append(headerOfObject4);
        sb.Append("<< /Serial 4242 >>\nendobj\n");
        sb.Append("%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The same document with every header written <c>N G\nobj</c>.</summary>
    private static byte[] AllHeadersOnOwnLine()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0\nobj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0\nobj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0\nobj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        sb.Append("4 0\nobj\n<< /Serial 4242 >>\nendobj\n");
        sb.Append("%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
