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
/// <see cref="PdfCompressedFileWriter"/>, batch 1: three rules the sweep found untested — the
/// decoded-size cap on an object stream, a top-level object's offset when the file starts past
/// byte 0, and storing a stream raw when Flate would enlarge it.
/// </summary>
public class CompressedWriterBatch1Tests
{
    /// <summary>
    /// Forty 100-byte objects under the smallest size cap, 1024 bytes, take several object streams;
    /// the count cap alone (128) would put them all in one. The control, with the default cap,
    /// uses one.
    /// </summary>
    [Theory]
    [InlineData(1024, 4)]
    [InlineData(PdfCompressedWriteOptions.DefaultMaxObjectStreamBytes, 1)]
    public void TheSizeCapClosesAnObjectStream(int maxBytes, int minimumStreams)
    {
        var objects = Catalog();
        for (int n = 3; n < 43; n++)
            objects.Add((n, new PdfString(new string('x', 100))));

        byte[] pdf = Write(objects, new PdfCompressedWriteOptions { MaxObjectStreamBytes = maxBytes });

        int streams = Count(pdf, "/Type /ObjStm");
        Assert.True(streams >= minimumStreams, $"{streams} object streams");
        if (minimumStreams == 1) Assert.Equal(1, streams);
    }

    /// <summary>
    /// Written after a 100-byte prefix, a top-level object (a stream, which is never packed) is
    /// recorded at its offset in the PDF, not in the container. The existing prefix test's document
    /// had no top-level object, so the subtraction on that path was never read.
    /// </summary>
    [Fact]
    public void ATopLevelObjectsOffsetIsMeasuredFromTheHeader()
    {
        var objects = Catalog();
        var stream = new PdfStream { RawData = Encoding.ASCII.GetBytes("hello") };
        objects.Add((3, stream));

        using var container = new MemoryStream();
        container.Write(new byte[100], 0, 100);
        PdfCompressedFileWriter.Write(container, new PdfVersion(1, 7), objects, Trailer());
        byte[] whole = container.ToArray();
        var pdf = new byte[whole.Length - 100];
        Buffer.BlockCopy(whole, 100, pdf, 0, pdf.Length);

        long recorded = XrefOffset(pdf, 3);

        Assert.StartsWith("3 0 obj", Encoding.ASCII.GetString(pdf, (int)recorded, 7));
    }

    /// <summary>
    /// One tiny object makes an object stream a few bytes long, which Flate would enlarge; it is
    /// stored raw. The control, forty objects of repeated text, is compressed.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(40, true)]
    public void AnObjectStreamIsCompressedOnlyWhenThatShrinksIt(int count, bool compressed)
    {
        var objects = new List<(int, PdfObject)>();
        for (int n = 1; n <= count; n++)
            objects.Add((n, count == 1 ? new PdfNumber(5) : new PdfString(new string('x', 50))));

        byte[] pdf = Write(objects, null);

        string text = Encoding.ASCII.GetString(pdf);
        int at = text.IndexOf("/Type /ObjStm", StringComparison.Ordinal);
        Assert.True(at >= 0, "no object stream was written");
        string dictionary = text.Substring(at, text.IndexOf(">>", at, StringComparison.Ordinal) - at);
        Assert.Equal(compressed, dictionary.Contains("/FlateDecode"));
    }

    private static List<(int, PdfObject)> Catalog()
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        pages.Set(PdfName.Kids, new PdfArray());
        pages.Set(PdfName.Count, new PdfNumber(0));
        return new List<(int, PdfObject)> { (1, catalog), (2, pages) };
    }

    private static PdfDictionary Trailer()
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        return trailer;
    }

    private static byte[] Write(List<(int, PdfObject)> objects, PdfCompressedWriteOptions options)
    {
        using var ms = new MemoryStream();
        PdfCompressedFileWriter.Write(ms, new PdfVersion(1, 7), objects, Trailer(), options);
        return ms.ToArray();
    }

    private static int Count(byte[] pdf, string needle)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// The type-1 offset the cross-reference stream records for <paramref name="objectNumber"/>,
    /// read from the stream's own rows rather than through a parser that could recover from a
    /// wrong offset by scanning.
    /// </summary>
    private static long XrefOffset(byte[] pdf, int objectNumber)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int idx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        string tail = text.Substring(idx + "startxref".Length).TrimStart('\r', '\n');
        int end = 0;
        while (end < tail.Length && char.IsDigit(tail[end])) end++;
        long startxref = long.Parse(tail.Substring(0, end), CultureInfo.InvariantCulture);

        var parser = new PdfParser(new MemoryStream(pdf, (int)startxref, pdf.Length - (int)startxref));
        var xref = Assert.IsType<PdfStream>(parser.ParseIndirectObject());
        byte[] rows = xref.GetDecodedData();

        var w = (PdfArray)xref[PdfName.W];
        int w1 = ((PdfNumber)w[0]).IntValue, w2 = ((PdfNumber)w[1]).IntValue, w3 = ((PdfNumber)w[2]).IntValue;
        var index = (PdfArray)xref[PdfName.Index];
        int record = w1 + w2 + w3, row = 0;
        for (int s = 0; s < index.Count; s += 2)
        {
            int first = ((PdfNumber)index[s]).IntValue, n = ((PdfNumber)index[s + 1]).IntValue;
            for (int k = 0; k < n; k++, row++)
            {
                if (first + k != objectNumber) continue;
                int p = row * record;
                Assert.Equal(1, Field(rows, p, w1));
                return Field(rows, p + w1, w2);
            }
        }
        throw new InvalidOperationException($"object {objectNumber} has no cross-reference row");
    }

    private static long Field(byte[] data, int at, int width)
    {
        long v = 0;
        for (int i = 0; i < width; i++) v = (v << 8) | data[at + i];
        return v;
    }
}
