// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// <see cref="PdfParser.CrossReferenceSectionPositions"/>: the stream position of every section the
/// walk read, in the order it read them — so a host after revision boundaries need not re-parse the
/// whole document once per <c>/Prev</c> hop, which goes wrong past the second hop, because each
/// re-parse re-reads the file's own tail.
/// </summary>
public class CrossReferenceSectionPositionsTests
{
    private const string Prefix = "junk-junk-junk\n";

    /// <summary>
    /// Three classic revisions: newest first, and positions rather than written offsets — the
    /// prefixed variant is what tells the two apart.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(Prefix)]
    public void Three_classic_revisions_are_listed_newest_first_at_their_stream_positions(string prefix)
    {
        byte[] pdf = ThreeClassicRevisions(prefix, out long[] sections);

        PdfParser parser = Parse(pdf);

        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(new[] { sections[3], sections[2], sections[1] }, parser.CrossReferenceSectionPositions);
    }

    [Fact]
    public void Three_cross_reference_stream_revisions_are_listed_newest_first()
    {
        byte[] pdf = ThreeXrefStreamRevisions(out long[] sections);

        PdfParser parser = Parse(pdf);

        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(new[] { sections[3], sections[2], sections[1] }, parser.CrossReferenceSectionPositions);
    }

    /// <summary>
    /// §7.5.8.4: a classic section's <c>/XRefStm</c> stream is searched before its <c>/Prev</c>, and
    /// that is the order the walk reads them in.
    /// </summary>
    [Fact]
    public void A_hybrid_files_XRefStm_stream_is_listed_between_its_own_section_and_its_Prev()
    {
        byte[] pdf = HybridUpdate(out long xref1, out long xrefStm, out long xref2);

        PdfParser parser = Parse(pdf);

        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(new[] { xref2, xrefStm, xref1 }, parser.CrossReferenceSectionPositions);
    }

    /// <summary>A file recovered by scanning read no section at all.</summary>
    [Fact]
    public void Scan_recovery_leaves_the_list_empty()
    {
        byte[] pdf = ThreeClassicRevisions("", out _);
        byte[] damaged = WithStartxrefIntoTheHeader(pdf);

        PdfParser parser = Parse(damaged);

        Assert.Contains("was skipped", PdfParser.LastXrefSectionError);
        Assert.Empty(parser.CrossReferenceSectionPositions);
        Assert.Empty(parser.RevisionSectionPositions);
    }

    /// <summary>
    /// <see cref="PdfParser.RevisionSectionPositions"/> is the same list without the hybrid stream —
    /// one entry per revision, which is what a caller counting revisions reads. §7.5.8.4 makes an
    /// <c>/XRefStm</c> stream part of the revision whose classic section names it.
    /// </summary>
    [Fact]
    public void The_revision_list_leaves_out_a_hybrid_files_XRefStm_stream()
    {
        byte[] pdf = HybridUpdate(out long xref1, out long xrefStm, out long xref2);

        PdfParser parser = Parse(pdf);

        Assert.Equal(new[] { xref2, xref1 }, parser.RevisionSectionPositions);
        Assert.Contains(xrefStm, parser.CrossReferenceSectionPositions);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Prefix)]
    public void The_revision_list_is_the_section_list_when_no_section_is_hybrid(string prefix)
    {
        byte[] pdf = ThreeClassicRevisions(prefix, out long[] sections);

        PdfParser parser = Parse(pdf);

        Assert.Equal(new[] { sections[3], sections[2], sections[1] }, parser.RevisionSectionPositions);
        Assert.Equal(parser.CrossReferenceSectionPositions, parser.RevisionSectionPositions);
    }

    /// <summary>
    /// A section that could not be read is not listed, and the walk cannot continue past it: the
    /// middle revision's <c>xref</c> keyword is overwritten, so only the newest section is read.
    /// </summary>
    [Fact]
    public void A_section_that_could_not_be_read_is_not_listed()
    {
        byte[] pdf = ThreeClassicRevisions("", out long[] sections);
        byte[] damaged = (byte[])pdf.Clone();
        foreach (char c in "xxxx") damaged[sections[2] + Array.IndexOf("xxxx".ToCharArray(), c)] = (byte)'!';

        PdfParser parser = Parse(damaged);

        Assert.Contains("xref section at " + sections[2].ToString(CultureInfo.InvariantCulture), PdfParser.LastXrefSectionError);
        Assert.Equal(new[] { sections[3] }, parser.CrossReferenceSectionPositions);
    }

    /// <summary>A second parse of another document starts from an empty list.</summary>
    [Fact]
    public void The_list_is_reset_for_every_parse()
    {
        byte[] three = ThreeClassicRevisions("", out _);
        byte[] one = HybridUpdate(out _, out _, out _);

        var parser = new PdfParser(new MemoryStream(three));
        parser.ParseCrossReferences();
        Assert.Equal(3, parser.CrossReferenceSectionPositions.Count);
        Assert.Equal(3, parser.RevisionSectionPositions.Count);

        var again = new PdfParser(new MemoryStream(one));
        again.ParseCrossReferences();
        Assert.Equal(3, again.CrossReferenceSectionPositions.Count);
        Assert.Equal(2, again.RevisionSectionPositions.Count);   // the hybrid stream is a section, not a revision

        parser.ParseCrossReferences();
        Assert.Equal(3, parser.CrossReferenceSectionPositions.Count);
        Assert.Equal(3, parser.RevisionSectionPositions.Count);
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static PdfParser Parse(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseCrossReferences();
        return parser;
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string InUse(long offset) => offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n";

    private sealed class PdfBytes
    {
        private readonly MemoryStream _ms = new MemoryStream();
        internal long Position => _ms.Position;
        internal PdfBytes Ascii(string text) { byte[] b = Encoding.ASCII.GetBytes(text); _ms.Write(b, 0, b.Length); return this; }
        internal PdfBytes Raw(byte[] bytes) { _ms.Write(bytes, 0, bytes.Length); return this; }
        internal byte[] ToArray() => _ms.ToArray();
    }

    /// <summary>
    /// Revision 1 defines objects 1 and 2; revisions 2 and 3 each rewrite object 2. Offsets count
    /// from the header (§7.5.2); <paramref name="sections"/>[n] is the stream position of revision
    /// n's <c>xref</c> keyword.
    /// </summary>
    private static byte[] ThreeClassicRevisions(string prefix, out long[] sections)
    {
        var pdf = new PdfBytes();
        pdf.Ascii(prefix);
        long origin = pdf.Position;
        pdf.Ascii("%PDF-1.4\n");
        sections = new long[4];

        long o1 = pdf.Position; pdf.Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = pdf.Position; pdf.Ascii("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 /Marker 1 >>\nendobj\n");
        sections[1] = pdf.Position;
        pdf.Ascii("xref\n0 3\n0000000000 65535 f \n").Ascii(InUse(o1 - origin)).Ascii(InUse(o2 - origin))
           .Ascii("trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n" + Num(sections[1] - origin) + "\n%%EOF\n");

        for (int revision = 2; revision <= 3; revision++)
        {
            long o2b = pdf.Position;
            pdf.Ascii("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 /Marker " + Num(revision) + " >>\nendobj\n");
            sections[revision] = pdf.Position;
            pdf.Ascii("xref\n2 1\n").Ascii(InUse(o2b - origin))
               .Ascii("trailer\n<< /Size 3 /Root 1 0 R /Prev " + Num(sections[revision - 1] - origin) + " >>\n")
               .Ascii("startxref\n" + Num(sections[revision] - origin) + "\n%%EOF\n");
        }

        return pdf.ToArray();
    }

    /// <summary>The same three revisions with cross-reference streams (objects 10, 11, 12).</summary>
    private static byte[] ThreeXrefStreamRevisions(out long[] sections)
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        sections = new long[4];

        long o1 = pdf.Position; pdf.Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = pdf.Position; pdf.Ascii("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 /Marker 1 >>\nendobj\n");
        sections[1] = pdf.Position;
        XrefStream(pdf, 10, "/Size 13 /Root 1 0 R",
            (0, 0, 0, 65535), (1, 1, o1, 0), (2, 1, o2, 0), (10, 1, sections[1], 0));
        pdf.Ascii("startxref\n" + Num(sections[1]) + "\n%%EOF\n");

        for (int revision = 2; revision <= 3; revision++)
        {
            long o2b = pdf.Position;
            pdf.Ascii("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 /Marker " + Num(revision) + " >>\nendobj\n");
            sections[revision] = pdf.Position;
            XrefStream(pdf, 8 + revision, "/Size 13 /Root 1 0 R /Prev " + Num(sections[revision - 1]),
                (2, 1, o2b, 0), (8 + revision, 1, sections[revision], 0));
            pdf.Ascii("startxref\n" + Num(sections[revision]) + "\n%%EOF\n");
        }

        return pdf.ToArray();
    }

    /// <summary>
    /// Revision 1 is classic. Revision 2 is a hybrid update: object 3 hidden in object stream 4,
    /// named by cross-reference stream 5, which the classic table's trailer points at with
    /// <c>/XRefStm</c>.
    /// </summary>
    private static byte[] HybridUpdate(out long xref1, out long xrefStm, out long xref2)
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");

        long o1 = pdf.Position; pdf.Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = pdf.Position; pdf.Ascii("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        xref1 = pdf.Position;
        pdf.Ascii("xref\n0 3\n0000000000 65535 f \n").Ascii(InUse(o1)).Ascii(InUse(o2))
           .Ascii("trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n" + Num(xref1) + "\n%%EOF\n");

        const string Index = "3 0 ";
        string data = Index + "<< /Marker 1 >>";
        long o4 = pdf.Position;
        pdf.Ascii("4 0 obj\n<< /Type /ObjStm /N 1 /First " + Num(Index.Length) + " /Length " + Num(data.Length) +
                  " >>\nstream\n" + data + "\nendstream\nendobj\n");
        xrefStm = pdf.Position;
        XrefStream(pdf, 5, "/Size 6 /Root 1 0 R", (3, 2, 4, 0));

        xref2 = pdf.Position;
        pdf.Ascii("xref\n4 2\n").Ascii(InUse(o4)).Ascii(InUse(xrefStm))
           .Ascii("trailer\n<< /Size 6 /Root 1 0 R /XRefStm " + Num(xrefStm) + " /Prev " + Num(xref1) + " >>\n")
           .Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>One uncompressed cross-reference stream, <c>/W [1 4 2]</c>, one <c>/Index</c> subsection per row.</summary>
    private static void XrefStream(PdfBytes pdf, int number, string trailerEntries,
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

        pdf.Ascii(Num(number) + " 0 obj\n<< /Type /XRef " + trailerEntries + " /W [1 4 2] /Index [" +
                  index.ToString().TrimEnd() + "] /Length " + Num(data.Length) + " >>\nstream\n")
           .Raw(data)
           .Ascii("\nendstream\nendobj\n");
    }

    /// <summary>The operand of the last <c>startxref</c> overwritten, digit for digit, by 3.</summary>
    private static byte[] WithStartxrefIntoTheHeader(byte[] pdf)
    {
        byte[] damaged = (byte[])pdf.Clone();
        string text = Encoding.ASCII.GetString(damaged);
        int start = text.LastIndexOf("startxref", StringComparison.Ordinal) + "startxref".Length;
        while (damaged[start] == (byte)'\n') start++;
        int end = start;
        while (end < damaged.Length && damaged[end] >= (byte)'0' && damaged[end] <= (byte)'9') end++;
        for (int i = start; i < end; i++) damaged[i] = (byte)'0';
        damaged[end - 1] = (byte)'3';
        return damaged;
    }
}
