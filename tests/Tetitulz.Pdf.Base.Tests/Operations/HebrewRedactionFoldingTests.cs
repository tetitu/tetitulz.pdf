// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="TextRedactionOptions.HebrewFolding"/> — redacting a Hebrew word whose spelling on the
/// page differs from the one the caller typed.
/// </summary>
/// <remarks>
/// <para><b>Off by default here, on by default in a find box, and the difference is the point.</b>
/// A search miss costs a keystroke; a spurious redaction destroys content irreversibly. So the two
/// defaults are opposite on purpose, and <c>PdfTextSearch</c> and
/// <c>PdfViewerControl.FindHebrewFolding</c> each say so where they live.</para>
///
/// <para><b>Every claim is asserted with folding OFF beside it.</b> Pointed Hebrew is unusual enough
/// that a test asserting only "the word was removed" would pass against a redactor that matched on
/// something else entirely — or against one that redacted the whole line.</para>
///
/// <para><b>The fixture writes each line in VISUAL order, and that is not cosmetic.</b> A PDF
/// stores glyphs in the order they are painted, and <c>PdfStructuredTextExtractor</c> — which is
/// what the redactor searches — returns each RTL run in reading order, so a fixture written in
/// logical order comes back <i>reversed</i> and every pattern misses. Measured, not assumed: the
/// first version of this file wrote logical order and five tests failed with empty match lists,
/// which is indistinguishable from a broken fold. It is the RTL trap this repository has recorded
/// twice, in its third shape — and note that it bites here even though
/// <c>PdfStructuredTextExtractorOptions.ApplyBidi</c> defaults to <c>false</c>: that flag chooses
/// <i>which</i> reordering runs, not whether one does.</para>
///
/// <para>Nothing below depends on which end of the line the text starts at. The claims are about
/// which characters were matched, and they are asserted per code point rather than as a
/// substring.</para>
/// </remarks>
public sealed class HebrewRedactionFoldingTests
{
    // The page's own alphabet. One byte per glyph, mapped to Hebrew through /ToUnicode — which is
    // how a real Hebrew PDF stores text, and what makes per-glyph geometry available for a box.
    private const char Alef = 'א';       // א
    private const char Bet = 'ב';        // ב
    private const char Qamats = 'ָ';     // ָ  — a niqqud mark, its own glyph on the page
    private const char FinalKaf = 'ך';   // ך
    private const char Kaf = 'כ';        // כ
    private const char Gimel = 'ג';      // ג

    private static readonly (byte code, char text)[] Alphabet =
    {
        ((byte)'a', Alef), ((byte)'b', Bet), ((byte)'c', Qamats),
        ((byte)'d', FinalKaf), ((byte)'e', Kaf), ((byte)'f', Gimel),
    };

    /// <summary>Encodes a Unicode string into the single-byte codes the fixture font uses.</summary>
    private static string Codes(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            (byte code, char _) = Alphabet.FirstOrDefault(a => a.text == c);
            if (code == 0) throw new InvalidOperationException(
                $"U+{(int)c:X4} is not in the fixture alphabet; add it to Alphabet.");
            sb.Append((char)code);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A one-page document with each string on its own line, each painted in the visual order a
    /// real Hebrew producer would paint it: left to right on the page, which for an RTL run is the
    /// reverse of the logical string.
    /// </summary>
    /// <remarks>
    /// The reversal is what makes the extracted line equal to the <paramref name="lines"/> entry
    /// that produced it. Painting logical order instead yields a page whose extracted text is
    /// backwards, and every assertion below then fails for a reason that has nothing to do with
    /// folding — see the class remarks.
    /// </remarks>
    private static byte[] Document(params string[] lines)
    {
        var content = new StringBuilder("BT /F1 12 Tf\n");
        double y = 700;
        foreach (string line in lines)
        {
            content.AppendFormat("1 0 0 1 72 {0} Tm ({1}) Tj\n", y, Codes(Visual(line)));
            y -= 20;
        }
        content.Append("ET\n");
        return Assemble(content.ToString());
    }

    /// <summary>The painting order of an RTL run: the logical string, reversed.</summary>
    private static string Visual(string logical)
    {
        char[] chars = logical.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static string ToUnicodeCMap()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CMapName /Fixture def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<00> <FF>\nendcodespacerange\n");
        sb.Append(Alphabet.Length).Append(" beginbfchar\n");
        foreach ((byte code, char text) in Alphabet)
            sb.AppendFormat("<{0:X2}> <{1:X4}>\n", code, (int)text);
        sb.Append("endbfchar\nendcmap\n");
        sb.Append("CMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    private static byte[] Assemble(string stream)
    {
        string cmap = ToUnicodeCMap();
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding " +
            "/ToUnicode 6 0 R >>",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static TextRedactionOptions With(HebrewFolding folding) =>
        new() { HebrewFolding = folding };

    private static System.Collections.Generic.IReadOnlyList<TextRedactionMatch> Find(
        byte[] pdf, string pattern, TextRedactionOptions options)
    {
        using var input = new MemoryStream(pdf, writable: false);
        return PdfTextRedactor.FindMatches(input, pattern, options);
    }

    private static string Redact(byte[] pdf, string pattern, TextRedactionOptions options)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PdfTextRedactor.Redact(input, output, pattern, options);

        using var read = new MemoryStream(output.ToArray(), writable: false);
        return PdfOperations.ExtractText(read) ?? string.Empty;
    }

    // ================================================================ the fold

    /// <summary>
    /// An unpointed pattern finds the pointed word on the page — and does not without the option,
    /// which is what makes the assertion about the folding rather than about a loose matcher.
    /// </summary>
    [Fact]
    public void UnpointedPatternMatchesPointedTextOnlyWhenFoldingIsOn()
    {
        byte[] pdf = Document($"{Alef}{Qamats}{Bet}", $"{Gimel}{Gimel}");
        string unpointed = $"{Alef}{Bet}";

        Assert.Empty(Find(pdf, unpointed, With(HebrewFolding.None)));
        Assert.Single(Find(pdf, unpointed, With(HebrewFolding.Niqqud)));
    }

    /// <summary>
    /// The match reports the text that is <b>on the page</b>, marks included — not the folded
    /// spelling the matcher worked in.
    /// </summary>
    /// <remarks>
    /// <c>FindMatches</c> is the review step a caller reads before destroying anything, so reporting
    /// the query back at them would hide precisely the difference they turned folding on to bridge.
    /// </remarks>
    [Fact]
    public void ReportedMatchTextIsWhatIsOnThePageNotTheFoldedForm()
    {
        byte[] pdf = Document($"{Alef}{Qamats}{Bet}");

        TextRedactionMatch match = Find(pdf, $"{Alef}{Bet}", With(HebrewFolding.Niqqud)).Single();

        Assert.Equal($"{Alef}{Qamats}{Bet}", match.Text);
        Assert.Contains(Qamats, match.Text);
    }

    /// <summary>
    /// The area covers every glyph of the match, the stripped marks included — so the last letter of
    /// a pointed word is removed rather than left standing.
    /// </summary>
    /// <remarks>
    /// <b>This is the assertion that catches the obvious wrong end.</b> Deriving the match's end as
    /// <c>start + foldedLength</c> is correct for unpointed text and short by one glyph per mark
    /// inside the match — here it would cover א and its qamats and leave ב on the page, which is a
    /// redaction that reports success and discloses the word.
    /// </remarks>
    [Fact]
    public void RedactionRemovesTheWholePointedWordIncludingItsMarks()
    {
        byte[] pdf = Document($"{Alef}{Qamats}{Bet}", $"{Gimel}{Gimel}");

        string after = Redact(pdf, $"{Alef}{Bet}", With(HebrewFolding.Niqqud));

        Assert.DoesNotContain(Alef, after);
        Assert.DoesNotContain(Qamats, after);
        Assert.DoesNotContain(Bet, after);

        // The neighbouring line is untouched, or "the word is gone" would also be true of a
        // redactor that blanked the page.
        Assert.Contains(Gimel, after);
    }

    /// <summary>
    /// Folding final forms lets a pattern written with the medial letter match the word-final one,
    /// and only with the option on.
    /// </summary>
    [Fact]
    public void MedialPatternMatchesAFinalFormOnlyWhenFoldingIsOn()
    {
        byte[] pdf = Document($"{Alef}{FinalKaf}");
        string medial = $"{Alef}{Kaf}";

        Assert.Empty(Find(pdf, medial, With(HebrewFolding.None)));
        Assert.Single(Find(pdf, medial, With(HebrewFolding.FinalForms)));
    }

    /// <summary>
    /// Two spellings of one word are one pattern once folded, and are reported once rather than
    /// twice.
    /// </summary>
    /// <remarks>
    /// De-duplicating on the raw pattern instead would report every hit as many times as the caller
    /// supplied spellings — and a caller who lists the variants they are unsure about is exactly the
    /// caller who turns folding on.
    /// </remarks>
    [Fact]
    public void SpellingsThatFoldTogetherAreOnePattern()
    {
        byte[] pdf = Document($"{Alef}{Qamats}{Bet}");
        var patterns = new[] { $"{Alef}{Bet}", $"{Alef}{Qamats}{Bet}" };

        using var input = new MemoryStream(pdf, writable: false);
        Assert.Single(PdfTextRedactor.FindMatches(input, patterns, With(HebrewFolding.Niqqud)));
    }

    // ================================================================ refusals

    /// <summary>
    /// Folding and <see cref="TextRedactionOptions.UseRegex"/> are refused together, because a
    /// quantifier would count folded characters and cover a different region than it names.
    /// </summary>
    [Fact]
    public void FoldingWithARegularExpressionIsRefused()
    {
        byte[] pdf = Document($"{Alef}{Qamats}{Bet}");
        var options = new TextRedactionOptions
        {
            HebrewFolding = HebrewFolding.Niqqud,
            UseRegex = true,
        };

        var ex = Assert.Throws<PdfRedactionException>(() => Find(pdf, ".", options));
        Assert.Contains("UseRegex", ex.Message);

        // The same expression without folding is accepted, so the refusal is about the combination
        // rather than about regular expressions being unwelcome.
        using var input = new MemoryStream(pdf, writable: false);
        Assert.NotEmpty(PdfTextRedactor.FindMatches(
            input, ".", new TextRedactionOptions { UseRegex = true }));
    }

    /// <summary>
    /// A pattern the options themselves erase is refused rather than quietly dropped.
    /// </summary>
    /// <remarks>
    /// The caller supplied something non-empty; folding it away is a surprise the caller has to see,
    /// and "no matches" for an operation as destructive as this reads as "the word is not in the
    /// document" — the opposite of the truth.
    /// </remarks>
    [Fact]
    public void APatternThatFoldsAwayEntirelyIsRefused()
    {
        byte[] pdf = Document($"{Alef}{Qamats}{Bet}");

        var ex = Assert.Throws<PdfRedactionException>(
            () => Find(pdf, Qamats.ToString(), With(HebrewFolding.Niqqud)));
        Assert.Contains("folds away", ex.Message);

        // Without folding the very same pattern is an ordinary literal that matches.
        Assert.Single(Find(pdf, Qamats.ToString(), With(HebrewFolding.None)));
    }

    /// <summary>
    /// The default is <see cref="HebrewFolding.None"/>: a caller who passes no options gets exact
    /// matching, and a document that would only match under folding is left alone.
    /// </summary>
    /// <remarks>
    /// Asserted on behaviour rather than on the property, because the property alone would still
    /// read <c>None</c> if some entry point substituted its own options object on the way past.
    /// </remarks>
    [Fact]
    public void DefaultOptionsDoNotFold()
    {
        byte[] pdf = Document($"{Alef}{Qamats}{Bet}");

        Assert.Equal(HebrewFolding.None, new TextRedactionOptions().HebrewFolding);

        using var input = new MemoryStream(pdf, writable: false);
        Assert.Empty(PdfTextRedactor.FindMatches(input, $"{Alef}{Bet}"));
    }
}
