// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Review-fix coverage for the incremental-update xref generation number.
///
/// <see cref="PdfWriter"/>.WriteXrefTable previously hardcoded generation 0
/// in every <c>n</c> entry, while <see cref="IncrementalUpdateWriter"/> wrote
/// the object's real generation in the object header.  For a gen&gt;0 object
/// the two disagreed and strict readers dropped the incremental update.  The
/// fix adds a generation-aware <c>WriteXrefTable</c> overload and threads
/// <c>obj.GenerationNumber</c> from the writer into the xref entry.
/// </summary>
public class ReviewFixCore_IncrementalGenerationTests
{
    [Fact]
    public void IncrementalUpdate_Gen2Object_WritesGeneration2InXref()
    {
        // Append a single object with generation 2.  Its xref entry must read
        // "<offset> 00002 n", matching the "<obj> 2 obj" header — not the old
        // hardcoded "00000".
        var modified = new List<IndirectObject>
        {
            new IndirectObject(5, 2, new PdfNumber(42)),
        };
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, new PdfNumber(6));
        trailer.Set(PdfName.Root, new PdfReference(1, 0));

        using var input = BuildMinimalPdfInput();
        using var output = new MemoryStream();

        IncrementalUpdateWriter.WriteUpdate(
            input, output, modified, trailer,
            previousXrefOffset: MinimalPdfXrefOffset(), size: 6);

        byte[] outBytes = output.ToArray();
        string text = Encoding.ASCII.GetString(outBytes);

        // The object header carries gen 2 ...
        Assert.Contains("5 2 obj", text);

        // ... and so must the corresponding xref entry in the *appended* section.
        // Inspect only the bytes written after the copied original (appendStart =
        // original length) so the original's gen-0 entries don't confuse the check.
        long appendStart = BuildMinimalPdfInput().Length;
        string appended = Encoding.ASCII.GetString(
            outBytes, (int)appendStart, outBytes.Length - (int)appendStart);

        // The appended xref subsection for object 5 is "5 1\n<offset> 00002 n \n".
        Assert.Contains("00002 n", appended);
        // The bug would have emitted "00000 n" here instead.  There is no gen-0
        // object in the appended section (only object 5, gen 2), so its absence
        // proves the generation was threaded through.
        //
        // That premise is what makes the chained offset above load-bearing: a section with no
        // /Prev is standalone and re-declares the original's objects, object 1 among them at
        // gen 0, so on an unchained write this assertion would fail for a reason that has
        // nothing to do with generation threading.
        Assert.DoesNotContain("00000 n", appended);
    }

    [Fact]
    public void IncrementalUpdate_Gen0Object_StillWritesGeneration0()
    {
        // The common (gen 0) path must be unchanged.
        var modified = new List<IndirectObject>
        {
            new IndirectObject(5, 0, new PdfNumber(42)),
        };
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, new PdfNumber(6));
        trailer.Set(PdfName.Root, new PdfReference(1, 0));

        using var input = BuildMinimalPdfInput();
        using var output = new MemoryStream();

        IncrementalUpdateWriter.WriteUpdate(
            input, output, modified, trailer,
            previousXrefOffset: MinimalPdfXrefOffset(), size: 6);

        string text = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("5 0 obj", text);
        Assert.Contains("00000 n", text);
    }

    [Fact]
    public void WriteXrefTable_GenerationAwareOverload_EmitsGenerationField()
    {
        // Directly exercise the new (objNum, gen, offset) overload.
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var entries = new List<(int objNum, int gen, long offset)>
        {
            (7, 3, 12345L),
        };
        writer.WriteXrefTable(entries, includeObject0: true);

        string text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("0000012345 00003 n", text);
        // Object 0 is still the free-list head.
        Assert.Contains("0000000000 65535 f", text);
    }

    [Fact]
    public void WriteXrefTable_LegacyOverload_DefaultsGenerationZero()
    {
        // The original (objNum, offset) overload must still produce gen-0 entries
        // (used by every full-write path, which only ever emits gen-0 objects).
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var entries = new List<(int objNum, long offset)>
        {
            (7, 12345L),
        };
        writer.WriteXrefTable(entries, includeObject0: true);

        string text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("0000012345 00000 n", text);
    }

    /// <summary>
    /// Minimal "previous revision" PDF.  IncrementalUpdateWriter copies it
    /// verbatim and appends, so it does not need to be fully parseable here —
    /// but its <c>xref</c> section does have to be <i>locatable</i>, because the
    /// writer verifies the offset it is handed by reading the bytes there.
    /// </summary>
    private static MemoryStream BuildMinimalPdfInput()
        => new MemoryStream(Encoding.ASCII.GetBytes(MinimalPdfText()));

    /// <summary>
    /// Byte offset of the <c>xref</c> keyword in <see cref="BuildMinimalPdfInput"/> — located in
    /// the text rather than hardcoded, which is how this fixture came to declare
    /// <c>startxref 50</c> for a section that begins at 45.
    /// </summary>
    private static long MinimalPdfXrefOffset()
    {
        int offset = MinimalPdfText().IndexOf("xref\n0 2\n", StringComparison.Ordinal);
        Assert.True(offset > 0, "fixture: the minimal PDF must contain a classic xref section");
        return offset;
    }

    private static string MinimalPdfText()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        sb.Append("xref\n0 2\n0000000000 65535 f \n0000000010 00000 n \n");
        sb.Append("trailer\n<< /Size 2 /Root 1 0 R >>\nstartxref\n45\n%%EOF\n");
        return sb.ToString();
    }
}
