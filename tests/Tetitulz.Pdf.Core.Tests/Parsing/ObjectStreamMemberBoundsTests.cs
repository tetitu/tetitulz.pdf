// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Two ways an object stream could let a small file make the parser do unbounded work: members
/// sharing an offset, each parsed and cached as its own copy of the same bytes; and a full index
/// cache that admits nothing more, so every member of a further stream re-reads that stream's
/// whole index.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class ObjectStreamMemberBoundsTests
{
    private static readonly PdfName Marker = new PdfName("Marker");

    /// <summary>
    /// Members 3 and 4 name offset 0; member 5 its own. Object 3 is read, object 4 is refused with
    /// the reason — ISO 32000-2 §7.5.7 has the offsets increase — rather than coming back as a second
    /// copy of object 3's bytes stamped with its own number; object 5 is read.
    /// </summary>
    [Fact]
    public void AMemberAtAnOffsetAnEarlierMemberHolds_IsRefusedWithTheReason_WhereItsOwnIsRead()
    {
        PdfParser parser = ParserOver(BuildObjStm(sharedOffsetForMember4: true));

        Assert.Equal(3, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetInteger(Marker));

        Assert.IsType<PdfNull>(parser.GetObject(4));
        Assert.Contains("where an earlier member", PdfParser.LastObjectParseError, StringComparison.Ordinal);

        Assert.Equal(5, Assert.IsType<PdfDictionary>(parser.GetObject(5)).GetInteger(Marker));
    }

    /// <summary>The control: the same stream with member 4 at its own offset reads all three.</summary>
    [Fact]
    public void TheSameStreamWithItsOffsetsIncreasing_ReadsEveryMember()
    {
        PdfParser parser = ParserOver(BuildObjStm(sharedOffsetForMember4: false));

        for (int n = 3; n <= 5; n++)
            Assert.Equal(n, Assert.IsType<PdfDictionary>(parser.GetObject(n)).GetInteger(Marker));
    }

    /// <summary>
    /// Fourteen objects packed four to a stream (four streams), read in order under an index cache
    /// of two streams' worth: each stream's index is read once — four reads. A cache that stopped
    /// admitting readers once full would read the third and fourth streams' indexes again for every
    /// member: eight reads here, and one per member of a large stream in a hostile file.
    /// </summary>
    [Fact]
    public void AStreamPastAFullIndexCache_HasItsIndexReadOnceForAllItsMembers()
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

        byte[] pdf;
        using (var ms = new MemoryStream())
        {
            PdfCompressedFileWriter.Write(ms, new PdfVersion(1, 7), objects, trailer,
                new PdfCompressedWriteOptions { MaxObjectsPerObjectStream = 4 });
            pdf = ms.ToArray();
        }

        try
        {
            // Two streams of four entries, eight bytes an entry.
            PdfLimits.MaxObjStmIndexCacheBytes = 2 * 4 * 8;
            PdfParser parser = ParserOver(pdf);

            for (int n = 3; n <= 14; n++)
                Assert.Equal(n, Assert.IsType<PdfDictionary>(parser.GetObject(n)).GetInteger(Marker));
            Assert.Equal(PdfName.Catalog, Assert.IsType<PdfDictionary>(parser.GetObject(1)).GetName(PdfName.Type));

            // Objects 1 and 2 share the first stream with 3 and 4; it was evicted by the time object 1
            // was asked for, so it is read a second time — the one re-read eviction costs.
            Assert.Equal(5, parser.ObjectStreamIndexReads);
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

    /// <summary>
    /// A catalog, one object stream holding objects 3, 4 and 5 (each <c>&lt;&lt; /Marker n &gt;&gt;</c>), and
    /// a cross-reference stream. With <paramref name="sharedOffsetForMember4"/> the index names
    /// object 4 at object 3's offset, 0.
    /// </summary>
    private static byte[] BuildObjStm(bool sharedOffsetForMember4)
    {
        var pairs = new StringBuilder();
        var bodies = new StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            int number = 3 + i;
            int offset = sharedOffsetForMember4 && number == 4 ? 0 : bodies.Length;
            pairs.Append(number).Append(' ').Append(offset).Append(' ');
            bodies.Append("<< /Marker ").Append(number).Append(" >>\n");
        }
        string header = pairs.ToString();
        byte[] objStmData = Encoding.ASCII.GetBytes(header + bodies);

        var file = new MemoryStream();
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.5\n");
        long catalogOffset = file.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        long objStmOffset = file.Position;
        Write($"2 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Length {objStmData.Length} >>\nstream\n");
        file.Write(objStmData);
        Write("\nendstream\nendobj\n");

        long xrefOffset = file.Position;
        var xref = new MemoryStream();
        void Entry(byte type, uint f2, uint f3)
        {
            xref.WriteByte(type);
            xref.WriteByte((byte)(f2 >> 24)); xref.WriteByte((byte)(f2 >> 16));
            xref.WriteByte((byte)(f2 >> 8)); xref.WriteByte((byte)f2);
            xref.WriteByte((byte)(f3 >> 8)); xref.WriteByte((byte)f3);
        }
        Entry(0, 0, 0xFFFF);
        Entry(1, (uint)catalogOffset, 0);
        Entry(1, (uint)objStmOffset, 0);
        for (uint i = 0; i < 3; i++) Entry(2, 2, i);
        Entry(1, (uint)xrefOffset, 0);
        byte[] xrefBytes = xref.ToArray();
        Write($"6 0 obj\n<< /Type /XRef /Size 7 /W [1 4 2] /Root 1 0 R /Length {xrefBytes.Length} >>\nstream\n");
        file.Write(xrefBytes);
        Write("\nendstream\nendobj\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return file.ToArray();
    }
}
