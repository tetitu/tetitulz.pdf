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
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// Every classic cross-reference entry this library writes is exactly
/// <see cref="PdfXrefFormat.EntryLength"/> bytes, end-of-line included (§7.5.4).
/// </summary>
/// <remarks>
/// <para>A reader is entitled to seek an entry by <c>first + 20 × i</c> rather than parsing forward,
/// so <b>one byte wrong shifts every subsequent entry's meaning</b>. That has already happened here
/// twice: <c>PdfXrefFormat</c>'s own remarks record two copies built with
/// <c>StringBuilder.AppendLine</c>, which appends <c>Environment.NewLine</c> — 19 visible characters
/// plus a two-byte CRLF is <b>21</b> bytes on Windows and 20 on Linux, so the emitted PDF was
/// malformed, and differently malformed per platform.</para>
///
/// <para><b>The row is spelled once now.</b> Seven files hand-wrote it; the last five were routed
/// through <see cref="PdfXrefFormat.Entry"/> in 2026-08-23. These tests assert the width from the
/// <b>written bytes</b> rather than from the helper, because "the helper is 20 bytes" is not the
/// claim — "what the writers emit is 20 bytes" is.</para>
/// </remarks>
public class XrefEntryWidthTests
{
    /// <summary>The helper itself, across the values that change the row's shape.</summary>
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(9999999999L, 65535, true)]
    [InlineData(1, 7, false)]
    [InlineData(-5, -1, true)]              // clamped rather than widened
    [InlineData(123, 70000, true)]          // generation clamped to five digits
    public void AnEntryIsAlwaysExactlyTwentyBytes(long offset, int generation, bool inUse)
    {
        string entry = PdfXrefFormat.Entry(offset, generation, inUse);

        Assert.Equal(PdfXrefFormat.EntryLength, Encoding.ASCII.GetByteCount(entry));
        Assert.Equal(20, PdfXrefFormat.EntryLength);   // the spec's number, not a moving target
    }

    /// <summary>The free-list head is the same width as every other row.</summary>
    [Fact]
    public void TheFreeListHeadIsTheSameWidth()
        => Assert.Equal(PdfXrefFormat.EntryLength,
                        Encoding.ASCII.GetByteCount(PdfXrefFormat.FreeListHead));

    /// <summary>
    /// And the writer's real output agrees: every row of an emitted <c>xref</c> section is 20 bytes.
    /// This is the assertion the helper's own width cannot make — a caller that hand-spells the row
    /// again would pass the theory above and fail this.
    /// </summary>
    [Fact]
    public void EveryRowOfAWrittenXrefSectionIsTwentyBytes()
    {
        byte[] pdf = WriteSmallDocument();
        string text = Encoding.ASCII.GetString(pdf);

        // The keyword on its own line — "startxref" also contains "xref", and searching for the
        // bare word finds that one instead, which is how this fixture first failed.
        int at = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
        Assert.True(at >= 0, "the fixture emitted no classic xref section");

        // "xref\n", then a "first count" subsection header, then `count` entries.
        int cursor = text.IndexOf('\n', at + 1) + 1;
        int headerEnd = text.IndexOf('\n', cursor) + 1;
        string[] header = text.Substring(cursor, headerEnd - cursor - 1).Trim().Split(' ');
        int count = int.Parse(header[1]);
        Assert.True(count > 1, "the fixture needs more than the free-list head to be a witness");

        for (int i = 0; i < count; i++)
        {
            int start = headerEnd + i * PdfXrefFormat.EntryLength;
            string row = text.Substring(start, PdfXrefFormat.EntryLength);

            // 10 digits, space, 5 digits, space, type, then the two-byte EOL §7.5.4 requires —
            // either " \n" or "\r\n". Twenty bytes exactly, which is what the substring length
            // above already assumed and this confirms is really where the next row starts.
            Assert.Matches(@"^\d{10} \d{5} [nf][ \r]\n$", row);
        }
    }

    private static byte[] WriteSmallDocument()
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        pages.Set(PdfName.Kids, new PdfArray());
        pages.Set(PdfName.Count, new PdfNumber(0));

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteHeader(new PdfVersion(1, 7));

        var entries = new List<(int objNum, long offset)>
        {
            (1, writer.WriteIndirectObject(1, 0, catalog)),
            (2, writer.WriteIndirectObject(2, 0, pages)),
        };

        long xref = writer.WriteXrefTable(entries);

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, new PdfNumber(3));
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        writer.WriteTrailer(trailer, xref);

        return ms.ToArray();
    }
}
