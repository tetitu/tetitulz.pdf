// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// <see cref="PdfIncrementalSaveValidator"/> counts revisions off the parser's own cross-reference
/// walk: every one of them, at its stream position, and none for a hybrid file's <c>/XRefStm</c>
/// stream.
/// </summary>
/// <remarks>
/// <para><b>It could never count past two.</b> The collector took the newest section from one parse
/// and then, per <c>/Prev</c> hop, re-parsed the whole document from its tail — which returned the
/// same merged trailer every time, whose <c>/Prev</c> was the newest section's again. The loop
/// stopped on the second hop, so a three-revision document reported two; and every offset was read
/// as a stream position, which a file with bytes before its header (ISO 32000-2 §7.5.2) is not.</para>
///
/// <para>The fixtures are the ones Core's <c>CrossReferenceSectionPositionsTests</c> uses, copied
/// because a test project may not reference another: three classic revisions with and without a
/// prefix, and a hybrid update whose classic section names a cross-reference stream.</para>
/// </remarks>
public class IncrementalSaveRevisionCountTests
{
    private const string Prefix = "junk-junk-junk\n";

    [Theory]
    [InlineData("")]
    [InlineData(Prefix)]
    public void ThreeRevisions_AreCountedAsThree_AtTheirStreamPositions(string prefix)
    {
        byte[] pdf = ThreeClassicRevisions(prefix, out long[] sections);

        IncrementalSaveInfo info = Inspect(pdf);

        Assert.Equal(3, info.RevisionCount);
        Assert.Equal(new[] { sections[1], sections[2], sections[3] }, info.RevisionOffsets);
        foreach (long at in info.RevisionOffsets)   // a position into the bytes as read, not an offset as written
            Assert.Equal("xref", Encoding.ASCII.GetString(pdf, (int)at, 4));
        Assert.True(info.SignaturesPreserved, "append-only revisions preserve the first revision's bytes");
    }

    /// <summary>§7.5.8.4: the stream a classic section's <c>/XRefStm</c> names is part of that revision.</summary>
    [Fact]
    public void AHybridUpdatesXRefStmStream_IsNotARevision()
    {
        byte[] pdf = HybridUpdate(out long xref1, out long xrefStm, out long xref2);

        IncrementalSaveInfo info = Inspect(pdf);

        Assert.Equal(2, info.RevisionCount);
        Assert.Equal(new[] { xref1, xref2 }, info.RevisionOffsets);
        Assert.DoesNotContain(xrefStm, info.RevisionOffsets);
    }

    /// <summary>
    /// A file the parser could only recover by scanning has no chain to count, and keeps the
    /// one-revision answer it always had rather than reporting none.
    /// </summary>
    [Fact]
    public void ARecoveredFile_KeepsTheOneRevisionAnswer()
    {
        byte[] damaged = WithStartxrefIntoTheHeader(ThreeClassicRevisions("", out _));

        IncrementalSaveInfo info = Inspect(damaged);

        Assert.Equal(1, info.RevisionCount);
        Assert.Single(info.RevisionOffsets);
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static IncrementalSaveInfo Inspect(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfIncrementalSaveValidator.Inspect(ms);
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
