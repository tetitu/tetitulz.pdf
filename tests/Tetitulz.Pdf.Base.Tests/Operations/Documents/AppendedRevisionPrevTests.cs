// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// An appended revision's <c>/Prev</c> points at the cross-reference section the document already
/// had — the link that makes everything written before it still reachable.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep over CLAUDE.md's shared decision points
/// replaced <c>doc.Parser.StartXrefOffset</c> with <c>0L</c> in
/// <c>PdfPageContentAppender.AppendRevision</c> — the one trailer-and-write-tail every appended
/// revision in Base goes through — and <b>four suites stayed green</b>: Base, Core, Accessibility
/// and Conformance. Every fixture that appends a revision then reads the result back through a
/// parser that recovers by scanning when the chain is broken, so a <c>/Prev</c> of 0 was invisible
/// to all of them.</para>
///
/// <para><b>What it costs is not this reader but every other one.</b> §7.5.6 makes <c>/Prev</c> the
/// only link from the newest section to the previous one. Written as 0 it names the file's first
/// byte, so a conforming reader that trusts the chain — rather than scanning, as this library's
/// repair path can — finds exactly the objects in the appended revision and nothing else: a signed
/// document whose signature revision is intact and whose pages have vanished.</para>
///
/// <para>Both assertions are made, and they are not the same one. The <c>/Prev</c> value is the
/// direct evidence; the page still being reachable is the consequence, and on its own it would pass
/// against a parser that had scanned its way around the damage.</para>
/// </remarks>
public sealed class AppendedRevisionPrevTests
{
    [Fact]
    public void AnAppendedRevision_ChainsToThePreviousCrossReferenceSection()
    {
        byte[] original = OnePageDocument();
        long originalStartxref;

        using var source = new MemoryStream(original, writable: false);
        using var output = new MemoryStream();

        using (PdfDocument doc = PdfDocument.Open(source))
        {
            originalStartxref = doc.Parser.StartXrefOffset;
            Assert.True(originalStartxref > 0, "the fixture must carry a real cross-reference offset");

            var added = new PdfDictionary();
            added.Set(PdfName.Type, PdfName.Page);

            source.Position = 0;
            PdfPageContentAppender.AppendRevision(
                source, output, doc,
                new List<IndirectObject> { new IndirectObject(10, 0, added) },
                nextObjectNumber: 11);
        }

        byte[] appended = output.ToArray();

        using PdfDocument reopened = PdfDocument.Open(new MemoryStream(appended, writable: false));

        // The direct evidence: the newest trailer names the section the document already had.
        PdfObject prev = reopened.Trailer[PdfName.Prev];
        var prevNumber = Assert.IsType<PdfNumber>(prev);
        Assert.Equal(originalStartxref, prevNumber.LongValue);

        // And the consequence: everything written before the revision is still reachable.
        Assert.Single(reopened.Pages);
    }

    /// <summary>
    /// The control: the same document, unappended, has no <c>/Prev</c> at all — so the assertion
    /// above is about the link the append created rather than one that was always there.
    /// </summary>
    [Fact]
    public void TheUnappendedDocument_HasNoPrev()
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(OnePageDocument(), writable: false));

        Assert.Null(doc.Trailer[PdfName.Prev]);
        Assert.Single(doc.Pages);
    }

    // ------------------------------------------------------------------

    /// <summary>One page, with a real cross-reference table so <c>StartXrefOffset</c> is meaningful.</summary>
    private static byte[] OnePageDocument()
    {
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
