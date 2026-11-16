// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// The <c>N G obj</c> header rule, now owned by <see cref="ObjectHeaderScanner"/> and applied by
/// both <see cref="PdfParser"/>'s recovery scan and <c>Base.Operations.Documents.RawObjectScanner</c>,
/// the scan behind <c>PdfRepair</c> and FDF import.
/// </summary>
/// <remarks>
/// <para>
/// The rule existed twice and the copies disagreed. Three spellings are legal PDF and separated
/// them — any §7.2.2 white space may precede the keyword (not only <c>0x20</c>), a delimiter may
/// follow it, and the white space between the generation number and the keyword is a <i>run</i>
/// — and in every one of them the parser recovered the object while the repair tool dropped it.
/// </para>
/// <para>
/// These cases assert the recovered object number, its byte <b>offset</b>, and the parsed content
/// at that offset. A scanner that returns a match at the wrong offset is worse than one that
/// returns nothing, so "it found something" is not the claim.
/// </para>
/// </remarks>
public class ObjectHeaderScanTests
{
    // ================================================================
    //  The three separating spellings, at the scanner
    // ================================================================

    /// <summary>
    /// <c>12 0 obj</c> — a plain 0x20 space, the spelling both copies always agreed on. Present as
    /// the control: without it the theory below cannot show that the other three are the change.
    /// </summary>
    [Theory]
    [InlineData("12 0 obj")]     // the control
    [InlineData("12 0\nobj")]    // LF between generation and keyword
    [InlineData("12 0\robj")]
    [InlineData("12 0\tobj")]
    [InlineData("12 0  obj")]    // a white-space RUN, not one byte
    [InlineData("12 0 obj<</Type/Page>>")]   // a delimiter after the keyword
    public void EveryLegalSpellingOfTheHeaderYieldsObject12AtOffsetZero(string spelling)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(spelling);

        Assert.True(ObjectHeaderScanner.TryFindNext(bytes, bytes.Length, 0, out var header));
        Assert.Equal(12, header.ObjectNumber);
        Assert.Equal(0L, header.Offset);
        Assert.True(ObjectHeaderScanner.TryParseDigits(
            bytes, header.GenerationStart, header.GenerationLength, out int generation));
        Assert.Equal(0, generation);
        Assert.Equal(spelling.IndexOf("obj", System.StringComparison.Ordinal) + 3, header.KeywordEnd);
    }

    /// <summary>
    /// The keyword may sit at end of buffer with nothing after it — <c>afterKeyword &lt; count</c>
    /// is the EOF case, and demanding a following byte would drop the last object of a truncated
    /// file.
    /// </summary>
    [Fact]
    public void AHeaderEndingTheBufferIsStillAHeader()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("7 0 obj");

        Assert.True(ObjectHeaderScanner.TryFindNext(bytes, bytes.Length, 0, out var header));
        Assert.Equal(7, header.ObjectNumber);
        Assert.Equal(bytes.Length, header.KeywordEnd);
    }

    /// <summary>
    /// A non-zero generation is carried out as digits, and the object's own offset still points at
    /// the object number rather than the keyword.
    /// </summary>
    [Fact]
    public void TheGenerationSpanCoversTheDigitsTheHeaderDeclares()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n12 65535 obj\n");

        Assert.True(ObjectHeaderScanner.TryFindNext(bytes, bytes.Length, 0, out var header));
        Assert.Equal(12, header.ObjectNumber);
        Assert.Equal(9L, header.Offset);
        Assert.Equal(5, header.GenerationLength);
        Assert.True(ObjectHeaderScanner.TryParseDigits(
            bytes, header.GenerationStart, header.GenerationLength, out int generation));
        Assert.Equal(65535, generation);
    }

    // ================================================================
    //  What must NOT match — the guard half of the rule
    // ================================================================

    /// <summary>
    /// The floor. Accepting any of these would make the scan mint plausible-looking wrong entries:
    /// <c>endobj</c> is not a header, a digit run that is the tail of something else is not an
    /// object number, a letter after the keyword makes it a different token, and a header with no
    /// generation number is not a header.
    /// </summary>
    [Theory]
    [InlineData("<< /Length 3 >>\nstream\nabc\nendstream\nendobj\n")]  // `endobj` only
    [InlineData("x12 0 obj\n")]        // the digits are the tail of `x12`
    [InlineData("12 0 objx\n")]        // a letter after the keyword
    [InlineData("12 obj\n")]           // no generation number
    [InlineData("obj\n")]              // no numbers at all
    [InlineData("12 0 endobj\n")]      // `endobj` preceded by a well-formed pair
    public void NoneOfTheseIsAnObjectHeader(string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        Assert.False(ObjectHeaderScanner.TryFindNext(bytes, bytes.Length, 0, out _));
    }

    /// <summary>
    /// A rejected candidate must not be re-examined, or a caller looping on
    /// <see cref="ObjectHeaderMatch.KeywordEnd"/> would spin on it forever. Asserted by giving the
    /// scan a rejected header followed by a good one and requiring it to reach the good one.
    /// </summary>
    [Fact]
    public void ARejectedCandidateDoesNotStopTheScanReachingTheNextHeader()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("x12 0 obj\nendobj\n34 0\nobj\n");

        Assert.True(ObjectHeaderScanner.TryFindNext(bytes, bytes.Length, 0, out var header));
        Assert.Equal(34, header.ObjectNumber);
        Assert.Equal(bytes.Length - "34 0\nobj\n".Length, (int)header.Offset);
    }

    /// <summary>
    /// An object number too large for an <see cref="int"/> is refused rather than wrapped — a
    /// wrapped number names a different object, which is the "plausible-looking wrong entry" this
    /// scan exists to avoid.
    /// </summary>
    [Fact]
    public void AnOversizedObjectNumberIsRefusedRatherThanWrapped()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("99999999999 0 obj\n");

        Assert.False(ObjectHeaderScanner.TryFindNext(bytes, bytes.Length, 0, out _));
    }

    /// <summary>Leading zeros are digits, not a different number.</summary>
    [Fact]
    public void TryParseDigitsReadsALeadingZeroRunAsTheSameNumber()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("00000000000000012");

        Assert.True(ObjectHeaderScanner.TryParseDigits(bytes, 0, bytes.Length, out int value));
        Assert.Equal(12, value);
    }

    // ================================================================
    //  The parser's recovery scan, over the same spellings
    // ================================================================

    /// <summary>
    /// End to end through <see cref="PdfParser"/>: each spelling recovers object 12 at offset 0 and
    /// the object at that offset parses back to the dictionary the fixture wrote. The marker value
    /// is what separates "recovered the object" from "recorded an entry".
    /// </summary>
    [Theory]
    [InlineData("12 0 obj")]
    [InlineData("12 0\nobj")]
    [InlineData("12 0  obj")]
    public void RecoveryScanReadsTheObjectBackWhateverSeparatesTheGenerationFromTheKeyword(string header)
    {
        byte[] fragment = Encoding.ASCII.GetBytes(header + "\n<< /Marker 12 >>\nendobj\n");

        var parser = new PdfParser(new MemoryStream(fragment));
        parser.ParseCrossReferences();

        var entry = parser.CrossReferenceTable.GetEntry(12);
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Offset);

        var dict = Assert.IsType<PdfDictionary>(parser.GetObject(12));
        Assert.Equal(12, dict.GetInteger(new PdfName("Marker")));
    }

    /// <summary>
    /// The delimiter case has to be spelled without the intervening newline, so it gets its own
    /// fact: <c>12 0 obj&lt;&lt;…</c> is what a producer that omits optional white space writes.
    /// </summary>
    [Fact]
    public void RecoveryScanReadsAnObjectWhoseDictionaryAbutsTheKeyword()
    {
        byte[] fragment = Encoding.ASCII.GetBytes("12 0 obj<< /Marker 12 >>\nendobj\n");

        var parser = new PdfParser(new MemoryStream(fragment));
        parser.ParseCrossReferences();

        Assert.Equal(0, parser.CrossReferenceTable.GetEntry(12).Offset);
        var dict = Assert.IsType<PdfDictionary>(parser.GetObject(12));
        Assert.Equal(12, dict.GetInteger(new PdfName("Marker")));
    }

    // ================================================================
    //  Stepping over stream data stays linear in the file
    // ================================================================

    /// <summary>
    /// Twenty thousand headers with no <c>endobj</c>, and the same with a <c>stream</c> that never
    /// ends. Searched to the end of the buffer per header, the resume read the file twenty thousand
    /// times over, and RepairBoundsTests' million bare headers spun a test host for half an hour
    /// after its run; the resume searches read a small multiple of the file, whatever it holds.
    /// </summary>
    [Theory]
    [InlineData("1 0 obj ")]
    [InlineData("1 0 obj stream\n")]
    public void ResumeAfter_AnUnboundedRunOfHeaders_ReadsAMultipleOfTheFileNotOfTheHeaderCount(string header)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        for (int i = 0; i < 20_000; i++) sb.Append(header);
        byte[] buf = Encoding.ASCII.GetBytes(sb.ToString());

        var cursor = new ObjectHeaderScanner.ResumeCursor();
        int headers = 0, scanPos = 0;
        while (ObjectHeaderScanner.TryFindNext(buf, buf.Length, scanPos, out var match))
        {
            headers++;
            scanPos = ObjectHeaderScanner.ResumeAfter(buf, buf.Length, match.KeywordEnd, cursor);
        }

        Assert.Equal(20_000, headers);
        Assert.InRange(cursor.BytesExamined, 1, 4L * buf.Length);
    }

    /// <summary>The control: a stream's data is still stepped over — the header inside it is not scanned — by its direct <c>/Length</c>, and by the first <c>endstream</c> when the length is indirect.</summary>
    [Theory]
    [InlineData("/Length 23")]
    [InlineData("/Length 9 0 R")]
    public void ResumeAfter_AHeaderInsideStreamData_IsSteppedOver(string length)
    {
        byte[] buf = Encoding.ASCII.GetBytes($"1 0 obj\n<< {length} >>\nstream\n%PDF-1.4\n4 0 obj\n<< >>\nendstream\nendobj\n5 0 obj\n<< >>\nendobj\n");

        var seen = new System.Collections.Generic.List<int>();
        int scanPos = 0;
        while (ObjectHeaderScanner.TryFindNext(buf, buf.Length, scanPos, out var match))
        {
            seen.Add(match.ObjectNumber);
            scanPos = ObjectHeaderScanner.ResumeAfter(buf, buf.Length, match.KeywordEnd);
        }

        Assert.Equal(new[] { 1, 5 }, seen);
    }
}
