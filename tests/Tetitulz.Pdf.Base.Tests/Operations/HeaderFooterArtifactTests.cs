// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// &#167;14.8.2.2: a running head is page furniture, not document content, so it must be marked as
/// a pagination artifact or a screen reader announces it in the middle of the page's text.
/// </summary>
/// <remarks>
/// <para><b>These read the appended content stream, and they have to.</b> Extraction returns the
/// header's text whether or not it is marked, and neither renderer draws marked content
/// differently - so every assertion available at the document level passes with the marking
/// deleted. That blind spot is why the writer shipped unmarked beside <c>PdfBatesNumbering</c>,
/// which does mark, for as long as it did.</para>
///
/// <para>The subtype is asserted, not just the artifact: <c>/Pagination</c> alone would be as true
/// of a watermark, and it is the subtype that says which piece of furniture this is.</para>
/// </remarks>
public class HeaderFooterArtifactTests
{
    private const string HeaderText = "RUNNINGHEAD";
    private const string FooterText = "RUNNINGFOOT";

    private const string HeaderBdc = "/Artifact <</Type /Pagination /Subtype /Header>> BDC";
    private const string FooterBdc = "/Artifact <</Type /Pagination /Subtype /Footer>> BDC";

    /// <summary>The header opens a pagination artifact carrying the <c>/Header</c> subtype.</summary>
    [Fact]
    public void TheHeaderIsAPaginationArtifactWithTheHeaderSubtype()
    {
        string stream = AppendedStream(Stamp(header: HeaderText, footer: null));

        Assert.Contains(HeaderBdc, stream);
        AssertInsideAnArtifact(stream, HeaderBdc, HeaderText);
    }

    /// <summary>The footer's subtype is <c>/Footer</c>, not the header's.</summary>
    [Fact]
    public void TheFooterIsAPaginationArtifactWithTheFooterSubtype()
    {
        string stream = AppendedStream(Stamp(header: null, footer: FooterText));

        Assert.Contains(FooterBdc, stream);
        AssertInsideAnArtifact(stream, FooterBdc, FooterText);
        Assert.DoesNotContain(HeaderBdc, stream);
    }

    /// <summary>
    /// Header and footer are marked <b>separately</b>, each inside its own sequence.
    /// </summary>
    /// <remarks>
    /// The cheaper implementation wraps the whole stamp in one artifact. It would satisfy "the text
    /// is marked" while having to pick a single subtype, so one of the two would be announced as
    /// the other. This is the assertion that rejects it: each text must sit inside the sequence
    /// opened by <i>its own</i> subtype.
    /// </remarks>
    [Fact]
    public void TheHeaderAndTheFooterEachGetTheirOwnSequence()
    {
        string stream = AppendedStream(Stamp(header: HeaderText, footer: FooterText));

        AssertInsideAnArtifact(stream, HeaderBdc, HeaderText);
        AssertInsideAnArtifact(stream, FooterBdc, FooterText);

        // Two sequences, not one spanning both.
        Assert.Equal(2, Occurrences(stream, "BDC"));
        Assert.Equal(2, Occurrences(stream, "EMC"));

        // ...and the footer's text is NOT inside the header's sequence, which one wrapping
        // artifact would make true.
        int headerEmc = stream.IndexOf("EMC", stream.IndexOf(HeaderBdc, StringComparison.Ordinal),
                                       StringComparison.Ordinal);
        Assert.True(stream.IndexOf(FooterText, StringComparison.Ordinal) > headerEmc,
            "the footer must open its own sequence, not ride inside the header's");
    }

    /// <summary>
    /// The acceptance control: a stamp with only a header emits <b>no</b> footer sequence.
    /// </summary>
    /// <remarks>
    /// Without this, an implementation that opened both sequences unconditionally would pass every
    /// test above while writing an empty artifact - marked content with no content in it.
    /// </remarks>
    [Fact]
    public void AHeaderOnlyStampOpensExactlyOneSequence()
    {
        string stream = AppendedStream(Stamp(header: HeaderText, footer: null));

        Assert.Equal(1, Occurrences(stream, "BDC"));
        Assert.Equal(1, Occurrences(stream, "EMC"));
        Assert.DoesNotContain(FooterBdc, stream);
    }

    /// <summary>
    /// Every sequence opened is closed. An unbalanced <c>BDC</c> corrupts the marked-content
    /// nesting for everything drawn after it, which no assertion about the header would notice.
    /// </summary>
    [Fact]
    public void EverySequenceIsClosed()
    {
        string stream = AppendedStream(Stamp(header: HeaderText, footer: FooterText));

        Assert.Equal(Occurrences(stream, "BDC"), Occurrences(stream, "EMC"));
        Assert.True(stream.LastIndexOf("EMC", StringComparison.Ordinal)
                  > stream.LastIndexOf("BDC", StringComparison.Ordinal),
            "the last sequence opened must be closed after it");
    }

    /// <summary>
    /// Marking must not cost the text: it is still extractable, and the page's own body survives.
    /// </summary>
    /// <remarks>
    /// The correlation the content-stream assertions cannot make on their own - a writer that
    /// emitted the artifact and dropped the <c>Tj</c> would satisfy every one of them.
    /// </remarks>
    [Fact]
    public void TheStampedTextIsStillExtractable()
    {
        byte[] stamped = Stamp(header: HeaderText, footer: FooterText);

        using var ms = new MemoryStream(stamped, writable: false);
        string text = PdfOperations.ExtractText(ms) ?? string.Empty;

        Assert.Contains(HeaderText, text);
        Assert.Contains(FooterText, text);
        Assert.Contains("BODY", text);
    }

    // ----------------------------------------------------------------

    /// <summary>Asserts <paramref name="needle"/> falls between <paramref name="bdc"/> and its EMC.</summary>
    private static void AssertInsideAnArtifact(string stream, string bdc, string needle)
    {
        int open = stream.IndexOf(bdc, StringComparison.Ordinal);
        Assert.True(open >= 0, "no sequence opened by " + bdc + " in: " + stream);

        int close = stream.IndexOf("EMC", open, StringComparison.Ordinal);
        Assert.True(close > open, "the sequence opened by " + bdc + " is never closed in: " + stream);

        int at = stream.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at > open && at < close,
            needle + " is at " + at + ", outside the sequence spanning "
            + open + ".." + close + ": " + stream);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static byte[] Stamp(string header, string footer)
    {
        using var input = new MemoryStream(OnePageDocument(), writable: false);
        using var output = new MemoryStream();
        PdfHeaderFooterWriter.AddHeaderFooter(input, output, new HeaderFooterOptions
        {
            HeaderText = header,
            FooterText = footer,
        });
        return output.ToArray();
    }

    /// <summary>
    /// The decoded text of the <b>last</b> stream in page 0's <c>/Contents</c> - the one the
    /// appender added. The fixture's own content is the first, so the last is unambiguous.
    /// </summary>
    private static string AppendedStream(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        var contents = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]) as PdfArray;
        Assert.NotNull(contents);
        Assert.True(contents.Count >= 2, "the stamp is appended as a second content stream");

        var stream = doc.ResolveObject(contents[contents.Count - 1]) as PdfStream;
        Assert.NotNull(stream);
        return Encoding.ASCII.GetString(stream.GetDecodedData());
    }

    private static byte[] OnePageDocument()
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        const string content = "BT /F1 12 Tf 1 0 0 1 100 400 Tm (BODY) Tj ET";

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
