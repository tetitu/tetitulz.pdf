// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="IncrementalUpdateOptions.DiagnosticSink"/> is fanned out one subscriber at a time, and
/// a subscriber that throws neither blinds the others nor breaks the write.
/// </summary>
/// <remarks>
/// <para>This site was <c>options?.DiagnosticSink?.Invoke(operation, detail, ex)</c> — a single
/// multicast invoke with no <c>catch</c> — so a host that attached two handlers lost every
/// notification after the first one that threw, and the exception escaped into the writer.</para>
///
/// <para><b>The second half breaks a contract this repo states in three places.</b> The sink's own
/// documentation promises the writer "never swallows a problem silently"; CLAUDE.md describes it as
/// a writer that "never throws — it reports and falls back to the original bytes"; and
/// <c>Base.Operations.VerifiedWrite</c> exists precisely because a lost revision must be reported
/// rather than thrown. A host's logging callback could break all three from the outside, and the
/// caller would see an exception from a method documented as incapable of producing one.</para>
///
/// <para>The trigger is a null entry in the object list, which the writer reports and skips. Any
/// reported anomaly would do; this one needs no damaged file, so the fixture cannot fail for a
/// reason other than the one under test.</para>
/// </remarks>
public class DiagnosticSinkFanOutTests
{
    private const int CatalogNumber = 1;
    private const int PagesNumber = 2;
    private const int AppendedNumber = 40;

    /// <summary>
    /// The throwing handler is attached first, so a single multicast invoke never reaches the
    /// collector — and the collector must receive the message itself, not merely a count.
    /// </summary>
    [Fact]
    public void AThrowingDiagnosticSink_DoesNotBlindTheOnesAfterIt()
    {
        var received = new List<string>();

        Action<string, string, Exception> sink =
            (_, _, _) => throw new InvalidOperationException("a host logging callback that throws");
        sink += (operation, detail, _) => received.Add(operation + ": " + detail);

        WriteUpdateWithANullEntry(sink);

        Assert.Contains(received, m =>
            m.Contains("IncrementalUpdateWriter.WriteUpdate") &&
            m.Contains("is not a valid indirect object; it was skipped"));
    }

    /// <summary>
    /// The exception does not escape into the writer: the update is still written, and the object
    /// that <i>was</i> valid still reaches the output.
    /// </summary>
    /// <remarks>
    /// Asserted on the reopened document rather than on the absence of a throw. A body that only
    /// called the writer would pass with the write deleted; this one fails unless the appended
    /// revision is really there and really reopens.
    /// </remarks>
    [Fact]
    public void AThrowingDiagnosticSink_DoesNotBreakTheWrite()
    {
        Action<string, string, Exception> sink =
            (_, _, _) => throw new InvalidOperationException("a host logging callback that throws");

        byte[] updated = WriteUpdateWithANullEntry(sink);

        using var reopened = PdfDocument.Open(new MemoryStream(updated));
        var appended = Assert.IsType<PdfDictionary>(reopened.Parser.GetObject(AppendedNumber));
        Assert.Equal(PdfName.Page, appended.GetName(PdfName.Type));
    }

    // ================================================================ fixture

    /// <summary>
    /// Appends one valid object plus a <c>null</c> entry — the null is what the writer reports and
    /// skips — and returns the updated bytes.
    /// </summary>
    private static byte[] WriteUpdateWithANullEntry(Action<string, string, Exception> sink)
    {
        byte[] original = MinimalPdf();
        using PdfDocument source = PdfDocument.Open(new MemoryStream(original));

        var appended = new PdfDictionary();
        appended.Set(PdfName.Type, PdfName.Page);

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(
            original, output,
            new List<IndirectObject> { null, new IndirectObject(AppendedNumber, 0, appended) },
            new IncrementalUpdateOptions { Document = source, DiagnosticSink = sink });

        return output.ToArray();
    }

    private static byte[] MinimalPdf()
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(PagesNumber, 0));

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        pages.Set(PdfName.Kids, new PdfArray());
        pages.Set(PdfName.Count, new PdfNumber(0));

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(CatalogNumber, 0));

        using var buffer = new MemoryStream();
        PdfCompressedFileWriter.Write(
            buffer, new PdfVersion(1, 7),
            new List<(int, PdfObject)> { (CatalogNumber, catalog), (PagesNumber, pages) },
            trailer);
        return buffer.ToArray();
    }
}
