// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// How many members <see cref="PdfCompressedFileWriter"/> puts in one object stream when a caller
/// asks for more than a type-2 cross-reference entry can number. The entry's third field, the
/// member's index, is two bytes wide, so one stream holds at most 65 536 members — whatever the
/// read side's <see cref="PdfLimits.MaxObjStmEntries"/> allows.
/// </summary>
public class CompressedWriterObjectStreamIndexTests
{
    /// <summary>
    /// 65 540 numbers written under a caller's cap of <see cref="PdfLimits.MaxObjStmEntries"/>
    /// (131 072) all read back as themselves, the ones past index 65 535 included, because the
    /// writer starts a second object stream at 65 536 members. With the read limit as its only
    /// ceiling it packed them into one stream, and every member past index 65 535 was entered as
    /// index 65 535 — the field is clamped — so each of them read back as that one member.
    /// </summary>
    [Fact]
    public void MembersPastWhatTheIndexFieldNumbers_GoToASecondObjectStream()
    {
        const int First = 3, Count = 65_540;
        var objects = new List<(int, PdfObject)>(Count + 2);
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        pages.Set(PdfName.Kids, new PdfArray());
        pages.Set(PdfName.Count, new PdfNumber(0));
        objects.Add((1, catalog));
        objects.Add((2, pages));
        for (int n = First; n < First + Count; n++) objects.Add((n, new PdfNumber(n)));

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));

        byte[] pdf;
        using (var ms = new MemoryStream())
        {
            PdfCompressedFileWriter.Write(ms, new PdfVersion(1, 7), objects, trailer, new PdfCompressedWriteOptions
            {
                MaxObjectsPerObjectStream = PdfLimits.MaxObjStmEntries,
                // Far above what these objects take, so the member count alone decides where a stream ends.
                MaxObjectStreamBytes = int.MaxValue,
            });
            pdf = ms.ToArray();
        }

        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();

        int last = First + Count - 1;
        CrossReferenceEntry firstEntry = parser.CrossReferenceTable.GetEntry(First);
        CrossReferenceEntry lastEntry = parser.CrossReferenceTable.GetEntry(last);

        // Packed, both of them: it is the index field that places these members, not an offset.
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, firstEntry.Type);
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, lastEntry.Type);
        Assert.NotEqual(firstEntry.ObjectStreamNumber, lastEntry.ObjectStreamNumber);

        // The members on either side of index 65 535 in the order written: catalog and page tree
        // first, so the number at index 65 535 is object 65 536.
        for (int n = 65_530; n <= last; n++)
        {
            var value = Assert.IsType<PdfNumber>(parser.GetObject(n));
            Assert.Equal(n, value.LongValue);
        }
    }
}
