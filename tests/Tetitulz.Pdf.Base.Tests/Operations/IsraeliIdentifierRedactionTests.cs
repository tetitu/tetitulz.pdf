// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfTextRedactor.FindIsraeliIdentifiers"/> — locating nine-digit Israeli identifiers
/// on the page so they can be redacted.
/// </summary>
/// <remarks>
/// <para><b>The control is a number with the wrong check digit.</b> It has the same length, the same
/// shape and the same neighbours as the valid one, so a locator that reported every nine-digit run
/// would pass a test that only asserted the valid one was found. Every claim here pairs the two.</para>
///
/// <para><b>What this cannot do is asserted too.</b> Roughly one nine-digit number in ten passes a
/// single check digit, and that is a property of the arithmetic rather than of this code — so a test
/// pins the documented framing (find, review, then redact) by demonstrating an unrelated number that
/// happens to validate is reported like any other.</para>
/// </remarks>
public sealed class IsraeliIdentifierRedactionTests
{
    /// <summary>1 2 3 4 5 6 7 8 weighted 1,2,1,2… sums to 38, so 2 completes it.</summary>
    private const string Valid = "123456782";

    /// <summary>The same eight digits, the one final digit that does not complete them.</summary>
    private const string Invalid = "123456789";

    private const string Neighbour = "Ordinary";

    /// <summary>A one-page document with each string on its own line, standard-14 so the extractor
    /// recovers real per-glyph geometry.</summary>
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

        string stream = content.ToString();
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

    private static IReadOnlyList<TextRedactionMatch> Find(
        byte[] pdf, TextRedactionOptions options = null)
    {
        using var input = new MemoryStream(pdf, writable: false);
        return PdfTextRedactor.FindIsraeliIdentifiers(input, options);
    }

    private static string RedactAndExtract(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PdfTextRedactor.RedactIsraeliIdentifiers(input, output);

        using var read = new MemoryStream(output.ToArray(), writable: false);
        return PdfOperations.ExtractText(read) ?? string.Empty;
    }

    // ================================================================ locating

    /// <summary>
    /// A valid identifier is found and one with the wrong check digit is not, from the same page.
    /// </summary>
    [Fact]
    public void ValidIdentifierIsFoundAndAnInvalidOneIsNot()
    {
        TextRedactionMatch match = Find(Document(Valid, Invalid, Neighbour)).Single();

        Assert.Equal(Valid, match.Text);
        Assert.Equal(0, match.PageIndex);
    }

    /// <summary>The area is a real rectangle over the digits, not a degenerate box.</summary>
    /// <remarks>
    /// A zero-sized area is what a broken glyph map produces, and it redacts nothing while the call
    /// reports a match — the shape of failure this whole file exists to make visible. The box must
    /// also sit where the text was drawn, so its origin is checked against the <c>Tm</c> the fixture
    /// wrote rather than merely being non-zero.
    /// </remarks>
    [Fact]
    public void TheAreaCoversTheDigitsOnThePage()
    {
        PdfRedactionArea area = Find(Document(Valid)).Single().Area;

        Assert.True(area.Width > 20, $"width was {area.Width}");
        Assert.True(area.Height > 5, $"height was {area.Height}");
        Assert.InRange(area.X, 68, 74);      // drawn at x = 72, less the default 1pt padding
        Assert.InRange(area.Y, 695, 705);    // drawn at y = 700
    }

    /// <summary>
    /// Two numbers a space apart are two numbers to a redaction, though together they would make
    /// a valid identifier: only a hyphen joins groups here. Written as one number, it is found —
    /// the control.
    /// </summary>
    [Fact]
    public void TwoNumbersASpaceApartAreNotJoinedIntoAnIdentifier()
    {
        Assert.Empty(Find(Document("1234 56782")));
        Assert.Equal(Valid, Find(Document(Valid)).Single().Text);
    }

    /// <summary>
    /// The hyphenated spelling is one identifier, and the hyphen is part of what gets covered.
    /// </summary>
    [Fact]
    public void TheHyphenatedSpellingIsFound()
    {
        TextRedactionMatch match = Find(Document("12345678-2")).Single();
        Assert.Equal("12345678-2", match.Text);
    }

    /// <summary>
    /// A longer number does not contribute a nine-digit window, and neither does a decimal amount.
    /// </summary>
    /// <remarks>
    /// <para>Both are the same defect from opposite directions: a scan over digit runs that does not
    /// bound its candidates turns every long number into several, and one in ten of those validates.
    /// <c>1234567820000</c> contains <c>123456782</c>; <c>1,234.123456782</c> contains it after the
    /// decimal point.</para>
    ///
    /// <para><b>The twenty-digit case exists because a mutation survived without it.</b> Deleting
    /// the candidate expression's lookarounds left every shorter fixture green, because the greedy
    /// quantifier already swallows a run of up to twelve characters whole and the digit count then
    /// rejects it. A run of twenty is the shortest one where the scan reaches a SECOND window - the
    /// leftmost twelve are discarded, and the eight that remain are a valid eight-digit identifier.
    /// Only the lookbehind refuses it, because its neighbour to the left is a digit.</para>
    ///
    /// <para>The valid identifier is on the same page, so "nothing was found" cannot be the reason
    /// these pass.</para>
    /// </remarks>
    [Fact]
    public void FragmentsOfLongerNumbersAreNotIdentifiers()
    {
        var matches = Find(Document(
            "1234567820000",           // a thirteen-digit run containing a valid nine
            "1,234.123456782",         // a decimal amount whose fraction is a valid nine
            "123456782.50",            // ...and the same shape the other way round: a valid nine
                                       // followed by a decimal fraction. The lookbehind cannot see
                                       // this one; only the look-ahead refuses it.
            "1,234,123456782",         // a comma-grouped amount, so the separator is not a period
            "11111111111123456783",    // twenty digits ending in a valid EIGHT-digit form
            Valid));

        Assert.Equal(Valid, Assert.Single(matches).Text);
    }

    /// <summary>
    /// <b>A full stop that is punctuation, not a decimal point.</b> The separator test only refuses
    /// a candidate when the character on the separator's far side is a digit — so a number written
    /// straight after a word and a period is still an identifier, and is still redacted.
    /// </summary>
    /// <remarks>
    /// Dropping that far-side digit check costs nothing visible on a price list and silently stops
    /// redacting identifiers in ordinary prose, which is the direction this operation must not err
    /// in. It is paired with the fragment cases above: one of them has to fail for the rule to be
    /// doing anything at all.
    /// </remarks>
    [Fact]
    public void ANumberFollowingAWordAndAFullStopIsStillAnIdentifier()
    {
        var matches = Find(Document("Ref." + Valid));

        Assert.Equal(Valid, Assert.Single(matches).Text);
    }

    /// <summary>
    /// An eight-digit number written without its leading zero is found.
    /// </summary>
    /// <remarks>
    /// The number as printed on a form routinely drops it, and the weights are positional, so this
    /// is only correct because the validator pads before weighting. A locator that required nine
    /// characters would miss a whole class of real documents.
    /// </remarks>
    [Fact]
    public void AnEightDigitFormIsFound()
    {
        // 0 2 3 4 5 6 7 8 sums to 37, so 3 completes it.
        Assert.Equal("23456783", Find(Document("23456783")).Single().Text);
    }

    /// <summary>
    /// A run too short to be one of these is not reported, even though the validator would pad it
    /// into a valid nine-digit number.
    /// </summary>
    /// <remarks>
    /// <c>18</c> normalises to <c>000000018</c> and validates — correctly, as a number. On a page it
    /// is a page number, a quantity or a year, and reporting it as an identity number would bury the
    /// real hits in noise. The locator's length floor is what separates the two, and nothing in the
    /// validator can make that distinction.
    /// </remarks>
    [Fact]
    public void ShortNumbersThatPadIntoValidOnesAreNotReported()
    {
        // Both of these ARE valid numbers - the validator pads them and the check digit agrees -
        // and neither is an identity number on a page. "234-5684" is the one that discriminates:
        // it is long enough to reach the candidate expression, so only the digit-count floor
        // rejects it. Without the floor it is reported, which is what a mutation showed.
        Assert.True(IsraeliIdentifiers.IsValid("18"));
        Assert.True(IsraeliIdentifiers.IsValid("234-5684"));

        Assert.Empty(Find(Document("18", "Item 18 of 40", "234-5684")));
    }

    // ================================================================ redacting

    /// <summary>
    /// Redaction removes the valid identifier and leaves everything else, the invalid look-alike
    /// included.
    /// </summary>
    [Fact]
    public void RedactionRemovesOnlyTheValidIdentifier()
    {
        string after = RedactAndExtract(Document(Valid, Invalid, Neighbour));

        Assert.DoesNotContain(Valid, after);
        Assert.Contains(Invalid, after);
        Assert.Contains(Neighbour, after);
    }

    /// <summary>
    /// An encrypted document is refused rather than quietly returning a copy.
    /// </summary>
    /// <remarks>
    /// <b>The refusal comes from the engine, and this asserts the outcome rather than its source.</b>
    /// Deleting the early check in either redaction entry point leaves this green — measured, not
    /// assumed — because <c>PdfRedactionEngine.Redact</c> refuses an encrypted document before it
    /// looks at the area list, so an empty list reaches the same refusal. What the early check buys
    /// is that the refusal happens before a whole document is extracted, which no assertion about
    /// behaviour can see. The claim worth pinning is the one here: an encrypted input never returns
    /// a copy and calls it redacted.
    /// </remarks>
    [Fact]
    public void EncryptedInputIsRefused()
    {
        byte[] encrypted = EncryptedFixture();

        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfRedactionException>(
            () => PdfTextRedactor.RedactIsraeliIdentifiers(input, output));
        Assert.Contains("Encrypted", ex.Message);
    }

    /// <summary>A minimal document that declares an <c>/Encrypt</c> dictionary.</summary>
    private static byte[] EncryptedFixture()
    {
        byte[] plain = Document(Valid);
        string text = Encoding.ASCII.GetString(plain);

        // Declaring /Encrypt in the trailer is what PdfDocument.IsEncrypted reads; the body does
        // not have to be genuinely enciphered for the refusal under test to be the one that fires.
        text = text.Replace("trailer\n<< /Size 6 /Root 1 0 R >>",
                            "trailer\n<< /Size 6 /Root 1 0 R /Encrypt 5 0 R >>");
        return Encoding.ASCII.GetBytes(text);
    }

    // ================================================================ refusals

    /// <summary>
    /// The options that cannot apply to a digit scan are refused rather than silently ignored.
    /// </summary>
    /// <remarks>
    /// A caller who sets <c>HebrewFolding</c> and gets no effect concludes the fold does not work,
    /// not that it was never consulted. The box half of the same options object <i>is</i> read,
    /// which is why the type is shared rather than duplicated.
    /// </remarks>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void OptionsThatCannotApplyAreRefused(bool matchCase, bool useRegex, bool folding)
    {
        var options = new TextRedactionOptions
        {
            MatchCase = matchCase,
            UseRegex = useRegex,
            HebrewFolding = folding ? HebrewFolding.Default : HebrewFolding.None,
        };

        var ex = Assert.Throws<PdfRedactionException>(() => Find(Document(Valid), options));
        Assert.Contains("identifier scan", ex.Message);
    }

    /// <summary>The box half of the options object is read.</summary>
    /// <remarks>
    /// Paired with the refusal above so the two halves of the shared type are both pinned: one is
    /// honoured, the other cannot be set. Without this, refusing every option would satisfy the
    /// test above.
    /// </remarks>
    [Fact]
    public void PaddingAndFillAreHonoured()
    {
        PdfRedactionArea tight = Find(Document(Valid), new TextRedactionOptions { Padding = 0 })
            .Single().Area;
        PdfRedactionArea loose = Find(Document(Valid),
            new TextRedactionOptions { Padding = 10, FillR = 1f }).Single().Area;

        Assert.Equal(tight.Width + 20, loose.Width, 3);
        Assert.Equal(0f, tight.FillR);
        Assert.Equal(1f, loose.FillR);
    }
}
