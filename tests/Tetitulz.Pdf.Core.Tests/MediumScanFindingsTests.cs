// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Tests for defects that do not warrant a file of their own. Each test names the defect it pins;
/// the reasoning lives on the code it guards.
/// </summary>
public class MediumScanFindingsTests
{
    // ==================================================================
    //  OpenTypeShaper — virama-initial Devanagari fragment
    // ==================================================================

    [Fact]
    public void ShapeDevanagari_MalformedViramaInitialFragment_ReturnsAReorderedString()
    {
        // "्कि" — virama, ka, vowel sign i. The pre-base matra walk decremented insertPos by 2
        // per virama-joined form, guarding BEFORE the decrement but not after, so it left the loop
        // at -1 and List.Insert(-1, …) threw straight out of Shape.
        string shaped = OpenTypeShaper.Shape("्कि");

        // The matra moved ahead of its cluster; nothing was lost.
        Assert.Equal(3, shaped.Length);
        Assert.Equal('ि', shaped[0]);
        Assert.Contains('्', shaped);
        Assert.Contains('क', shaped);
    }

    // ==================================================================
    //  UnicodeBidi — Latin-1 punctuation was Boundary Neutral
    // ==================================================================

    [Fact]
    public void GuillemetsAreMirroredInAnRtlLine()
    {
        // « and » are Bidi_Mirrored ON. Classified BN they were invisible to N1/N2 and never
        // reached L4, so they drew pointing the Latin way inside a Hebrew line.
        string reordered = UnicodeBidi.Reorder("שלום «abc» עולם", BaseDirection.RightToLeft);

        // Both guillemets resolve to level 1 (N2, embedding direction) and L4 mirrors every
        // Bidi_Mirrored character at an odd level. Reversal puts the logically-LATER » to the left
        // of "abc" and the logically-earlier « to its right; mirroring then swaps their shapes, so
        // the pair reads « abc » in visual order.
        //
        // This is the discriminating assertion: with the guillemets classified BN, L4 never saw
        // them, so the two came out the other way round — » abc « — which is the reversal alone.
        int a = reordered.IndexOf('a');
        int c = reordered.IndexOf('c');
        Assert.True(a > 0 && c > a);
        Assert.Equal('«', reordered[a - 1]);
        Assert.Equal('»', reordered[c + 1]);
    }

    [Fact]
    public void LatinOnePunctuationResolvesAsANeutralNotAsBoundaryNeutral()
    {
        // U+00BF ¿ is Other Neutral and — unlike the guillemets — is NOT Bidi_Mirrored, so the
        // mirror table cannot rescue it; only the classification can. Between two Latin runs in an
        // RTL paragraph, N1 resolves a neutral from its matching surroundings and gives it level 2.
        // As BN it is not neutral at all, so N1/N2 skip it and it stays at the paragraph level 1.
        var paragraph = UnicodeBidi.ResolveParagraph("abc¿def", BaseDirection.RightToLeft);

        Assert.Equal(1, paragraph.ParagraphLevel);
        Assert.Equal(2, paragraph.LevelAt(0));   // 'a' — Latin run
        Assert.Equal(2, paragraph.LevelAt(3));   // '¿' — resolved from the Latin on both sides
        Assert.Equal(2, paragraph.LevelAt(4));   // 'd'
    }

    [Fact]
    public void LatinOneSuperscriptDigitsAreEuropeanNumbers()
    {
        // U+00B2 is EN, not BN. Same catch-all swallowed it.
        var paragraph = UnicodeBidi.ResolveParagraph("שלום 12²", BaseDirection.RightToLeft);

        // The superscript joins the digit run rather than sitting at the paragraph level.
        Assert.Equal(paragraph.LevelAt(5), paragraph.LevelAt(7));
    }

    [Fact]
    public void LatinOnePunctuationDoesNotSplitTheNeutralRunAroundIt()
    {
        // BN is excluded from IsNeutral, so each guillemet TERMINATED the neutral run it sat in
        // and N2 forced the surrounding neutrals to the paragraph direction. With ON they resolve
        // from the Latin content, so the bracketed Latin run stays contiguous and in order.
        string reordered = UnicodeBidi.Reorder("שלום «abc» עולם", BaseDirection.RightToLeft);

        Assert.Contains("abc", reordered);
    }

    // ==================================================================
    //  ContentStreamParser — inline dictionary values
    // ==================================================================

    [Fact]
    public void InlineDictionary_StringValue_IsPreserved()
    {
        // /Span <</ActualText(fi)>> BDC — the standard way a producer records that a ligature
        // glyph stands for two characters. The value was consumed and stored as PdfNull.
        var ops = ContentStreamParser.Parse(
            Encoding.ASCII.GetBytes("/Span <</ActualText(fi)>> BDC\n"));

        var bdc = ops.Single(o => o.Operator == "BDC");
        var dict = Assert.IsType<PdfDictionary>(bdc.Operands[1]);
        var actual = Assert.IsType<PdfString>(dict[new PdfName("ActualText")]);
        Assert.Equal("fi", Encoding.ASCII.GetString(actual.RawBytes));
    }

    [Fact]
    public void InlineDictionary_ArrayValue_DoesNotDesynchroniseTheTokenStream()
    {
        // /OC <</Type/OCMD/OCGs[5 0 R]>> BDC. The opening '[' was consumed, the value became
        // PdfNull and the array body was left in the stream: parsing resumed mid-array, `R` was
        // emitted as an unknown operator, and BDC arrived with NO property dictionary — so the
        // optional-content group was never evaluated and hidden content was drawn.
        var ops = ContentStreamParser.Parse(
            Encoding.ASCII.GetBytes("/OC <</Type/OCMD/OCGs[5 0 R]>> BDC\nQ\n"));

        var bdc = ops.Single(o => o.Operator == "BDC");
        Assert.Equal(2, bdc.Operands.Length);
        var dict = Assert.IsType<PdfDictionary>(bdc.Operands[1]);
        Assert.Equal("OCMD", (dict[new PdfName("Type")] as PdfName)?.Value);
        Assert.IsType<PdfArray>(dict[new PdfName("OCGs")]);

        // And the operators that follow are still parsed as operators.
        Assert.Contains(ops, o => o.Operator == "Q");
        Assert.DoesNotContain(ops, o => o.Operator == "R");
    }

    [Fact]
    public void InlineDictionary_NestedDictionaryValue_IsParsedNotDropped()
    {
        var ops = ContentStreamParser.Parse(
            Encoding.ASCII.GetBytes("/Span <</A <</B 3>> >> BDC\n"));

        var bdc = ops.Single(o => o.Operator == "BDC");
        var outer = Assert.IsType<PdfDictionary>(bdc.Operands[1]);
        var inner = Assert.IsType<PdfDictionary>(outer[new PdfName("A")]);
        Assert.Equal(3, (inner[new PdfName("B")] as PdfNumber)?.LongValue);
    }

    // ==================================================================
    //  PdfFunctionEvaluator — Type 4 output selection and indirect /Range
    // ==================================================================

    [Fact]
    public void Type4_ProgramThatLeavesItsInputUnconsumed_ReturnsTheTopNValues()
    {
        // §7.10.5: the outputs are the TOPMOST n values. `{ 0 0 0 1 }` is a legal constant-black
        // tint transform that ignores its input, so the stack is (t, 0, 0, 0, 1) and the four
        // outputs must be 0 0 0 1 — solid black. Taking the bottom four returned (t, 0, 0, 0),
        // a mid cyan.
        var fn = Type4Function("{ 0 0 0 1 }", inputCount: 1, outputCount: 4);

        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f });

        Assert.Equal(new[] { 0f, 0f, 0f, 1f }, r);
    }

    [Fact]
    public void Type4_IndirectRange_IsResolvedSoTheOutputCountIsCorrect()
    {
        // `fn[PdfName.Range]` is the raw indexer, so an indirect /Range yielded null and n
        // silently became stack.Count — a five-component colour for a four-component space.
        var range = new PdfArray();
        for (int i = 0; i < 4; i++) { range.Add(new PdfNumber(0)); range.Add(new PdfNumber(1)); }

        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));
        var domain = new PdfArray();
        domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
        fn.Set(new PdfName("Domain"), domain);
        fn.Set(new PdfName("Range"), new PdfReference(9, 0));
        fn.RawData = Encoding.ASCII.GetBytes("{ 0 0 0 1 }");

        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f },
            obj => obj is PdfReference ? range : obj);

        Assert.Equal(4, r.Length);
        Assert.Equal(new[] { 0f, 0f, 0f, 1f }, r);
    }

    private static PdfStream Type4Function(string code, int inputCount, int outputCount)
    {
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));
        var domain = new PdfArray();
        for (int i = 0; i < inputCount; i++) { domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1)); }
        fn.Set(new PdfName("Domain"), domain);
        var range = new PdfArray();
        for (int i = 0; i < outputCount; i++) { range.Add(new PdfNumber(0)); range.Add(new PdfNumber(1)); }
        fn.Set(new PdfName("Range"), range);
        fn.RawData = Encoding.ASCII.GetBytes(code);
        return fn;
    }

    // ==================================================================
    //  LzwDecodeFilter — silent truncation
    // ==================================================================

    [Fact]
    public void Lzw_CorruptCode_ReportsThatTheOutputIsTruncated()
    {
        // A code that is neither in the table nor the next entry to be added stops the decode and
        // returns what it has — deliberately lenient, and reported, so a half-decoded content
        // stream is told apart from a short one.
        var filter = new LzwDecodeFilter();
        byte[] output = filter.Decode(CorruptLzw(), parameters: null);

        Assert.NotNull(LzwDecodeFilter.LastTruncationReason);
        Assert.Contains("LZWDecode stopped", LzwDecodeFilter.LastTruncationReason!);
        Assert.True(output.Length < 16);
    }

    [Fact]
    public void Lzw_LastTruncationReason_IsClearedByASubsequentCleanDecode()
    {
        var filter = new LzwDecodeFilter();
        filter.Decode(CorruptLzw(), parameters: null);
        Assert.NotNull(LzwDecodeFilter.LastTruncationReason);

        // 'A' (0x41), then EOD (257). 9-bit codes: 0 0100 0001 1 0000 0001 …
        byte[] clean = PackCodes(new[] { 0x41, 257 }, 9);
        byte[] output = filter.Decode(clean, parameters: null);

        Assert.Null(LzwDecodeFilter.LastTruncationReason);
        Assert.Equal(new byte[] { 0x41 }, output);
    }

    /// <summary>A stream whose second code (300) is far past the table's 259 entries.</summary>
    private static byte[] CorruptLzw() => PackCodes(new[] { 0x41, 300 }, 9);

    private static byte[] PackCodes(IReadOnlyList<int> codes, int codeSize)
    {
        var bits = new List<int>();
        foreach (int code in codes)
            for (int b = codeSize - 1; b >= 0; b--)
                bits.Add((code >> b) & 1);
        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i] != 0) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    // ==================================================================
    //  FilterPipeline — indirect /DecodeParms array element
    // ==================================================================

    [Fact]
    public void DecodeParmsArrayElementThatIsNotADictionary_IsReported()
    {
        // `/DecodeParms [null 12 0 R]` — the element casts to null, which is indistinguishable
        // from "this stage has no parameters", so the predictor is never removed and the image
        // renders as diagonal smear. The adjacent /Filter branch already reported its equivalent.
        var filters = new PdfArray();
        filters.Add(new PdfName("ASCIIHexDecode"));
        filters.Add(new PdfName("FlateDecode"));

        var parms = new PdfArray();
        parms.Add(PdfNull.Instance);
        parms.Add(new PdfReference(12, 0));

        FilterPipeline.ResetLastUnknownFilterName();
        try
        {
            FilterPipeline.Decode(Encoding.ASCII.GetBytes(">"), filters, parms);
        }
        catch (Exception)
        {
            // The payload is not valid Flate; what is under test is the reported drop.
        }

        Assert.NotNull(FilterPipeline.LastUnknownFilterName);
        Assert.Contains("DecodeParms[1]", FilterPipeline.LastUnknownFilterName!);
    }

    // ==================================================================
    //  CcittFaxDecoder — reconstructed scanlines
    // ==================================================================

    [Fact]
    public void Ccitt_MalformedHuffmanPrefix_PadsInWhiteAndReportsTheRecovery()
    {
        // The bare catch filled the rest of the scanline with `color`, which alternates and is
        // black on every second run — the opposite of the doc comment three lines above it — and
        // set no diagnostic, so a fax clipped by a wrong /Length came back with black bands and
        // LastRowRecovered == null.
        // 0x00 bytes are not a resolvable white prefix, so the very first run throws.
        byte[] encoded = new byte[] { 0x00, 0x00, 0x00, 0x00 };

        CcittFaxDecoder.DecodeGroup3_1D(encoded, columns: 16, rows: 1, endOfBlock: false,
            encodedByteAlign: false, requireEol: false, out int rowsDecoded);

        Assert.Equal(1, rowsDecoded);
        Assert.NotNull(CcittFaxDecoder.LastRowRecovered);
        Assert.Contains("reconstructed", CcittFaxDecoder.LastRowRecovered!);
    }

    [Fact]
    public void Ccitt_LastRowRecovered_IsClearedByASubsequentCleanDecode()
    {
        byte[] broken = new byte[] { 0x00, 0x00, 0x00, 0x00 };
        CcittFaxDecoder.DecodeGroup3_1D(broken, 16, 1, false, false, false, out _);
        Assert.NotNull(CcittFaxDecoder.LastRowRecovered);

        // All-white G4 rows: V0 ("1") per row against an all-white reference.
        byte[] clean = new byte[] { 0xFF };
        CcittFaxDecoder.DecodeGroup4(clean, columns: 8, rows: 8, endOfBlock: false, out _);

        Assert.Null(CcittFaxDecoder.LastRowRecovered);
    }
}
