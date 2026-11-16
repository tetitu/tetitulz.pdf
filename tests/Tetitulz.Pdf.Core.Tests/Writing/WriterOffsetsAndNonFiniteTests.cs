// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// Three ways a writer produced a file that no reader accepts, or damaged the caller's stream,
/// without saying anything:
///
/// <list type="number">
///   <item><see cref="PdfWriter"/> formats a non-finite <see cref="PdfNumber"/> with <c>"F6"</c>,
///   which yields the bare keyword <c>NaN</c> / <c>Infinity</c> — §7.3.3 has no such syntax;</item>
///   <item><see cref="PdfCompressedFileWriter"/> records absolute <see cref="Stream.Position"/>
///   values as PDF byte offsets, so the file it documents as starting "at the current position"
///   is unreadable when that position is not 0;</item>
///   <item>the original <see cref="IncrementalUpdateWriter.WriteUpdate(Stream, Stream,
///   List{IndirectObject}, PdfDictionary, long, int,
///   IPdfObjectEncryptor)"/> validates <c>/Size</c> only after the objects are already in the
///   caller's stream.</item>
/// </list>
/// </summary>
public class WriterOffsetsAndNonFiniteTests
{
    // ================================================================
    //  1. Non-finite numbers never reach the output as keywords
    // ================================================================

    /// <summary>
    /// A layout that divides by zero reaches the writer with <see cref="double.NaN"/> — and
    /// <c>PdfDocumentBuilder.AddPage</c> lets it through, because its guard is
    /// <c>widthPt &lt;= 0</c> and <c>NaN &lt;= 0</c> is false. The output must stay parseable, and
    /// the caller must be able to find out that it was not what it asked for.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void WriteIndirectObject_NonFiniteNumber_WritesZeroAndCountsIt(double value)
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var box = new PdfArray();
        box.Add(new PdfNumber(0));
        box.Add(new PdfNumber(value));
        var dict = new PdfDictionary();
        dict.Set(PdfName.MediaBox, box);
        writer.WriteIndirectObject(7, 0, dict);

        Assert.Equal(1, writer.NonFiniteNumbersCoercedToZero);

        // Assert on the re-parsed object, not on the text: a bare `NaN` tokenizes as a keyword, so
        // this is exactly the read that used to fail on a file this writer had just produced.
        var parsed = ReparseIndirectObject(ms.ToArray());
        var reparsedBox = Assert.IsType<PdfArray>(parsed[PdfName.MediaBox]);
        Assert.Equal(2, reparsedBox.Count);
        Assert.Equal(0d, Assert.IsType<PdfNumber>(reparsedBox[1]).DoubleValue);
    }

    /// <summary>
    /// The honest negative: an ordinary fractional number is written unchanged and nothing is
    /// counted, so the coercion cannot be quietly applied to every number.
    /// </summary>
    [Fact]
    public void WriteIndirectObject_FiniteNumber_IsUnchangedAndCountsNothing()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Count, new PdfNumber(841.89));
        writer.WriteIndirectObject(7, 0, dict);

        Assert.Equal(0, writer.NonFiniteNumbersCoercedToZero);
        var parsed = ReparseIndirectObject(ms.ToArray());
        Assert.Equal(841.89d, Assert.IsType<PdfNumber>(parsed[PdfName.Count]).DoubleValue, 6);
    }

    private static PdfDictionary ReparseIndirectObject(byte[] bytes)
    {
        var parser = new PdfParser(new MemoryStream(bytes));
        return Assert.IsType<PdfDictionary>(parser.ParseIndirectObject());
    }

    // ================================================================
    //  2. Compressed-file offsets are measured from the %PDF- header
    // ================================================================

    /// <summary>
    /// The caller embeds a generated PDF into a container, which the <c>output</c> parameter is
    /// documented to support. Every type-1 entry and the <c>startxref</c> used to be too large by
    /// the prefix length, so extracting the PDF portion afterwards produced an unopenable file
    /// with no warning at all.
    /// </summary>
    [Fact]
    public void Write_ToPrePositionedStream_ProducesAFileValidOnceThePrefixIsStripped()
    {
        const int PrefixLength = 512;

        using var container = new MemoryStream();
        container.Write(new byte[PrefixLength], 0, PrefixLength);
        PdfCompressedFileWriter.Write(container, new PdfVersion(1, 5), BuildDocument(), Trailer());

        byte[] whole = container.ToArray();
        var extracted = new byte[whole.Length - PrefixLength];
        Buffer.BlockCopy(whole, PrefixLength, extracted, 0, extracted.Length);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        Assert.Single(doc.Pages);
        Assert.Equal(612d, ((PdfNumber)doc.Pages[0].MediaBox[2]).DoubleValue);

        // …and the recorded startxref really is an offset into the extracted file, not into the
        // container. Resolving the catalog above already needs it, but this names the number.
        Assert.Equal(StartXrefValue(extracted), OffsetOfXrefStream(extracted));
    }

    /// <summary>
    /// The unchanged case, so the subtraction cannot be a no-op that happens to please the test
    /// above: written at position 0 the file is still valid and its offsets still resolve.
    /// </summary>
    [Fact]
    public void Write_ToStreamAtPositionZero_IsUnaffected()
    {
        using var ms = new MemoryStream();
        PdfCompressedFileWriter.Write(ms, new PdfVersion(1, 5), BuildDocument(), Trailer());

        byte[] bytes = ms.ToArray();
        using var doc = PdfDocument.Open(new MemoryStream(bytes));
        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        Assert.Equal(StartXrefValue(bytes), OffsetOfXrefStream(bytes));
    }

    /// <summary>The <c>startxref</c> operand, read out of the file's tail.</summary>
    private static long StartXrefValue(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int idx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(idx >= 0, "the writer emitted no startxref");
        string tail = text.Substring(idx + "startxref".Length).TrimStart('\r', '\n');
        int end = 0;
        while (end < tail.Length && char.IsDigit(tail[end])) end++;
        return long.Parse(tail.Substring(0, end), CultureInfo.InvariantCulture);
    }

    /// <summary>Where the <c>/Type /XRef</c> object actually begins in <paramref name="pdf"/>.</summary>
    private static long OffsetOfXrefStream(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int dictIdx = text.LastIndexOf("/Type /XRef", StringComparison.Ordinal);
        Assert.True(dictIdx >= 0, "the writer emitted no cross-reference stream");
        // Walk back to the `N 0 obj` header that introduces it.
        int headerIdx = text.LastIndexOf(" obj", dictIdx, StringComparison.Ordinal);
        Assert.True(headerIdx >= 0);
        int start = headerIdx;
        while (start > 0 && text[start - 1] != '\n') start--;
        return start;
    }

    // ================================================================
    //  3. /Size is validated before anything is written
    // ================================================================

    /// <summary>
    /// <c>/Size</c> is max+1 and the parameter is documented as "total number of objects", so
    /// passing one too small is easy. The check existed, but ran after the write loop — leaving
    /// the caller's stream holding the original file plus a run of orphan object bodies with no
    /// xref section and no <c>%%EOF</c> after them, which is worse than either outcome it chooses
    /// between.
    /// </summary>
    [Fact]
    public void LegacyWriteUpdate_SizeBelowHighestObjectNumber_WritesNothingAtAll()
    {
        byte[] original = MinimalPdf();
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();

        var objects = new List<IndirectObject>
        {
            new IndirectObject(7, 0, new PdfString("appended")),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IncrementalUpdateWriter.WriteUpdate(input, output, objects, new PdfDictionary(),
                previousXrefOffset: 0, size: 5));

        Assert.Contains("/Size (5)", ex.Message);
        Assert.Contains("(7)", ex.Message);

        // The evidence that matters: the caller's stream is untouched, so there is no orphan
        // object body for a forward-scanning reader to pick up.
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The honest negative: a <c>/Size</c> that does cover the objects still writes a document
    /// whose appended object is reachable, so the early return cannot be refusing everything.
    /// </summary>
    [Fact]
    public void LegacyWriteUpdate_SizeCoveringTheObjects_AppendsAReachableRevision()
    {
        byte[] original = MinimalPdf();
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));

        var appended = new PdfDictionary();
        appended.Set(PdfName.Type, PdfName.Page);
        var objects = new List<IndirectObject>
        {
            new IndirectObject(7, 0, appended),
        };

        var result = IncrementalUpdateWriter.WriteUpdate(input, output, objects, trailer,
            previousXrefOffset: 0, size: 8);

        Assert.True(output.Length > original.Length);
        Assert.True(result.ObjectOffsets.ContainsKey(7));

        var parser = new PdfParser(new MemoryStream(output.ToArray()));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        var fetched = Assert.IsType<PdfDictionary>(parser.GetObject(7));
        Assert.Equal(PdfName.Page, fetched.GetName(PdfName.Type));
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static List<(int objectNumber, PdfObject obj)> BuildDocument()
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        var kids = new PdfArray();
        kids.Add(new PdfReference(3, 0));
        pages.Set(PdfName.Kids, kids);
        pages.Set(PdfName.Count, new PdfNumber(1));

        var page = new PdfDictionary();
        page.Set(PdfName.Type, PdfName.Page);
        page.Set(PdfName.Parent, new PdfReference(2, 0));
        var box = new PdfArray();
        box.Add(new PdfNumber(0));
        box.Add(new PdfNumber(0));
        box.Add(new PdfNumber(612));
        box.Add(new PdfNumber(792));
        page.Set(PdfName.MediaBox, box);
        page.Set(PdfName.Contents, new PdfReference(4, 0));

        var contents = new PdfStream();
        contents.RawData = Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (offset test) Tj ET\n");

        return new List<(int, PdfObject)>
        {
            (1, catalog), (2, pages), (3, page), (4, contents),
        };
    }

    private static PdfDictionary Trailer()
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        return trailer;
    }

    /// <summary>A three-object classic file, used as the base revision for the update tests.</summary>
    private static byte[] MinimalPdf()
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        long o1 = file.Position; Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = file.Position; Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        long o3 = file.Position; Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 4\n0000000000 65535 f \n");
        xs.Append(o1.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o2.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o3.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }
}
