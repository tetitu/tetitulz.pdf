// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;
using B = Tetitulz.Pdf.Core.Tests.IO.Filters.Jbig2TestEncoder.Bitmap;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Arithmetic text regions and symbol dictionaries where refinement and aggregation take part
/// (T.88 §6.4.11, §6.5.8.2), each stream encoded by <see cref="Jbig2TestEncoder"/> from the standard.
/// </summary>
public class Jbig2TextRefinementTests
{
    private const int PageW = 40, PageH = 16;

    private static readonly B Sym0 = B.Random(6, 8, 11);
    private static readonly B Sym1 = B.Random(5, 8, 23);

    /// <summary>
    /// SBREFINE=1: three instances, the middle one refined from symbol 1 (5x8) into a 6x8 bitmap
    /// (RDW 1). The decoder drew every instance as a plain copy and read none of RI, RDW..RDY or the
    /// refinement bitmap, so the stream was out of step from the first instance.
    /// </summary>
    [Fact]
    public void ARefinedInstanceIsDecodedAndDrawn()
    {
        B refined = Sym1.Clone(6, 8);
        refined[5, 3] = 1; refined[2, 4] = 1 - refined[2, 4];

        var e = new Jbig2TestEncoder();
        int iadt = e.Block(512), iafs = e.Block(512), iads = e.Block(512), iari = e.Block(512);
        int iardw = e.Block(512), iardh = e.Block(512), iardx = e.Block(512), iardy = e.Block(512);
        int iaid = e.Block(2), gr = e.Block(1 << 13);

        e.Int(iadt, 0);                                  // STRIPT 0
        e.Int(iadt, 2);                                  // strip at T 2
        e.Int(iafs, 1);                                  // first S 1
        e.Id(iaid, 1, 0); e.Int(iari, 0);                // symbol 0 at S 1; then S 6
        e.Int(iads, 2);                                  // S 8
        e.Id(iaid, 1, 1); e.Int(iari, 1);                // symbol 1, refined
        e.Int(iardw, 1); e.Int(iardh, 0); e.Int(iardx, 1); e.Int(iardy, 1);
        e.Refinement(gr, refined, Sym1, dx: 1, dy: 1);   // reference offset (⌊1/2⌋ + 1, ⌊0/2⌋ + 1)
        e.Int(iads, 3);                                  // S 13 + 3 = 16
        e.Id(iaid, 1, 0); e.Int(iari, 0);
        e.Int(iads, null);                               // end of strip

        var region = new MemoryStream();
        Jbig2TestEncoder.TextRegionHeader(region, PageW, PageH, refine: true, instances: 3);
        byte[] coded = e.Finish();
        region.Write(coded, 0, coded.Length);

        var expected = new B(PageW, PageH);
        Sym0.DrawOn(expected, 1, 2);
        refined.DrawOn(expected, 8, 2);
        Sym0.DrawOn(expected, 16, 2);

        Assert.Equal(expected.Packed(), Decode(Jbig2TestEncoder.PlainDictionary(new[] { Sym0, Sym1 }), region.ToArray()));
    }

    /// <summary>
    /// A text region over a one-symbol dictionary codes no ID bits (SBSYMCODELEN = ⌈log₂ 1⌉ = 0).
    /// The decoder read one bit per instance, so the second instance was misplaced.
    /// </summary>
    [Fact]
    public void AOneSymbolDictionaryCodesNoIdBits()
    {
        var e = new Jbig2TestEncoder();
        int iadt = e.Block(512), iafs = e.Block(512), iads = e.Block(512);
        e.Int(iadt, 0); e.Int(iadt, 1); e.Int(iafs, 3);
        e.Int(iads, 4);                                  // S 3 + 5 + 4 = 12
        e.Int(iads, null);

        var region = new MemoryStream();
        Jbig2TestEncoder.TextRegionHeader(region, PageW, PageH, refine: false, instances: 2);
        byte[] coded = e.Finish();
        region.Write(coded, 0, coded.Length);

        var expected = new B(PageW, PageH);
        Sym0.DrawOn(expected, 3, 1);
        Sym0.DrawOn(expected, 12, 1);

        Assert.Equal(expected.Packed(), Decode(Jbig2TestEncoder.PlainDictionary(new[] { Sym0 }), region.ToArray()));
    }

    /// <summary>
    /// SDREFAGG=1 over the two-symbol dictionary: a symbol refined from symbol 1 (REFAGGNINST 1,
    /// §6.5.8.2.2) and a 13-wide aggregate of symbols 0 and 1 (REFAGGNINST 2, §6.5.8.2.1), both
    /// drawn by a text region. The decoder read refinement deltas §6.5.8.2.2 does not code, read no
    /// symbol ID, refined the first input symbol whatever the stream said, and decoded the aggregate
    /// with a CURT and no RI.
    /// <para>The rows vary SDRTEMPLATE, which is flag bit 12. The decoder read bit 9 ("bitmap
    /// coding context retained"): template 1 was taken for 0, and the retained bit for template 1.</para>
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ADictionarysRefinedAndAggregateSymbolsDecode(int refinementTemplate, bool contextRetained)
    {
        B r0 = Sym1.Clone(6, 8);
        r0[0, 0] = 1 - r0[0, 0]; r0[4, 6] = 1 - r0[4, 6];
        var aggregate = new B(13, 8);
        Sym0.DrawOn(aggregate, 0, 0);
        Sym1.DrawOn(aggregate, 7, 0);

        var e = new Jbig2TestEncoder();
        int iadh = e.Block(512), iadw = e.Block(512), iaai = e.Block(512), iaex = e.Block(512);
        int iadt = e.Block(512), iafs = e.Block(512), iads = e.Block(512), iari = e.Block(512);
        int iardx = e.Block(512), iardy = e.Block(512);
        const int codeLength = 2;                        // ⌈log₂(2 inputs + 2 new)⌉
        int iaid = e.Block(1 << codeLength), gr = e.Block(1 << 13);

        e.Int(iadh, 8);
        e.Int(iadw, 6);                                  // symbol 2: symbol 1 refined, one instance
        e.Int(iaai, 1);
        e.Id(iaid, codeLength, 1); e.Int(iardx, 1); e.Int(iardy, 0);
        e.Refinement(gr, r0, Sym1, dx: 1, dy: 0, refinementTemplate);
        e.Int(iadw, 7);                                  // symbol 3: width 13, two instances
        e.Int(iaai, 2);
        e.Int(iadt, 0); e.Int(iadt, 0); e.Int(iafs, 0);
        e.Id(iaid, codeLength, 0); e.Int(iari, 0);       // symbol 0 at 0; S then 5
        e.Int(iads, 2);                                  // S 7
        e.Id(iaid, codeLength, 1); e.Int(iari, 0);
        e.Int(iads, null);
        e.Int(iadw, null);
        e.Int(iaex, 2); e.Int(iaex, 2);                  // the inputs out, the new symbols in

        var dictionary = new MemoryStream();
        Jbig2TestEncoder.U16(dictionary,                 // SDREFAGG, SDTEMPLATE 0
            0x0002 | (refinementTemplate << 12) | (contextRetained ? 1 << 9 : 0));
        dictionary.Write(Jbig2TestEncoder.DefaultGenericAt, 0, 8);
        if (refinementTemplate == 0) dictionary.Write(Jbig2TestEncoder.RefinementAt, 0, 4);
        Jbig2TestEncoder.U32(dictionary, 2); Jbig2TestEncoder.U32(dictionary, 2);
        byte[] coded = e.Finish();
        dictionary.Write(coded, 0, coded.Length);

        var t = new Jbig2TestEncoder();
        int tdt = t.Block(512), tfs = t.Block(512), tds = t.Block(512), tid = t.Block(2);
        t.Int(tdt, 0); t.Int(tdt, 0); t.Int(tfs, 0);
        t.Id(tid, 1, 0);                                 // the refined symbol at 0; S then 5
        t.Int(tds, 15);                                  // S 20
        t.Id(tid, 1, 1);                                 // the aggregate
        t.Int(tds, null);
        var region = new MemoryStream();
        Jbig2TestEncoder.TextRegionHeader(region, PageW, PageH, refine: false, instances: 2);
        byte[] tc = t.Finish();
        region.Write(tc, 0, tc.Length);

        var s = new MemoryStream();
        Jbig2TestEncoder.Segment(s, 0, 48, null, Jbig2TestEncoder.PageInfo(PageW, PageH));
        Jbig2TestEncoder.Segment(s, 1, 0, null, Jbig2TestEncoder.PlainDictionary(new[] { Sym0, Sym1 }));
        Jbig2TestEncoder.Segment(s, 2, 0, new byte[] { 1 }, dictionary.ToArray());
        Jbig2TestEncoder.Segment(s, 3, 6, new byte[] { 2 }, region.ToArray());

        var expected = new B(PageW, PageH);
        r0.DrawOn(expected, 0, 0);
        aggregate.DrawOn(expected, 20, 0);

        Assert.Equal(expected.Packed(), Jbig2Decoder.Decode(s.ToArray(), null, out _, out _));
    }

    /// <summary>
    /// A symbol dictionary's symbols use its own AT pixels (SDAT). The decoder's symbol loop read
    /// the template's defaults and never SDAT, so here, with A1 at (-4, -1), every symbol decoded
    /// wrong.
    /// </summary>
    [Fact]
    public void ADictionarysSymbolsUseItsAtPixels()
    {
        var at = new (int X, int Y)[] { (-4, -1), (-3, -1), (2, -2), (-2, -2) };

        var e = new Jbig2TestEncoder();
        int iadt = e.Block(512), iafs = e.Block(512), iads = e.Block(512), iaid = e.Block(2);
        e.Int(iadt, 0); e.Int(iadt, 0); e.Int(iafs, 0);
        e.Id(iaid, 1, 0); e.Int(iads, 2);               // symbol 0 at 0, then S 7
        e.Id(iaid, 1, 1); e.Int(iads, null);
        var region = new MemoryStream();
        Jbig2TestEncoder.TextRegionHeader(region, PageW, PageH, refine: false, instances: 2);
        byte[] coded = e.Finish();
        region.Write(coded, 0, coded.Length);

        var expected = new B(PageW, PageH);
        Sym0.DrawOn(expected, 0, 0);
        Sym1.DrawOn(expected, 7, 0);

        Assert.Equal(expected.Packed(), Decode(Jbig2TestEncoder.PlainDictionary(new[] { Sym0, Sym1 }, at), region.ToArray()));
    }

    /// <summary>
    /// Malformed dictionary: it declares one new symbol and codes two in its height class. Reading
    /// to the class's OOB, the second is refused rather than decoded past the declared count; the
    /// control, declaring two, decodes both.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Malformed_MoreSymbolsThanDeclared_IsRefused(int declared, bool accepted)
    {
        byte[] body = Jbig2TestEncoder.PlainDictionary(new[] { Sym0, Sym1 });
        body[2 + 8 + 4 + 3] = (byte)declared;            // SDNUMNEWSYMS, after flags, SDAT and SDNUMEXSYMS

        if (accepted)
            Assert.Equal(2, Jbig2Decoder.DecodeSymbolDictionary(body, new System.Collections.Generic.List<Jbig2Decoder.Jbig2Bitmap>(), null).Length);
        else
            Assert.Throws<InvalidDataException>(() =>
                Jbig2Decoder.DecodeSymbolDictionary(body, new System.Collections.Generic.List<Jbig2Decoder.Jbig2Bitmap>(), null));
    }

    private static byte[] Decode(byte[] dictionary, byte[] region)
    {
        var s = new MemoryStream();
        Jbig2TestEncoder.Segment(s, 0, 48, null, Jbig2TestEncoder.PageInfo(PageW, PageH));
        Jbig2TestEncoder.Segment(s, 1, 0, null, dictionary);
        Jbig2TestEncoder.Segment(s, 2, 6, new byte[] { 1 }, region);
        return Jbig2Decoder.Decode(s.ToArray(), null, out _, out _);
    }
}
