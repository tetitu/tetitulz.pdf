// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// When an appended revision cannot chain to a previous cross-reference section it must
/// re-declare the original objects itself — and say so when it could not.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddRecoveryEntries</c> skips <c>entry.Type != Normal</c>, which silently excludes every
/// type-2 (in-object-stream) object, and then reported only how many it <i>did</i> re-declare.
/// In a modern PDF the catalog and page tree are exactly the objects living in an <c>/ObjStm</c>
/// — including anything this library's own <c>PdfCompressedFileWriter</c> produces, which packs
/// the catalog by design — so the section it wrote named the container streams and omitted
/// everything a reader needs to open the file.
/// </para>
/// <para>
/// The diagnostic said "…N original object(s) were re-declared at their recovered offsets" and
/// never mentioned the ones it could not name, so the honest-failure channel reported a complete
/// table for an incomplete one. Both halves shipped: a cross-reference <b>stream</b> section
/// re-declares them as type-2 rows and says how many, and a <b>classic</b> table — which has no
/// type field and so genuinely cannot name them — reports the gap instead. The tests below cover
/// the stream half; <c>UnchainedRecoveryAndPackedObjectTests</c> covers the classic half.
/// </para>
/// </remarks>
public class RecoveryEntriesCompressedObjectTests
{
    /// <summary>
    /// The larger fix, and the one that shipped: a cross-reference <b>stream</b> section carries a
    /// type field, so an object living in an <c>/ObjStm</c> is re-declared as a type-2 row rather
    /// than reported as unnameable. The diagnostic must say so — reporting only the byte-offset
    /// count would understate what the section actually names.
    /// </summary>
    [Fact]
    public void UnchainedRevision_ReDeclaresObjectStreamMembersAsTypeTwoRows()
    {
        var reported = new List<string>();

        AppendUnchainedRevision(ObjectStreamPdf(0), ObjectStreamPdf(1), reported);

        string recovery = Assert.Single(reported,
            m => m.Contains("re-declared at their recovered offsets", StringComparison.Ordinal));

        Assert.Contains("as object-stream (type 2) entries", recovery, StringComparison.Ordinal);

        // …and having named them, it must not also claim the section is incomplete.
        Assert.DoesNotContain("could not be re-declared", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("the appended section is incomplete", recovery, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count has to be the number actually re-declared, not a fixed phrase. Objects 1
    /// (catalog), 2 (pages) and 4 (the marker) are packed into object stream 5; object 3 is the
    /// page and object 5 the container, both top-level. The revision below rewrites none of them,
    /// so two are named by byte offset and three as type-2 rows.
    /// </summary>
    [Fact]
    public void UnchainedRevision_NamesHowManyObjectStreamMembersItReDeclared()
    {
        var reported = new List<string>();

        AppendUnchainedRevision(ObjectStreamPdf(0), ObjectStreamPdf(1), reported);

        string recovery = Assert.Single(reported,
            m => m.Contains("re-declared at their recovered offsets", StringComparison.Ordinal));

        Assert.Contains("3 as object-stream (type 2) entries", recovery, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document with no object streams must not gain the warning — a channel that fires on
    /// conformant input is one a caller learns to ignore.
    /// </summary>
    [Fact]
    public void UnchainedRevision_OverAClassicDocument_DoesNotWarnAboutObjectStreams()
    {
        var reported = new List<string>();

        AppendUnchainedRevision(ClassicPdf(0), ClassicPdf(1), reported);

        string recovery = Assert.Single(reported,
            m => m.Contains("re-declared at their recovered offsets", StringComparison.Ordinal));

        Assert.DoesNotContain("object streams", recovery, StringComparison.Ordinal);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    /// <summary>
    /// Appends a revision that cannot chain to a previous section.
    /// </summary>
    /// <param name="intact">
    /// Bytes the document is PARSED from, so the cross-reference table carries the real type-2
    /// entries an object-stream document has.
    /// </param>
    /// <param name="damagedTail">
    /// Bytes handed to the writer, whose <c>startxref</c> points at nothing. That is what makes
    /// <c>ResolvePreviousXrefOffset</c> give up — it falls back to scanning for the literal
    /// <c>startxref</c> token, so merely passing <c>PreviousXrefOffset = 0</c> is not enough — and
    /// puts the writer on the re-declare-everything path. It models the finding's own scenario:
    /// a damaged or truncated tail on a document whose objects are otherwise intact.
    /// </param>
    /// <param name="reported">Collects the diagnostic sink's messages.</param>
    private static void AppendUnchainedRevision(
        byte[] intact, byte[] damagedTail, List<string> reported)
    {
        using var doc = PdfDocument.Open(new MemoryStream(intact));

        var revised = new PdfDictionary();
        revised.Set(new PdfName("Marker"), new PdfNumber(9));

        var options = new IncrementalUpdateOptions
        {
            Document = doc,
            PreviousXrefOffset = 0,
            DiagnosticSink = (operation, detail, ex) => reported.Add(operation + ": " + detail),
        };

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(
            damagedTail, output, new List<IndirectObject> { new IndirectObject(7, 0, revised) }, options);
    }

    /// <summary>
    /// A PDF whose catalog, page-tree node and marker object all live in an <c>/ObjStm</c>,
    /// indexed by a cross-reference stream — the shape a modern producer emits.
    /// </summary>
    private static byte[] ObjectStreamPdf(long startxrefOverride)
    {
        var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        void Raw(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void RawBytes(byte[] b) => ms.Write(b, 0, b.Length);

        Raw("%PDF-1.5\n");

        offsets[3] = ms.Length;
        Raw("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        // Object stream holding objects 1, 2 and 4.
        string o1 = "<< /Type /Catalog /Pages 2 0 R >>";
        string o2 = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        string o4 = "<< /Marker 1 >>";
        string header = $"1 0 2 {o1.Length + 1} 4 {o1.Length + 1 + o2.Length + 1} ";
        byte[] objStm = Encoding.ASCII.GetBytes(header + o1 + " " + o2 + " " + o4);

        offsets[5] = ms.Length;
        Raw($"5 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Length {objStm.Length} >>\nstream\n");
        RawBytes(objStm);
        Raw("\nendstream\nendobj\n");

        // Cross-reference stream: /W [1 2 1], objects 0..6.
        var rows = new List<byte>();
        void Row(int type, int a, int b) { rows.Add((byte)type); rows.Add((byte)(a >> 8)); rows.Add((byte)a); rows.Add((byte)b); }
        Row(0, 0, 255);                    // 0 — free head
        Row(2, 5, 0);                      // 1 — in ObjStm 5, index 0
        Row(2, 5, 1);                      // 2 — in ObjStm 5, index 1
        Row(1, (int)offsets[3], 0);        // 3 — top-level
        Row(2, 5, 2);                      // 4 — in ObjStm 5, index 2
        Row(1, (int)offsets[5], 0);        // 5 — the object stream itself
        Row(1, 0, 0);                      // 6 — this xref stream, patched below
        byte[] xrefData = rows.ToArray();

        long xrefPos = ms.Length;
        xrefData[6 * 4 + 1] = (byte)(xrefPos >> 8);
        xrefData[6 * 4 + 2] = (byte)xrefPos;

        Raw($"6 0 obj\n<< /Type /XRef /W [1 2 1] /Size 7 /Root 1 0 R /Length {xrefData.Length} >>\nstream\n");
        RawBytes(xrefData);
        Raw("\nendstream\nendobj\n");
        Raw($"startxref\n{(startxrefOverride > 0 ? startxrefOverride : xrefPos)}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>The same document with every object written top-level under a classic table.</summary>
    private static byte[] ClassicPdf(long startxrefOverride)
    {
        var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        void Raw(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int n, string body) { offsets[n] = ms.Length; Raw($"{n} 0 obj\n{body}\nendobj\n"); }
        string Entry(long off, int gen, char kind)
            => off.ToString("D10") + " " + gen.ToString("D5") + " " + kind + " \n";

        Raw("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        Obj(4, "<< /Marker 1 >>");

        long xref = ms.Length;
        Raw("xref\n0 5\n");
        Raw(Entry(0, 65535, 'f'));
        Raw(Entry(offsets[1], 0, 'n'));
        Raw(Entry(offsets[2], 0, 'n'));
        Raw(Entry(offsets[3], 0, 'n'));
        Raw(Entry(offsets[4], 0, 'n'));
        Raw("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        Raw($"startxref\n{(startxrefOverride > 0 ? startxrefOverride : xref)}\n%%EOF\n");
        return ms.ToArray();
    }
}
