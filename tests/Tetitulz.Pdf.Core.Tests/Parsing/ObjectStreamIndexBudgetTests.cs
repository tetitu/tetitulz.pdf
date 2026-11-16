// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// The index cache bounds what a parser retains, not what it does: members of more object streams
/// than the cache holds, taken round-robin, miss every time, and each miss reads one whole index
/// again (GAPS §7b). <see cref="PdfLimits.MaxObjStmIndexEntriesRead"/> bounds the reads themselves,
/// charged on every one.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class ObjectStreamIndexBudgetTests
{
    private static readonly PdfName Marker = new PdfName("Marker");

    /// <summary>
    /// Four streams of four members under a cache holding one index: reading members round-robin
    /// re-reads an index for every member (the fixture's potency: five reads for five members), and
    /// the sixth member's read — twenty-four entries against a budget of twenty — is refused with the
    /// reason, while a member of the stream still cached is read. The control: the same budget with
    /// a cache that holds every index reads all fourteen objects in four index reads, each index once.
    /// </summary>
    [Fact]
    public void MembersCyclingThroughMoreIndexesThanTheCacheHolds_StopAtTheIndexBudget_WhereCachedOnesAreNotCharged()
    {
        byte[] pdf = FourStreamsOfFour();
        try
        {
            PdfLimits.MaxObjStmIndexEntriesRead = 20;
            PdfLimits.MaxObjStmIndexCacheBytes = 4 * 8; // one index of four entries, eight bytes an entry
            PdfParser parser = ParserOver(pdf);

            foreach (int n in new[] { 3, 5, 9, 4, 6 })
                Assert.Equal(n, Assert.IsType<PdfDictionary>(parser.GetObject(n)).GetInteger(Marker));
            Assert.Equal(5, parser.ObjectStreamIndexReads);

            Assert.IsType<PdfNull>(parser.GetObject(10));
            Assert.Contains("past the 20 index entries one parser reads (20 read so far)", PdfParser.LastObjectParseError, StringComparison.Ordinal);
            Assert.Equal(5, parser.ObjectStreamIndexReads);

            // Object 6's stream was the last admitted, so object 7 costs no read and is not refused.
            Assert.Equal(7, Assert.IsType<PdfDictionary>(parser.GetObject(7)).GetInteger(Marker));
            Assert.Null(PdfParser.LastObjectParseError);

            PdfLimits.MaxObjStmIndexCacheBytes = PdfLimits.DefaultMaxObjStmIndexCacheBytes;
            PdfParser control = ParserOver(pdf);
            for (int n = 3; n <= 14; n++)
                Assert.Equal(n, Assert.IsType<PdfDictionary>(control.GetObject(n)).GetInteger(Marker));
            Assert.Equal(4, control.ObjectStreamIndexReads);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// A reader's retained bytes are its index and, when its stream did not keep the decoded body,
    /// that body too — past the document's memo ceiling the reader is the body's only owner. Under a
    /// ceiling of one byte, which the cross-reference stream's decode spends, no object stream keeps
    /// its body, so each reader is charged more than its four eight-byte entries; under the default
    /// each is charged its entries alone.
    /// </summary>
    [Fact]
    public void AReaderWhoseStreamDidNotKeepItsBody_IsChargedTheBodyToo()
    {
        byte[] pdf = FourStreamsOfFour();
        const long entries = 4 * 8;
        try
        {
            PdfLimits.MaxMemoizedDecodedBytesPerDocument = 1;
            PdfParser parser = ParserOver(pdf);
            Assert.Equal(3, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetInteger(Marker));
            long one = parser.ObjectStreamCacheBytes;
            Assert.True(one > entries, $"the reader holds its stream's body alone and must be charged it: {one} bytes");
            Assert.Equal(5, Assert.IsType<PdfDictionary>(parser.GetObject(5)).GetInteger(Marker));
            Assert.True(parser.ObjectStreamCacheBytes > one + entries,
                $"the second reader holds its body alone too: {parser.ObjectStreamCacheBytes} bytes after {one}");

            PdfLimits.ResetToDefaults();
            PdfParser control = ParserOver(pdf);
            control.GetObject(3);
            Assert.Equal(entries, control.ObjectStreamCacheBytes);
            control.GetObject(5);
            Assert.Equal(2 * entries, control.ObjectStreamCacheBytes);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private static PdfParser ParserOver(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    /// <summary>Objects 1 to 14 — a catalog, a page tree and twelve <c>&lt;&lt; /Marker n &gt;&gt;</c> — packed four to an object stream: {1..4}, {5..8}, {9..12}, {13, 14}.</summary>
    private static byte[] FourStreamsOfFour()
    {
        var objects = new List<(int, PdfObject)>();
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        pages.Set(PdfName.Kids, new PdfArray());
        pages.Set(PdfName.Count, new PdfNumber(0));
        objects.Add((1, catalog));
        objects.Add((2, pages));
        for (int n = 3; n <= 14; n++)
        {
            var d = new PdfDictionary();
            d.Set(Marker, new PdfNumber(n));
            objects.Add((n, d));
        }
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));

        using var ms = new MemoryStream();
        PdfCompressedFileWriter.Write(ms, new PdfVersion(1, 7), objects, trailer,
            new PdfCompressedWriteOptions { MaxObjectsPerObjectStream = 4 });
        return ms.ToArray();
    }
}
