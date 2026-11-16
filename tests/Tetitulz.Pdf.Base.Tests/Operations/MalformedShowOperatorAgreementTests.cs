// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A <c>"</c> operator with fewer than its three operands (§9.4.3 Table 109) leaves the text
/// position untouched — in the writers as well as the readers.
/// </summary>
/// <remarks>
/// <para><b>Six implementations of the §9.4.2 text-position state machine live in this library, and
/// they split three-to-two on this operator.</b> The raster and SVG renderers,
/// <c>PdfStructuredTextExtractor</c> and Core's <c>PdfTextExtractor</c> all require three operands
/// before touching <c>Tm</c>, <c>Tlm</c>, <c>Tw</c> or <c>Tc</c>. <c>PdfTemplateEngine</c> and
/// <c>PdfRedactionEngine</c> did not, and their operand helper returns <c>0</c> for a missing
/// operand — so a one-operand <c>"</c> zeroed the spacing and moved the line origin down one
/// leading.</para>
///
/// <para><b>For the redaction engine that is a leak, not a cosmetic drift.</b> Every later line in
/// the text object sits one leading lower in the engine's model than where it is actually painted,
/// so a rectangle the caller aims at real text misses it — <b>and the text the caller asked to
/// remove survives into the output.</b> Which behaviour is "right" for a malformed operator is not
/// the question: an engine that edits a page must model the position the way the renderer paints
/// it.</para>
///
/// <para><b>This is the defect class CLAUDE.md warns pixel comparison cannot reach.</b> Each engine
/// stays internally self-consistent, so the divergence shows only against a <i>different</i>
/// component's answer. These assert the writer against the reader.</para>
///
/// <para><b>Only the redaction half is asserted here, and that is stated rather than implied.</b>
/// <c>PdfTemplateEngine</c> carries the identical fix, but no fixture reaching its public
/// <c>Replace</c> could be made to observe the drift: it locates and erases a token by a route that
/// survives the shifted line origin, and the replacement text it draws is not extracted back. Two
/// candidate tests were written — one on the replacement's baseline, one on the erasure — and both
/// were dropped when mutating that engine's guard back left them green. A test whose name claims a
/// consequence it cannot reach is worse than no test.</para>
/// </remarks>
public sealed class MalformedShowOperatorAgreementTests
{
    private const int Leading = 40;
    private const int FirstBaseline = 700;

    /// <summary>The reader's answer, which is the specification of correct here.</summary>
    [Fact]
    public void TheReaderDoesNotMoveTheLineForAMalformedQuote()
    {
        Assert.Equal(SecondLineBaseline(WithoutQuote()), SecondLineBaseline(WithMalformedQuote()), 1);
    }

    /// <summary>
    /// <b>The leak.</b> A rectangle over the second line's real position must remove it. With the
    /// phantom line move the engine believed that line was one leading lower, so the rectangle
    /// missed and the text survived redaction.
    /// </summary>
    [Fact]
    public void RedactionRemovesTextTheMalformedQuoteUsedToDisplace()
    {
        byte[] redacted = RedactSecondLine(WithMalformedQuote());

        Assert.DoesNotContain("SECOND", ExtractedText(redacted), StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and the half that stops the case above from passing on a rectangle that removes
    /// everything: with no <c>"</c> at all the same rectangle removes the second line and leaves
    /// the first alone.
    /// </summary>
    [Fact]
    public void TheRectangleRemovesTheSecondLineAndSparesTheFirst()
    {
        string text = ExtractedText(RedactSecondLine(WithoutQuote()));

        Assert.DoesNotContain("SECOND", text, StringComparison.Ordinal);
        Assert.Contains("FIRST", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The premise: the fixture really carries a one-operand <c>"</c>, and a conforming
    /// three-operand one really does move the line — so the guard cannot be "ignore every
    /// <c>"</c>".
    /// </summary>
    [Fact]
    public void TheFixtureIsMalformedAndAWellFormedQuoteStillMoves()
    {
        Assert.Contains("(x) \"", Encoding.ASCII.GetString(WithMalformedQuote()), StringComparison.Ordinal);

        float malformed = SecondLineBaseline(WithMalformedQuote());
        float wellFormed = SecondLineBaseline(WithWellFormedQuote());

        Assert.True(wellFormed < malformed - 1f,
            $"a conforming quote must advance a line (conforming {wellFormed}, malformed {malformed})");
    }

    // ------------------------------------------------------------------

    private static float SecondLineBaseline(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var page = PdfStructuredTextExtractor.Extract(ms, 0);

        var second = page.Lines.FirstOrDefault(
            l => l.Text != null && l.Text.Contains("SECOND", StringComparison.Ordinal));
        Assert.True(second != null, "the fixture's second line was not extracted");
        return second.Y;
    }

    private static string ExtractedText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var page = PdfStructuredTextExtractor.Extract(ms, 0);
        return string.Join("\n", page.Lines.Select(l => l.Text ?? string.Empty));
    }

    /// <summary>
    /// Redacts a band around the second line's <b>real</b> baseline, well clear of the first.
    /// </summary>
    private static byte[] RedactSecondLine(byte[] pdf)
    {
        const int secondBaseline = FirstBaseline - Leading;    // 660

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(input, output, new[]
        {
            new PdfRedactionArea
            {
                PageIndex = 0,
                X = 60,
                Y = secondBaseline - 4,
                Width = 300,
                Height = 20,
            },
        });
        return output.ToArray();
    }

    /// <summary>The baseline y of the line containing <paramref name="needle"/>.</summary>
    private static float BaselineOf(byte[] pdf, string needle)
    {
        using var ms = new MemoryStream(pdf);
        var page = PdfStructuredTextExtractor.Extract(ms, 0);

        var line = page.Lines.FirstOrDefault(
            l => l.Text != null && l.Text.Contains(needle, StringComparison.Ordinal));
        Assert.True(line != null, $"'{needle}' was not extracted from the output");
        return line.Y;
    }



    private static byte[] WithMalformedQuote() => Build("(x) \"\n");
    private static byte[] WithWellFormedQuote() => Build("0 0 (x) \"\n");
    private static byte[] WithoutQuote() => Build("");

    /// <summary>
    /// One text object: the first line, the caller's injected operator, an explicit <c>T*</c>, then
    /// the second line. Only the injected operator differs between fixtures, so any difference in
    /// where the second line lands is that operator's doing.
    /// </summary>
    private static byte[] Build(string injected)
    {
        string content =
            $"BT /F1 12 Tf {Leading} TL 72 {FirstBaseline} Td\n" +
            "(FIRST) Tj\n" +
            injected +
            "T*\n" +
            "(SECOND {{k}}) Tj\n" +
            "ET\n";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
