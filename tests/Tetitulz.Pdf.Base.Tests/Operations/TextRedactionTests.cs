// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// "Redact every occurrence of this pattern" — the join of three components that all shipped and
/// never met: <c>PdfTextSearch</c> returns a page and a string with <b>no rectangle</b>,
/// <c>PdfRedactionArea</c> needs one, and <c>PdfStructuredTextExtractor</c> has had per-character
/// geometry all along.
///
/// <para><b>The assertions are that the text is GONE</b>, read back through extraction — not that a
/// cover box was drawn. A cover box over intact text is the failure mode this library has a whole
/// class named after (<c>CoverRegions</c>), and it looks identical on screen.</para>
/// </summary>
public sealed class TextRedactionTests
{
    private const string Secret = "Confidential";
    private const string Kept = "Ordinary";

    /// <summary>
    /// A one-page document with each word on its own line, drawn with a standard-14 font so the
    /// extractor recovers real per-glyph geometry.
    /// </summary>
    private static byte[] Document(params string[] lines)
    {
        var content = new StringBuilder("BT /F1 12 Tf\n");
        double y = 700;
        foreach (string line in lines)
        {
            content.AppendFormat("1 0 0 1 72 {0} Tm ({1}) Tj\n", y, line);
            y -= 20;
        }
        content.Append("ET\n");
        return Assemble(content.ToString());
    }

    /// <summary>Wraps a content stream in a one-page document with a standard-14 font.</summary>
    private static byte[] Assemble(string stream)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
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

    private static byte[] Redact(byte[] pdf, string pattern, TextRedactionOptions options = null)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PdfTextRedactor.Redact(input, output, pattern, options);
        return output.ToArray();
    }

    private static string Extract(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractText(ms) ?? string.Empty;
    }

    // ================================================================ the join

    /// <summary>
    /// The matched text is removed from the document, and the rest is not.
    /// </summary>
    [Fact]
    public void Redact_RemovesTheMatchedTextAndKeepsTheRest()
    {
        byte[] pdf = Document(Secret, Kept);

        // The control: both words really are extractable to begin with.
        string before = Extract(pdf);
        Assert.Contains(Secret, before);
        Assert.Contains(Kept, before);

        string after = Extract(Redact(pdf, Secret));

        Assert.DoesNotContain(Secret, after);
        Assert.Contains(Kept, after);
    }

    /// <summary>
    /// Every occurrence goes, not just the first — which is the difference between "redact this
    /// pattern" and "redact one rectangle", and the whole reason the feature exists.
    /// </summary>
    [Fact]
    public void Redact_RemovesEveryOccurrence()
    {
        byte[] pdf = Document(Secret, Kept, Secret, Secret);

        using var input = new MemoryStream(pdf, writable: false);
        var matches = PdfTextRedactor.FindMatches(input, Secret);

        Assert.Equal(3, matches.Count);
        Assert.DoesNotContain(Secret, Extract(Redact(pdf, Secret)));
    }

    /// <summary>
    /// The search is case-insensitive by default, and case-sensitive on request — a redaction that
    /// silently missed "CONFIDENTIAL" would leak exactly what it was asked to remove.
    /// </summary>
    [Fact]
    public void Redact_IsCaseInsensitiveByDefaultAndCaseSensitiveOnRequest()
    {
        byte[] pdf = Document("CONFIDENTIAL", Kept);

        Assert.DoesNotContain("CONFIDENTIAL", Extract(Redact(pdf, Secret)));

        Assert.Contains("CONFIDENTIAL",
            Extract(Redact(pdf, Secret, new TextRedactionOptions { MatchCase = true })));
    }

    /// <summary>A regular expression matches, when the caller asks for one.</summary>
    [Fact]
    public void Redact_WithARegularExpression_RemovesWhatItMatches()
    {
        byte[] pdf = Document("Account 123456789", Kept);

        string after = Extract(Redact(pdf, @"\d{9}", new TextRedactionOptions { UseRegex = true }));

        Assert.DoesNotContain("123456789", after);
        Assert.Contains(Kept, after);
    }

    /// <summary>...and a literal containing regex metacharacters is treated as a literal.</summary>
    [Fact]
    public void FindMatches_LiteralWithMetacharacters_IsNotTreatedAsAPattern()
    {
        byte[] pdf = Document("a.b", "axb");

        using var input = new MemoryStream(pdf, writable: false);
        var matches = PdfTextRedactor.FindMatches(input, "a.b");

        // As a regex, "a.b" would match "axb" as well.
        Assert.Single(matches);
        Assert.Equal("a.b", matches[0].Text);
    }

    /// <summary>
    /// Two occurrences on <b>one line</b> both go.
    /// </summary>
    /// <remarks>
    /// <b>This test exists because a mutation survived.</b> Replacing the match loop with a single
    /// <c>break</c> — find the first occurrence on a line and stop — left every other test green,
    /// because the multi-occurrence fixture put each occurrence on its own line and each line's
    /// first match is still found. The loop only earns its keep when a line repeats the pattern,
    /// which is the ordinary case in a paragraph.
    /// </remarks>
    [Fact]
    public void Redact_TwoOccurrencesOnOneLine_RemovesBoth()
    {
        byte[] pdf = SpacedWords((Secret, 72), (Kept, 180), (Secret, 300));

        using var input = new MemoryStream(pdf, writable: false);
        var matches = PdfTextRedactor.FindMatches(input, Secret);
        Assert.Equal(2, matches.Count);

        string after = Extract(Redact(pdf, Secret));
        Assert.DoesNotContain(Secret, after);
        Assert.Contains(Kept, after);
    }

    /// <summary>
    /// A match that <b>starts inside a multi-character cluster</b> still finds its glyph.
    /// </summary>
    /// <remarks>
    /// <para><b>This test exists because a mutation survived, and the hazard was already written in
    /// a comment.</b> One <c>PdfTextChar</c> can carry several characters — an <c>ffi</c> ligature
    /// mapped through <c>/ToUnicode</c> is one glyph and three characters — so every position it
    /// occupies must map back to it. Mapping only the first position leaves the rest pointing at
    /// nothing, and a match starting at one of them finds no glyphs and produces <b>no area</b>:
    /// the occurrence is silently not redacted.</para>
    /// <para>Every other fixture here is standard-14 WinAnsi, one character per glyph, so the
    /// mapping was unfalsifiable. This one declares a <c>/ToUnicode</c> that maps a single code to
    /// three characters, which is what a real ligature looks like.</para>
    /// </remarks>
    [Fact]
    public void FindMatches_MatchStartingInsideACluster_StillProducesAnArea()
    {
        byte[] pdf = LigatureDocument();

        using var input = new MemoryStream(pdf, writable: false);

        // The control: the cluster really did arrive as three characters from one glyph.
        Assert.Contains("ffi", Extract(pdf));

        // "fi" starts at the cluster's SECOND character, which only exists in the map when every
        // position of the cluster points at the glyph.
        var match = Assert.Single(PdfTextRedactor.FindMatches(input, "fi"));

        Assert.NotNull(match.Area);
        Assert.InRange(match.Area.X, 68, 78);
        Assert.True(match.Area.Width > 0, "the area has no width, so it covers nothing");
    }

    /// <summary>
    /// A page drawing one glyph whose <c>/ToUnicode</c> maps it to the three characters
    /// <c>ffi</c> — a ligature, as far as extraction is concerned.
    /// </summary>
    private static byte[] LigatureDocument()
    {
        string toUnicode =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "/CMapName /Test def /CMapType 2 def\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "1 beginbfchar <41> <00660066006900> endbfchar\n" +
            "endcmap end end\n";

        string content = "BT /F1 12 Tf 1 0 0 1 72 700 Tm (A) Tj ET\n";

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding " +
            "/ToUnicode 6 0 R >>",
            $"<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream",
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

    // ================================================================ geometry

    /// <summary>
    /// The area covers the matched glyphs and nothing more — it is built from their boxes, not from
    /// the line's.
    /// </summary>
    /// <remarks>
    /// A whole-line area would still redact the secret and would also destroy everything beside it,
    /// which no assertion about the secret's absence can detect.
    /// </remarks>
    [Fact]
    public void FindMatches_AreaCoversTheMatchedGlyphsOnly()
    {
        byte[] pdf = Document(Secret + " " + Kept);

        using var input = new MemoryStream(pdf, writable: false);
        var match = Assert.Single(PdfTextRedactor.FindMatches(input, Secret));

        // The line starts at x=72; the match is its first word, so the area starts there and stops
        // well short of the line's full width.
        Assert.InRange(match.Area.X, 68, 74);
        Assert.InRange(match.Area.Width, 10, 90);
        Assert.Equal(0, match.Area.PageIndex);

        // ...and the word beside it survives the redaction that follows.
        Assert.Contains(Kept, Extract(Redact(pdf, Secret)));
    }

    /// <summary>
    /// A match spanning two words picks up both, and the area spans them — the synthetic separator
    /// between words maps to no glyph and must be skipped rather than treated as a box at the
    /// origin, which would stretch the area back to the corner of the page.
    /// </summary>
    /// <remarks>
    /// <b>The words are drawn far enough apart that the extractor splits them by the gap alone</b>,
    /// which is what makes this test about the separator at all. Written with a single <c>Tj</c>
    /// and an ordinary space, "Top Secret" once came back as one word at 12 pt, so the test passed
    /// through a code path it was not written for. A drawn space now ends the word too
    /// (<c>SpaceGlyphWordBoundaryTests</c>); the explicit positions keep this fixture independent
    /// of that rule.
    /// </remarks>
    [Fact]
    public void FindMatches_MultiWordMatch_DoesNotStretchTheAreaToTheOrigin()
    {
        byte[] pdf = SpacedWords(("Top", 72), ("Secret", 140), ("Material", 260));

        using var input = new MemoryStream(pdf, writable: false);
        var match = Assert.Single(PdfTextRedactor.FindMatches(input, "Top Secret"));

        Assert.InRange(match.Area.X, 68, 74);       // not 0
        Assert.InRange(match.Area.Y, 690, 715);     // not 0
        // ...and it reaches the second word, so the separator did not end the area early.
        Assert.InRange(match.Area.X + match.Area.Width, 160, 220);
    }

    /// <summary>
    /// One line whose words are drawn at explicit x positions, far enough apart that the
    /// extractor's gap heuristic splits them.
    /// </summary>
    private static byte[] SpacedWords(params (string Text, double X)[] words)
    {
        var content = new StringBuilder("BT /F1 12 Tf\n");
        foreach (var (text, x) in words)
            content.AppendFormat("1 0 0 1 {0} 700 Tm ({1}) Tj\n", x, text);
        content.Append("ET\n");
        return Assemble(content.ToString());
    }

    /// <summary>Padding widens the area on both axes.</summary>
    [Fact]
    public void FindMatches_PaddingWidensTheArea()
    {
        byte[] pdf = Document(Secret);

        using var input = new MemoryStream(pdf, writable: false);
        var tight = Assert.Single(PdfTextRedactor.FindMatches(
            input, Secret, new TextRedactionOptions { Padding = 0 }));

        input.Position = 0;
        var padded = Assert.Single(PdfTextRedactor.FindMatches(
            input, Secret, new TextRedactionOptions { Padding = 5 }));

        Assert.Equal(tight.Area.Width + 10, padded.Area.Width, 3);
        Assert.Equal(tight.Area.X - 5, padded.Area.X, 3);

        // Both axes, as the summary says. These two were missing, and mutating the padding out of
        // Y and Height reddened nothing — a redaction box padded horizontally and tight vertically
        // clips the ascenders and descenders it was widened to cover.
        Assert.Equal(tight.Area.Height + 10, padded.Area.Height, 3);
        Assert.Equal(tight.Area.Y - 5, padded.Area.Y, 3);
    }

    /// <summary>
    /// <b>Padding never shrinks the box.</b> A negative value is clamped to zero rather than pulling
    /// the edges inward, which would leave the glyphs it was meant to cover sticking out of the
    /// area the caller is about to paint over.
    /// </summary>
    [Fact]
    public void FindMatches_NegativePaddingDoesNotShrinkTheArea()
    {
        byte[] pdf = Document(Secret);

        using var input = new MemoryStream(pdf, writable: false);
        var tight = Assert.Single(PdfTextRedactor.FindMatches(
            input, Secret, new TextRedactionOptions { Padding = 0 }));

        input.Position = 0;
        var negative = Assert.Single(PdfTextRedactor.FindMatches(
            input, Secret, new TextRedactionOptions { Padding = -5 }));

        Assert.Equal(tight.Area.Width, negative.Area.Width, 3);
        Assert.Equal(tight.Area.Height, negative.Area.Height, 3);
        Assert.Equal(tight.Area.X, negative.Area.X, 3);
        Assert.Equal(tight.Area.Y, negative.Area.Y, 3);
    }

    // ================================================================ review before destroying

    /// <summary>
    /// <c>FindMatches</c> reports what would go <b>without changing the document</b> — redaction is
    /// irreversible and a pattern is easy to get wrong.
    /// </summary>
    [Fact]
    public void FindMatches_DoesNotChangeTheDocument()
    {
        byte[] pdf = Document(Secret, Kept);

        using var input = new MemoryStream(pdf, writable: false);
        PdfTextRedactor.FindMatches(input, Secret);

        Assert.Contains(Secret, Extract(pdf));
    }

    /// <summary>
    /// A pattern that matches nothing copies the input rather than rewriting it as a single
    /// revision — which would discard the document's incremental history, and any signature over
    /// it, to accomplish nothing.
    /// </summary>
    [Fact]
    public void Redact_MatchingNothing_CopiesTheInputUnchanged()
    {
        byte[] pdf = Document(Kept);

        byte[] after = Redact(pdf, "NotPresentAnywhere");

        Assert.Equal(pdf, after);
    }

    // ================================================================ a scanned page's text layer

    /// <summary>
    /// Text drawn invisible (render mode 3) is found and removed. That is how an OCR tool, and
    /// <c>PdfOperations.MakeSearchable</c>, puts searchable text over a scanned page, and it is
    /// what <c>ExtractText</c> returns for one.
    /// </summary>
    /// <remarks>
    /// Were structured extraction to drop every invisible glyph, the search would find nothing on a
    /// scanned page, <c>Redact</c> would remove nothing, and the text anyone could search for would
    /// survive.
    /// </remarks>
    [Fact]
    public void Redact_TextInAnInvisibleOcrLayer_IsFoundAndRemoved()
    {
        byte[] pdf = Assemble(
            "BT /F1 12 Tf 3 Tr 1 0 0 1 72 700 Tm (" + Secret + ") Tj\n" +
            "1 0 0 1 72 680 Tm (" + Kept + ") Tj ET\n");

        // The control: plain extraction returns the invisible layer.
        Assert.Contains(Secret, Extract(pdf));

        using var input = new MemoryStream(pdf, writable: false);
        Assert.Single(PdfTextRedactor.FindMatches(input, Secret));

        string after = Extract(Redact(pdf, Secret));
        Assert.DoesNotContain(Secret, after);
        Assert.Contains(Kept, after);
    }

    // ================================================================ the refusals stay

    /// <summary>
    /// <b>The text-driven entry point is not a way around the engine's refusals.</b> An encrypted
    /// document is refused here exactly as it is for rectangle-driven redaction, because the removal
    /// is still done by the one engine that knows about it.
    /// </summary>
    [Fact]
    public void Redact_EncryptedDocument_IsStillRefused()
    {
        byte[] encrypted = EncryptedDocument();

        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfRedactionException>(
            () => PdfTextRedactor.Redact(input, output, Secret));
        Assert.Contains("Encrypted", ex.Message);
    }

    /// <summary>
    /// A document carrying an <c>/Encrypt</c> dictionary. The keys are nonsense — what is being
    /// asserted is that a writer <i>refuses</i>, which is decided by the entry's presence.
    /// </summary>
    private static byte[] EncryptedDocument()
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
            "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
            "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>",
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
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R /Encrypt 4 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ================================================================ hostile input

    /// <summary>
    /// A caller-supplied expression with catastrophic backtracking is <b>bounded</b>, not allowed
    /// to hang the call. The guard's own evidence is that the call returns at all — and that the
    /// matches found on other lines are not lost with it.
    /// </summary>
    [Fact]
    public void FindMatches_AdversarialRegexBacktracking_IsBoundedAndKeepsOtherMatches()
    {
        // A classic backtracking bomb against a line of repeated characters.
        byte[] pdf = Document(new string('a', 40) + "!", Secret);

        using var input = new MemoryStream(pdf, writable: false);
        var matches = PdfTextRedactor.FindMatches(input,
            new[] { "(a+)+$", Secret },
            new TextRedactionOptions { UseRegex = true, RegexTimeout = TimeSpan.FromMilliseconds(50) });

        // Whatever the bomb did or did not match, the ordinary pattern beside it still found its
        // occurrence — a timeout on one line must not cost the rest of the document.
        Assert.Contains(matches, m => m.Text == Secret);
    }

    /// <summary>
    /// A zero-width match cannot loop forever. <c>Regex.Match</c> at the same index returns the same
    /// empty match, so advancing by the match length alone never moves.
    /// </summary>
    [Fact]
    public void FindMatches_ZeroWidthPattern_Terminates()
    {
        byte[] pdf = Document(Kept);

        using var input = new MemoryStream(pdf, writable: false);
        var matches = PdfTextRedactor.FindMatches(
            input, "x*", new TextRedactionOptions { UseRegex = true });

        // The claim is termination; the count is whatever the engine found, and an empty match
        // yields no glyphs so no area is produced for it.
        Assert.Empty(matches);
    }
}
