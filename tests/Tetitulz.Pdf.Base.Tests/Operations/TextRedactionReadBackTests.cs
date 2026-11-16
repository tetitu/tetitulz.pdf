// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A search-and-redact that found nothing it could place must not hand back a page still showing the
/// word: the result is read back by the extractor that reads in drawing order, whatever the direction.
/// </summary>
public sealed class TextRedactionReadBackTests
{
    private const string Word = "ZQSECRET";

    private static byte[] Pdf(string content, string pageEntries = "")
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> {pageEntries} >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The layout cannot hold these together, so the search reports nothing, and the page still shows the word.</summary>
    [Theory]
    [InlineData("BT /F1 12 Tf 0.7 0.7 -0.7 0.7 50 50 Tm (" + Word + ") Tj ET")]
    [InlineData("BT /F1 12 Tf 0 1 -1 0 100 20 Tm (" + Word + ") Tj ET")]
    [InlineData("BT /F1 12 Tf -1 0 0 1 150 100 Tm (" + Word + ") Tj ET")]
    [InlineData("q 0 1 -1 0 150 0 cm BT /F1 12 Tf 10 10 Td (" + Word + ") Tj ET Q")]
    [InlineData("BT /F1 12 Tf 150 100 Td -100 Tz (" + Word + ") Tj ET")]
    public void AdversarialTextTheSearchCannotPlace_RefusesWithNothingWritten(string content)
    {
        byte[] pdf = Pdf(content);
        Assert.Empty(PdfTextRedactor.FindMatches(new MemoryStream(pdf), Word));   // why the read-back exists

        using var output = new MemoryStream();
        var refused = Assert.Throws<PdfRedactionException>(() => PdfTextRedactor.Redact(new MemoryStream(pdf), output, Word));

        Assert.Contains("still on page 1", refused.Message, System.StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The controls: what can be placed is removed; a word that is not there is no refusal, and the file comes back as it was.</summary>
    [Fact]
    public void TextTheSearchCanPlace_IsRemovedAndHandedOver()
    {
        byte[] pdf = Pdf("BT /F1 12 Tf 10 100 Td (" + Word + ") Tj ET");
        using var output = new MemoryStream();

        Assert.Single(PdfTextRedactor.Redact(new MemoryStream(pdf), output, Word));

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.DoesNotContain(Word, PdfTextExtractor.ExtractText(doc), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AWordNotThere_IsACopy()
    {
        byte[] pdf = Pdf("BT /F1 12 Tf 10 100 Td (other) Tj ET");
        using var output = new MemoryStream();

        Assert.Empty(PdfTextRedactor.Redact(new MemoryStream(pdf), output, Word));
        Assert.Equal(pdf, output.ToArray());
    }

    /// <summary>Case and regex as the search reads them: the read-back asks the same question.</summary>
    [Fact]
    public void TheReadBackAsksWhatTheOptionsAsk()
    {
        byte[] pdf = Pdf("BT /F1 12 Tf 0 1 -1 0 100 20 Tm (" + Word + ") Tj ET");

        Assert.Throws<PdfRedactionException>(() => PdfTextRedactor.Redact(new MemoryStream(pdf), new MemoryStream(), "zqsecret"));

        using var caseSensitive = new MemoryStream();
        Assert.Empty(PdfTextRedactor.Redact(new MemoryStream(pdf), caseSensitive, "zqsecret", new TextRedactionOptions { MatchCase = true }));
        Assert.True(caseSensitive.Length > 0);

        Assert.Throws<PdfRedactionException>(() => PdfTextRedactor.Redact(new MemoryStream(pdf), new MemoryStream(), "ZQ.ECRET", new TextRedactionOptions { UseRegex = true }));
    }

    /// <summary>A caller's sequence that runs once is read for the search and for the read-back alike.</summary>
    [Fact]
    public void APatternSequenceThatRunsOnce_IsAskedOfTheReadBackToo()
    {
        static IEnumerable<string> Once()
        {
            yield return Word;
        }
        using IEnumerator<string> single = Once().GetEnumerator();
        IEnumerable<string> OneShot()
        {
            while (single.MoveNext()) yield return single.Current;
        }

        byte[] pdf = Pdf("BT /F1 12 Tf 0 1 -1 0 100 20 Tm (" + Word + ") Tj ET");

        Assert.Throws<PdfRedactionException>(() => PdfTextRedactor.Redact(new MemoryStream(pdf), new MemoryStream(), OneShot()));
    }

    /// <summary>The identifier scan reads back too: a valid number drawn sideways is refused, an invalid one is no match.</summary>
    [Theory]
    [InlineData("123456782", true)]
    [InlineData("123456789", false)]
    public void AnIdentifierDrawnSideways_IsRefusedIfValid(string digits, bool refused)
    {
        byte[] pdf = Pdf("BT /F1 12 Tf 0 1 -1 0 100 20 Tm (" + digits + ") Tj ET");

        if (refused) Assert.Throws<PdfRedactionException>(() => PdfTextRedactor.RedactIsraeliIdentifiers(new MemoryStream(pdf), new MemoryStream()));
        else Assert.Empty(PdfTextRedactor.RedactIsraeliIdentifiers(new MemoryStream(pdf), new MemoryStream()));
    }
}
