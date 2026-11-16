// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A page whose <c>/Contents</c> is an <b>array</b> of streams, rewritten by the single-revision
/// rewriter that redaction and templating share.
/// </summary>
/// <remarks>
/// <para><b>Why the array form needs its own tests.</b> The replacement is one stream covering the
/// whole page, so part 0 receives it and <i>every other part must be emptied</i> — otherwise the
/// content those parts carry is drawn again, unredacted, from a page the caller was told had been
/// cleaned. And each part keeps its own object number, because that number is the only thing that
/// identifies a stream as shared with another page.</para>
///
/// <para>Only two tests in the repository reached <c>PdfFullRewriter.TrySubstitutePageContent</c>
/// before these, and both use the single-stream or whole-array shape where neither decision can go
/// wrong: emptying the leftover parts, keeping their identity, and refusing a page with no content
/// all survived being mutated out.</para>
/// </remarks>
public class MultiPartContentsRewriteTests
{
    /// <summary>
    /// <b>Every part is rewritten, not just the first.</b> A whole-page redaction removes both
    /// strings; leaving the trailing parts untouched draws the second one again from its original
    /// stream, so the word is redacted from one part of the page and not from the other.
    /// </summary>
    [Fact]
    public void EveryPartOfAMultiPartContentsIsRewrittenNotJustTheFirst()
    {
        using var input = new MemoryStream(TwoPartContents());
        using var output = new MemoryStream();

        PdfRedactionEngine.Redact(input, output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 0, Y = 0, Width = 200, Height = 200 },
        });

        output.Position = 0;
        string text = PdfOperations.ExtractText(output);

        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        // The second stream's text, which lives in parts[1] and is what the leftover-emptying loop
        // is for. Without it this survives while the first part's text is correctly gone.
        Assert.DoesNotContain("TRAILING", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The floor: both words really are in the document before redaction, so the absences above are
    /// claims about the rewrite rather than about a fixture that never had them.
    /// </summary>
    [Fact]
    public void TheFixtureContainsBothWordsBeforeRedaction()
    {
        using var ms = new MemoryStream(TwoPartContents());
        string text = PdfOperations.ExtractText(ms);

        Assert.Contains("SECRET", text, StringComparison.Ordinal);
        Assert.Contains("TRAILING", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A part shared with another page keeps its identity.</b> Each element of the array carries
    /// its own object number, and that number is what marks it as shared — lose it and the part is
    /// read as private, emptied in place, and the page that was not named loses its content.
    /// </summary>
    [Fact]
    public void RedactingAnArrayContentsPageLeavesAPageSharingOnePartIntact()
    {
        using var input = new MemoryStream(SharedSecondPart());
        using var output = new MemoryStream();

        PdfRedactionEngine.Redact(input, output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 0, Y = 0, Width = 200, Height = 200 },
        });

        output.Position = 0;
        string text = PdfOperations.ExtractText(output);

        // Page 2 was not named and still draws the shared stream, so its text must survive...
        Assert.Contains("SHARED", text, StringComparison.Ordinal);
        // ...while page 1's own part is gone.
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page with no <c>/Contents</c> is refused with the engine's own exception, rather than
    /// indexing an empty list. The refusal is the documented contract; an
    /// <see cref="IndexOutOfRangeException"/> is not.
    /// </summary>
    [Fact]
    public void APageWithNoContentsIsRefusedWithTheEnginesOwnException()
    {
        using var input = new MemoryStream(NoContents());
        using var output = new MemoryStream();

        Assert.Throws<PdfRedactionException>(() =>
            PdfRedactionEngine.Redact(input, output, new[]
            {
                new PdfRedactionArea { PageIndex = 0, X = 0, Y = 0, Width = 200, Height = 200 },
            }));
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>One page, content split across objects 4 and 6.</summary>
    /// <summary>
    /// <b>With no allocator, a page whose content is shared is refused — not edited in place.</b>
    /// The parameter's own contract: altering a page the caller never named is the worse failure, so
    /// the method answers false and changes nothing.
    /// </summary>
    /// <remarks>
    /// Both production callers always pass an allocator, so this branch is reachable only through the
    /// method itself — which is why the sweep's "proceed anyway" mutation survived every engine test.
    /// Without the guard the call does not even fail cleanly: it dereferences the missing allocator.
    /// </remarks>
    [Fact]
    public void ASharedContentStreamWithNoAllocatorIsRefusedAndLeftUntouched()
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(SharedSecondPart()));
        var page = doc.Pages[0];
        var shared = (Tetitulz.Pdf.Core.Objects.PdfStream)doc.GetObject(6);
        byte[] before = (byte[])shared.RawData.Clone();

        bool rewritten = PdfFullRewriter.TrySubstitutePageContent(
            page, doc, Encoding.ASCII.GetBytes("q Q\n"), allocateObjectNumber: null, extras: null);

        Assert.False(rewritten);
        Assert.Equal(before, shared.RawData);
        Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfArray>(page.Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.Contents]);
    }

    /// <summary>
    /// The acceptance control for the refusal above: the same page, given an allocator, is rewritten
    /// into a fresh private stream — and the shared one still keeps its bytes for the other page.
    /// </summary>
    [Fact]
    public void TheSamePageWithAnAllocatorIsRewrittenIntoAPrivateClone()
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(SharedSecondPart()));
        var page = doc.Pages[0];
        var shared = (Tetitulz.Pdf.Core.Objects.PdfStream)doc.GetObject(6);
        byte[] before = (byte[])shared.RawData.Clone();
        var extras = new Dictionary<int, Tetitulz.Pdf.Core.Objects.PdfObject>();

        bool rewritten = PdfFullRewriter.TrySubstitutePageContent(
            page, doc, Encoding.ASCII.GetBytes("q Q\n"), allocateObjectNumber: () => 42, extras: extras);

        Assert.True(rewritten);
        var contents = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfReference>(page.Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.Contents]);
        Assert.Equal(42, contents.ReferenceObjectNumber);
        Assert.Equal("q Q\n", Encoding.ASCII.GetString(((Tetitulz.Pdf.Core.Objects.PdfStream)extras[42]).RawData));
        Assert.Equal(before, shared.RawData);
    }

    private static byte[] TwoPartContents() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents [4 0 R 6 0 R] " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
        Stream("BT /F1 12 Tf 20 150 Td (SECRET) Tj ET\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Stream("BT /F1 12 Tf 20 100 Td (TRAILING) Tj ET\n"));

    /// <summary>
    /// Page 1 is <c>[4 0 R 6 0 R]</c>; page 2 draws object 6 alone, so that part is shared and must
    /// survive a redaction of page 1.
    /// </summary>
    private static byte[] SharedSecondPart() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents [4 0 R 6 0 R] " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
        Stream("BT /F1 12 Tf 20 150 Td (SECRET) Tj ET\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Stream("BT /F1 12 Tf 20 100 Td (SHARED) Tj ET\n"),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>");

    private static byte[] NoContents() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>");

    private static string Stream(string data) =>
        "<< /Length " + data.Length + " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
