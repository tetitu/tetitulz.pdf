// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The template engine's self-checks, made to fire. <c>VerifySurvivingGlyphs</c> refuses a rewrite
/// that moved or dropped a glyph outside the replaced token, and <c>VerifyErasure</c> reports a
/// token still extractable — but a correct engine never drifts, so neither changes an outcome on a
/// real document (GAPS.md §9, "A self-check is only exercised by a fault"). The engine's test seams
/// (<see cref="PdfTemplateEngine.RewrittenStreamTamper"/>,
/// <see cref="PdfTemplateEngine.ProducedDocumentTamper"/>) hand each check a drift on purpose.
/// </summary>
/// <remarks>
/// The fixture draws "{{name}} Smith" in Helvetica at 12 points from <c>72 700 Td</c>. The engine
/// rewrites it as <c>72 700 Td [-3837 &lt;20536D697468&gt;] TJ</c> — the token's advance as one
/// adjustment, then the space and "Smith" — and the replacement is drawn separately, so the doctored
/// stream holds only the original glyphs the check is about.
/// </remarks>
public class TemplateVerifierSeamTests
{
    private const string Content = "BT /F1 12 Tf 72 700 Td ({{name}} Smith) Tj ET\n";

    [Fact]
    public void AnUndoctoredRewrite_Passes()
    {
        var (result, written) = Replace(Content, (page, id, bytes) => bytes);
        Assert.Contains("name", result.ReplacedKeys);
        Assert.NotEmpty(written);
    }

    /// <summary>
    /// Every surviving glyph moved a point right is refused before anything reaches the caller's
    /// stream. The control is the undoctored rewrite.
    /// </summary>
    [Fact]
    public void AMovedSurvivingGlyph_IsRefused_AndNothingIsWritten()
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<PdfTemplateException>(() =>
            Replace(Content, Doctor("72 700 Td", "73 700 Td"), output));

        Assert.Contains("does not preserve the surrounding text", ex.Message);
        Assert.Contains("is no longer drawn there", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>A surviving glyph dropped from the rewrite — the "h" of Smith — is refused, by name.</summary>
    [Fact]
    public void ADroppedSurvivingGlyph_IsRefused()
    {
        var ex = Assert.Throws<PdfTemplateException>(() =>
            Replace(Content, Doctor("<20536D697468>", "<20536D6974>")));

        Assert.Contains("'h' was at (", ex.Message);
    }

    /// <summary>
    /// Positions are compared within a hundredth of a point: a drift of two hundredths or three —
    /// far below anything visible — is refused, across the line and up it.
    /// </summary>
    [Theory]
    [InlineData("72.03 700 Td")]
    [InlineData("72 700.03 Td")]
    [InlineData("72.02 700 Td")]
    [InlineData("72 700.02 Td")]
    public void ADriftOfTwoHundredthsOfAPointOrMore_IsRefused(string moved)
    {
        var ex = Assert.Throws<PdfTemplateException>(() =>
            Replace(Content, Doctor("72 700 Td", moved)));

        Assert.Contains("is no longer drawn there", ex.Message);
    }

    /// <summary>
    /// A drift of five thousandths of a point is within the hundredth and passes, even where it
    /// crosses the grid the positions are keyed on: the space after the token moves from 118.044
    /// to 118.049 — 11804.4 to 11804.9 hundredths, the next cell — and is found there. That is the
    /// drift the rounding of one re-emitted number makes, which must not refuse a page.
    /// </summary>
    [Theory]
    [InlineData("72.005 700 Td")]
    [InlineData("72 700.005 Td")]
    public void ADriftWithinAHundredthOfAPoint_Passes_AcrossTheGrid(string moved)
    {
        var (result, written) = Replace(Content, Doctor("72 700 Td", moved));

        Assert.Contains("name", result.ReplacedKeys);
        Assert.NotEmpty(written);
    }

    /// <summary>
    /// Text in a font the page's resources do not define decodes to nothing — the walk cannot say
    /// what it draws — so it carries no claim about where it must be drawn, and the rewrite passes.
    /// Holding it to a position would refuse every such page, since a glyph with no text is never
    /// counted as present.
    /// </summary>
    [Fact]
    public void AnUndecodableGlyph_CarriesNoPositionClaim()
    {
        var (result, written) = Replace("BT /F1 12 Tf 72 700 Td ({{name}} ) Tj /F2 12 Tf (AB) Tj ET\n", (page, id, bytes) => bytes);
        Assert.Contains("name", result.ReplacedKeys);
        Assert.NotEmpty(written);
    }

    /// <summary>
    /// The erasure check re-reads the produced document: handed the template itself, it finds the
    /// token still there and reports the key unverified. The control, the real output, reports
    /// nothing.
    /// </summary>
    [Fact]
    public void ATokenStillExtractable_IsReportedUnverified()
    {
        byte[] template = Pdf(Content);
        var (doctored, _) = Replace(Content, produced: _ => template);
        Assert.Contains("name", doctored.UnverifiedKeys);

        var (real, _) = Replace(Content);
        Assert.DoesNotContain("name", real.UnverifiedKeys);
        Assert.Contains("name", real.ReplacedKeys);
    }

    /// <summary>
    /// A re-read document with fewer pages than the edited one is not indexed past its end: an
    /// edited page it does not have is skipped, and the check reports no failure of its own. The
    /// control — bytes that are not a PDF — shows that failure is reported when it happens.
    /// </summary>
    [Fact]
    public void AReReadDocumentWithFewerPages_IsNotIndexedPastItsEnd()
    {
        const string couldNotReRead = "could not be re-read";

        var (empty, _) = Replace(Content, produced: _ => EmptyPdf());
        Assert.DoesNotContain(empty.Warnings, w => w.Contains(couldNotReRead));

        var (garbage, _) = Replace(Content, produced: _ => Encoding.ASCII.GetBytes("not a pdf"));
        Assert.Contains(garbage.Warnings, w => w.Contains(couldNotReRead));
    }

    private static Func<int, int, byte[], byte[]> Doctor(string from, string to) => (page, id, bytes) =>
    {
        string s = Encoding.Latin1.GetString(bytes);
        Assert.True(s.Contains(from), $"the rewritten stream no longer holds '{from}': {s}");
        return Encoding.Latin1.GetBytes(s.Replace(from, to));
    };

    private static (TemplateReplaceResult Result, byte[] Written) Replace(
        string content, Func<int, int, byte[], byte[]> rewritten = null, MemoryStream output = null,
        Func<byte[], byte[]> produced = null)
    {
        output ??= new MemoryStream();
        PdfTemplateEngine.RewrittenStreamTamper = rewritten;
        PdfTemplateEngine.ProducedDocumentTamper = produced;
        try
        {
            var result = PdfTemplateEngine.Replace(new MemoryStream(Pdf(content)), output,
                new Dictionary<string, string> { ["name"] = "Jo" }, new TemplateReplaceOptions());
            return (result, output.ToArray());
        }
        finally
        {
            PdfTemplateEngine.RewrittenStreamTamper = null;
            PdfTemplateEngine.ProducedDocumentTamper = null;
        }
    }

    private static byte[] Pdf(string content) => Assemble(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    private static byte[] EmptyPdf() => Assemble(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [] /Count 0 >>");

    private static byte[] Assemble(params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(ms.Length);
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Length;
        var sb = new StringBuilder($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }
}
