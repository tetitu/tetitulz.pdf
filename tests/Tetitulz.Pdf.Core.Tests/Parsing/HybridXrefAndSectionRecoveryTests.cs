// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// The cross-reference walk: hybrid-reference files (§7.5.8.4), unreadable sections, offsets that
/// are not byte positions, and the entry budget that bounds the whole <c>/Prev</c> chain.
///
/// <para><b>Every assertion here is on a parsed object or on the reported reason</b> — never on
/// "the document opened", which a file with an empty cross-reference table also does.</para>
/// </summary>
public class HybridXrefAndSectionRecoveryTests
{
    // ===============================================================
    //  Byte-level fixture construction
    // ===============================================================

    /// <summary>A PDF assembled byte by byte, so an xref offset can be a real byte position.</summary>
    private sealed class PdfBytes
    {
        private readonly MemoryStream _ms = new MemoryStream();

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

        /// <summary>One 20-byte in-use entry of a classic table (§7.5.4).</summary>
        internal PdfBytes InUse(long offset) =>
            Ascii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

        /// <summary>One 20-byte free entry — what §7.5.8.4 writes for a compressed object.</summary>
        internal PdfBytes Free() => Ascii("0000000000 65535 f \n");

        internal byte[] ToArray() => _ms.ToArray();
    }

    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PageTree = "<< /Type /Pages /Kids [] /Count 0 >>";

    /// <summary>
    /// An object stream holding exactly one object: <c>4 0 obj &lt;&lt; /Marker n &gt;&gt;</c>.
    /// Written uncompressed so the fixture stays readable and the test is about the xref, not Flate.
    /// </summary>
    private static string ObjectStreamHolding(int objectNumber, int marker)
    {
        string index = objectNumber.ToString(CultureInfo.InvariantCulture) + " 0 ";
        string body = "<< /Marker " + marker.ToString(CultureInfo.InvariantCulture) + " >>";
        string data = index + body;
        return "3 0 obj\n<< /Type /ObjStm /N 1 /First " + index.Length +
               " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream\nendobj\n";
    }

    /// <summary>
    /// A cross-reference stream naming <paramref name="objectNumber"/> as object
    /// <paramref name="index"/> of object stream <paramref name="container"/> — a type-2 entry,
    /// which is the only shape a compressed object has.
    /// </summary>
    private static void WriteXRefStm(PdfBytes pdf, int streamObjectNumber, int objectNumber,
                                     int container, int index)
    {
        var data = new byte[]
        {
            2,                                                       // field 1: type 2
            (byte)(container >> 24), (byte)(container >> 16),
            (byte)(container >> 8),  (byte)container,                // field 2: container objnum
            (byte)(index >> 8), (byte)index,                         // field 3: index within it
        };

        pdf.Ascii(streamObjectNumber.ToString(CultureInfo.InvariantCulture) +
                  " 0 obj\n<< /Type /XRef /Size 8 /W [1 4 2] /Index [" +
                  objectNumber.ToString(CultureInfo.InvariantCulture) + " 1] /Root 1 0 R /Length " +
                  data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n")
           .Raw(data)
           .Ascii("\nendstream\nendobj\n");
    }

    private static PdfParser ParseFrom(byte[] pdf, int maxEntries = 0)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        if (maxEntries > 0) parser.MaxCrossReferenceEntries = maxEntries;
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    private static long MarkerOf(PdfObject obj)
    {
        var dict = Assert.IsType<PdfDictionary>(obj);
        PdfObject marker = dict[new PdfName("Marker")];
        return Assert.IsType<PdfNumber>(marker).LongValue;
    }

    // ===============================================================
    //  1. §7.5.8.4 — the classic table's FREE entry must not bury the /XRefStm one
    // ===============================================================

    [Fact]
    public void Compressed_object_marked_free_by_the_classic_table_resolves_through_the_XRefStm()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");

        long o1 = pdf.Position; pdf.Ascii("1 0 obj\n" + Catalog + "\nendobj\n");
        long o2 = pdf.Position; pdf.Ascii("2 0 obj\n" + PageTree + "\nendobj\n");
        long o3 = pdf.Position; pdf.Ascii(ObjectStreamHolding(objectNumber: 4, marker: 2));
        long o5 = pdf.Position; WriteXRefStm(pdf, streamObjectNumber: 5, objectNumber: 4, container: 3, index: 0);

        long xref = pdf.Position;
        pdf.Ascii("xref\n0 6\n").Free().InUse(o1).InUse(o2).InUse(o3)
           // Object 4 is FREE here — §7.5.8.4's backwards-compatibility construction, so that a
           // pre-1.5 reader treats the object it cannot read as deleted.
           .Free()
           .InUse(o5)
           .Ascii("trailer\n<< /Size 6 /Root 1 0 R /XRefStm " +
                  o5.ToString(CultureInfo.InvariantCulture) + " >>\n")
           .Ascii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        PdfParser parser = ParseFrom(pdf.ToArray());

        Assert.Equal(CrossReferenceEntry.EntryType.Compressed,
            parser.CrossReferenceTable.GetEntry(4).Type);
        Assert.Equal(2, MarkerOf(parser.GetObject(4)));
    }

    // ===============================================================
    //  2. §7.5.8.4 — the contemporary /XRefStm outranks the older /Prev section
    // ===============================================================

    [Fact]
    public void Object_superseded_in_the_XRefStm_wins_over_the_previous_revisions_copy()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");

        // --- revision 1: object 4 is an ordinary in-file object -------------------
        long o1 = pdf.Position; pdf.Ascii("1 0 obj\n" + Catalog + "\nendobj\n");
        long o2 = pdf.Position; pdf.Ascii("2 0 obj\n" + PageTree + "\nendobj\n");
        long o4 = pdf.Position; pdf.Ascii("4 0 obj\n<< /Marker 1 >>\nendobj\n");

        long xref1 = pdf.Position;
        pdf.Ascii("xref\n0 3\n").Free().InUse(o1).InUse(o2)
           .Ascii("4 1\n").InUse(o4)
           .Ascii("trailer\n<< /Size 5 /Root 1 0 R >>\n")
           .Ascii("startxref\n" + xref1.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        // --- revision 2: object 4 is superseded, and lives in an object stream ----
        long o3 = pdf.Position; pdf.Ascii(ObjectStreamHolding(objectNumber: 4, marker: 2));
        long o5 = pdf.Position; WriteXRefStm(pdf, streamObjectNumber: 5, objectNumber: 4, container: 3, index: 0);

        long xref2 = pdf.Position;
        pdf.Ascii("xref\n0 1\n").Free()
           .Ascii("3 1\n").InUse(o3)
           .Ascii("5 1\n").InUse(o5)
           .Ascii("trailer\n<< /Size 6 /Root 1 0 R /Prev " +
                  xref1.ToString(CultureInfo.InvariantCulture) + " /XRefStm " +
                  o5.ToString(CultureInfo.InvariantCulture) + " >>\n")
           .Ascii("startxref\n" + xref2.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        PdfParser parser = ParseFrom(pdf.ToArray());

        // 1 is the revision the /XRefStm supersedes. Reading it back is not a cosmetic defect:
        // for a real document it is a pre-redaction content stream or a stale field value.
        Assert.Equal(2, MarkerOf(parser.GetObject(4)));
    }

    // ===============================================================
    //  3. An unreadable xref STREAM must be skipped, not thrown out of the open
    // ===============================================================

    [Fact]
    public void Malformed_newest_xref_stream_is_skipped_instead_of_aborting_the_open()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");

        long o1 = pdf.Position; pdf.Ascii("1 0 obj\n" + Catalog + "\nendobj\n");
        long o2 = pdf.Position; pdf.Ascii("2 0 obj\n" + PageTree + "\nendobj\n");

        long xref1 = pdf.Position;
        pdf.Ascii("xref\n0 3\n").Free().InUse(o1).InUse(o2)
           .Ascii("trailer\n<< /Size 3 /Root 1 0 R >>\n")
           .Ascii("startxref\n" + xref1.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        // The newest section is an xref stream whose /Filter chain is longer than
        // PdfLimits.MaxFilterChainLength, so decoding it raises PdfFilterException — which derives
        // from Exception directly, so neither `catch (PdfParseException)` nor `catch (PdfException)`
        // would have caught it, and the whole open failed on a file whose /Prev is intact.
        var filters = new StringBuilder("[");
        for (int i = 0; i <= PdfLimits.MaxFilterChainLength; i++) filters.Append(" /FlateDecode");
        filters.Append(" ]");

        long o5 = pdf.Position;
        pdf.Ascii("5 0 obj\n<< /Type /XRef /Size 6 /W [1 4 2] /Index [1 1] /Root 1 0 R /Prev " +
                  xref1.ToString(CultureInfo.InvariantCulture) + " /Filter " + filters +
                  " /Length 7 >>\nstream\n")
           .Raw(new byte[] { 1, 0, 0, 0, 9, 0, 0 })
           .Ascii("\nendstream\nendobj\n")
           .Ascii("startxref\n" + o5.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        using var document = PdfDocument.Open(new MemoryStream(pdf.ToArray()));

        // The unreadable section contributes nothing, and what the rest of the file defines is
        // still there — the documented behaviour, which the escaping exception bypassed entirely.
        var catalog = Assert.IsType<PdfDictionary>(document.Parser.GetObject(1));
        Assert.Equal(PdfName.Catalog, catalog.GetName(PdfName.Type));
        Assert.NotNull(PdfParser.LastXrefSectionError);
        Assert.Contains("xref section at " + o5.ToString(CultureInfo.InvariantCulture),
            PdfParser.LastXrefSectionError);
    }

    // ===============================================================
    //  4. Offsets that are not byte positions
    // ===============================================================

    [Fact]
    public void Adversarial_negative_classic_entry_offset_is_refused_instead_of_reaching_Stream_Position()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");

        long o1 = pdf.Position; pdf.Ascii("1 0 obj\n" + Catalog + "\nendobj\n");
        long o2 = pdf.Position; pdf.Ascii("2 0 obj\n" + PageTree + "\nendobj\n");
        pdf.Ascii("3 0 obj\n<< /Marker 7 >>\nendobj\n");

        long xref = pdf.Position;
        pdf.Ascii("xref\n0 4\n").Free().InUse(o1).InUse(o2)
           // NumberStyles.Integer accepts the sign, so this parses to Offset = -1.
           .Ascii("-000000001 00000 n \n")
           .Ascii("trailer\n<< /Size 4 /Root 1 0 R >>\n")
           .Ascii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        PdfParser parser = ParseFrom(pdf.ToArray());

        // The guard's own evidence: the reason is reported, and the object is recovered by the
        // scan fallback rather than throwing ArgumentOutOfRangeException out of GetObject.
        Assert.Contains("Negative offset", PdfParser.LastXrefSectionError);
        Assert.Equal(7, MarkerOf(parser.GetObject(3)));
    }

    [Fact]
    public void Adversarial_negative_Prev_offset_is_dropped_rather_than_enqueued()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");

        long o1 = pdf.Position; pdf.Ascii("1 0 obj\n" + Catalog + "\nendobj\n");
        long o2 = pdf.Position; pdf.Ascii("2 0 obj\n" + PageTree + "\nendobj\n");

        long xref = pdf.Position;
        pdf.Ascii("xref\n0 3\n").Free().InUse(o1).InUse(o2)
           .Ascii("trailer\n<< /Size 3 /Root 1 0 R /Prev -1 >>\n")
           .Ascii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        PdfParser parser = ParseFrom(pdf.ToArray());

        Assert.Contains("outside the file", PdfParser.LastXrefSectionError);
        var catalog = Assert.IsType<PdfDictionary>(parser.GetObject(1));
        Assert.Equal(PdfName.Catalog, catalog.GetName(PdfName.Type));
    }

    // ===============================================================
    //  5. The entry budget spans the whole /Prev chain
    // ===============================================================

    [Fact]
    public void Adversarial_chained_xref_streams_share_one_entry_budget()
    {
        // Each section declares three entries over a DISJOINT object-number range, which is what
        // stops the first-wins table from deduplicating them — the amplification the per-section
        // cap could not see.
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");

        pdf.Ascii("1 0 obj\n" + Catalog + "\nendobj\n");

        long older = pdf.Position; WriteThreeEntrySection(pdf, objectNumber: 6, firstObject: 10, prev: -1);
        long newer = pdf.Position; WriteThreeEntrySection(pdf, objectNumber: 7, firstObject: 20, prev: older);

        pdf.Ascii("startxref\n" + newer.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        PdfParser parser = ParseFrom(pdf.ToArray(), maxEntries: 4);

        // Three from the newest section plus one from the older one, then the refusal: a budget
        // that reset per section would have admitted all six.
        Assert.Equal(4, parser.CrossReferenceTable.Count);
        Assert.Contains("-entry limit for one document", PdfParser.LastXrefSectionError);
    }

    [Fact]
    public void The_shared_entry_budget_is_generous_enough_for_an_ordinary_chain()
    {
        // The counterpart of the test above: with the real ceiling in force, the same file keeps
        // every entry of both sections. A guard that refused here would be a regression, not a
        // defence.
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        pdf.Ascii("1 0 obj\n" + Catalog + "\nendobj\n");
        long older = pdf.Position; WriteThreeEntrySection(pdf, objectNumber: 6, firstObject: 10, prev: -1);
        long newer = pdf.Position; WriteThreeEntrySection(pdf, objectNumber: 7, firstObject: 20, prev: older);
        pdf.Ascii("startxref\n" + newer.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        PdfParser parser = ParseFrom(pdf.ToArray());

        Assert.Equal(6, parser.CrossReferenceTable.Count);
        Assert.Null(PdfParser.LastXrefSectionError);
    }

    /// <summary>
    /// One cross-reference stream declaring three type-1 entries for
    /// <paramref name="firstObject"/>..+2, all pointing at the same harmless offset.
    /// </summary>
    private static void WriteThreeEntrySection(PdfBytes pdf, int objectNumber, int firstObject, long prev)
    {
        var data = new byte[3 * 7];
        for (int i = 0; i < 3; i++)
        {
            data[i * 7] = 1;          // type 1
            data[i * 7 + 4] = 9;      // offset 9 — inside the file, never dereferenced here
        }

        pdf.Ascii(objectNumber.ToString(CultureInfo.InvariantCulture) +
                  " 0 obj\n<< /Type /XRef /Size 40 /W [1 4 2] /Index [" +
                  firstObject.ToString(CultureInfo.InvariantCulture) + " 3] /Root 1 0 R" +
                  (prev >= 0 ? " /Prev " + prev.ToString(CultureInfo.InvariantCulture) : "") +
                  " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n")
           .Raw(data)
           .Ascii("\nendstream\nendobj\n");
    }
}
