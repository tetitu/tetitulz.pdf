// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.TestHelpers;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxCodestreamParser"/> against hand-built codestreams, each written out segment by
/// segment in <see cref="JpxCodestreams"/> as T.800 Annex A lays them out.
/// </summary>
/// <remarks>
/// Every refusal case changes one thing about <see cref="JpxCodestreams.Minimal"/> and must be refused
/// <b>for that reason</b> — the test asserts the reason's wording, not merely a false return, so a
/// case that failed earlier for some other reason would not pass as this one.
/// <see cref="Minimal_ParsesTheMainHeaderAndTheTilePart"/> is the acceptance control they all vary.
/// </remarks>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class JpxCodestreamParserTests
{
    private static readonly byte[] Data5 = { 1, 2, 3, 4, 5 };

    private static JpxCodestream Parse(byte[] data)
    {
        Assert.True(JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out string reason), reason);
        return cs;
    }

    // ── acceptance ─────────────────────────────────────────────────────────────

    [Fact]
    public void Minimal_ParsesTheMainHeaderAndTheTilePart()
    {
        byte[] data = J.Minimal();
        JpxCodestream cs = Parse(data);

        Assert.Equal((64L, 64L, 1), (cs.Size.Xsiz, cs.Size.Ysiz, cs.Size.Components.Length));
        Assert.Equal(1, cs.Size.NumTiles);
        Assert.Equal(1, cs.Main.Coding.Layers);
        Assert.Equal(1, cs.ComponentCodingFor(0, 0).DecompositionLevels);
        Assert.Equal((6, 6), (cs.ComponentCodingFor(0, 0).CodeBlockWidthExponent, cs.ComponentCodingFor(0, 0).CodeBlockHeightExponent));
        Assert.True(cs.ComponentCodingFor(0, 0).Reversible);
        Assert.Equal(JpxQuantizationStyle.None, cs.QuantizationFor(0, 0).Style);
        Assert.Equal(4, cs.QuantizationFor(0, 0).Exponents.Length);
        Assert.True(cs.EndOfCodestreamSeen);

        // The tile-part's data is exactly the five bytes after SOD, in place.
        JpxTilePart part = Assert.Single(cs.Tiles[0].Parts);
        Assert.Equal(5, part.Data.Length);
        Assert.Equal(Data5, data.AsSpan((int)part.Data.Start, 5).ToArray());
    }

    [Fact]
    public void AJp2Container_IsParsedThroughItsCodestreamBox()
    {
        byte[] jp2 = Jp2Headers.Concat(Jp2Headers.SignatureBox(), Jp2Headers.Box("jp2c", J.Minimal()));
        Assert.Equal(64, Parse(jp2).Size.Xsiz);
    }

    /// <summary>
    /// T.800 §A.6.1–A.6.2: tile COC &gt; tile COD &gt; main COC &gt; main COD. The case that matters is
    /// component 1 in tile 0: a main-header COC exists for it, and the tile's COD still wins.
    /// </summary>
    [Fact]
    public void CodingStyle_FollowsT800Precedence()
    {
        byte[] data = J.Concat(J.Soc,
            J.Siz(128, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1), (0x07, 1, 1), (0x07, 1, 1) }),
            J.Cod(levels: 5), J.Coc(1, levels: 3), J.QcdNone(5),
            J.TilePart(0, 0, J.Concat(J.Cod(levels: 2), J.Coc(2, levels: 1)), Data5),
            J.TilePart(1, 0, Array.Empty<byte>(), Data5),
            J.Eoc);
        JpxCodestream cs = Parse(data);

        Assert.Equal(2, cs.ComponentCodingFor(0, 0).DecompositionLevels);   // tile COD
        Assert.Equal(2, cs.ComponentCodingFor(0, 1).DecompositionLevels);   // tile COD beats main COC
        Assert.Equal(1, cs.ComponentCodingFor(0, 2).DecompositionLevels);   // tile COC
        Assert.Equal(5, cs.ComponentCodingFor(1, 0).DecompositionLevels);   // main COD
        Assert.Equal(3, cs.ComponentCodingFor(1, 1).DecompositionLevels);   // main COC
    }

    /// <summary>T.800 §A.6.4–A.6.5: tile QCC &gt; tile QCD &gt; main QCC &gt; main QCD, told apart by guard bits.</summary>
    [Fact]
    public void Quantization_FollowsT800Precedence()
    {
        byte[] steps = { 0x40, 0x40, 0x40, 0x40 };
        byte[] data = J.Concat(J.Soc,
            J.Siz(128, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1), (0x07, 1, 1), (0x07, 1, 1) }),
            J.Cod(), J.Qcd(1 << 5, steps), J.Qcc(1, false, 2 << 5, steps),
            J.TilePart(0, 0, J.Concat(J.Qcd(3 << 5, steps), J.Qcc(2, false, 4 << 5, steps)), Data5),
            J.TilePart(1, 0, Array.Empty<byte>(), Data5),
            J.Eoc);
        JpxCodestream cs = Parse(data);

        Assert.Equal(3, cs.QuantizationFor(0, 0).GuardBits);   // tile QCD
        Assert.Equal(3, cs.QuantizationFor(0, 1).GuardBits);   // tile QCD beats main QCC
        Assert.Equal(4, cs.QuantizationFor(0, 2).GuardBits);   // tile QCC
        Assert.Equal(1, cs.QuantizationFor(1, 0).GuardBits);   // main QCD
        Assert.Equal(2, cs.QuantizationFor(1, 1).GuardBits);   // main QCC
    }

    [Fact]
    public void ScalarQuantization_SplitsExponentAndMantissa()
    {
        // 0x4A_3C = exponent 9 (top 5 bits), mantissa 0x23C (low 11 bits).
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Cod(transform: 0), J.Qcd(0x02 | (2 << 5), 0x4A, 0x3C, 0x4A, 0x3C, 0x4A, 0x3C, 0x4A, 0x3C),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        JpxQuantization q = Parse(data).QuantizationFor(0, 0);

        Assert.Equal(JpxQuantizationStyle.ScalarExpounded, q.Style);
        Assert.Equal(2, q.GuardBits);
        Assert.Equal(9, q.Exponents[0]);
        Assert.Equal(0x23C, q.Mantissas[0]);
    }

    [Fact]
    public void RoiShift_TileOverridesMain()
    {
        byte[] data = J.Concat(J.Soc, J.Siz(128, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1) }), J.Cod(), J.QcdNone(), J.Rgn(0, 5),
            J.TilePart(0, 0, J.Rgn(0, 9), Data5), J.TilePart(1, 0, Array.Empty<byte>(), Data5), J.Eoc);
        JpxCodestream cs = Parse(data);

        Assert.Equal(9, cs.RoiShiftFor(0, 0));
        Assert.Equal(5, cs.RoiShiftFor(1, 0));
    }

    /// <summary>
    /// POC's component indices are 8 bits below 257 components and 16 bits from 257 on (T.800 Table
    /// A.32), and an 8-bit end index of 0 means 256.
    /// </summary>
    [Fact]
    public void ProgressionChanges_ParseBothComponentIndexWidths()
    {
        byte[] compact = J.Concat(J.Soc, J.Siz64(3), J.Cod(), J.QcdNone(), J.Poc(false, (0, 0, 1, 33, 0, 1)),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        JpxProgressionChange c = Assert.Single(Parse(compact).ProgressionChangesFor(0));
        Assert.Equal((0, 0, 1, 33, 256, JpxProgressionOrder.Rlcp),
            (c.ResolutionStart, c.ComponentStart, c.LayerEnd, c.ResolutionEnd, c.ComponentEnd, c.Order));

        byte[] wide = J.Concat(J.Soc, J.Siz64(257), J.Cod(), J.QcdNone(), J.Poc(true, (1, 2, 3, 5, 300, 4)),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        JpxProgressionChange w = Assert.Single(Parse(wide).ProgressionChangesFor(0));
        Assert.Equal((1, 2, 3, 5, 300, JpxProgressionOrder.Cprl),
            (w.ResolutionStart, w.ComponentStart, w.LayerEnd, w.ResolutionEnd, w.ComponentEnd, w.Order));
    }

    /// <summary>T.800 §A.7.4: PPM segments concatenate in increasing <c>Zppm</c>, whatever order they appear in.</summary>
    [Fact]
    public void PackedHeaders_AreOrderedByTheirZIndex()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(),
            J.Segment(0xFF60, 1, 0xBB), J.Segment(0xFF60, 0, 0xAA),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        JpxCodestream cs = Parse(data);

        Assert.Equal(2, cs.PackedHeadersMain.Count);
        Assert.Equal(0xAA, data[cs.PackedHeadersMain[0].Start]);
        Assert.Equal(0xBB, data[cs.PackedHeadersMain[1].Start]);

        byte[] ppt = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, J.Concat(J.Segment(0xFF61, 1, 0xD2), J.Segment(0xFF61, 0, 0xD1)), Data5), J.Eoc);
        byte[] pptData = ppt;
        JpxTilePart part = Assert.Single(Parse(ppt).Tiles[0].Parts);
        Assert.Equal(0xD1, pptData[part.PackedHeaders[0].Start]);
        Assert.Equal(0xD2, pptData[part.PackedHeaders[1].Start]);
    }

    /// <summary>T.800 §A.1: an unknown segment is discarded by its length; 0xFF30–0xFF3F have no length at all.</summary>
    [Fact]
    public void UnknownAndParameterlessMarkers_AreSkipped()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Segment(0xFF6A, 9, 9, 9), new byte[] { 0xFF, 0x30 }, J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, J.Segment(0xFF64, 0, 1, (byte)'x'), Data5), J.Eoc);
        Assert.Equal(1, Parse(data).Main.Coding.Layers);
    }

    [Fact]
    public void APsotOfZero_RunsToTheEoc()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5, psot: 0), J.Eoc);
        JpxCodestream cs = Parse(data);

        Assert.Equal(data.Length - 2, cs.Tiles[0].Parts[0].Data.End);   // stops before EOC
        Assert.True(cs.EndOfCodestreamSeen);
    }

    /// <summary>
    /// A tile-part longer than the data — a download cut short — is clamped, not refused: a truncated
    /// codestream still decodes (T.800 §A.4.4 NOTE 2). The missing EOC says so.
    /// </summary>
    [Fact]
    public void ATruncatedCodestream_IsClampedAndHasNoEoc()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5, psot: 5000));
        JpxCodestream cs = Parse(data);

        Assert.Equal(data.Length, cs.Tiles[0].Parts[0].Data.End);
        Assert.False(cs.EndOfCodestreamSeen);
        Assert.Contains("past the end of the codestream; clamped to it, and the codestream is truncated", Assert.Single(cs.Deviations), StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralTileParts_KeepTheirOrder()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.TilePart(0, 1, Array.Empty<byte>(), new byte[] { 7, 7 }), J.Eoc);
        JpxTile tile = Parse(data).Tiles[0];

        Assert.Equal(new[] { 0, 1 }, new[] { tile.Parts[0].Index, tile.Parts[1].Index });
        Assert.Equal(2, tile.Parts[1].Data.Length);
    }

    /// <summary>
    /// T.801 §M.9.2.3 admits Part 2's multiple component transformation; its segments are kept for the
    /// reconstruction stage. The non-linearity bit is "useful to decode" (T.801 Table A.2) and ignored.
    /// </summary>
    [Fact]
    public void Part2MultipleComponentTransform_IsAcceptedAndItsSegmentsKept()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(3, rsiz: 0x8000 | 0x0100 | 0x0200), J.Cod(), J.QcdNone(), J.Segment(0xFF74, 0, 0, 0),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        JpxCodestream cs = Parse(data);

        Assert.Equal(0x0300, cs.Part2Capabilities);
        var kept = Assert.Single(cs.Part2Segments);
        Assert.Equal(0xFF74, kept.Key);
    }

    /// <summary>Without Rsiz's Part 2 bit a Part 2 marker is just an unknown segment, skipped, not kept.</summary>
    [Fact]
    public void APart2MarkerWithoutThePart2Bit_IsSkipped()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.Segment(0xFF74, 0, 0, 0),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        JpxCodestream cs = Parse(data);

        Assert.Empty(cs.Part2Segments);
        Assert.Equal(0, cs.Part2Capabilities);
    }

    /// <summary>A CAP segment with no capability bit set asks for nothing, and is accepted — the control for the CAP refusal.</summary>
    [Fact]
    public void ACapWithNoCapabilities_IsAccepted()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Segment(0xFF50, 0, 0, 0, 0), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.Equal(1, Parse(data).Main.Coding.Layers);
    }

    // ── refusals, each for its own reason ─────────────────────────────────────

    public static IEnumerable<object[]> Refusals()
    {
        byte[] tp = J.TilePart(0, 0, Array.Empty<byte>(), Data5);
        byte[] With(byte[] siz, params byte[][] header) => J.Concat(J.Soc, siz, J.Concat(header), tp, J.Eoc);
        byte[] Body(params byte[][] header) => With(J.Siz64(), header);

        // Through the byte-array entry point a missing SOC means no codestream was found at all; the
        // ranged entry point, given the range, says what is wrong with it (the test after this one).
        yield return Case("no SOC", J.Concat(J.Siz64(), J.Cod(), J.QcdNone(), tp, J.Eoc), "no JPEG 2000 codestream");
        yield return Case("SIZ not first", J.Concat(J.Soc, J.Cod(), J.Siz64(), J.QcdNone(), tp, J.Eoc), "SOC is not followed by a SIZ");
        yield return Case("SIZ length", With(J.Siz(64, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1) }, lengthOverride: 40), J.Cod(), J.QcdNone()), "SIZ is 40 bytes long");
        yield return Case("no components", With(J.Siz(64, 64, 0, 0, 64, 64, 0, 0, Array.Empty<(int, int, int)>()), J.Cod(), J.QcdNone()), "declares 0 components");
        yield return Case("39-bit samples", With(J.Siz(64, 64, 0, 0, 64, 64, 0, 0, new[] { (0x26, 1, 1) }), J.Cod(), J.QcdNone()), "39-bit samples");
        yield return Case("zero separation", With(J.Siz(64, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 0, 1) }), J.Cod(), J.QcdNone()), "sample separation of 0");
        yield return Case("empty image", With(J.Siz(10, 64, 10, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1) }), J.Cod(), J.QcdNone()), "image area is empty");
        yield return Case("tile offset past image", With(J.Siz(64, 64, 0, 0, 64, 64, 5, 0, new[] { (0x07, 1, 1) }), J.Cod(), J.QcdNone()), "(T.800 B-3)");
        // On the boundary: the first tile ends exactly where the image begins. A tile ending short of
        // it is refused by either comparison; this case is what tells <= from <.
        yield return Case("first tile outside image", With(J.Siz(200, 64, 100, 0, 100, 64, 0, 0, new[] { (0x07, 1, 1) }), J.Cod(), J.QcdNone()), "(T.800 B-4)");
        yield return Case("tile count", With(J.Siz(65536, 65536, 0, 0, 1, 1, 0, 0, new[] { (0x07, 1, 1) }), J.Cod(), J.QcdNone()), "4294967296 tiles (65536x65536); the limit is 65535");
        yield return Case("no COD", Body(J.QcdNone()), "no COD");
        yield return Case("no QCD", Body(J.Cod()), "no QCD");
        yield return Case("second COD", Body(J.Cod(), J.Cod(), J.QcdNone()), "second COD");
        yield return Case("progression order 5", Body(J.Cod(order: 5), J.QcdNone()), "progression order 5");
        yield return Case("no layers", Body(J.Cod(layers: 0), J.QcdNone()), "0 quality layers");
        // MCT on a 1-component image is tolerated: MctOnFewerThanThreeComponents_IsIgnoredAndReported.
        yield return Case("33 levels", Body(J.Cod(levels: 33), J.QcdNone()), "33 decomposition levels");
        yield return Case("code-block offset 9", Body(J.Cod(xcbValue: 9), J.QcdNone()), "exponent offset of 9");
        yield return Case("code-block area", Body(J.Cod(xcbValue: 6, ycbValue: 4), J.QcdNone()), "limits xcb + ycb to 12");
        yield return Case("wavelet 2", Body(J.Cod(transform: 2), J.QcdNone()), "wavelet transformation 2");
        yield return Case("zero precinct above level 0", Body(J.Cod(levels: 1, precincts: new byte[] { 0x00, 0x00 }), J.QcdNone()), "precinct exponent of 0 at resolution level 1");
        yield return Case("COD too long", Body(J.Segment(0xFF52, 0, 0, 0, 1, 0, 1, 4, 4, 0, 1, 0x77), J.QcdNone()), "coding-style parameters are 6 bytes where they need 5");
        yield return Case("quantisation style 3", Body(J.Cod(), J.Qcd(0x03, 0x40)), "quantisation style 3");
        yield return Case("derived with two steps", Body(J.Cod(), J.Qcd(0x01, 0, 0, 0, 0)), "derived but signals 2");
        yield return Case("ROI style 1", Body(J.Cod(), J.QcdNone(), J.Rgn(0, 3, style: 1)), "ROI style 1");
        yield return Case("COC names a missing component", Body(J.Cod(), J.QcdNone(), J.Coc(5)), "names component 5");
        yield return Case("marker past the end", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), new byte[] { 0xFF, 0x64, 0x01, 0x00 }), "past the end of the codestream");
        yield return Case("no tile-parts", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.Eoc), "no tile-parts");
        yield return Case("tile index", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(3, 0, Array.Empty<byte>(), Data5), J.Eoc), "names tile 3");
        yield return Case("Psot below 14", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5, psot: 10), J.Eoc), "declares 10 bytes");
        yield return Case("Lsot 11", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5, lsot: 11), J.Eoc), "fixes it at 10");
        yield return Case("tile-part out of order", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 1, Array.Empty<byte>(), Data5), J.Eoc), "tile-part 1 appears where tile-part 0 belongs");
        yield return Case("differing COD in a later tile-part", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), tp, J.TilePart(0, 1, J.Cod(levels: 2), Data5), J.Eoc), "COD appears in tile-part 1, differing from the one in effect");
        yield return Case("COD in a later tile-part differing from the tile's own", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(levels: 2), J.TilePart(0, 0, J.Cod(levels: 2), Data5), J.TilePart(0, 1, J.Cod(), Data5), J.Eoc), "COD appears in tile-part 1, differing from the one in effect");
        yield return Case("differing QCC in a later tile-part", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), tp, J.TilePart(0, 1, J.Qcc(0, false, 0x40, 0x40, 0x40, 0x40, 0x40), Data5), J.Eoc), "QCC appears in tile-part 1, differing from the one in effect");
        byte[] poc = J.Poc(false, (0, 0, 1, 33, 0, 1));
        yield return Case("POC in a later tile-part without one in the first", J.Concat(J.Soc, J.Siz64(3), J.Cod(), J.QcdNone(), tp, J.TilePart(0, 1, poc, Data5), J.Eoc), "whose first tile-part has none");
        yield return Case("two POCs in one later tile-part", J.Concat(J.Soc, J.Siz64(3), J.Cod(), J.QcdNone(), J.TilePart(0, 0, poc, Data5), J.TilePart(0, 1, J.Concat(poc, poc), Data5), J.Eoc), "has a second POC");
        yield return Case("PPM and PPT", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.Segment(0xFF60, 0, 1), J.TilePart(0, 0, J.Segment(0xFF61, 0, 1), Data5), J.Eoc), "both PPM and PPT");
        // Bytes between tile-parts that are not an SOT are tolerated: JunkBetweenTileParts_IsSkippedAndReported.
        yield return Case("Part 2 decomposition style", With(J.Siz64(rsiz: 0x8020), J.Cod(), J.QcdNone()), "arbitrary decomposition style");
        yield return Case("CAP Pcap15 without Ccap15", Body(J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00), J.Cod(), J.QcdNone()), "without its Ccap15 field");
        yield return Case("CAP Ccap15 reserved", Body(J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x40, 0x00), J.Cod(), J.QcdNone()), "bits 14 and 15 of 01 are reserved");
        yield return Case("CAP Pcap1", Body(J.Segment(0xFF50, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00), J.Cod(), J.QcdNone()), "capabilities this library does not decode: Pcap1");
        // Review of 15a–15f (2026-09-24), syntax rows 1, 3, 4 and 13.
        yield return Case("TLM in a tile-part header", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, J.Segment(0xFF55, 0, 0x40, 0, 0, 0, 0, 0, 0), Data5), J.Eoc), "TLM appears in a tile-part header");
        yield return Case("PLM in a tile-part header", J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, J.Segment(0xFF57, 0, 1, 5), Data5), J.Eoc), "PLM appears in a tile-part header");
        // ITU-T T.814 §A.3.2: CAP decides what code-block style bits 6 and 7 mean. HTONLY reads them as HT,
        // MIXED as either coder (HtCodeBlocksAndTheirCapabilityAreAccepted); Table A.4 reserves bypass and
        // termination on each pass under MIXED, which §A.4 forbids its Part 1 blocks.
        yield return Case("MIXED code-blocks with selective bypass", Body(J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0xC0, 0x00), J.Cod(style: 0xC1), J.QcdNone()), "which ITU-T T.814 Table A.4 reserves");
        yield return Case("MIXED code-blocks with termination on each pass", Body(J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0xC0, 0x00), J.Cod(style: 0xC4), J.QcdNone()), "which ITU-T T.814 Table A.4 reserves");
        yield return Case("code-block style bits 6 and 7 under an HTDECLARED CAP", Body(J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x80, 0x00), J.Cod(style: 0xC0), J.QcdNone()), "declares the HTDECLARED set, where bit 7 is 0");
        yield return Case("code-block style bits 6 and 7 with no CAP", Body(J.Cod(style: 0xC0), J.QcdNone()), "with no CAP's Ccap15");
        yield return Case("code-block style bit 7", Body(J.Cod(style: 0x80), J.QcdNone()), "bit 7 without bit 6");
        yield return Case("mct 3 with the Part 2 capability", With(J.Siz64(3, rsiz: 0x8000 | 0x0100), J.Cod(mct: 3), J.QcdNone()), "combining Annex G's with a Part 2 one");
        yield return Case("a component with no samples", With(J.Siz(2, 64, 1, 0, 2, 64, 0, 0, new[] { (0x07, 2, 1) }), J.Cod(), J.QcdNone()), "component 0 has no samples");
    }

    /// <summary>
    /// HTJ2K's acceptance controls (ITU-T T.814 §A.3, §A.4): code-block style bit 6 alone is read as
    /// HT code-blocks, keeping the vertically causal bit; a CAP with only Pcap15 and a Ccap15 Table A.2
    /// defines is accepted and its Ccap15 kept (0x0020: HTONLY, irreversible transforms allowed).
    /// </summary>
    [Fact]
    public void HtCodeBlocksAndTheirCapabilityAreAccepted()
    {
        JpxCodestream ht = Parse(J.Concat(J.Soc, J.Siz64(), J.Cod(style: 0x48), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc));
        Assert.Equal(JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.VerticallyCausal, ht.CodingFor(0).Component.CodeBlockStyle);
        Assert.Equal(-1, ht.HtCapabilities);

        JpxCodestream cap = Parse(J.Concat(J.Soc, J.Siz64(), J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x00, 0x20), J.Cod(style: 0x40), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc));
        Assert.Equal(0x0020, cap.HtCapabilities);
        Assert.Equal(JpxCodeBlockStyle.HighThroughput, cap.CodingFor(0).Component.CodeBlockStyle);

        // §A.3.2's NOTE: an HTONLY codestream may still set bits 6 and 7 — every block is HT, Table
        // A.4 read as Table A.3: vertical causality kept (COD and COC alike), the Part 1 bits dropped.
        byte[] htOnlyCap = J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00);
        JpxCodestream both = Parse(J.Concat(J.Soc, J.Siz64(3), htOnlyCap, J.Cod(style: 0xFF), J.QcdNone(), J.Coc(1, style: 0xF7),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc));
        Assert.Equal(JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.VerticallyCausal, both.ComponentCodingFor(0, 0).CodeBlockStyle);
        Assert.Equal(JpxCodeBlockStyle.HighThroughput, both.ComponentCodingFor(0, 1).CodeBlockStyle);

        // §A.4: under a MIXED CAP, bits 6 and 7 keep the Part 1 bits Table A.4 lets the Part 1 blocks use
        // (reset, vertical causality, predictable termination, segmentation symbols); bit 6 alone is HT only.
        byte[] mixedCap = J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0xC0, 0x00);
        JpxCodestream mixed = Parse(J.Concat(J.Soc, J.Siz64(2), mixedCap, J.Cod(style: 0xFA), J.QcdNone(), J.Coc(1, style: 0x40),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc));
        Assert.Equal(JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.Mixed | JpxCodeBlockStyle.ResetContexts | JpxCodeBlockStyle.VerticallyCausal
            | JpxCodeBlockStyle.PredictableTermination | JpxCodeBlockStyle.SegmentationSymbols, mixed.ComponentCodingFor(0, 0).CodeBlockStyle);
        Assert.Equal(JpxCodeBlockStyle.HighThroughput, mixed.ComponentCodingFor(0, 1).CodeBlockStyle);
    }

    /// <summary>
    /// The acceptance controls of the review's refusals: a TLM in the main header is skipped (and a
    /// PLT in a tile-part header is read, its Iplt value kept); code-block style bits 0–5 and
    /// <c>mct</c> 2 under the Part 2 capability are accepted; a sub-sampled component with one
    /// column of samples is accepted where one with none is refused.
    /// </summary>
    [Fact]
    public void TheReviewRefusals_HaveAcceptanceControls()
    {
        byte[] tlmInMain = J.Concat(J.Soc, J.Siz64(), J.Segment(0xFF55, 0, 0x40, 0, 0, 0, 0, 0, 0), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, J.Segment(0xFF58, 0, 0x85, 0x05), Data5), J.Eoc);
        JpxTilePart part = Assert.Single(Parse(tlmInMain).Tiles[0].Parts);
        Assert.Equal(new long[] { (5 << 7) | 5 }, part.PacketLengths);   // Iplt: two 7-bit groups, 0x85 0x05

        Assert.Equal((JpxCodeBlockStyle)0x3F, Parse(J.Concat(J.Soc, J.Siz64(), J.Cod(style: 0x3F), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc)).CodingFor(0).Component.CodeBlockStyle);

        JpxCodestream part2 = Parse(J.Concat(J.Soc, J.Siz64(3, rsiz: 0x8000 | 0x0100), J.Cod(mct: 2), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc));
        Assert.False(part2.CodingFor(0).MultipleComponentTransform);

        JpxCodestream oneColumn = Parse(J.Concat(J.Soc, J.Siz(3, 64, 1, 0, 3, 64, 0, 0, new[] { (0x07, 2, 1) }), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc));
        Assert.Equal(1L, JpxGeometry.Component(oneColumn.Size, 0).Width);   // ⌈3/2⌉ − ⌈1/2⌉
    }

    /// <summary>
    /// A <c>Psot</c> past the end of a file that nonetheless ends in EOC is the encoder's overshoot, not
    /// a truncation: the tile-part is clamped short of the EOC, the EOC is seen, and the overshoot is
    /// reported (review of 15a–15f, syntax row 2).
    /// </summary>
    [Fact]
    public void APsotOvershootingAFileThatEndsInEoc_StopsBeforeTheEoc()
    {
        byte[] data = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5, psot: 12 + 2 + 5 + 1), J.Eoc);
        JpxCodestream cs = Parse(data);

        Assert.Equal(data.Length - 2, cs.Tiles[0].Parts[0].Data.End);
        Assert.True(cs.EndOfCodestreamSeen);
        string deviation = Assert.Single(cs.Deviations);
        Assert.Contains("declares 20 bytes, 1 past the EOC; clamped to it", deviation, StringComparison.Ordinal);
    }

    private static object[] Case(string name, byte[] data, string reason) => new object[] { name, data, reason };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void EachViolation_IsRefusedForItsOwnReason(string name, byte[] data, string expectedReason)
    {
        Assert.False(JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out string reason), $"{name}: accepted");
        Assert.Null(cs);
        Assert.Contains(expectedReason, reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// POC may appear in any tile-part header of a tile that opened with one, and a later tile-part's
    /// progression volumes follow the earlier ones in order (T.800 §A.6.6, §B.12.3 Figure B.15b) —
    /// the layout of conformance codestreams p0_07 and e1_colr.
    /// </summary>
    [Fact]
    public void PocInALaterTilePart_AppendsItsVolumes()
    {
        byte[] first = J.Poc(false, (0, 0, 1, 33, 0, 1));
        byte[] later = J.Poc(false, (0, 0, 2, 33, 0, 4));
        byte[] appended = J.Concat(J.Soc, J.Siz64(3), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, first, Data5), J.TilePart(0, 1, later, Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(appended, out JpxCodestream cs, out string reason), reason);
        List<JpxProgressionChange> changes = cs.ProgressionChangesFor(0);
        Assert.Equal(2, changes.Count);
        Assert.Equal((1, JpxProgressionOrder.Rlcp), (changes[0].LayerEnd, changes[0].Order));
        Assert.Equal((2, JpxProgressionOrder.Cprl), (changes[1].LayerEnd, changes[1].Order));

        // Control: a later tile-part without a POC adds nothing.
        byte[] plain = J.Concat(J.Soc, J.Siz64(3), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, first, Data5), J.TilePart(0, 1, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(plain, out cs, out reason), reason);
        Assert.Single(cs.ProgressionChangesFor(0));
    }

    /// <summary>
    /// COD's component-transformation flag on an image with fewer than three components is
    /// non-conforming (T.800 G.2) but unambiguous — there is nothing to transform — and greyscale
    /// codestreams in the wild carry it (non-regression 123.j2c, bug.j2c). It is ignored and reported.
    /// </summary>
    [Fact]
    public void MctOnFewerThanThreeComponents_IsIgnoredAndReported()
    {
        byte[] tp = J.TilePart(0, 0, Array.Empty<byte>(), Data5);
        byte[] grey = J.Concat(J.Soc, J.Siz64(), J.Cod(mct: 1), J.QcdNone(), tp, J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(grey, out JpxCodestream cs, out string reason), reason);
        Assert.False(cs.CodingFor(0).MultipleComponentTransform);
        string deviation = Assert.Single(cs.Deviations);
        Assert.Contains("components 0-2 of a 1-component image", deviation, StringComparison.Ordinal);

        // Control: three components keep the flag, and nothing is reported.
        byte[] colour = J.Concat(J.Soc, J.Siz64(3), J.Cod(mct: 1), J.QcdNone(), tp, J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(colour, out cs, out reason), reason);
        Assert.True(cs.CodingFor(0).MultipleComponentTransform);
        Assert.Empty(cs.Deviations);
    }

    /// <summary>
    /// COD, COC, QCD, QCC and RGN belong to a tile's first tile-part header, but an encoder that
    /// restates the main header in every tile-part (non-regression db11217111510058.jp2) changes
    /// nothing: a byte-identical restatement is ignored and reported. One that differs is refused
    /// (<see cref="Refusals"/>).
    /// </summary>
    [Fact]
    public void RestatedCodingSegmentsInLaterTileParts_AreIgnoredAndReported()
    {
        byte[] tp = J.TilePart(0, 0, Array.Empty<byte>(), Data5);
        byte[] restated = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), tp,
            J.TilePart(0, 1, J.Concat(J.Cod(), J.QcdNone()), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(restated, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(2, cs.Tiles[0].Parts.Count);
        Assert.Equal(1, cs.CodingFor(0).Component.DecompositionLevels);
        Assert.Collection(cs.Deviations,
            d => Assert.StartsWith("COD is restated in tile-part 1", d, StringComparison.Ordinal),
            d => Assert.StartsWith("QCD is restated in tile-part 1", d, StringComparison.Ordinal));

        // The comparison is against the tile's own first tile-part when it has the segment: a tile
        // that set two levels there may restate two levels although the main header says one.
        byte[] own = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(levels: 2),
            J.TilePart(0, 0, J.Cod(levels: 2), Data5), J.TilePart(0, 1, J.Cod(levels: 2), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(own, out cs, out reason), reason);
        Assert.Equal(2, cs.CodingFor(0).Component.DecompositionLevels);
        Assert.Single(cs.Deviations);

        // The identity is the segment and its component: with COCs for components 0 and 1 in the
        // main header, a tile-part restating component 0's is matched against component 0's.
        byte[] perComponent = J.Concat(J.Soc, J.Siz64(2), J.Cod(), J.Coc(0), J.Coc(1, levels: 2), J.QcdNone(levels: 2), tp,
            J.TilePart(0, 1, J.Coc(0), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(perComponent, out cs, out reason), reason);
        Assert.StartsWith("COC is restated in tile-part 1", Assert.Single(cs.Deviations), StringComparison.Ordinal);
        Assert.Equal(2, cs.ComponentCodingFor(0, 1).DecompositionLevels);
    }

    /// <summary>
    /// Bytes between tile-parts that are neither an SOT nor the EOC — non-regression issue226.j2k
    /// has two before its EOC — are skipped to the next tile boundary and reported; without one the
    /// codestream ends there, as a truncated one does.
    /// </summary>
    [Fact]
    public void JunkBetweenTileParts_IsSkippedAndReported()
    {
        byte[] tp = J.TilePart(0, 0, Array.Empty<byte>(), Data5);
        byte[] junk = { 0x80, 0x80 };

        byte[] beforeEoc = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), tp, junk, J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(beforeEoc, out JpxCodestream cs, out string reason), reason);
        Assert.True(cs.EndOfCodestreamSeen);
        string deviation = Assert.Single(cs.Deviations);
        Assert.StartsWith($"2 bytes at offset {beforeEoc.Length - 4} are not a tile-part", deviation, StringComparison.Ordinal);
        Assert.EndsWith($"EOC at offset {beforeEoc.Length - 2}", deviation, StringComparison.Ordinal);

        // A stray marker segment is junk like any other: skipped by content, and the next tile-part found.
        byte[] beforeSot = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), tp, J.Segment(0xFF64, 1, 2, 3),
            J.TilePart(0, 1, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(beforeSot, out cs, out reason), reason);
        Assert.Equal(2, cs.Tiles[0].Parts.Count);
        Assert.StartsWith("7 bytes at offset", Assert.Single(cs.Deviations), StringComparison.Ordinal);

        // Junk to the end: what was read decodes; the codestream is unterminated.
        byte[] toEnd = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), tp, junk, junk);
        Assert.True(JpxCodestreamParser.TryParse(toEnd, out cs, out reason), reason);
        Assert.False(cs.EndOfCodestreamSeen);
        Assert.Single(cs.Tiles[0].Parts);
        Assert.Contains("4 bytes at offset", Assert.Single(cs.Deviations), StringComparison.Ordinal);
        Assert.Contains("no SOT or EOC follows", cs.Deviations[0], StringComparison.Ordinal);

        // Control: a clean codestream reports nothing.
        Assert.True(JpxCodestreamParser.TryParse(J.Minimal(), out cs, out reason), reason);
        Assert.Empty(cs.Deviations);
    }

    /// <summary>
    /// Equation B-4's boundary: a first tile ending exactly where the image begins holds nothing
    /// (refused: <see cref="Refusals"/>); one column wider it holds a column, and is accepted.
    /// </summary>
    [Fact]
    public void AFirstTile_HoldingOneColumn_IsAccepted()
    {
        byte[] tp = J.TilePart(0, 0, Array.Empty<byte>(), Data5);
        byte[] oneColumn = J.Concat(J.Soc, J.Siz(200, 64, 100, 0, 101, 64, 0, 0, new[] { (0x07, 1, 1) }),
            J.Cod(), J.QcdNone(), tp, J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(oneColumn, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(1L, JpxGeometry.Tile(cs.Size, 0).Width);
        Assert.Equal(2L, cs.Size.NumXTiles);   // ⌈200 / 101⌉
    }

    /// <summary>Given an explicit codestream range, a missing SOC is named as such (T.800 §A.4.1).</summary>
    [Fact]
    public void TheRangedEntryPoint_NamesAMissingSoc()
    {
        byte[] noSoc = J.Concat(J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.False(JpxCodestreamParser.TryParse(noSoc, 0, noSoc.Length, out _, out string reason));
        Assert.Contains("does not start with an SOC", reason, StringComparison.Ordinal);

        byte[] valid = J.Minimal();
        Assert.True(JpxCodestreamParser.TryParse(valid, 0, valid.Length, out _, out reason), reason);
    }

    /// <summary>
    /// <see cref="PdfLimits.MaxJpxTiles"/> is read at parse time, not captured: a 2x2-tile codestream
    /// passes at 4 and is refused at 3. That is the forwarding the tile-count refusal above depends on.
    /// </summary>
    [Fact]
    public void TheTileCeiling_IsReadFromPdfLimits()
    {
        byte[] data = J.Concat(J.Soc, J.Siz(128, 128, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1) }), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        try
        {
            PdfLimits.MaxJpxTiles = 4;
            Assert.Equal(4, Parse(data).Size.NumTiles);

            PdfLimits.MaxJpxTiles = 3;
            Assert.False(JpxCodestreamParser.TryParse(data, out _, out string reason));
            Assert.Contains("4 tiles (2x2); the limit is 3", reason, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
