// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Fetching several distinct objects out of one <c>/ObjStm</c> (ISO 32000-1 §7.5.7).
/// <para>
/// The only object-stream coverage before this was a negative case — a stream with an unknown
/// <c>/Filter</c> yielding <see cref="PdfNull"/>. Nothing asserted that fetching object
/// <i>k</i> actually returns object <i>k</i>'s body, which is the property that matters and the
/// one a shared, cached tokenizer over the decoded stream could break: the index and the object
/// bodies are read through the same stream position, so a stale seek would silently return a
/// neighbouring object rather than fail.
/// </para>
/// </summary>
public class ObjectStreamFetchTests
{
    /// <summary>
    /// Every object in the stream resolves to its own body. Fetched in a deliberately jumbled
    /// order so a reader that only ever moves forward, or that relies on the previous call
    /// having left the position where it wants it, comes out wrong.
    /// </summary>
    [Fact]
    public void GetObject_EachCompressedObject_ReturnsItsOwnBody()
    {
        byte[] pdf = BuildPdfWithObjStm(objectCount: 6);
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        // Objects 3..8 live in ObjStm 2 at indices 0..5; each carries /Marker <its number>.
        foreach (int objNum in new[] { 8, 3, 6, 4, 7, 5 })
        {
            var dict = Assert.IsType<PdfDictionary>(parser.GetObject(objNum));
            Assert.Equal(objNum, dict.GetInteger(new PdfName("Marker")));
        }
    }

    /// <summary>
    /// The same object fetched twice is the identical instance, and a fetch of a different
    /// object in between does not disturb it. This pins that caching the stream's index did not
    /// turn the per-object cache into a per-stream one.
    /// </summary>
    /// <remarks>
    /// The intermediate fetch is asserted, not discarded, and that is the whole value of this
    /// test. Immediately after the index is read the shared tokenizer happens to sit exactly at
    /// <c>/First</c> — which is the <i>first</i> contained object's body — so a reader that
    /// never seeks still returns object 3 correctly on the first call. Only the second, non-first
    /// object exposes it.
    /// </remarks>
    [Fact]
    public void GetObject_RepeatedFetchAcrossOtherObjects_IsStable()
    {
        byte[] pdf = BuildPdfWithObjStm(objectCount: 4);
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        var first        = parser.GetObject(3);
        var intermediate = parser.GetObject(5);
        var second       = parser.GetObject(3);

        Assert.Equal(5, ((PdfDictionary)intermediate).GetInteger(new PdfName("Marker")));
        Assert.Same(first, second);
        Assert.Equal(3, ((PdfDictionary)second).GetInteger(new PdfName("Marker")));
    }

    /// <summary>
    /// An index past the end of the stream's own <c>/N</c> yields <see cref="PdfNull"/> rather
    /// than throwing or returning a neighbouring object — <b>and says why</b>.
    /// </summary>
    /// <remarks>
    /// <para>The reason is the half this test used to be missing, and the omission is the defect
    /// rather than a gap in the assertion: a bare <c>PdfNull</c> is exactly what an object the file
    /// never declared produces, so a caller had no way to tell a corrupt cross-reference entry from
    /// an ordinary absent object.</para>
    ///
    /// <para><b>And that is not hypothetical — this test was asking for the wrong object.</b> It
    /// fetched object 9 while the builder's extra entry lands on object 7, because the entries are
    /// written in order from object 0 and the fixture holds eight of them. So the call took the
    /// <i>absent</i> path and the assertion passed for the wrong reason, invisibly, because both
    /// paths return <c>PdfNull</c>. Adding the reason is what surfaced it. The stream staying usable
    /// afterwards is the control — a refusal that poisoned the reader would satisfy the first
    /// assertion just as well.</para>
    /// </remarks>
    [Fact]
    public void GetObject_IndexBeyondObjStmEntryCount_ReturnsPdfNullAndSaysWhy()
    {
        // The xref claims object 7 sits at index 6 of a stream that holds only 3 objects.
        byte[] pdf = BuildPdfWithObjStm(objectCount: 3, extraOutOfRangeEntry: true);
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        Assert.IsType<PdfNull>(parser.GetObject(7));

        string reason = PdfParser.LastObjectParseError;
        Assert.NotNull(reason);
        Assert.Contains("index 6", reason, System.StringComparison.Ordinal);
        Assert.Contains("3 objects", reason, System.StringComparison.Ordinal);

        // ...and the stream is still usable afterwards, which also clears the reason.
        Assert.Equal(3, ((PdfDictionary)parser.GetObject(3)).GetInteger(new PdfName("Marker")));
        Assert.Null(PdfParser.LastObjectParseError);
    }

    /// <summary>
    /// An entry whose offset falls outside the decoded stream is refused <b>with a reason</b>,
    /// rather than seeking past the end and handing back whatever parses there.
    /// </summary>
    /// <remarks>
    /// This is the sibling of the bounds check above and was silent for the same reason: the index
    /// is in range, so the entry looks well-formed until its offset is added to <c>/First</c>. The
    /// acceptance control is the other object in the same stream, which still resolves — so the
    /// refusal is about this entry and not about the stream being unreadable.
    /// </remarks>
    [Fact]
    public void GetObject_ObjStmEntryOffsetOutsideTheDecodedStream_IsRefusedWithAReason()
    {
        byte[] pdf = BuildPdfWithObjStm(objectCount: 3, offsetOverrideForIndex0: 1_000_000);
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        Assert.IsType<PdfNull>(parser.GetObject(3));

        string reason = PdfParser.LastObjectParseError;
        Assert.NotNull(reason);
        Assert.Contains("decoded bytes", reason, System.StringComparison.Ordinal);

        // The stream itself is fine: its other objects still resolve to their own bodies.
        Assert.Equal(4, ((PdfDictionary)parser.GetObject(4)).GetInteger(new PdfName("Marker")));
    }

    /// <summary>
    /// An <c>/N</c> the stream body cannot possibly contain is refused, and the refusal names
    /// both numbers.
    ///
    /// <para><b>Why the count cap alone was not enough.</b> <c>/N</c> was bounded only by a
    /// constant (65 536), and the index array is allocated from it — so ~67 bytes of file bought
    /// 524 288 bytes of retained array, roughly 7 800x amplification, entirely inside the limit.
    /// An index entry is two integers and a separator, so <i>N</i> entries need at least 4<i>N</i>
    /// decoded bytes; charging the declared count against the body the file actually supplied is
    /// what removes the ratio. A real object stream carries its index, so nothing legitimate can
    /// trip this — which the sibling tests above are the control for.</para>
    /// </summary>
    [Fact]
    public void GetObject_ObjStmDeclaringMoreEntriesThanItsBodyCanHold_IsRefused()
    {
        byte[] pdf = BuildPdfWithObjStm(objectCount: 3, declaredEntryCount: 65536);
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        Assert.IsType<PdfNull>(parser.GetObject(3));

        string reason = PdfParser.LastObjectParseError;
        Assert.NotNull(reason);
        Assert.Contains("65536", reason);          // what the file claimed…
        Assert.Contains("262144", reason);         // …and the index bytes that would have required
    }

    /// <summary>
    /// A real object stream holds 65 542 members, the catalog among them (another reader bug1978317), and
    /// the old cap of 65 536 refused it (GAPS §19h item 6): a stream of 65 537 members is read under
    /// the raised default, its last member included, and one declaring twice the default is still
    /// refused by the cap, with the count in the reason.
    /// </summary>
    [Fact]
    public void GetObject_ObjStmOf65537Members_IsReadUnderTheRaisedCap_AndOneAboveItIsRefused()
    {
        byte[] pdf = BuildPdfWithObjStm(objectCount: 65537);
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        int last = 3 + 65536;
        var member = Assert.IsType<PdfDictionary>(parser.GetObject(last));
        Assert.Equal(last, member.GetInteger(new PdfName("Marker")));
        Assert.Null(PdfParser.LastObjectParseError);

        int above = Tetitulz.Pdf.Core.Constants.PdfLimits.DefaultMaxObjStmEntries + 1;
        byte[] refused = BuildPdfWithObjStm(objectCount: 3, declaredEntryCount: above);
        using var ms2 = new MemoryStream(refused);
        var parser2 = new PdfParser(ms2);
        parser2.ParseCrossReferences();
        Assert.IsType<PdfNull>(parser2.GetObject(3));
        // The cap's own reason: without it the 4N body check refuses the same fixture, with its own words.
        Assert.Contains("invalid /N entry count " + above.ToString(System.Globalization.CultureInfo.InvariantCulture), PdfParser.LastObjectParseError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a PDF 1.5 file with a cross-reference stream and one uncompressed
    /// <c>/ObjStm</c> holding <paramref name="objectCount"/> dictionaries, numbered from 3.
    /// Each carries <c>/Marker</c> equal to its own object number, so a mixed-up fetch is
    /// visible rather than plausible.
    /// </summary>
    /// <param name="extraOutOfRangeEntry">
    /// When true, adds an xref entry for one further object pointing at an index past the end
    /// of the stream's <c>/N</c>.
    /// </param>
    /// <param name="declaredEntryCount">
    /// When non-negative, the <c>/N</c> written into the stream dictionary, independently of how
    /// many objects the body really holds. The xref still points at the real ones, so a parser
    /// that accepted the inflated count would allocate for it and then read the index out of
    /// whatever followed.
    /// </param>
    /// <param name="offsetOverrideForIndex0">
    /// When non-negative, the offset written into the stream's own index for the first contained
    /// object, independently of where its body really is. The index entry is then in range while the
    /// position it names is not — the one shape that reaches the offset check rather than the
    /// bounds check above it.
    /// </param>
    private static byte[] BuildPdfWithObjStm(int objectCount, bool extraOutOfRangeEntry = false,
                                             int declaredEntryCount = -1,
                                             long offsetOverrideForIndex0 = -1)
    {
        //  Obj 1               Catalog                (regular)
        //  Obj 2               the ObjStm             (regular)
        //  Obj 3 .. 2+count    contained dictionaries (type-2 xref entries)
        //  Obj 3+count         the XRef stream        (regular)
        int firstContained = 3;
        int xrefNum = firstContained + objectCount;
        int size = xrefNum + 1 + (extraOutOfRangeEntry ? 1 : 0);

        // The object stream's payload: an index of "objnum offset" pairs, then the bodies.
        var pairs = new StringBuilder();
        var bodies = new StringBuilder();
        for (int i = 0; i < objectCount; i++)
        {
            long declaredOffset = i == 0 && offsetOverrideForIndex0 >= 0
                ? offsetOverrideForIndex0
                : bodies.Length;
            pairs.Append($"{firstContained + i} {declaredOffset} ");
            bodies.Append($"<< /Type /TestObj /Marker {firstContained + i} >>\n");
        }
        string header = pairs.ToString();
        byte[] objStmData = Encoding.ASCII.GetBytes(header + bodies);

        var file = new MemoryStream();
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.5\n");

        long catalogOffset = file.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 1 0 R >>\nendobj\n");

        long objStmOffset = file.Position;
        Write($"2 0 obj\n<< /Type /ObjStm /N {(declaredEntryCount >= 0 ? declaredEntryCount : objectCount)} " +
              $"/First {header.Length} /Length {objStmData.Length} >>\nstream\n");
        file.Write(objStmData);
        Write("\nendstream\nendobj\n");

        long xrefOffset = file.Position;

        // /W [1 4 2]: type byte, 4-byte field 2, 2-byte field 3 — three bytes for the third field
        // once a member index no longer fits two, as a real stream of 65 542 members writes it.
        int indexWidth = objectCount + 4 > 0xFFFF ? 3 : 2;
        var xref = new MemoryStream();
        void Entry(byte type, uint f2, uint f3)
        {
            xref.WriteByte(type);
            xref.WriteByte((byte)(f2 >> 24)); xref.WriteByte((byte)(f2 >> 16));
            xref.WriteByte((byte)(f2 >> 8));  xref.WriteByte((byte)f2);
            if (indexWidth == 3) xref.WriteByte((byte)(f3 >> 16));
            xref.WriteByte((byte)(f3 >> 8));  xref.WriteByte((byte)f3);
        }

        Entry(0, 0, 0xFFFF);                                    // object 0, free-list head
        Entry(1, (uint)catalogOffset, 0);                       // 1: catalog
        Entry(1, (uint)objStmOffset, 0);                        // 2: the ObjStm itself
        for (int i = 0; i < objectCount; i++)                   // 3..: contained, index i
            Entry(2, 2, (uint)i);
        Entry(1, (uint)xrefOffset, 0);                          // the xref stream
        if (extraOutOfRangeEntry)
            Entry(2, 2, (uint)(objectCount + 3));               // index past /N

        byte[] xrefBytes = xref.ToArray();
        Write($"{xrefNum} 0 obj\n<< /Type /XRef /Size {size} /W [1 4 {indexWidth}] /Root 1 0 R " +
              $"/Length {xrefBytes.Length} >>\nstream\n");
        file.Write(xrefBytes);
        Write("\nendstream\nendobj\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return file.ToArray();
    }
}
