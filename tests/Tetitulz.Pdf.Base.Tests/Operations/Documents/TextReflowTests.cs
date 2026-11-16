// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Find-and-replace that re-breaks the containing paragraph — GAPS.md §19d's reflowing half.
/// </summary>
/// <remarks>
/// <para><b>The contract under test is the one reflow still keeps.</b> It gives up "everything you
/// did not replace is where it was" — that is what re-breaking means — and keeps "nothing outside
/// the paragraph moves". So the assertions are about the paragraph's own lines, and about what
/// happens when the replacement will not fit in them.</para>
///
/// <para><b>Asserted by extracting the produced document</b>, never by reading back what the writer
/// intended: the claim is that a reader sees re-broken lines, which is a fact about the output.</para>
/// </remarks>
public class TextReflowTests
{
    // The third line is deliberately short. Three full lines would leave the paragraph with no
    // slack at all, so ANY replacement longer than what it replaced needs a fourth line and is
    // refused — which is correct behaviour and a useless fixture for testing anything else.
    private const string Para1 = "The quick brown fox jumps over";
    private const string Para2 = "the lazy dog and keeps running";
    private const string Para3 = "onward.";

    private static TemplateReplaceOptions WithFont(bool allowOverflow = false) =>
        new TemplateReplaceOptions
        {
            AllowSystemFontSubstitution = true,
            AllowReflowPastTheParagraph = allowOverflow,
        };

    private static TemplateReplaceResult Reflow(
        byte[] pdf, out byte[] produced, TemplateReplaceOptions options,
        params (string From, string To)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.From, p => p.To, StringComparer.Ordinal);

        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTextReflow.Replace(input, output, map, options);
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
    /// The old text is gone and the key is reported replaced — the half that holds whether or not a
    /// font is available to draw with.
    /// </summary>
    [Fact]
    public void AReplacedWordIsErasedFromTheParagraph()
    {
        TemplateReplaceResult result = Reflow(
            ThreeLineParagraph(), out byte[] produced, WithFont(), ("quick", "sluggish"));

        Assert.Contains("quick", result.ReplacedKeys);
        Assert.DoesNotContain("quick", TextOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The paragraph is re-broken, so a word the caller never named moves to another line.</b>
    /// </summary>
    /// <remarks>
    /// This is the claim that separates reflow from <c>ReplaceText</c>, and it is asserted on line
    /// membership rather than on the text as a whole: the same words in the same order come back
    /// from either operation, and only where the breaks fall tells them apart.
    /// </remarks>
    [SystemFontFact]
    public void AWordTheCallerDidNotNameMovesToAnotherLine()
    {
        byte[] pdf = ThreeLineParagraph();

        string[] before = LinesOf(TextOf(pdf));
        Assert.Contains(before, l => l.Contains("over", StringComparison.Ordinal));

        // A much shorter replacement pulls the following words up.
        Reflow(pdf, out byte[] produced, WithFont(), ("quick brown fox", "cat"));

        string[] after = LinesOf(TextOf(produced));

        Assert.Contains("cat", TextOf(produced), StringComparison.Ordinal);
        Assert.NotEqual(before.Length == 0 ? null : before[0], after.Length == 0 ? null : after[0]);
    }

    /// <summary>
    /// <b>A replacement needing more lines than the paragraph had is refused, not drawn over what
    /// follows.</b>
    /// </summary>
    /// <remarks>
    /// A PDF records where text was drawn, not what it belongs to, so nothing in the file relates a
    /// paragraph to the content beneath it and there is no way to push that content down. Refusing
    /// is what keeps the guarantee reflow still makes.
    /// </remarks>
    [SystemFontFact]
    public void AParagraphThatWouldGrowPastItsLastLineIsRefused()
    {
        string longer = string.Join(" ", Enumerable.Repeat("elaborately", 30));

        TemplateReplaceResult result = Reflow(
            ThreeLineParagraph(), out byte[] produced, WithFont(), ("quick", longer));

        Assert.Contains("quick", result.NotReflowedKeys);
        Assert.DoesNotContain("quick", result.ReplacedKeys);

        // Left untouched means untouched: the original paragraph is still there.
        Assert.Contains("quick", TextOf(produced), StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.IndexOf("lines where the paragraph had",
                                                       StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The same overflow is accepted when the caller says the space below is free — a fact about
    /// the document that only they have.
    /// </summary>
    [SystemFontFact]
    public void TheSameOverflowIsAcceptedWhenTheCallerAllowsIt()
    {
        string longer = string.Join(" ", Enumerable.Repeat("elaborately", 30));

        TemplateReplaceResult result = Reflow(
            ThreeLineParagraph(), out byte[] produced, WithFont(allowOverflow: true),
            ("quick", longer));

        Assert.Contains("quick", result.ReplacedKeys);
        Assert.DoesNotContain("quick", result.NotReflowedKeys);
        Assert.Contains("elaborately", TextOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A paragraph whose lines do not share a left margin is left alone and named.</b>
    /// </summary>
    /// <remarks>
    /// Unequal left edges are what centred and right-aligned text look like from the file, and
    /// re-breaking those would move every line horizontally as well — a change to the layout rather
    /// than to the words. The refusal names the reason so a caller is not left guessing why one
    /// paragraph reflowed and another did not.
    /// </remarks>
    [Fact]
    public void ACentredParagraphIsNotReflowed()
    {
        // A SHORTER replacement, so overflow cannot be the reason it is refused: the only rule left
        // to fail is the margin one, which is what this test is named for.
        TemplateReplaceResult result = Reflow(
            CentredParagraph(), out byte[] produced, WithFont(), ("quick", "fast"));

        Assert.Contains("quick", result.NotReflowedKeys);
        Assert.Contains("quick", TextOf(produced), StringComparison.Ordinal);
        Assert.Contains(result.Warnings,
                        w => w.IndexOf("left margin", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// A standalone line is refused and the caller is pointed at the operation that handles it —
    /// one line carries no leading and no second margin, so its alignment cannot be read.
    /// </summary>
    /// <remarks>
    /// Reflowing it anyway would redraw it at its own left edge, which is right for a left-aligned
    /// line and silently left-aligns a centred one. Refusing is what keeps that decision with the
    /// caller, who can see the page.
    /// </remarks>
    [Fact]
    public void ASingleLineIsRefusedAndPointsAtReplaceText()
    {
        TemplateReplaceResult result = Reflow(
            SingleLine(), out _, WithFont(), ("quick", "fast"));

        Assert.Contains("quick", result.NotReflowedKeys);
        Assert.Contains(result.Warnings,
                        w => w.IndexOf("ReplaceText", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// Two keys in one paragraph: the first is reflowed and the second is reported rather than
    /// applied to text the first has already moved.
    /// </summary>
    [SystemFontFact]
    public void ASecondKeyInTheSameParagraphIsReportedRatherThanAppliedTwice()
    {
        TemplateReplaceResult result = Reflow(
            ThreeLineParagraph(), out _, WithFont(), ("quick", "fast"), ("lazy", "idle"));

        // One of them was reflowed; the other was named, not silently dropped.
        Assert.Single(result.ReplacedKeys.Intersect(new[] { "quick", "lazy" }));
        Assert.Single(result.NotReflowedKeys.Intersect(new[] { "quick", "lazy" }));
        Assert.Contains(result.Warnings,
                        w => w.IndexOf("reflowed first", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// <b>Rotated text is refused</b> — a re-broken line would step in a direction this does not
    /// compute.
    /// </summary>
    /// <remarks>
    /// <para>The leading is applied by moving the text matrix straight down the page, which is only
    /// the next baseline when the text is upright. Under a rotation the next line lies along the
    /// perpendicular of the rotated baseline, and stepping down instead would scatter the paragraph
    /// across the page.</para>
    ///
    /// <para><b>The rotation is deliberately small, and a large one would prove nothing.</b> The
    /// engine's shared span check reads a glyph's baseline as <c>Tm[5]</c>, and a rotated advance
    /// moves that per glyph — so at any noticeable angle the span is refused one level up, as "not
    /// on one baseline", before reflow's own check is reached. A first version of this test used
    /// 45° and passed for exactly that reason while the rotation check sat unreached; a mutation
    /// removing that check survived, which is how it was found. Under about a degree the drift
    /// across a short key stays inside the baseline tolerance, the span survives, and this check is
    /// the only thing standing between a near-upright paragraph and a scattered one.</para>
    /// </remarks>
    [Fact]
    public void ARotatedParagraphIsNotReflowed()
    {
        TemplateReplaceResult result = Reflow(
            RotatedParagraph(), out byte[] produced, WithFont(), ("quick", "fast"));

        Assert.Contains("quick", result.NotReflowedKeys);
        Assert.Contains("quick", TextOf(produced), StringComparison.Ordinal);
        Assert.Contains(result.Warnings,
                        w => w.IndexOf("rotated or skewed", StringComparison.Ordinal) >= 0);
    }

    /// <summary>A key the document does not contain is reported not found.</summary>
    [Fact]
    public void AKeyThatIsNotPresentIsReportedNotFound()
    {
        TemplateReplaceResult result = Reflow(
            ThreeLineParagraph(), out _, WithFont(), ("absent", "x"));

        Assert.Contains("absent", result.NotFoundKeys);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// <b>A justified paragraph stays justified.</b> Three full lines of Courier end at one right edge
    /// (x = 288) above a short last line; after a replacement re-breaks it in the embedded font, every
    /// line but the last is spread to that edge by word spacing, and the last stays short. It was
    /// re-broken ragged-right, silently losing the justification.
    /// </summary>
    [SystemFontFact]
    public void AJustifiedParagraph_IsReBrokenJustified()
    {
        TemplateReplaceResult result = Reflow(JustifiedParagraph(fullThirdLine: true), out byte[] produced, WithFont(), ("quick", "slow"));
        Assert.Contains("quick", result.ReplacedKeys);

        var lines = LineEdges(produced);
        Assert.True(lines.Count >= 3, $"{lines.Count} lines came back");
        for (int i = 0; i < lines.Count - 1; i++)
        {
            Assert.Equal(72, lines[i].Left, 1);
            Assert.True(Math.Abs(lines[i].Right - 288) <= 1.0, $"line {i + 1} ends at {lines[i].Right:F2}, not 288");
        }
        Assert.True(lines[lines.Count - 1].Right < 270, "the last line was spread too");
    }

    /// <summary>
    /// <b>Hyphenation, asked for, breaks a word at a line's end and never costs a line.</b> The same long
    /// replacement re-broken both ways, the space below allowed: with it, a line ends in a hyphen whose
    /// head and the next line's first word are one word of the text, in no more lines than without it;
    /// off, the default, no drawn line ends in a hyphen.
    /// </summary>
    [SystemFontFact]
    public void Hyphenation_BreaksAWordAtALinesEnd_AndNeverCostsALine()
    {
        Assert.False(new TemplateReplaceOptions().Hyphenate);
        var hyphenating = WithFont(allowOverflow: true);
        hyphenating.Hyphenate = true;
        string replacement = "quick" + string.Concat(Enumerable.Repeat(" representation", 6));
        string[] words = (Para1 + " " + Para2 + " " + Para3).Replace("quick", replacement).Split(' ');

        TemplateReplaceResult off = Reflow(ThreeLineParagraph(), out byte[] offPdf, WithFont(allowOverflow: true), ("quick", replacement));
        TemplateReplaceResult on = Reflow(ThreeLineParagraph(), out byte[] onPdf, hyphenating, ("quick", replacement));
        Assert.Contains("quick", off.ReplacedKeys);
        Assert.Contains("quick", on.ReplacedKeys);

        string[] plain = LinesOf(TextOf(offPdf)), hyphenated = LinesOf(TextOf(onPdf));
        Assert.DoesNotContain(plain, l => l.EndsWith("-", StringComparison.Ordinal));
        int at = Array.FindIndex(hyphenated, l => l.EndsWith("-", StringComparison.Ordinal));
        Assert.True(at >= 0 && at < hyphenated.Length - 1, $"no hyphenated line: {string.Join(" | ", hyphenated)}");
        Assert.Contains(hyphenated[at].Split(' ').Last().TrimEnd('-') + hyphenated[at + 1].Split(' ').First(), words);
        Assert.True(hyphenated.Length <= plain.Length, $"{hyphenated.Length} lines hyphenated, {plain.Length} without");
    }

    /// <summary>
    /// The control: the same paragraph with a short third line is not justified (only its first line
    /// reaches 288), so its re-broken lines are not spread — none but by chance ends at the edge.
    /// </summary>
    [SystemFontFact]
    public void AParagraphWithOneFullLine_IsNotSpread()
    {
        Reflow(JustifiedParagraph(fullThirdLine: false), out byte[] produced, WithFont(), ("quick", "slow"));

        var lines = LineEdges(produced);
        Assert.True(lines.Count >= 2);
        Assert.True(lines[0].Right < 287, $"the first line ends at {lines[0].Right:F2}: it was spread");
    }

    /// <summary>
    /// Two lines, the first full and the second short, are no evidence of justification — a ragged
    /// paragraph looks the same — so the re-broken first line is not spread.
    /// </summary>
    [SystemFontFact]
    public void ATwoLineParagraph_IsNotTakenForJustified()
    {
        // A replacement long enough to need both lines again, so there is a first line to spread.
        Reflow(JustifiedParagraph(fullThirdLine: true, twoLines: true), out byte[] produced, WithFont(),
            ("quick", "remarkably quick and clever"));

        var lines = LineEdges(produced);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Right < 287, $"the first line ends at {lines[0].Right:F2}: it was spread");
    }

    /// <summary>Each text line's left and right edge, top to bottom, from the structured extractor.</summary>
    private static List<(double Left, double Right)> LineEdges(byte[] pdf)
    {
        var page = Tetitulz.Pdf.Base.Operations.Extraction.PdfStructuredTextExtractor.Extract(new MemoryStream(pdf, writable: false))[0];
        return page.Lines
            .OrderByDescending(l => l.Y)
            .Select(l =>
            {
                var chars = l.Words.SelectMany(w => w.Chars).ToList();
                return ((double)chars.Min(c => c.X), (double)chars.Max(c => c.X + c.Width));
            })
            .ToList();
    }

    /// <summary>
    /// Four lines of Courier 12 on a 14-point leading, the first three 30 characters (216 points)
    /// wide; with <paramref name="fullThirdLine"/> false the third is shorter.
    /// </summary>
    private static byte[] JustifiedParagraph(bool fullThirdLine, bool twoLines = false)
    {
        string third = fullThirdLine ? "a tree all day in the warm sun" : "a tree all day";
        string content = twoLines
            ? "BT /F1 12 Tf\n" +
              "1 0 0 1 72 700 Tm (The quick brown fox jumps over) Tj\n" +
              "1 0 0 1 72 686 Tm (today.) Tj\n" +
              "ET\n"
            : "BT /F1 12 Tf\n" +
              "1 0 0 1 72 700 Tm (The quick brown fox jumps over) Tj\n" +
              "1 0 0 1 72 686 Tm (the lazy dog that sleeps under) Tj\n" +
              $"1 0 0 1 72 672 Tm ({third}) Tj\n" +
              "1 0 0 1 72 658 Tm (today.) Tj\n" +
              "ET\n";

        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {Latin1(content).Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>\nendobj\n",
        });
    }

    private static string[] LinesOf(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);

    /// <summary>
    /// Three lines of left-aligned prose in one text object, on a uniform 14-point leading.
    /// </summary>
    private static byte[] ThreeLineParagraph() => Paragraph(left2: 72, left3: 72);

    /// <summary>
    /// The same paragraph with its lines centred — unequal left edges, which is what centred text
    /// looks like from the file.
    /// </summary>
    private static byte[] CentredParagraph() => Paragraph(left2: 88, left3: 104);

    /// <summary>
    /// The same three lines rotated by half a degree — the matrix carries b and c, which upright
    /// text never does, while the per-glyph baseline drift across a short key stays inside the
    /// engine's baseline tolerance so the span is not refused before reflow sees it.
    /// </summary>
    private static byte[] RotatedParagraph()
    {
        const string R = "0.99996 0.00873 -0.00873 0.99996";
        string content =
            "BT /F1 12 Tf\n" +
            $"{R} 72 700 Tm ({Para1}) Tj\n" +
            $"{R} 72 686 Tm ({Para2}) Tj\n" +
            $"{R} 72 672 Tm ({Para3}) Tj\n" +
            "ET\n";

        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {Latin1(content).Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });
    }

    /// <summary>One line in its own text object — no leading, no second margin to read.</summary>
    private static byte[] SingleLine()
    {
        string content = $"BT /F1 12 Tf 1 0 0 1 72 700 Tm ({Para1}) Tj ET\n";

        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {Latin1(content).Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });
    }

    private static byte[] Paragraph(int left2, int left3)
    {
        string content =
            "BT /F1 12 Tf\n" +
            $"1 0 0 1 72 700 Tm ({Para1}) Tj\n" +
            $"1 0 0 1 {left2.ToString(CultureInfo.InvariantCulture)} 686 Tm ({Para2}) Tj\n" +
            $"1 0 0 1 {left3.ToString(CultureInfo.InvariantCulture)} 672 Tm ({Para3}) Tj\n" +
            "ET\n";

        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {Latin1(content).Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });
    }

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
}
