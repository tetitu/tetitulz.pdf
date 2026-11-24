// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Where a byte offset is measured from. ISO 32000-2 §7.5.2: "The PDF file begins with the 5
/// characters “%PDF–” and byte offsets shall be calculated from the PERCENT SIGN (25h)." Its NOTE 1:
/// "This provision allows for arbitrary bytes preceding the %PDF- without impacting the viability of
/// the PDF file and its byte offsets."
/// </summary>
/// <remarks>
/// <para><b>Why no other fixture in this suite could see it.</b> ISO 32000-1 §7.5.2 makes the header
/// "the first line of a PDF file", so in a conforming PDF 1.x file the percent sign is byte 0 and the
/// two readings coincide. The parser read every <c>startxref</c>, <c>/Prev</c>, <c>/XRefStm</c> and
/// entry offset as a stream position, so a file with bytes in front of its header — a web server's
/// preamble, a gateway's banner — lost every section, was rebuilt by scanning, got back the object its
/// newest revision had deleted, and reported <see cref="PdfParser.LastXrefSectionError"/> on a
/// well-formed file.</para>
/// <para><b>Two readings, one order.</b> Writers that count from byte 0 of their output exist —
/// <c>IncrementalUpdateWriter</c>'s six-argument <c>WriteUpdate</c> among them, when handed a prefixed
/// original — so a section that cannot be read at its §7.5.2 position is looked for at the byte-0 one
/// before it is given up on. Two of the facts below pin that the fallback is decided per section, and
/// one pins that it never outranks the §7.5.2 reading when both positions hold a cross-reference
/// section.</para>
/// </remarks>
public class HeaderOffsetOriginTests
{
    /// <summary>Fifteen bytes that are not white space and not a PDF token a section could start with.</summary>
    private const string Prefix = "junk-junk-junk\n";

    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string Pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string Page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>";

    // ================================================================
    //  §7.5.2 — offsets counted from the percent sign
    // ================================================================

    /// <summary>
    /// The audit's fixture: a two-revision classic file whose second revision deletes object 5, with
    /// <see cref="Prefix"/> in front of it. Every offset in it is correct under §7.5.2.
    /// </summary>
    [Fact]
    public void Prefixed_classic_file_reads_its_cross_reference_chain_from_the_percent_sign()
    {
        byte[] pdf = Concat(Prefix, TwoClassicRevisions(prefix: "", revision1FromHeader: true,
            revision2FromHeader: true, out long startxref, out Dictionary<int, long> positions));

        PdfParser parser = ParseWithHeader(pdf);

        AssertBothRevisionsWereRead(parser, startxref, expectedSectionsReadFromByteZero: 0);

        // The origin itself: where the percent sign sits, which is what the file's offsets count from.
        Assert.Equal((long)Prefix.Length, parser.HeaderOffset);

        // An entry records where the object IS in the stream, which is what every consumer that
        // indexes raw bytes with it (the repair tool, the DocMDP analyser) needs.
        Assert.Equal(Prefix.Length + positions[3], parser.CrossReferenceTable.GetEntry(3).Offset);

        // …and the origin is applied to byte offsets only. A free entry's first field is "the
        // 10-digit object number of the next free object" (§7.5.4), which no origin moves.
        Assert.Equal(0, parser.CrossReferenceTable.GetEntry(5).Offset);
    }

    /// <summary>
    /// The same rule through cross-reference streams, whose type-1 field is "the byte offset of the
    /// object, starting from the beginning of the PDF file" (§7.5.8.3), and through the bare
    /// <see cref="PdfParser.ParseCrossReferences"/> call that <c>PdfRepair</c> and
    /// <c>PdfIncrementalSaveValidator</c> make without <see cref="PdfParser.ParseHeader"/> first.
    /// </summary>
    [Fact]
    public void Prefixed_xref_stream_file_resolves_type_one_offsets_from_the_percent_sign()
    {
        byte[] body = TwoXrefStreamRevisions(out long startxref, out Dictionary<int, long> positions);
        byte[] pdf = Concat(Prefix, body);

        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseCrossReferences();

        AssertBothRevisionsWereRead(parser, startxref, expectedSectionsReadFromByteZero: 0);
        Assert.Equal(Prefix.Length + positions[3], parser.CrossReferenceTable.GetEntry(3).Offset);
    }

    /// <summary>
    /// The priority, pinned by a coincidence: the prefix is exactly as long as revision 1's
    /// cross-reference section, so the <c>startxref</c> value read from byte 0 lands squarely on
    /// revision 1's <c>xref</c> keyword. Both positions hold a section that parses; only the §7.5.2
    /// one is the file's newest revision.
    /// </summary>
    /// <remarks>
    /// Read from byte 0 this file is quietly wrong rather than damaged: the older section parses,
    /// names no <c>/Prev</c>, and every entry in it points <see cref="Prefix"/>-length bytes early —
    /// with <see cref="PdfParser.LastXrefSectionError"/> null, because nothing was skipped.
    /// </remarks>
    [Fact]
    public void When_the_byte_zero_reading_also_lands_on_a_section_the_percent_sign_reading_wins()
    {
        TwoClassicRevisions(prefix: "", revision1FromHeader: true, revision2FromHeader: true,
            out long unprefixedStartxref, out Dictionary<int, long> unprefixed);
        long revision1Section = unprefixed[Revision1SectionKey];
        int coincidentPrefixLength = checked((int)(unprefixedStartxref - revision1Section));
        Assert.True(coincidentPrefixLength + "%PDF-".Length < 1024,
            "the header must stay inside the 1024-byte window §7.5.2's search uses");

        string prefix = new string('j', coincidentPrefixLength - 1) + "\n";
        byte[] pdf = Concat(prefix, TwoClassicRevisions(prefix: "", revision1FromHeader: true,
            revision2FromHeader: true, out long startxref, out _));

        // The premise, checked rather than assumed: at byte `startxref` of the prefixed file sits
        // revision 1's section.
        Assert.Equal("xref", Encoding.ASCII.GetString(pdf, (int)startxref, 4));

        PdfParser parser = ParseWithHeader(pdf);

        AssertBothRevisionsWereRead(parser, startxref, expectedSectionsReadFromByteZero: 0);
    }

    // ================================================================
    //  The byte-0 fallback — per section
    // ================================================================

    /// <summary>
    /// A writer that counted every offset from byte 0 of an output already holding
    /// <see cref="Prefix"/>. Not conforming — and before §7.5.2 was honoured it was the one prefixed
    /// shape this parser could read — so honouring the rule must not turn it into a recovered file.
    /// </summary>
    [Fact]
    public void Prefixed_file_whose_writer_counted_from_byte_zero_still_reads_every_revision()
    {
        byte[] pdf = TwoClassicRevisions(prefix: Prefix, revision1FromHeader: false,
            revision2FromHeader: false, out long startxref, out _);

        PdfParser parser = ParseWithHeader(pdf);

        // Both sections are found only at the byte-0 reading.
        AssertBothRevisionsWereRead(parser, startxref, expectedSectionsReadFromByteZero: 2);
    }

    /// <summary>
    /// The shape of what <c>IncrementalUpdateWriter</c>'s six-argument <c>WriteUpdate</c> appends to a
    /// prefixed original: revision 1 counted from the header, revision 2 written at positions counted
    /// from byte 0 of the output, with a <c>/Prev</c> that is the caller's header-relative
    /// <see cref="PdfParser.StartXrefOffset"/>. One origin for the whole document reads only one of
    /// the two sections.
    /// </summary>
    [Fact]
    public void Update_appended_from_byte_zero_to_a_prefixed_file_reads_both_sections_on_their_own_origin()
    {
        byte[] pdf = TwoClassicRevisions(prefix: Prefix, revision1FromHeader: true,
            revision2FromHeader: false, out long startxref, out _);

        PdfParser parser = ParseWithHeader(pdf);

        // Revision 2's section is found only at the byte-0 reading; revision 1's where §7.5.2 says.
        AssertBothRevisionsWereRead(parser, startxref, expectedSectionsReadFromByteZero: 1);
    }

    // ================================================================
    //  No header at all
    // ================================================================

    /// <summary>
    /// The header is looked for inside the first 1024 bytes and nowhere else (PDF 32000-1:2008
    /// §7.5.2 puts it on the first line; the window is the tolerance). A header past the window is
    /// no header: <see cref="PdfParser.ParseHeader"/> reads none — the version assumed, and
    /// <see cref="PdfParser.LastHeaderRecovery"/> saying so — and a bare
    /// <see cref="PdfParser.ParseCrossReferences"/> counts from byte 0.
    /// </summary>
    [Fact]
    public void A_header_beyond_the_search_window_is_not_a_header()
    {
        byte[] pdf = Concat(new string('j', 1023) + "\n", TwoClassicRevisions(prefix: "",
            revision1FromHeader: true, revision2FromHeader: true, out _, out _));

        Assert.Equal(PdfParser.AssumedVersion, new PdfParser(new MemoryStream(pdf)).ParseHeader());
        Assert.Contains("no %PDF- header", PdfParser.LastHeaderRecovery, StringComparison.Ordinal);

        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseCrossReferences();
        Assert.Equal(0L, parser.HeaderOffset);
    }

    /// <summary>
    /// A stream with no <c>%PDF-</c> header in the search window — a fragment, an extracted revision,
    /// an embedded PDF a caller points the parser at — counts from byte 0.
    /// </summary>
    /// <remarks>
    /// Object 1 begins at byte 0 here, so an origin of "no header found" rather than 0 would place it
    /// one byte outside the file and <see cref="PdfParser.GetObject"/> would refuse it. That is the
    /// whole difference between the two, and it is why the search's miss is folded to 0 rather than
    /// carried as -1.
    /// </remarks>
    [Fact]
    public void Headerless_stream_still_counts_its_offsets_from_byte_zero()
    {
        var parser = new PdfParser(new MemoryStream(HeaderlessClassicFile()));
        parser.ParseCrossReferences();

        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(0L, parser.HeaderOffset);

        var first = Assert.IsType<PdfDictionary>(parser.GetObject(1));
        Assert.Equal(1, first.GetInteger(new PdfName("Marker")));
        Assert.Null(PdfParser.LastObjectParseError);
    }

    // ================================================================
    //  Assertions
    // ================================================================

    /// <summary>
    /// What reading the file through its cross-reference chain — and not through the recovery scan —
    /// produces: nothing skipped, the page defined only in revision 1, and revision 2's deletion.
    /// </summary>
    private static void AssertBothRevisionsWereRead(PdfParser parser, long writtenStartxref,
        int expectedSectionsReadFromByteZero)
    {
        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(writtenStartxref, parser.StartXrefOffset);

        // Which reading found the sections — the fallback succeeds silently, so this is the only
        // evidence of it. Two sections in every fixture.
        Assert.Equal(expectedSectionsReadFromByteZero, parser.SectionsReadFromByteZero);
        Assert.Equal(2, parser.CrossReferenceSectionPositions.Count);

        var page = Assert.IsType<PdfDictionary>(parser.GetObject(3));
        Assert.Equal(PdfName.Page, page.GetName(PdfName.Type));
        Assert.Null(PdfParser.LastObjectParseError);

        // Revision 2 deleted object 5. A scan-rebuilt table finds `5 0 obj` in revision 1's bytes
        // and serves it, so this is the assertion that tells the chain from the recovery.
        Assert.Equal(CrossReferenceEntry.EntryType.Free, parser.CrossReferenceTable.GetEntry(5).Type);
        Assert.IsType<PdfNull>(parser.GetObject(5));
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static PdfParser ParseWithHeader(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    private static byte[] Concat(string prefix, byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes(prefix);
        var all = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, all, 0, head.Length);
        Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
        return all;
    }

    /// <summary>A PDF assembled byte by byte, recording where each object's header starts.</summary>
    private sealed class PdfBytes
    {
        private readonly MemoryStream _ms = new MemoryStream();

        internal readonly Dictionary<int, long> Positions = new Dictionary<int, long>();

        internal long Position => _ms.Position;

        internal PdfBytes Ascii(string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            _ms.Write(bytes, 0, bytes.Length);
            return this;
        }

        internal PdfBytes Raw(byte[] bytes)
        {
            _ms.Write(bytes, 0, bytes.Length);
            return this;
        }

        internal PdfBytes Obj(int number, string body)
        {
            Positions[number] = Position;
            return Ascii(Num(number) + " 0 obj\n" + body + "\nendobj\n");
        }

        internal byte[] ToArray() => _ms.ToArray();
    }

    /// <summary>Key under which the builders record where revision 1's section starts.</summary>
    private const int Revision1SectionKey = -1;

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string InUse(long offset) =>
        offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n";

    private static string Free(long nextFreeObject, int generation) =>
        nextFreeObject.ToString("D10", CultureInfo.InvariantCulture) + " " +
        generation.ToString("D5", CultureInfo.InvariantCulture) + " f \n";

    /// <summary>
    /// <paramref name="prefix"/>, then a classic file: revision 1 defines objects 1–5, revision 2
    /// deletes object 5. Each revision writes its offsets counted either from the header (§7.5.2) or
    /// from byte 0 of the output; revision 2's <c>/Prev</c> repeats revision 1's <c>startxref</c>
    /// value exactly as written, which is what a writer taking the parser's
    /// <see cref="PdfParser.StartXrefOffset"/> does.
    /// </summary>
    /// <param name="positions">Stream positions of objects 1–5, and of revision 1's section under
    /// <see cref="Revision1SectionKey"/> as a <b>written</b> value.</param>
    private static byte[] TwoClassicRevisions(string prefix, bool revision1FromHeader, bool revision2FromHeader,
        out long startxref, out Dictionary<int, long> positions)
    {
        var pdf = new PdfBytes();
        pdf.Ascii(prefix);
        long header = pdf.Position;
        long origin1 = revision1FromHeader ? header : 0;
        long origin2 = revision2FromHeader ? header : 0;

        pdf.Ascii("%PDF-1.4\n");
        pdf.Obj(1, Catalog).Obj(2, Pages).Obj(3, Page).Obj(4, "<< /Marker 4 >>").Obj(5, "<< /Marker 5 >>");

        long xref1 = pdf.Position - origin1;
        pdf.Ascii("xref\n0 6\n").Ascii(Free(0, 65535));
        for (int n = 1; n <= 5; n++) pdf.Ascii(InUse(pdf.Positions[n] - origin1));
        pdf.Ascii("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + Num(xref1) + "\n%%EOF\n");

        // ---- revision 2: an ordinary incremental update that deletes object 5 ----
        long xref2 = pdf.Position - origin2;
        pdf.Ascii("xref\n5 1\n").Ascii(Free(0, 1))
           .Ascii("trailer\n<< /Size 6 /Root 1 0 R /Prev " + Num(xref1) + " >>\nstartxref\n" +
                  Num(xref2) + "\n%%EOF\n");

        startxref = xref2;
        positions = new Dictionary<int, long>(pdf.Positions) { [Revision1SectionKey] = xref1 };
        return pdf.ToArray();
    }

    /// <summary>
    /// Objects 1 and 2 with a classic table and no <c>%PDF-</c> header anywhere; object 1 begins at
    /// byte 0.
    /// </summary>
    private static byte[] HeaderlessClassicFile()
    {
        var pdf = new PdfBytes();
        pdf.Obj(1, "<< /Marker 1 >>").Obj(2, "<< /Marker 2 >>");

        long xref = pdf.Position;
        pdf.Ascii("xref\n0 3\n").Ascii(Free(0, 65535))
           .Ascii(InUse(pdf.Positions[1])).Ascii(InUse(pdf.Positions[2]))
           .Ascii("trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n" + Num(xref) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>
    /// A cross-reference-stream file with no prefix: revision 1 defines objects 1, 2, 3 and 5,
    /// revision 2 deletes object 5 with a type-0 entry. Offsets count from byte 0, which is the
    /// header, so prepending bytes afterwards yields a file correct under §7.5.2.
    /// </summary>
    private static byte[] TwoXrefStreamRevisions(out long startxref, out Dictionary<int, long> positions)
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        pdf.Obj(1, Catalog).Obj(2, Pages).Obj(3, Page).Obj(5, "<< /Marker 5 >>");

        long xref1 = pdf.Position;
        WriteXrefStream(pdf, 6, "/Size 7 /Root 1 0 R",
            (0, 0, 0, 65535),
            (1, 1, pdf.Positions[1], 0),
            (2, 1, pdf.Positions[2], 0),
            (3, 1, pdf.Positions[3], 0),
            (5, 1, pdf.Positions[5], 0),
            (6, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        // ---- revision 2: deletes object 5 ----
        long xref2 = pdf.Position;
        WriteXrefStream(pdf, 7, "/Size 8 /Root 1 0 R /Prev " + Num(xref1),
            (5, 0, 0, 1),
            (7, 1, xref2, 0));
        pdf.Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");

        startxref = xref2;
        positions = new Dictionary<int, long>(pdf.Positions);
        return pdf.ToArray();
    }

    /// <summary>
    /// One uncompressed cross-reference stream, <c>/W [1 4 2]</c>, with a one-entry <c>/Index</c>
    /// subsection per row so the rows need not be contiguous.
    /// </summary>
    private static void WriteXrefStream(PdfBytes pdf, int number, string trailerEntries,
        params (int objectNumber, byte type, long field2, int field3)[] rows)
    {
        var index = new StringBuilder();
        var data = new byte[rows.Length * 7];
        for (int i = 0; i < rows.Length; i++)
        {
            var (objectNumber, type, field2, field3) = rows[i];
            index.Append(Num(objectNumber)).Append(" 1 ");
            int at = i * 7;
            data[at] = type;
            data[at + 1] = (byte)(field2 >> 24);
            data[at + 2] = (byte)(field2 >> 16);
            data[at + 3] = (byte)(field2 >> 8);
            data[at + 4] = (byte)field2;
            data[at + 5] = (byte)(field3 >> 8);
            data[at + 6] = (byte)field3;
        }

        pdf.Positions[number] = pdf.Position;
        pdf.Ascii(Num(number) + " 0 obj\n<< /Type /XRef " + trailerEntries + " /W [1 4 2] /Index [" +
                  index.ToString().TrimEnd() + "] /Length " + Num(data.Length) + " >>\nstream\n")
           .Raw(data)
           .Ascii("\nendstream\nendobj\n");
    }
}
