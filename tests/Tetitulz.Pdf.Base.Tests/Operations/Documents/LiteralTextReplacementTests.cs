// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Replacing literal text in a document that was never authored as a template — GAPS.md §19d,
/// the half of find-and-replace that does <b>not</b> reflow.
/// </summary>
/// <remarks>
/// <para><b>The guarantee is what separates this from the reflowing kind.</b> A replacement is drawn
/// into the slot the original occupied and nothing around it moves, so everything the call did not
/// replace is exactly where it was. Reflow re-lays the containing paragraph and therefore moves text
/// it was not asked to touch; that is a different contract and is not attempted here.</para>
///
/// <para><b>Asserted by extracting the produced document</b>, not by inspecting what the writer
/// intended: the claim is that a reader sees the new text and no longer sees the old, which is a
/// fact about the output.</para>
/// </remarks>
public class LiteralTextReplacementTests
{
    /// <summary>
    /// Replacement needs a font to draw with. Without one the engine erases and reports
    /// <c>NotDrawn</c> — correctly, and loudly — so a test that omitted it would be asserting
    /// against a document with a hole in it rather than a replacement.
    /// </summary>
    private static TemplateReplaceOptions WithFont() =>
        new TemplateReplaceOptions { AllowSystemFontSubstitution = true };

    private static TemplateReplaceResult Replace(
        byte[] pdf, out byte[] produced, params (string From, string To)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.From, p => p.To, StringComparer.Ordinal);

        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTemplateEngine.ReplaceText(input, output, map, WithFont());
        produced = output.ToArray();
        return result;
    }

    private static string TextOf(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(stream);
        return PdfTextExtractor.ExtractText(doc);
    }

    /// <summary>
    /// The old text is gone from the produced document, and the engine reports the key replaced.
    /// </summary>
    /// <remarks>
    /// Asserted without needing the new text to be drawable, so it holds on any machine: the
    /// erasure is the half that never depends on a font being available.
    /// </remarks>
    [Fact]
    public void ALiteralWordIsErasedAndReportedReplaced()
    {
        TemplateReplaceResult result = Replace(
            MinimalPdfBuilder.CreateMinimalPdf("Hello World"), out byte[] produced,
            ("World", "There"));

        Assert.Contains("World", result.ReplacedKeys);
        Assert.DoesNotContain("World", TextOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// Text the call did not name is untouched — the guarantee that distinguishes this from reflow.
    /// </summary>
    [Fact]
    public void TextItWasNotAskedToReplaceIsUnchanged()
    {
        Replace(MinimalPdfBuilder.CreateMinimalPdf("Hello World"), out byte[] produced,
                ("World", "There"));

        Assert.Contains("Hello", TextOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The replacement is actually drawn and reads back</b> — the claim the erasure alone does
    /// not make.
    /// </summary>
    /// <remarks>
    /// Gated on host fonts because drawing needs one: with none available the engine erases and
    /// reports <c>NotDrawn</c> rather than inventing a face, so this would otherwise fail on a
    /// machine without fonts for a reason that has nothing to do with replacement.
    /// </remarks>
    [SystemFontFact]
    public void TheReplacementIsDrawnAndExtractable()
    {
        TemplateReplaceResult result = Replace(
            MinimalPdfBuilder.CreateMinimalPdf("Hello World"), out byte[] produced,
            ("World", "There"));

        Assert.Empty(result.NotDrawnKeys);
        Assert.Contains("There", TextOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A longer key wins over a shorter one it contains</b>, whatever order the caller supplied —
    /// otherwise replacing both would depend on dictionary ordering, and the short key would eat the
    /// first characters of the long one and strand the rest.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLongerOfTwoOverlappingKeysWins(bool shortKeyFirst)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");

        (string, string)[] pairs = shortKeyFirst
            ? new[] { ("Wor", "XX"), ("World", "Planet") }
            : new[] { ("World", "Planet"), ("Wor", "XX") };

        TemplateReplaceResult result = Replace(pdf, out _, pairs);

        // Which key was used is the claim, and it is visible in the report without depending on a
        // font: "Wor" must not have matched inside "World".
        Assert.Contains("World", result.ReplacedKeys);
        Assert.DoesNotContain("Wor", result.ReplacedKeys);
        Assert.Contains("Wor", result.NotFoundKeys);
    }

    /// <summary>
    /// A key the document does not contain is reported as not found rather than passing silently —
    /// the caller usually mistyped it.
    /// </summary>
    [Fact]
    public void AKeyThatIsNotPresentIsReportedNotFound()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("Hello World"),
                                           writable: false);
        using var output = new MemoryStream();

        TemplateReplaceResult result = PdfTemplateEngine.ReplaceText(
            input, output,
            new Dictionary<string, string> { ["Absent"] = "x" },
            WithFont());

        Assert.Contains("Absent", result.NotFoundKeys);
    }

    /// <summary>
    /// <b>The token delimiters are not required here</b>, because the literal locator never reads
    /// them — demanding them would reject a well-formed call for a setting it does not use.
    /// </summary>
    [Fact]
    public void EmptyTokenDelimitersAreAcceptedOnTheLiteralPath()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("Hello World"),
                                           writable: false);
        using var output = new MemoryStream();

        var options = new TemplateReplaceOptions { TokenOpen = string.Empty, TokenClose = string.Empty };

        TemplateReplaceResult result = PdfTemplateEngine.ReplaceText(
            input, output, new Dictionary<string, string> { ["World"] = "There" }, options);

        Assert.Contains("World", result.ReplacedKeys);
    }

    /// <summary>
    /// The token path still demands them, so this change did not quietly relax that.
    /// </summary>
    [Fact]
    public void EmptyTokenDelimitersAreStillRefusedOnTheTokenPath()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("Hello World"),
                                           writable: false);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => PdfTemplateEngine.Replace(
            input, output,
            new Dictionary<string, string> { ["a"] = "b" },
            new TemplateReplaceOptions { TokenOpen = string.Empty }));
    }

    /// <summary>
    /// <b>A replacement that contains its own key is not reported as an erasure failure.</b>
    /// </summary>
    /// <remarks>
    /// <para>Erasure is verified by re-reading the produced page and looking for what should be
    /// gone — on the literal path, the key itself. But the text just <i>drawn</i> is on that page
    /// too, so "Acme" → "Acme Corp" puts the key back by design: the check finds it, and reports
    /// that the erasure missed an occurrence it did not miss. <b>Extraction cannot tell the
    /// surviving original from the new replacement</b>, and a warning that fires on correct output
    /// is worse than no warning, because a caller learns to ignore it.</para>
    ///
    /// <para>Gated on host fonts for the reason
    /// <see cref="TheReplacementIsDrawnAndExtractable"/> is: with nothing drawn, the produced page
    /// cannot contain the replacement and the false positive cannot arise.</para>
    /// </remarks>
    [SystemFontFact]
    public void AReplacementContainingItsOwnKeyIsNotReportedAsAMissedErasure()
    {
        TemplateReplaceResult result = Replace(
            MinimalPdfBuilder.CreateMinimalPdf("Hello World"), out byte[] produced,
            ("World", "World Wide"));

        Assert.Contains("World Wide", TextOf(produced), StringComparison.Ordinal);
        Assert.Empty(result.UnverifiedKeys);
        Assert.DoesNotContain(result.Warnings,
                              w => w.IndexOf("still", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// <b>An empty key is ignored rather than matching at every position.</b>
    /// </summary>
    /// <remarks>
    /// An empty search string is a value a caller reaches by accident — a blank row in a
    /// configuration file, a trimmed-away field — not one they mean. <c>IndexOf(string.Empty, n)</c>
    /// answers <c>n</c>, so an unguarded scan would match before every glyph on the page and then
    /// index the glyph <i>before</i> the match. The key is skipped and reported not found, and the
    /// keys beside it are still replaced.
    /// </remarks>
    [Fact]
    public void AnEmptyKeyIsIgnoredRatherThanMatchingEverywhere()
    {
        TemplateReplaceResult result = Replace(
            MinimalPdfBuilder.CreateMinimalPdf("Hello World"), out byte[] produced,
            (string.Empty, "x"), ("World", "There"));

        Assert.Contains(string.Empty, result.NotFoundKeys);
        Assert.Contains("World", result.ReplacedKeys);
        Assert.DoesNotContain("x", TextOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A key that overlaps itself is matched once, not twice.</b>
    /// </summary>
    /// <remarks>
    /// The scan resumes past the end of each hit. Resuming one character past its <i>start</i>
    /// instead matches <c>aba</c> again at index 2 of <c>ababa</c> — a second match over glyphs the
    /// first one already owns, which erases the tail the caller never asked about. Only a key that
    /// overlaps itself can tell the two advances apart, which is why this fixture is contrived:
    /// every ordinary key produces identical output either way.
    /// </remarks>
    [Fact]
    public void AKeyThatOverlapsItselfIsMatchedOnceNotTwice()
    {
        TemplateReplaceResult result = Replace(
            MinimalPdfBuilder.CreateMinimalPdf("ababa"), out byte[] produced, ("aba", "X"));

        Assert.Contains("aba", result.ReplacedKeys);
        Assert.Contains("ba", TextOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>An occurrence that could not be erased is reported against the key that was.</b>
    /// </summary>
    /// <remarks>
    /// <para>A Form XObject drawn twice cannot be edited — rewriting it would change the other
    /// place it appears — so a key inside one survives while the same key in the page's own content
    /// is replaced. The document then still shows the text the caller asked to be rid of, and the
    /// engine says so: the key is reported <see cref="TemplateReplaceResult.NotEditableKeys"/> at
    /// the match, and <see cref="TemplateReplaceResult.UnverifiedKeys"/> after the produced file has
    /// been re-read.</para>
    ///
    /// <para><b>This is the case that makes the verification falsifiable on the literal path.</b>
    /// It searches for the key itself here, and for <c>{{key}}</c> on the token path; looking for a
    /// delimited token in a document that never held one is a check that cannot fail, which reads as
    /// evidence while proving nothing.</para>
    /// </remarks>
    [Fact]
    public void AnOccurrenceInsideASharedFormIsReportedAsStillExtractable()
    {
        TemplateReplaceResult result = Replace(
            PdfWithTheKeyAlsoInAFormDrawnTwice(), out _, ("World", "Planet"));

        Assert.Contains("World", result.ReplacedKeys);
        Assert.Contains("World", result.NotEditableKeys);
        Assert.Contains("World", result.UnverifiedKeys);
    }

    /// <summary>
    /// A page drawing "World here" in its own content and a Form XObject containing "World"
    /// <b>twice</b>, which is what makes the form non-editable.
    /// </summary>
    private static byte[] PdfWithTheKeyAlsoInAFormDrawnTwice()
    {
        string content = "BT /F1 12 Tf 72 700 Td (World here) Tj ET\n" +
                         "q 1 0 0 1 0 -20 cm /Fm1 Do Q\n" +
                         "q 1 0 0 1 0 -40 cm /Fm1 Do Q\n";
        string form = "BT /F1 12 Tf 72 700 Td (World) Tj ET\n";

        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm1 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {Latin1(content).Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 5 0 R >> >> /Length {Latin1(form).Length} >>\n" +
                $"stream\n{form}\nendstream\nendobj\n",
        });
    }

    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);

    /// <summary>Lays objects out in order and writes a cross-reference table over their offsets.</summary>
    private static byte[] Assemble(IReadOnlyList<string> bodies)
    {
        using var ms = new MemoryStream();
        byte[] head = Latin1("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);

        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            byte[] b = Latin1(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        byte[] tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// A page of five hundred occurrences of one key, then fifty keys once each at its end, among
    /// fifty keys the page does not hold: every key was searched to the end of the page after every
    /// match — 500 × 101 searches, matches × keys × text. A key is searched again now only when the
    /// cursor passes its last hit, and one found nowhere is never searched again; every occurrence is
    /// still replaced.
    /// </summary>
    [Fact]
    public void AdversarialPageOfOneKeyAmongAbsentKeys_IsSearchedOnceAKeyAHit()
    {
        var late = Enumerable.Range(0, 50).Select(i => "w" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        string run = string.Join(" ", Enumerable.Repeat("q", 500)) + " " + string.Join(" ", late);
        string content = $"BT /F1 6 Tf 20 700 Td ({run}) Tj ET\n";
        byte[] pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2000 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {Latin1(content).Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });
        var pairs = new List<(string, string)> { ("q", "z") };
        foreach (string w in late) pairs.Add((w, "v"));
        for (int i = 0; i < 50; i++) pairs.Add(("absent" + i, "-"));

        PdfTemplateEngine.LiteralSearches = 0;
        Replace(pdf, out byte[] produced, pairs.ToArray());

        string text = TextOf(produced);
        Assert.DoesNotContain("q", text, StringComparison.Ordinal);
        Assert.DoesNotContain("w4", text, StringComparison.Ordinal);
        Assert.Equal(500, text.Count(c => c == 'z'));
        Assert.Equal(50, text.Count(c => c == 'v'));
        Assert.InRange(PdfTemplateEngine.LiteralSearches, 1, 3 * (500 + 101));   // was 500 × 101
    }

    /// <summary>A null replacement map is a programming error, not an empty edit.</summary>
    [Fact]
    public void ANullMapIsRefused()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(), writable: false);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentNullException>(
            () => PdfTemplateEngine.ReplaceText(input, output, null, new TemplateReplaceOptions()));
    }
}
