// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Unit tests for ContentStreamParser, focusing on the concatenated-operator fix:
/// PDF generators that omit whitespace between operators (e.g. "DoQQ" instead of
/// "Do Q Q") must be split into the correct individual operator sequence.
/// </summary>
public class ContentStreamParserTests
{
    private static List<ContentStreamOperation> Parse(string stream)
        => ContentStreamParser.Parse(Encoding.Latin1.GetBytes(stream));

    // ── Which token types are operands ────────────────────────────────────────

    /// <summary>
    /// A closing bracket with no opening one is <b>not</b> an operand and must not become one.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is worth pinning.</b> The operand loop calls <c>ParseInlineValue</c> rather
    /// than carrying its own copy of the token-to-object switch, and its <c>default</c> maps an
    /// unrecognised token to <c>PdfNull</c>. That is right in a <i>value</i> position — an
    /// inline-image parameter, a marked-content property — and wrong in an operand list, so the
    /// operand loop keeps its own list of which token types are operands and ignores the rest.</para>
    /// <para>If a stray <c>]</c> became a <c>PdfNull</c> operand, every operand index after it would
    /// shift by one, which is how an operator reads its arguments out of position — and mutating
    /// that distinction away leaves the rest of the Core suite green.</para>
    /// </remarks>
    [Fact]
    public void AStrayClosingBracketIsNotAnOperand()
    {
        var ops = Parse("1 2 ] 3 4 re");

        var op = Assert.Single(ops);
        Assert.Equal("re", op.Operator);
        Assert.Equal(4, op.Operands.Length);
        Assert.All(op.Operands, o => Assert.IsType<PdfNumber>(o));
        Assert.Equal(1, ((PdfNumber)op.Operands[0]).IntValue);
        Assert.Equal(4, ((PdfNumber)op.Operands[3]).IntValue);
    }

    /// <summary>
    /// The acceptance control: the identical operator without the stray bracket. Without it the test
    /// above would pass on a parser that dropped operands wholesale.
    /// </summary>
    [Fact]
    public void TheSameOperatorWithoutAStrayBracket_HasTheSameFourOperands()
    {
        var ops = Parse("1 2 3 4 re");

        var op = Assert.Single(ops);
        Assert.Equal("re", op.Operator);
        Assert.Equal(4, op.Operands.Length);
        Assert.Equal(1, ((PdfNumber)op.Operands[0]).IntValue);
        Assert.Equal(4, ((PdfNumber)op.Operands[3]).IntValue);
    }

    // ── Fix 1: concatenated operators ─────────────────────────────────────────

    [Fact]
    public void DoQQ_SplitIntoThreeOperators()
    {
        // "DoQQ" with no whitespace — a pattern real producers write.
        var ops = Parse("DoQQ");

        Assert.Equal(3, ops.Count);
        Assert.Equal("Do", ops[0].Operator);
        Assert.Equal("Q",  ops[1].Operator);
        Assert.Equal("Q",  ops[2].Operator);
    }

    [Fact]
    public void NameOperandBeforeDoQQ_OperandBelongsToDoOnly()
    {
        // "/Im64 DoQQ" — the name /Im64 must become the single operand of Do;
        // Q and Q must receive no operands.
        var ops = Parse("/Im64 DoQQ");

        Assert.Equal(3, ops.Count);
        Assert.Equal("Do", ops[0].Operator);
        Assert.Single(ops[0].Operands);
        Assert.IsType<PdfName>(ops[0].Operands[0]);
        Assert.Equal("Im64", ((PdfName)ops[0].Operands[0]).Value);

        Assert.Equal("Q", ops[1].Operator);
        Assert.Empty(ops[1].Operands);

        Assert.Equal("Q", ops[2].Operator);
        Assert.Empty(ops[2].Operands);
    }

    [Fact]
    public void Qq_SplitIntoTwoOperators()
    {
        // "Qq" (capital Q then lowercase q — both are single-char known ops).
        var ops = Parse("Qq");

        Assert.Equal(2, ops.Count);
        Assert.Equal("Q", ops[0].Operator);
        Assert.Equal("q", ops[1].Operator);
    }

    [Fact]
    public void QqBT_SplitIntoThreeOperators()
    {
        // "QqBT" — exercises a 2-char known op (BT) after two single-char ops.
        var ops = Parse("QqBT");

        Assert.Equal(3, ops.Count);
        Assert.Equal("Q",  ops[0].Operator);
        Assert.Equal("q",  ops[1].Operator);
        Assert.Equal("BT", ops[2].Operator);
    }

    [Fact]
    public void KnownOperator_NotSplit()
    {
        // "cm" is a known 2-char operator and must NOT be split into "c" + "m".
        var ops = Parse("1 0 0 1 0 0 cm");

        Assert.Single(ops);
        Assert.Equal("cm", ops[0].Operator);
        Assert.Equal(6, ops[0].Operands.Length);
    }

    [Fact]
    public void KnownThreeCharOperator_NotSplit()
    {
        // "SCN" is a 3-char known op; should not split into S+C+N or SC+N etc.
        var ops = Parse("1 0 0 SCN");

        Assert.Single(ops);
        Assert.Equal("SCN", ops[0].Operator);
    }

    [Fact]
    public void UnknownOpThatCannotBeSplit_PassedThrough()
    {
        // "xyz" has no known-op prefix (neither "x", "xy", nor "xyz" are in the
        // known-ops table), so the entire token is emitted verbatim as a single
        // unknown operator rather than being dropped or throwing.
        var ops = Parse("xyz");

        Assert.Single(ops);
        Assert.Equal("xyz", ops[0].Operator);
    }

    [Fact]
    public void PartiallyDecomposable_KnownPrefixFollowedByUnknownRemainder()
    {
        // "qZ" — "q" is a known 1-char op, but "Z" is not known.
        // The greedy split yields "q" then falls back to remainder "Z".
        var ops = Parse("qZ");

        Assert.Equal(2, ops.Count);
        Assert.Equal("q", ops[0].Operator);
        Assert.Equal("Z", ops[1].Operator);
    }

    [Fact]
    public void EmptyStream_ReturnsEmptyList()
    {
        var ops = ContentStreamParser.Parse(Array.Empty<byte>());
        Assert.Empty(ops);
    }

    [Fact]
    public void NullStream_ReturnsEmptyList()
    {
        var ops = ContentStreamParser.Parse(null);
        Assert.Empty(ops);
    }

    [Fact]
    public void MultipleOperatorsWithOperands_ParsedCorrectly()
    {
        // A typical content stream excerpt — no concatenation, just normal parsing.
        var ops = Parse("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        Assert.Equal(5, ops.Count);
        Assert.Equal("BT", ops[0].Operator);
        Assert.Equal("Tf", ops[1].Operator);
        Assert.Equal("Td", ops[2].Operator);
        Assert.Equal("Tj", ops[3].Operator);
        Assert.Equal("ET", ops[4].Operator);
    }

    [Fact]
    public void Qcm_SplitIntoQAndCm()
    {
        // "Qcm" — Q (known 1-char) + cm (known 2-char, longest match).
        var ops = Parse("Qcm");

        Assert.Equal(2, ops.Count);
        Assert.Equal("Q",  ops[0].Operator);
        Assert.Equal("cm", ops[1].Operator);
    }

    [Fact]
    public void QSCNq_SplitCorrectly()
    {
        // "QSCNq" → Q + SCN + q  (3-char longest-match takes SCN before S+C+N).
        var ops = Parse("QSCNq");

        Assert.Equal(3, ops.Count);
        Assert.Equal("Q",   ops[0].Operator);
        Assert.Equal("SCN", ops[1].Operator);
        Assert.Equal("q",   ops[2].Operator);
    }

    // ── BI glued to the operator before it ───────────────────────────────────
    //
    // Comparing the WHOLE keyword token to "BI" fires only when a generator leaves whitespace
    // in front of the image. "qBI" would fall through to SplitOps, which correctly produces
    // q + BI — and the parser would then tokenise the image's raw sample bytes as operands and
    // operators. Every op in the payload would be fabricated: PdfTextExtractor reporting
    // characters the page never draws, and a renderer executing operators the document never
    // contained.

    [Fact]
    public void GluedQBI_InlineImageIsConsumed_DeclaredLength()
    {
        // "qBI" with no whitespace.  /L 9 declares the payload exactly, and the nine
        // payload bytes "BT(A)TjET" are deliberately valid-looking operators — if the
        // image is not consumed they parse as BT, Tj, ET and invent the text "A".
        var ops = Parse("qBI /W 3 /H 3 /BPC 8 /CS /G /L 9 ID BT(A)TjET EI Q\n");

        Assert.Equal(3, ops.Count);
        Assert.Equal("q",  ops[0].Operator);
        Assert.Equal("BI", ops[1].Operator);
        Assert.Equal("Q",  ops[2].Operator);

        var image = Assert.IsType<PdfStream>(Assert.Single(ops[1].Operands));
        Assert.Equal("BT(A)TjET", Encoding.Latin1.GetString(image.RawData));
    }

    [Fact]
    public void GluedQBI_PayloadNeverBecomesTextOperators()
    {
        // The consequence, stated directly: no operator from inside the image payload
        // may appear in the operation list.
        var ops = Parse("qBI /W 3 /H 3 /BPC 8 /CS /G /L 9 ID BT(A)TjET EI Q\n");

        Assert.DoesNotContain(ops, o => o.Operator == "Tj");
        Assert.DoesNotContain(ops, o => o.Operator == "BT");
        Assert.DoesNotContain(ops, o => o.Operator == "ET");
        Assert.DoesNotContain(ops, o => o.Operator == "ID");
        Assert.DoesNotContain(ops, o => o.Operator == "EI");
    }

    [Fact]
    public void GluedQBI_MatchesUnGluedForm()
    {
        // "q BI …" and "qBI …" describe the same page; they must parse identically.
        const string tail = " /W 3 /H 3 /BPC 8 /CS /G /L 9 ID BT(A)TjET EI Q\n";
        var glued   = Parse("qBI" + tail);
        var spaced  = Parse("q BI" + tail);

        Assert.Equal(spaced.Select(o => o.Operator), glued.Select(o => o.Operator));
    }

    [Fact]
    public void GluedQBI_InlineImageIsConsumed_HeuristicPath()
    {
        // No /L, and /F makes the byte count uncomputable from the geometry.  The payload is
        // also not a well-formed /AHx stream — "0011223344" carries no ">" end-of-data marker,
        // so the self-delimiting scan declines it too — which leaves the EI-scanning heuristic
        // as the last resort.  The glued "BI" must still reach it.
        var ops = Parse("qBI /W 4 /H 4 /BPC 8 /CS /G /F /AHx ID 0011223344 EI Q\n");

        Assert.Equal(3, ops.Count);
        Assert.Equal("q",  ops[0].Operator);
        Assert.Equal("BI", ops[1].Operator);
        Assert.Equal("Q",  ops[2].Operator);

        var image = Assert.IsType<PdfStream>(Assert.Single(ops[1].Operands));
        Assert.Equal("0011223344", Encoding.Latin1.GetString(image.RawData));
    }

    [Fact]
    public void GluedGsBI_PendingOperandsBelongToTheOperatorBeforeBI()
    {
        // "/GS1 gsBI …" — the operand must land on gs, and BI must still open the image.
        var ops = Parse("/GS1 gsBI /W 3 /H 3 /BPC 8 /CS /G /L 9 ID BT(A)TjET EI Q\n");

        Assert.Equal(3, ops.Count);
        Assert.Equal("gs", ops[0].Operator);
        Assert.Equal("GS1", Assert.IsType<PdfName>(Assert.Single(ops[0].Operands)).Value);
        Assert.Equal("BI", ops[1].Operator);
        Assert.Equal("Q",  ops[2].Operator);
    }

    [Fact]
    public void UnGluedBI_StillParsedAsSingleInlineImage()
    {
        // Regression guard for the pre-existing (working) whitespace-separated form.
        var ops = Parse("q BI /W 3 /H 3 /BPC 8 /CS /G /L 9 ID BT(A)TjET EI Q\n");

        Assert.Equal(3, ops.Count);
        Assert.Equal("q",  ops[0].Operator);
        Assert.Equal("BI", ops[1].Operator);
        Assert.Equal("Q",  ops[2].Operator);
    }

    [Fact]
    public void GluedQuoteBI_QuoteIsAKnownOperatorSoBIIsFound()
    {
        // ' and " are Table A.1 operators that were missing from the split table.  Without
        // them "'BI" has no known prefix, so the whole run was emitted verbatim as one unknown
        // operator and the image behind it was never recognised.
        var ops = Parse("(x)'BI /W 3 /H 3 /BPC 8 /CS /G /L 9 ID BT(A)TjET EI Q\n");

        Assert.Equal(3, ops.Count);
        Assert.Equal("'",  ops[0].Operator);
        Assert.Single(ops[0].Operands);
        Assert.Equal("BI", ops[1].Operator);
        Assert.Equal("Q",  ops[2].Operator);
        Assert.DoesNotContain(ops, o => o.Operator == "Tj");
    }

    [Fact]
    public void GluedBIID_RewindsToTheImageHeader()
    {
        // "qBIID" is q + BI + the ID of a parameter-less image, all in one keyword token.
        // The keyword reader swallowed the ID, so parsing must rewind to just past the BI;
        // otherwise the image header is already behind us and the payload is never bounded.
        var ops = Parse("qBIID abc EI Q\n");

        Assert.Equal(3, ops.Count);
        Assert.Equal("q",  ops[0].Operator);
        Assert.Equal("BI", ops[1].Operator);
        Assert.Equal("Q",  ops[2].Operator);

        var image = Assert.IsType<PdfStream>(Assert.Single(ops[1].Operands));
        Assert.Equal("abc", Encoding.Latin1.GetString(image.RawData));
    }

    [Fact]
    public void GluedBI_UnterminatedImage_DoesNotHangOrEmitPayloadOperators()
    {
        // Adversarial input: a glued BI whose image never supplies EI.  The scan must
        // stop at end-of-stream (not spin, not throw out of Parse) and must not turn the
        // dangling payload into operators.
        var ops = Parse("qBI /W 3 /H 3 /BPC 8 /CS /G ID BT(A)TjET Q q Q q Q");

        Assert.Equal("q", ops[0].Operator);
        Assert.DoesNotContain(ops, o => o.Operator == "Tj");
        Assert.DoesNotContain(ops, o => o.Operator == "BT");
    }

    // ── A self-delimiting filter bounds the payload exactly ──────────────────
    //
    // A filtered inline image with no /L cannot be sized from /W, /H, /BPC and /CS, since a
    // filter makes the byte count independent of them.  Four of the
    // seven inline-image filters carry their own end-of-data marker (ISO 32000-1 §7.4),
    // so the length is readable from the payload itself — and one of them, /A85, is the
    // case the heuristic provably cannot survive.

    [Fact]
    public void InlineImageAsciiHex_EodMarkerBoundsThePayloadWhenNoWhitespacePrecedesEi()
    {
        // §8.9.7 requires whitespace after ID, not before EI, and producers routinely write
        // ">EI".  The heuristic needs that leading whitespace to recognise the terminator, so
        // it would run to end-of-stream and swallow the Q that follows the image.
        var ops = Parse("qBI /W 2 /H 2 /BPC 8 /CS /G /F /AHx ID 00FF00FF>EI Q\n");

        Assert.Equal(new[] { "q", "BI", "Q" }, ops.Select(o => o.Operator).ToArray());

        var image = Assert.IsType<PdfStream>(Assert.Single(ops[1].Operands));
        Assert.Equal("00FF00FF>", Encoding.Latin1.GetString(image.RawData));
    }

    [Fact]
    public void InlineImageAscii85_TildeGreaterThanBoundsAPayloadTheHeuristicEndsEarly()
    {
        // The case the heuristic cannot survive.  An ASCII85 payload is printable ASCII by
        // construction and 'E' and 'I' are both in the base-85 alphabet, so a wrapped payload
        // can contain a whitespace-delimited "EI" followed by four more printable bytes —
        // every condition the heuristic tests.  It ended this image four bytes in and
        // tokenised "9M~>" as operators.
        var ops = Parse("qBI /W 2 /H 2 /BPC 8 /CS /G /F /A85 ID 8;Xa EI 9M~> EI Q\n");

        Assert.Equal(new[] { "q", "BI", "Q" }, ops.Select(o => o.Operator).ToArray());

        var image = Assert.IsType<PdfStream>(Assert.Single(ops[1].Operands));
        Assert.Equal("8;Xa EI 9M~>", Encoding.Latin1.GetString(image.RawData));
    }

    [Fact]
    public void InlineImageRunLength_RunWalkBeatsBothADecoyEiAndAnEodValueInsideTheData()
    {
        // 0xFF 0x80 is a repeat run — "emit 0x80 twice" — so the byte 128 appears in the
        // payload without being the §7.4.5 end-of-data marker.  The literal run after it holds
        // " EI abc", whose four following bytes are printable, so the heuristic terminates
        // there.  Only walking run by run reaches the real EOD at the last byte.
        byte[] payload =
        {
            0xFF, 0x80,                                      // repeat 0x80 x2
            0x06, 0x20, 0x45, 0x49, 0x20, 0x61, 0x62, 0x63,  // literal run: " EI abc"
            0x80,                                            // EOD
        };
        byte[] content = Cat(
            Latin1("qBI /W 2 /H 2 /BPC 8 /CS /G /F /RL ID "), payload, Latin1(" EI Q\n"));

        var ops = ContentStreamParser.Parse(content);

        Assert.Equal(new[] { "q", "BI", "Q" }, ops.Select(o => o.Operator).ToArray());

        var image = Assert.IsType<PdfStream>(Assert.Single(ops[1].Operands));
        Assert.Equal(payload, image.RawData);
    }

    [Fact]
    public void InlineImageDct_MarkerWalkSkipsAThumbnailEoiInsideTheJpegHeader()
    {
        // A JFIF/EXIF APP0 segment carries a thumbnail, which is a whole embedded JPEG and
        // therefore ends in FF D9.  Searching for FF D9 finds that one first and ends the image
        // inside its own header; the payload here also hides a printable " EI abcd" decoy for
        // the heuristic.  Every segment declares its length, so the walk steps over both.
        byte[] payload =
        {
            0xFF, 0xD8,                                                  // SOI
            0xFF, 0xE0, 0x00, 0x0C,                                      // APP0, segment length 12
            0x20, 0x45, 0x49, 0x20, 0x61, 0x62, 0x63, 0x64,              // " EI abcd" — decoy
            0xFF, 0xD9,                                                  // thumbnail EOI — decoy
            0xFF, 0xDA, 0x00, 0x04, 0x01, 0x00,                          // SOS, segment length 4
            0x12, 0x34, 0xFF, 0x00, 0x56,                                // entropy data, FF stuffed
            0xFF, 0xD9,                                                  // real EOI
        };
        byte[] content = Cat(
            Latin1("qBI /W 2 /H 2 /BPC 8 /CS /G /F /DCT ID "), payload, Latin1("\nEI Q\n"));

        var ops = ContentStreamParser.Parse(content);

        Assert.Equal(new[] { "q", "BI", "Q" }, ops.Select(o => o.Operator).ToArray());

        var image = Assert.IsType<PdfStream>(Assert.Single(ops[1].Operands));
        Assert.Equal(payload, image.RawData);
    }

    [Fact]
    public void InlineImageAsciiHex_MalformedPayloadFallsBackToTheHeuristicInsteadOfInventingALength()
    {
        // The dictionary says /AHx but the payload is not hex.  Measuring it would end the image
        // at a byte the file never marked, so the scan declines and the heuristic — which finds
        // the whitespace-delimited EI here — still bounds the image correctly.
        var ops = Parse("qBI /W 2 /H 2 /BPC 8 /CS /G /F /AHx ID zzz qqq EI Q\n");

        Assert.Equal(new[] { "q", "BI", "Q" }, ops.Select(o => o.Operator).ToArray());

        var image = Assert.IsType<PdfStream>(Assert.Single(ops[1].Operands));
        Assert.Equal("zzz qqq", Encoding.Latin1.GetString(image.RawData));
    }

    [Fact]
    public void InlineImageAscii85_UnterminatedPayloadDoesNotHangOrEmitPayloadOperators()
    {
        // Adversarial input: /A85 whose "~>" never arrives and whose EI never arrives either.
        // The scan must decline (not run off the end of the array), and the heuristic behind it
        // must still refuse to turn the dangling payload into operators.
        var ops = Parse("qBI /W 2 /H 2 /BPC 8 /CS /G /F /A85 ID 8;Xa9M");

        Assert.Equal("q", ops[0].Operator);
        Assert.Equal("BI", ops[1].Operator);
        Assert.DoesNotContain(ops, o => o.Operator == "EI");
    }

    // ── Inline-image parameter values: one value parser, three call sites ──────
    //
    // ParseInlineValue replaced three copies of the same switch, each missing something
    // different. The dictionary copy had been repaired twice already (strings, then composites);
    // the inline-image and array copies had not, and the inline-image one was doing damage.

    /// <summary>
    /// <c>/DP</c> is a <b>dictionary</b>, and the inline-image copy of the value switch had no
    /// case for one — so the <c>&lt;&lt;</c> was consumed, the value became <c>PdfNull</c>, and the
    /// body was left in the token stream to be read as further image keys.
    ///
    /// <para>Asserted as a dictionary with the right contents rather than as "not null": the
    /// desynchronisation is what makes this expensive, and only the inner values prove the
    /// tokenizer resumed in the right place.</para>
    /// </summary>
    [Fact]
    public void InlineImage_DictionaryValuedDecodeParms_IsParsedAsADictionary()
    {
        var ops = Parse("BI /W 8 /H 2 /BPC 1 /CS /G /F /CCF" +
                        " /DP << /K -1 /Columns 8 /Rows 2 >> /L 2 ID \x01\x02 EI\n");

        var img = Assert.IsType<PdfStream>(Assert.Single(ops[0].Operands));
        var dp = Assert.IsType<PdfDictionary>(img[new PdfName("DP")]);

        Assert.Equal(-1, ((PdfNumber)dp[new PdfName("K")]).LongValue);
        Assert.Equal(8,  ((PdfNumber)dp[new PdfName("Columns")]).LongValue);
        Assert.Equal(2,  ((PdfNumber)dp[new PdfName("Rows")]).LongValue);
    }

    /// <summary>
    /// The other half of the same defect, and the half a caller sees first: the inner keys were
    /// hoisted into the <b>image</b> dictionary, where <c>/Columns</c> and <c>/Rows</c> are not
    /// image keys at all and <c>/K</c> collides with nothing only by luck.
    /// </summary>
    [Fact]
    public void InlineImage_DecodeParmsKeys_DoNotLeakIntoTheImageDictionary()
    {
        var ops = Parse("BI /W 8 /H 2 /BPC 1 /CS /G /F /CCF" +
                        " /DP << /K -1 /Columns 8 /Rows 2 >> /L 2 ID \x01\x02 EI\n");

        var img = Assert.IsType<PdfStream>(Assert.Single(ops[0].Operands));

        Assert.Null(img[new PdfName("K")]);
        Assert.Null(img[new PdfName("Columns")]);
        Assert.Null(img[new PdfName("Rows")]);
    }

    /// <summary>
    /// The image dictionary's key set, stated exactly.
    ///
    /// <para>The leak and the loss are one defect seen from two sides, and only the whole set rules
    /// out both at once: a spot check for one absent key passes while a different one is still
    /// leaking. Written after the mutation run — asserting that <c>/L</c> <i>survived</i> the
    /// desynchronisation was a claim that held with the defect in place, because <c>&gt;&gt;</c> is
    /// not a Name token and the key loop skips it, so parsing recovers by the next key.</para>
    /// </summary>
    [Fact]
    public void InlineImage_ParameterKeys_AreExactlyThoseWritten()
    {
        var ops = Parse("BI /W 8 /H 2 /BPC 1 /CS /G /F /CCF" +
                        " /DP << /K -1 /Columns 8 /Rows 2 >> /L 2 ID \x01\x02 EI\n");

        var img = Assert.IsType<PdfStream>(Assert.Single(ops[0].Operands));

        Assert.Equal(
            new[] { "BPC", "CS", "DP", "F", "H", "L", "W" },
            img.Keys.Select(k => k.Value).OrderBy(v => v, System.StringComparer.Ordinal).ToArray());
        Assert.Equal(2, img.RawData.Length);
    }

    /// <summary>
    /// The array copy had no <c>Boolean</c> case, so a boolean element was dropped <b>and every
    /// element after it moved down one index</b> — which is how a positional array (a
    /// <c>/Decode</c>, a <c>/Matrix</c>) becomes silently wrong rather than obviously short.
    /// </summary>
    [Fact]
    public void MarkedContentPropertyArray_KeepsBooleanElementsInPosition()
    {
        var ops = Parse("/OC << /Bits [true false 7] >> BDC EMC\n");

        var props = Assert.IsType<PdfDictionary>(ops[0].Operands[1]);
        var bits = Assert.IsType<PdfArray>(props[new PdfName("Bits")]);

        Assert.Equal(3, bits.Count);
        Assert.True(((PdfBoolean)bits[0]).Value);
        Assert.False(((PdfBoolean)bits[1]).Value);
        Assert.Equal(7, ((PdfNumber)bits[2]).LongValue);
    }

    /// <summary>
    /// A string value in an inline-image dictionary — the case the marked-content copy gained
    /// years ago and this one never did.
    /// </summary>
    [Fact]
    public void InlineImage_StringValuedKey_SurvivesParsing()
    {
        var ops = Parse("BI /W 1 /H 1 /BPC 8 /CS /G /Name (Logo) /L 1 ID \x01 EI\n");

        var img = Assert.IsType<PdfStream>(Assert.Single(ops[0].Operands));
        var name = Assert.IsType<PdfString>(img[new PdfName("Name")]);
        Assert.Equal("Logo", Encoding.Latin1.GetString(name.RawBytes));
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Cat(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] p in parts) total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            System.Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }
}
