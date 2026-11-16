// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Util;
using Xunit;
using static Tetitulz.Pdf.Core.Tests.IO.Filters.Jbig2TestEncoder;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// What a symbol dictionary with refinement aggregation (SDREFAGG 1, T.88 §6.5.8.2) costs: its
/// symbols' pool grown a symbol at a time, and every refined symbol, aggregate instance and aggregate
/// box charged to the work budget, in both codings. The dictionaries are written by
/// <see cref="Jbig2TestEncoder"/> and <see cref="Jbig2HuffmanRefinementTests.HuffmanBits"/>; each
/// refers to one 1 × 1 input symbol.
/// </summary>
public class Jbig2AggregateDictionaryCostTests
{
    /// <summary>
    /// The coverage-guided fuzzer's 910 bytes, a page's content stream filtered through JBIG2: a
    /// symbol dictionary of some 75 000 aggregate symbols whose MQ data runs on past its end, every
    /// readers' decode of the page paying for them again. Their boxes are wide, and charged before
    /// they are allocated, the budget refuses the dictionary within its first boxes.
    /// </summary>
    [Fact]
    public void Hostile_TheFuzzersAggregateDictionary_IsRefusedByTheBudgetForItsBoxes()
    {
        byte[] stream = Convert.FromBase64String(FuzzerStream);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] output = new Jbig2DecodeFilter().Decode(stream, parameters: null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(stream, output);
        Assert.Contains("Work budget for JBIG2 decode exceeded", Jbig2DecodeFilter.LastFallbackReason, StringComparison.Ordinal);
        Assert.InRange(allocated, 0, 64L << 20);
    }

    /// <summary>
    /// Twenty thousand two-instance aggregates in either coding: in place the pool is one list, a
    /// few megabytes with the symbols; copied a symbol at a time it is 1.6 GB of references.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hostile_ManyAggregateSymbols_TheirPoolIsGrownInPlace(bool huffman)
    {
        const int symbols = 20_000;
        byte[] dictionary = huffman ? HuffmanAggregateDictionary(symbols) : AggregateDictionary(symbols);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Jbig2Decoder.Jbig2Bitmap[] decoded = Jbig2Decoder.DecodeSymbolDictionary(dictionary, OneInputSymbol(), null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1 + symbols, decoded.Length);
        Assert.InRange(allocated, 0, 64L << 20);
    }

    /// <summary>
    /// A count a header declares is not an allocation: a Huffman dictionary declaring ten million
    /// new symbols and coding one sizes its lists by what it decodes, not by the declaration.
    /// </summary>
    [Fact]
    public void Hostile_AHuffmanDictionaryDeclaringTenMillionSymbols_DoesNotAllocateForThem()
    {
        byte[] dictionary = HuffmanAggregateDictionary(1, declaredNew: 10_000_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        try { Jbig2Decoder.DecodeSymbolDictionary(dictionary, OneInputSymbol(), null); }
        catch (InvalidDataException) { }   // the zero bits past the one symbol code nothing coherent
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 8L << 20);
    }

    /// <summary>
    /// Each kind of aggregate symbol spends its measured price, pinned through the budget's own
    /// evidence: a refined symbol its decodes (<see cref="Jbig2Decoder.TextInstanceUnits"/>), the
    /// refinement region's setup and its one pixel, and a Huffman-coded one the fresh arithmetic
    /// decoder too; a two-instance aggregate its box and, per instance, the instance and a
    /// one-pixel draw.
    /// </summary>
    [Fact]
    public void EachAggregateSymbol_IsChargedItsPrice()
    {
        Assert.Equal(16, Jbig2Decoder.TextInstanceUnits);
        Assert.Equal(16, Jbig2Decoder.RefinementRegionSetupUnits);
        Assert.Equal(256, Jbig2Decoder.HuffmanRefinementSetupUnits);
        const int symbols = 5;
        long refined = 16 + 16 + 1;
        long huffmanRefined = refined + 256;
        long aggregate = 64 * 16 + 2 * (16 + 1 + 1);

        Assert.Equal(symbols * refined, Spent(RefinedDictionary(symbols)));
        Assert.Equal(symbols * huffmanRefined, Spent(HuffmanRefinedDictionary(symbols)));
        Assert.Equal(symbols * aggregate, Spent(AggregateDictionary(symbols, 64, 16)));
        Assert.Equal(symbols * aggregate, Spent(HuffmanAggregateDictionary(symbols, 64, 16)));
    }

    /// <summary>
    /// An aggregate's box is charged before it is allocated: under a budget of two 4096 × 4096 boxes
    /// and their instances, the third is refused by the budget, in either coding, and within a budget
    /// of three the same dictionary decodes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateBoxesPastTheBudget_AreRefusedByIt(bool huffman)
    {
        const int side = 4096;
        byte[] dictionary = huffman ? HuffmanAggregateDictionary(3, side, side) : AggregateDictionary(3, side, side);
        long box = (long)side * side, instances = 2 * (16 + 1 + 1);

        var refusal = Assert.Throws<WorkBudgetExceededException>(() =>
            Jbig2Decoder.DecodeSymbolDictionary(dictionary, OneInputSymbol(), new WorkBudget(2 * (box + instances) + box / 2)));
        Assert.Contains("budget", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(4, Jbig2Decoder.DecodeSymbolDictionary(dictionary, OneInputSymbol(), new WorkBudget(3 * (box + instances))).Length);
    }

    private static long Spent(byte[] dictionary)
    {
        var budget = new WorkBudget(long.MaxValue);
        Jbig2Decoder.DecodeSymbolDictionary(dictionary, OneInputSymbol(), budget);
        return budget.Spent;
    }

    private static List<Jbig2Decoder.Jbig2Bitmap> OneInputSymbol()
        => new List<Jbig2Decoder.Jbig2Bitmap> { new Jbig2Decoder.Jbig2Bitmap(1, 1) };

    // ---------------------------------------------------------------- dictionaries

    /// <summary>
    /// <paramref name="symbols"/> 1 × 1 symbols, each one instance (REFAGGNINST 1) refining input
    /// symbol 0; every symbol, the input one included, exported. At most 65 535 symbols: past a
    /// 16-bit symbol ID the decoder folds the ID's contexts, which this encoder does not.
    /// </summary>
    internal static byte[] RefinedDictionary(int symbols)
    {
        var e = new Jbig2TestEncoder();
        int codeLength = Jbig2Decoder.SymbolCodeLength(1L + symbols);
        e.Block(1 << 16);                                    // the generic region's, unused
        int iadh = e.Block(512), iadw = e.Block(512), iaai = e.Block(512), iaex = e.Block(512);
        int iardx = e.Block(512), iardy = e.Block(512), iaid = e.Block(1 << codeLength), refinement = e.Block(1 << 13);
        var pixel = new Bitmap(1, 1);
        e.Int(iadh, 1);
        for (int i = 0; i < symbols; i++)
        {
            e.Int(iadw, i == 0 ? 1 : 0);
            e.Int(iaai, 1);
            e.Id(iaid, codeLength, 0);
            e.Int(iardx, 0);
            e.Int(iardy, 0);
            e.Refinement(refinement, pixel, pixel, 0, 0);
        }
        return ArithmeticDictionary(e, iadw, iaex, symbols);
    }

    /// <summary>
    /// <paramref name="symbols"/> aggregates of <paramref name="width"/> × <paramref name="height"/>,
    /// each two instances of input symbol 0 at S 0 on one strip, neither refined.
    /// </summary>
    internal static byte[] AggregateDictionary(int symbols, int width = 1, int height = 1)
    {
        var e = new Jbig2TestEncoder();
        int codeLength = Jbig2Decoder.SymbolCodeLength(1L + symbols);
        e.Block(1 << 16);
        int iadh = e.Block(512), iadw = e.Block(512), iaai = e.Block(512), iaex = e.Block(512);
        int iadt = e.Block(512), iafs = e.Block(512), iads = e.Block(512), iari = e.Block(512);
        int iaid = e.Block(1 << codeLength);
        e.Int(iadh, height);
        for (int i = 0; i < symbols; i++)
        {
            e.Int(iadw, i == 0 ? width : 0);
            e.Int(iaai, 2);
            e.Int(iadt, 0);                                  // the strip's initial STRIPT
            e.Int(iadt, 0);                                  // DT
            e.Int(iafs, 0);                                  // DFS
            e.Id(iaid, codeLength, 0); e.Int(iari, 0); e.Int(iads, 0);
            e.Id(iaid, codeLength, 0); e.Int(iari, 0); e.Int(iads, null);
        }
        return ArithmeticDictionary(e, iadw, iaex, symbols);
    }

    /// <summary>The height class's end, the export runs and the segment body around the coded data.</summary>
    private static byte[] ArithmeticDictionary(Jbig2TestEncoder e, int iadw, int iaex, int symbols)
    {
        e.Int(iadw, null);
        e.Int(iaex, 0);
        e.Int(iaex, 1 + symbols);

        var body = new MemoryStream();
        U16(body, 0x0002);                                   // SDHUFF 0, SDREFAGG 1, SDTEMPLATE 0, SDRTEMPLATE 0
        body.Write(DefaultGenericAt, 0, DefaultGenericAt.Length);
        body.Write(RefinementAt, 0, RefinementAt.Length);
        U32(body, 1 + symbols); U32(body, symbols);
        byte[] coded = e.Finish();
        body.Write(coded, 0, coded.Length);
        return body.ToArray();
    }

    /// <summary><paramref name="symbols"/> Huffman-coded 1 × 1 refinements of input symbol 0, each BMSIZE 0.</summary>
    internal static byte[] HuffmanRefinedDictionary(int symbols)
    {
        int codeLength = Jbig2Decoder.SymbolCodeLength(1L + symbols);
        var bits = new Jbig2HuffmanRefinementTests.HuffmanBits();
        bits.Value(Jbig2Decoder.StandardTableD, 1);          // HCDH
        for (int i = 0; i < symbols; i++)
        {
            bits.Value(Jbig2Decoder.StandardTableB, i == 0 ? 1 : 0);   // DW
            bits.Value(Jbig2Decoder.StandardTableA, 1);      // REFAGGNINST
            bits.Raw(0, codeLength);                         // input symbol 0
            bits.Value(Jbig2Decoder.StandardTableO, 0);      // RDX
            bits.Value(Jbig2Decoder.StandardTableO, 0);      // RDY
            bits.Value(Jbig2Decoder.StandardTableA, 0);      // BMSIZE
            bits.Align();
        }
        return HuffmanDictionary(bits, symbols, symbols);
    }

    /// <summary>
    /// <paramref name="symbols"/> Huffman-coded aggregates of <paramref name="width"/> ×
    /// <paramref name="height"/>, each two instances of input symbol 0, neither refined, the header
    /// declaring <paramref name="declaredNew"/> new symbols (the coded count by default).
    /// </summary>
    internal static byte[] HuffmanAggregateDictionary(int symbols, int width = 1, int height = 1, int declaredNew = -1)
    {
        int declared = declaredNew < 0 ? symbols : declaredNew;
        int codeLength = Jbig2Decoder.SymbolCodeLength(1L + declared);
        var bits = new Jbig2HuffmanRefinementTests.HuffmanBits();
        bits.Value(Jbig2Decoder.StandardTableD, height);     // HCDH
        for (int i = 0; i < symbols; i++)
        {
            bits.Value(Jbig2Decoder.StandardTableB, i == 0 ? width : 0);   // DW
            bits.Value(Jbig2Decoder.StandardTableA, 2);      // REFAGGNINST
            // Table B.11 has no line for 0: -1 then +1 leaves the strip at T 0.
            bits.Value(Jbig2Decoder.StandardTableK, 1);
            bits.Value(Jbig2Decoder.StandardTableK, 1);
            bits.Value(Jbig2Decoder.StandardTableF, 0);      // DFS
            bits.Raw(0, codeLength); bits.Raw(0, 1);         // symbol 0, not refined
            bits.Value(Jbig2Decoder.StandardTableH, 0);      // DS
            bits.Raw(0, codeLength); bits.Raw(0, 1);
            bits.Value(Jbig2Decoder.StandardTableH, Jbig2Decoder.Jbig2HuffmanTable.OOB);
        }
        return HuffmanDictionary(bits, symbols, declared);
    }

    private static byte[] HuffmanDictionary(Jbig2HuffmanRefinementTests.HuffmanBits bits, int symbols, int declared)
    {
        bits.Value(Jbig2Decoder.StandardTableB, Jbig2Decoder.Jbig2HuffmanTable.OOB);   // end of the height class
        bits.Value(Jbig2Decoder.StandardTableA, 0);          // export: none excluded
        bits.Value(Jbig2Decoder.StandardTableA, 1 + symbols); // export: every symbol
        return Jbig2HuffmanRefinementTests.HuffmanDictionary(1 + symbols, declared, bits.ToArray());
    }

    private const string FuzzerStream =
        "eJwzAAAAABQUFBQUCmVuAAAAAABlY29kZSAvSkJJRzJEZWNvZHN0YYp0eCVQREYxIHRyAAAAAAAAAAAAAAAAAAAAAFIgL0sgWzEzIDAgUiAwIDw8IC9UeXBlIC9NQ1IgLwAAAAAAAAAAAAAAAAAAAAAAAAAAAGFpbGVyCjw8IC9TemUgNogvZSFZwp5dCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJJSUlJSUlJSUlJSUlJSUlKCUlJSUlJSUlJSUlJSUJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQIJCQkJCQkJCQkJCQkJCTs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7OzsJ//9lICxGbGF0ZURlY29kZSAvY29kZSAvIC9DeXJwdCAvQ3J5cHQgL0pCSUcyRGVjb2RlIC9GbGF0ZURlZSBdIC9MZZKddGggMjcgPj4Kc3RyZWFtCnicMwAAAAAUFBQUFAplbgAAAAAAZWNvZGUgL0pCSUcyRGVjb2RzdGGKdHglUERGMSB0cgAAAAAAAAAAAAAAAAAAAABSIC9LIFsxMyAwIFIgMCA8PCAvVHlwZSAvTUNSIC8AAAAAAAAAAAAAAAAAAAAAAAAAAABhaWxlcgo8PCAvU3plIDaIL2UhWcKeXQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCSUlJSUlJSUlJSUlJSUlJSglJSUlJSUlJSUlJSUlCQkJCQkJCQkJCQkJCQkJCQl/CQkJCQkJCQkJCQkJCQkJCQkJCQkJ9/b29wkJCQldXV1dcmVmCjcxMAolAAABcGUAAAAAYmoKMiAwIDg4ODg4OG9iago0IDAgb2JqCjw8IC9UeXBlIC9PYmpTdG0gL04gNzLExsrfL0ZpcnN0IDkBAAAAAAAA/XMnIDUgMCBSIC9MZW5ndGggNzY0IDAgb2JqCjw8IC9GaWx0ZXIgWy9GbGF0ZURlY29vQGRlIC8gL0NyeXB0IC9DcnlwdCAvSkJJRzJEZWNvZGUgL0ZsYXRlKkRlY29kZSAvQ3J5cHQgL0FTQ0kkSUhleERlY29kBQAAAAAAAGVEZWNvZGUgL0ZsYXRlRGVjb2RlIC9BU0NJSUhlY29kZSCnAAAAAAAAAF0gL0xlbmd0aCAyNiA+PgpzdHJlYW0KeM8UFBQUJVBERtYtMS43CjEgMCBvYmoKPDggZg==";
}
