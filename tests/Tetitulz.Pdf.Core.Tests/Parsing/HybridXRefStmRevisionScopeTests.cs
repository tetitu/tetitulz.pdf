// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// How far a hybrid-reference file's <c>/XRefStm</c> reaches. ISO 32000-2 §7.5.8.4: "if an entry is
/// not found in any given standard cross-reference section, the search shall proceed to a
/// cross-reference stream specified by the XRefStm entry before looking in the previous
/// cross-reference section (the Prev entry in the trailer)" — and, for the object §7.5.8.4's
/// construction hides behind a free entry, a reader "shall look in the cross-reference stream first,
/// find the object there, and shall ignore the free entry in the previous section."
/// </summary>
/// <remarks>
/// <para>So an <c>/XRefStm</c> ranks below the classic table of the section whose trailer names it and
/// above every older section — and below every <b>newer</b> one, which the search has already asked.
/// The free entry it may look past is one its own revision wrote (the same section, which is how
/// single-section hybrid producers write it, or an older one); a free entry a later revision wrote is
/// a deletion.</para>
/// <para>The walk installs the newest revision's entries first, so a merge letting an <c>/XRefStm</c>
/// entry displace <b>any</b> installed free entry would bring an object a later classic update
/// deleted back through the older revision's cross-reference stream.
/// <c>HybridXrefAndSectionRecoveryTests</c> and <c>HybridXrefAndOffsetTests</c> hold the same-section
/// cases; these two hold the revision boundary, one on each side of it.</para>
/// </remarks>
public class HybridXRefStmRevisionScopeTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PageTree = "<< /Type /Pages /Kids [] /Count 0 >>";

    /// <summary>
    /// Revision 1 hides object 3 in an object stream; revision 2, an ordinary classic update, deletes
    /// it. Revision 2's free entry is installed first and is the answer.
    /// </summary>
    [Fact]
    public void Object_freed_by_a_newer_classic_update_stays_freed_although_an_older_XRefStm_lists_it()
    {
        PdfParser parser = ParseFrom(HybridRevisionThenClassicUpdate(update: Update.DeletesObject3));

        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(CrossReferenceEntry.EntryType.Free, parser.CrossReferenceTable.GetEntry(3).Type);
        Assert.IsType<PdfNull>(parser.GetObject(3));
    }

    /// <summary>
    /// The acceptance control on the same fixture: revision 2 changes the catalog and leaves object 3
    /// alone, so the free entry for it is revision 1's own §7.5.8.4 marker — reached through
    /// <c>/Prev</c>, not in the newest section — and revision 1's <c>/XRefStm</c> still supersedes it.
    /// </summary>
    [Fact]
    public void Hidden_object_of_an_older_hybrid_revision_still_resolves_through_that_revisions_XRefStm()
    {
        PdfParser parser = ParseFrom(HybridRevisionThenClassicUpdate(update: Update.RewritesTheCatalog));

        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, parser.CrossReferenceTable.GetEntry(3).Type);
        Assert.Equal(1, MarkerOf(parser.GetObject(3)));

        // The update itself was read: the catalog is revision 2's copy.
        var catalog = Assert.IsType<PdfDictionary>(parser.GetObject(1));
        Assert.Equal(2, catalog.GetInteger(new PdfName("Marker")));
    }

    // ================================================================
    //  Fixture
    // ================================================================

    public enum Update
    {
        DeletesObject3,
        RewritesTheCatalog,
    }

    private static PdfParser ParseFrom(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    private static long MarkerOf(PdfObject obj)
    {
        var dict = Assert.IsType<PdfDictionary>(obj);
        return Assert.IsType<PdfNumber>(dict[new PdfName("Marker")]).LongValue;
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string InUse(long offset) =>
        offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n";

    private static string Free(int generation) =>
        "0000000000 " + generation.ToString("D5", CultureInfo.InvariantCulture) + " f \n";

    /// <summary>
    /// Revision 1 is a hybrid-reference file: object 3 (<c>/Marker 1</c>) lives in object stream 4,
    /// its classic table marks 3 free and its trailer names cross-reference stream 5, which records 3
    /// as a type-2 entry. Revision 2 is a classic update chained by <c>/Prev</c>.
    /// </summary>
    private static byte[] HybridRevisionThenClassicUpdate(Update update)
    {
        var file = new MemoryStream();
        void Ascii(string text) { byte[] b = Encoding.ASCII.GetBytes(text); file.Write(b, 0, b.Length); }

        Ascii("%PDF-1.5\n");
        long o1 = file.Position; Ascii("1 0 obj\n" + Catalog + "\nendobj\n");
        long o2 = file.Position; Ascii("2 0 obj\n" + PageTree + "\nendobj\n");

        const string Index = "3 0 ";
        string streamData = Index + "<< /Marker 1 >>";
        long o4 = file.Position;
        Ascii("4 0 obj\n<< /Type /ObjStm /N 1 /First " + Num(Index.Length) + " /Length " +
              Num(streamData.Length) + " >>\nstream\n" + streamData + "\nendstream\nendobj\n");

        // /W [1 4 2]: object 3 is entry 0 of object stream 4.
        byte[] row = { 2, 0, 0, 0, 4, 0, 0 };
        long o5 = file.Position;
        Ascii("5 0 obj\n<< /Type /XRef /Size 6 /W [1 4 2] /Index [3 1] /Root 1 0 R /Length " +
              Num(row.Length) + " >>\nstream\n");
        file.Write(row, 0, row.Length);
        Ascii("\nendstream\nendobj\n");

        long xref1 = file.Position;
        Ascii("xref\n0 6\n" + Free(65535) + InUse(o1) + InUse(o2) +
              Free(65535) +                                   // object 3: hidden, per §7.5.8.4
              InUse(o4) + InUse(o5));
        Ascii("trailer\n<< /Size 6 /Root 1 0 R /XRefStm " + Num(o5) + " >>\n");
        Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        // ---- revision 2 ----
        if (update == Update.DeletesObject3)
        {
            long xref2 = file.Position;
            Ascii("xref\n3 1\n" + Free(1));
            Ascii("trailer\n<< /Size 6 /Root 1 0 R /Prev " + Num(xref1) + " >>\n");
            Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        }
        else
        {
            long o1b = file.Position;
            Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Marker 2 >>\nendobj\n");
            long xref2 = file.Position;
            Ascii("xref\n1 1\n" + InUse(o1b));
            Ascii("trailer\n<< /Size 6 /Root 1 0 R /Prev " + Num(xref1) + " >>\n");
            Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        }

        return file.ToArray();
    }
}
