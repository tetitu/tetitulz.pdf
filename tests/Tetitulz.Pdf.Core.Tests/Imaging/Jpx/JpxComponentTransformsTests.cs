// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxComponentTransforms"/> against T.800 Annex G, with the forward transforms written
/// out here as the oracle; <see cref="JpxMultipleComponentTransform"/> and
/// <see cref="JpxNonLinearity"/> against T.801 Annex J, Annex K and §M.9.2.3, on marker segments
/// built byte by byte from Tables A.29 to A.46.
/// </summary>
public sealed class JpxComponentTransformsTests
{
    private static readonly byte[] Data5 = { 1, 2, 3, 4, 5 };

    // ---- Annex G ---------------------------------------------------------------------------------

    /// <summary>The forward RCT, G-3 to G-5: Y0 = ⌊(R + 2G + B) / 4⌋, Y1 = B − G, Y2 = R − G.</summary>
    private static (int Y0, int Y1, int Y2) ForwardRct(int r, int g, int b) =>
        ((int)Math.Floor((r + 2 * g + b) / 4.0), b - g, r - g);

    /// <summary>
    /// A hand triple: R 200, G 100, B 50 gives Y0 112, Y1 −50, Y2 100, and the inverse gives the
    /// triple back — the roles of Y1 and Y2 are the classic mistake, so R ≠ B here. Then a triple
    /// whose (Y1 + Y2) / 4 is negative and fractional (−42.5), which ⌊·⌋ takes to −43 and a
    /// truncation to −42.
    /// </summary>
    [Fact]
    public void InverseRct_UndoesTheForwardRctOfG3ToG5()
    {
        Assert.Equal((112, -50, 100), ForwardRct(200, 100, 50));
        int[] y0 = { 112 }, y1 = { -50 }, y2 = { 100 };
        JpxComponentTransforms.InverseRct(y0, y1, y2);
        Assert.Equal((200, 100, 50), (y0[0], y1[0], y2[0]));

        (int a, int b, int c) = ForwardRct(10, 100, 20);
        Assert.Equal((57, -80, -90), (a, b, c));
        y0 = new[] { a }; y1 = new[] { b }; y2 = new[] { c };
        JpxComponentTransforms.InverseRct(y0, y1, y2);
        Assert.Equal((10, 100, 20), (y0[0], y1[0], y2[0]));
    }

    /// <summary>Every triple of a small cube round-trips exactly, negatives included (the RCT is lossless).</summary>
    [Fact]
    public void InverseRct_IsExactOverACubeOfTriples()
    {
        var rs = new List<int>(); var gs = new List<int>(); var bs = new List<int>();
        var y0 = new List<int>(); var y1 = new List<int>(); var y2 = new List<int>();
        for (int r = -128; r <= 127; r += 15)
            for (int g = -128; g <= 127; g += 17)
                for (int b = -128; b <= 127; b += 19)
                {
                    rs.Add(r); gs.Add(g); bs.Add(b);
                    (int a, int c, int d) = ForwardRct(r, g, b);
                    y0.Add(a); y1.Add(c); y2.Add(d);
                }
        int[] c0 = y0.ToArray(), c1 = y1.ToArray(), c2 = y2.ToArray();
        JpxComponentTransforms.InverseRct(c0, c1, c2);
        Assert.Equal(rs, c0);
        Assert.Equal(gs, c1);
        Assert.Equal(bs, c2);
        Assert.True(rs.Count > 4000);
    }

    /// <summary>
    /// G-12 to G-14 on hand values: Y 124.2, Cb −41.876, Cr 54.0655 (the forward of RGB 200, 100, 50
    /// by G-9 to G-11) give R 199.9998, G 100.0005, B 49.9957 — each constant pinned to 1e-3, where a
    /// change in the fourth decimal of any of the four moves a result by more than 4e-3.
    /// </summary>
    [Fact]
    public void InverseIct_FollowsG12ToG14()
    {
        double[] y = { 124.2, -4.48 }, cb = { -41.876, 70.2498 }, cr = { 54.0655, -68.131 };
        JpxComponentTransforms.InverseIct(y, cb, cr);
        Assert.InRange(y[0], 199.9998 - 1e-3, 199.9998 + 1e-3);
        Assert.InRange(cb[0], 100.0005 - 1e-3, 100.0005 + 1e-3);
        Assert.InRange(cr[0], 49.9957 - 1e-3, 49.9957 + 1e-3);
        Assert.InRange(y[1], -99.9997 - 1e-3, -99.9997 + 1e-3);
        Assert.InRange(cb[1], 20.0000 - 1e-3, 20.0000 + 1e-3);
        Assert.InRange(cr[1], 120.0026 - 1e-3, 120.0026 + 1e-3);

        // The round trip through the forward matrix (G-9 to G-11), written here: within 0.01, the
        // two matrices being only approximately inverse at the printed precision.
        double r = 37, g = 211, b = 90;
        double[] fy = { 0.299 * r + 0.587 * g + 0.114 * b };
        double[] fcb = { -0.16875 * r - 0.331260 * g + 0.5 * b };
        double[] fcr = { 0.5 * r - 0.41869 * g - 0.08131 * b };
        JpxComponentTransforms.InverseIct(fy, fcb, fcr);
        Assert.InRange(fy[0], r - 0.01, r + 0.01);
        Assert.InRange(fcb[0], g - 0.01, g + 0.01);
        Assert.InRange(fcr[0], b - 0.01, b + 0.01);
    }

    /// <summary>
    /// G-2 on an unsigned 8-bit component adds 128: −3 becomes 125, and 200 and −200 clip to 255 and
    /// 0. A signed component is not shifted (G.1.2 applies to unsigned components only): −3 stays −3,
    /// and 200 and −200 clip to 127 and −128.
    /// </summary>
    [Fact]
    public void LevelShiftAndClamp_ShiftsUnsignedComponentsOnlyAndClipsBothEnds()
    {
        int[] unsigned = { -3, 200, -200, 0, 127 };
        JpxComponentTransforms.LevelShiftAndClamp(unsigned, 8, isSigned: false);
        Assert.Equal(new[] { 125, 255, 0, 128, 255 }, unsigned);

        int[] signed = { -3, 200, -200, 0, 127 };
        JpxComponentTransforms.LevelShiftAndClamp(signed, 8, isSigned: true);
        Assert.Equal(new[] { -3, 127, -128, 0, 127 }, signed);

        // The clip alone (after a Part 2 transformation): no shift for anyone.
        int[] clipped = { -3, 4096, 4095, 12 };
        JpxComponentTransforms.Clamp(clipped, 12, isSigned: false);
        Assert.Equal(new[] { 0, 4095, 4095, 12 }, clipped);

        Assert.Equal((0, 4095), (JpxComponentTransforms.MinSample(12, false), JpxComponentTransforms.MaxSample(12, false)));
        Assert.Equal((-2048, 2047), (JpxComponentTransforms.MinSample(12, true), JpxComponentTransforms.MaxSample(12, true)));
        Assert.Equal((0, int.MaxValue), (JpxComponentTransforms.MinSample(31, false), JpxComponentTransforms.MaxSample(31, false)));
        // T.800's deepest component, 38 bits, has a range no int holds; past it is refused.
        Assert.Equal((-(1L << 37), (1L << 37) - 1), (JpxComponentTransforms.MinSample(38, true), JpxComponentTransforms.MaxSample(38, true)));
        Assert.Equal((0, (1L << 38) - 1), (JpxComponentTransforms.MinSample(38, false), JpxComponentTransforms.MaxSample(38, false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxComponentTransforms.MaxSample(39, false));
        Assert.True(JpxComponentTransforms.IsWide(32) && !JpxComponentTransforms.IsWide(31));

        // The shift and clip on a wide component's plane, in double, exact at 38 bits.
        double[] wide = { -3, (1L << 38) + 5, -(1L << 37), 0, (1L << 37) - 1 };
        JpxComponentTransforms.LevelShiftAndClamp(wide, 38, isSigned: false);
        Assert.Equal(new double[] { (1L << 37) - 3, (1L << 38) - 1, 0, 1L << 37, (1L << 38) - 1 }, wide);
    }

    /// <summary>Half away from zero: 2.5 → 3, −2.5 → −3, 2.4 → 2, −0.5 → −1; a value past the int range saturates.</summary>
    [Fact]
    public void RoundToSamples_RoundsHalfAwayFromZero()
    {
        var samples = new int[6];
        JpxComponentTransforms.RoundToSamples(new[] { 2.5, -2.5, 2.4, -0.5, 3e9, -3e9 }, samples);
        Assert.Equal(new[] { 3, -3, 2, -1, int.MaxValue, int.MinValue }, samples);
        Assert.Equal(0, JpxComponentTransforms.RoundAndClamp(double.NaN, -5, 5));
    }

    /// <summary>G.2: three components sharing separation and depth; fewer, or a sub-sampled or deeper one, is refused with the reason.</summary>
    [Fact]
    public void CanTransformFirstThree_RequiresThreeAlikeComponents()
    {
        Assert.True(JpxComponentTransforms.CanTransformFirstThree(Siz(new[] { (0x07, 1, 1), (0x07, 1, 1), (0x07, 1, 1), (0x0B, 2, 2) }), out string reason), reason);
        Assert.False(JpxComponentTransforms.CanTransformFirstThree(Siz(new[] { (0x07, 1, 1), (0x07, 1, 1) }), out reason));
        Assert.Contains("2-component", reason, StringComparison.Ordinal);
        Assert.False(JpxComponentTransforms.CanTransformFirstThree(Siz(new[] { (0x07, 1, 1), (0x07, 2, 1), (0x07, 1, 1) }), out reason));
        Assert.Contains("sub-sampled 2x1", reason, StringComparison.Ordinal);
        Assert.False(JpxComponentTransforms.CanTransformFirstThree(Siz(new[] { (0x07, 1, 1), (0x07, 1, 1), (0x0B, 1, 1) }), out reason));
        Assert.Contains("12-bit", reason, StringComparison.Ordinal);
    }

    private static JpxImageSize Siz((int Ssiz, int XR, int YR)[] components)
    {
        byte[] siz = J.Siz(64, 64, 0, 0, 64, 64, 0, 0, components);
        Assert.True(JpxImageSize.TryRead(siz, 2, siz.Length, out JpxImageSize size, out string reason), reason);
        return size;
    }

    // ---- The COD's SGcod byte under Part 2 ---------------------------------------------------------

    /// <summary>
    /// T.801 Table A.8: value 1 is Annex G's RCT/ICT; value 2 is the array-based Part 2
    /// transformation, which MCO, MCC and MCT describe — not the RCT/ICT, which a driver reading
    /// <see cref="JpxCodingDefault.MultipleComponentTransform"/> would otherwise apply; value 4, the
    /// wavelet-based one, is outside the JPX baseline and refused by name.
    /// </summary>
    [Fact]
    public void CodValue2_IsThePart2TransformationAndNotAnnexGs()
    {
        byte[] part2 = J.Concat(J.Soc, J.Siz64(3, rsiz: 0x8100), J.Cod(mct: 2), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(part2, out JpxCodestream cs, out string reason), reason);
        Assert.False(cs.CodingFor(0).MultipleComponentTransform);

        byte[] part1 = J.Concat(J.Soc, J.Siz64(3, rsiz: 0x8100), J.Cod(mct: 1), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(part1, out cs, out reason), reason);
        Assert.True(cs.CodingFor(0).MultipleComponentTransform);

        byte[] wavelet = J.Concat(J.Soc, J.Siz64(3, rsiz: 0x8100), J.Cod(mct: 4), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.False(JpxCodestreamParser.TryParse(wavelet, out _, out reason));
        Assert.Contains("wavelet-based", reason, StringComparison.Ordinal);
    }

    // ---- T.801 marker segments, from the tables ----------------------------------------------------

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] U32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>CBD (Table A.29): Ncbd, then one BDcbd per reconstructed component.</summary>
    private static byte[] Cbd(params int[] depths) =>
        J.Segment(0xFF78, J.Concat(U16(depths.Length), depths.Select(d => (byte)d).ToArray()));

    /// <summary>MCT (Table A.32): Zmct, Imct = index | type &lt;&lt; 8 | element type &lt;&lt; 10 (Table A.33), Ymct when Zmct is 0, the elements.</summary>
    private static byte[] Mct(int index, JpxMctArrayType type, int elementType, byte[] elements, int zmct = 0, int ymct = 0)
    {
        int imct = index | ((int)type << 8) | (elementType << 10);
        byte[] head = zmct == 0 ? J.Concat(U16(zmct), U16(imct), U16(ymct)) : J.Concat(U16(zmct), U16(imct));
        return J.Segment(0xFF74, J.Concat(head, elements));
    }

    private static byte[] Floats(params float[] values)
    {
        var p = new List<byte>();
        foreach (float v in values)
        {
            byte[] b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            p.AddRange(b);
        }
        return p.ToArray();
    }

    private static byte[] Doubles(params double[] values)
    {
        var p = new List<byte>();
        foreach (double v in values)
        {
            byte[] b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            p.AddRange(b);
        }
        return p.ToArray();
    }

    private static byte[] Shorts(params int[] values) => J.Concat(values.Select(U16).ToArray());
    private static byte[] Ints(params int[] values) => J.Concat(values.Select(v => U32(v)).ToArray());

    /// <summary>MCC (Table A.34): Zmcc 0, Imcc, Ymcc 0, Qmcc, then the collections.</summary>
    private static byte[] Mcc(int imcc, params byte[][] collections) =>
        J.Segment(0xFF75, J.Concat(U16(0), new[] { (byte)imcc }, U16(0), U16(collections.Length), J.Concat(collections)));

    /// <summary>
    /// One array-based collection: Xmcc (Table A.35), Nmcc and the 8-bit inputs, Mmcc and the
    /// outputs, and the 24-bit Tmcc of Table A.38 — transform array index, offset array index in bits
    /// 8–15, the reversible bit 16.
    /// </summary>
    private static byte[] Collection(int kind, int[] inputs, int[] outputs, int transformIndex, int offsetIndex, bool reversible = false)
    {
        int tmcc = transformIndex | (offsetIndex << 8) | (reversible ? 0x10000 : 0);
        return J.Concat(new[] { (byte)kind }, U16(inputs.Length), inputs.Select(i => (byte)i).ToArray(),
            U16(outputs.Length), outputs.Select(o => (byte)o).ToArray(),
            new[] { (byte)(tmcc >> 16), (byte)(tmcc >> 8), (byte)tmcc });
    }

    /// <summary>A wavelet-based collection (Xmcc 3): a 32-bit Tmcc (Table A.39) and Omcc follow the outputs.</summary>
    private static byte[] WaveletCollection(int[] inputs, int[] outputs) =>
        J.Concat(new byte[] { 3 }, U16(inputs.Length), inputs.Select(i => (byte)i).ToArray(),
            U16(outputs.Length), outputs.Select(o => (byte)o).ToArray(), U32(0x00000001), U32(0));

    /// <summary>MCO (Table A.40): Nmco, then one Imco per stage.</summary>
    private static byte[] Mco(params int[] imco) =>
        J.Segment(0xFF77, J.Concat(new[] { (byte)imco.Length }, imco.Select(i => (byte)i).ToArray()));

    /// <summary>NLT (Table A.41): Cnlt, BDnlt, Tnlt, STnlt.</summary>
    private static byte[] Nlt(int cnlt, int bdnlt, int tnlt, byte[]? stnlt = null) =>
        J.Segment(0xFF76, J.Concat(U16(cnlt), new[] { (byte)bdnlt, (byte)tnlt }, stnlt ?? Array.Empty<byte>()));

    /// <summary>An 8-bit integer with a 16-bit fraction (Table A.45).</summary>
    private static byte[] Fixed(double v)
    {
        int raw = (int)Math.Round(v * 65536);
        return new[] { (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw };
    }

    private static byte[] GammaParams(double e, double s, double t, double a, double b) =>
        J.Concat(Fixed(e), Fixed(s), Fixed(t), Fixed(a), Fixed(b));

    /// <summary>Table A.46: Npoints (the count less one), Dmin, Dmax, PTval, then the values, 1, 2 or 4 bytes each by PTval.</summary>
    private static byte[] LutParams(int pointsField, long dminRaw, long dmaxRaw, int ptval, params int[] values)
    {
        var p = new List<byte>();
        p.AddRange(U16(pointsField)); p.AddRange(U32(dminRaw)); p.AddRange(U32(dmaxRaw)); p.Add((byte)ptval);
        foreach (int v in values)
        {
            if (ptval <= 8) p.Add((byte)v);
            else if (ptval <= 16) p.AddRange(U16(v));
            else p.AddRange(U32(v));
        }
        return p.ToArray();
    }

    /// <summary>The inverse ICT as a Part 2 decorrelation array — rows of J-2 in storage order.</summary>
    private static readonly float[] InverseIctMatrix = { 1, 0, 1.402f, 1, -0.34413f, -0.71414f, 1, 1.772f, 0 };

    /// <summary>
    /// A baseline codestream: three 8-bit components, Rsiz with the Part 2 and MCT bits, COD value 2,
    /// a CBD of three 8-bit components, decorrelation array 1 (float32) holding the inverse ICT, offset
    /// array 1 (int16) of 128s, MCC 1 with one collection over components 0-2 naming both arrays, and
    /// an MCO of that one stage. Each piece can be replaced; an empty array leaves the segment out.
    /// </summary>
    private static byte[] Baseline(byte[]? cbd = null, byte[]? mct = null, byte[]? offsets = null, byte[]? mcc = null,
        byte[]? mco = null, byte[]? extra = null, byte[]? tileHeader = null, byte[]? siz = null, int codMct = 2)
    {
        return J.Concat(J.Soc, siz ?? J.Siz64(3, rsiz: 0x8100), J.Cod(mct: codMct), J.QcdNone(),
            cbd ?? Cbd(7, 7, 7),
            mct ?? Mct(1, JpxMctArrayType.Decorrelation, 2, Floats(InverseIctMatrix)),
            offsets ?? Mct(1, JpxMctArrayType.Offset, 0, Shorts(128, 128, 128)),
            mcc ?? Mcc(1, Collection(1, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, transformIndex: 1, offsetIndex: 1)),
            mco ?? Mco(1),
            extra ?? Array.Empty<byte>(),
            J.TilePart(0, 0, tileHeader ?? Array.Empty<byte>(), Data5), J.Eoc);
    }

    private static JpxCodestream Parse(byte[] data)
    {
        Assert.True(JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out string reason), reason);
        return cs;
    }

    private static JpxMultipleComponentTransform Resolve(byte[] data)
    {
        Assert.True(JpxMultipleComponentTransform.TryResolve(Parse(data), 0, out JpxMultipleComponentTransform transform, out string reason), reason);
        Assert.NotNull(transform);
        return transform;
    }

    private static string RefusalReason(byte[] data)
    {
        Assert.False(JpxMultipleComponentTransform.TryResolve(Parse(data), 0, out _, out string reason));
        return reason;
    }

    // ---- The Part 2 decorrelation transformation ---------------------------------------------------

    /// <summary>
    /// The baseline set resolves: inputs and outputs 0-2, the float32 matrix and int16 offsets as
    /// written, the CBD's depths with the inputs' separation; and J-2 applied to two samples gives
    /// the numbers worked independently (the matrix at float32 precision, plus 128).
    /// </summary>
    [Fact]
    public void BaselineTransformation_ResolvesAndAppliesEquationJ2()
    {
        JpxMultipleComponentTransform t = Resolve(Baseline());

        Assert.Equal(new[] { 0, 1, 2 }, t.Inputs);
        Assert.Equal(new[] { 0, 1, 2 }, t.Outputs);
        Assert.Equal(InverseIctMatrix.Select(v => (double)v), t.Matrix);
        Assert.Equal(new double[] { 128, 128, 128 }, t.Offsets);
        Assert.Equal(3, t.ReconstructedComponents.Length);
        Assert.Equal((8, false, 1, 1), (t.ReconstructedComponents[1].Precision, t.ReconstructedComponents[1].IsSigned, t.ReconstructedComponents[1].XRsiz, t.ReconstructedComponents[1].YRsiz));

        double[][] inputs = { new[] { 100.0, 50.0 }, new[] { -20.0, 10.0 }, new[] { 30.0, -40.0 } };
        double[][] outputs = { new double[2], new double[2], new double[2] };
        t.Apply(inputs, outputs);
        Assert.InRange(outputs[0][0], 270.06 - 1e-3, 270.06 + 1e-3);
        Assert.InRange(outputs[1][0], 213.4584 - 1e-3, 213.4584 + 1e-3);
        Assert.InRange(outputs[2][0], 192.56 - 1e-3, 192.56 + 1e-3);
        Assert.InRange(outputs[0][1], 121.92 - 1e-3, 121.92 + 1e-3);
        Assert.InRange(outputs[1][1], 203.1243 - 1e-3, 203.1243 + 1e-3);
        Assert.InRange(outputs[2][1], 195.72 - 1e-3, 195.72 + 1e-3);
    }

    /// <summary>
    /// Cmcc orders the inputs and Wmcc the outputs (J.2.3.1, J.2.3.3): with inputs (2, 0, 1), outputs
    /// (1, 2, 0), the identity (array index 0) and offsets (10, 20, 30), reconstructed component 1 is
    /// C2 + 10, component 2 is C0 + 20 and component 0 is C1 + 30. A fourth reconstructed component
    /// no output names is left as the caller made it — J.2.3.3's null component.
    /// </summary>
    [Fact]
    public void CollectionLists_PermuteInputsAndOutputs()
    {
        JpxMultipleComponentTransform t = Resolve(Baseline(
            cbd: Cbd(7, 7, 7, 7),
            offsets: Mct(1, JpxMctArrayType.Offset, 1, Ints(10, 20, 30)),
            mcc: Mcc(1, Collection(1, new[] { 2, 0, 1 }, new[] { 1, 2, 0 }, transformIndex: 0, offsetIndex: 1))));

        double[][] inputs = { new[] { 1.0 }, new[] { 2.0 }, new[] { 3.0 } };
        double[][] outputs = { new double[1], new double[1], new double[1], new[] { -7.0 } };
        t.Apply(inputs, outputs);
        Assert.Equal(13.0, outputs[1][0]);
        Assert.Equal(21.0, outputs[2][0]);
        Assert.Equal(32.0, outputs[0][0]);
        Assert.Equal(-7.0, outputs[3][0]);
        Assert.Equal(4, t.ReconstructedComponents.Length);
    }

    /// <summary>
    /// The Part 2 transformation end to end, through <see cref="JpxDecoder.TryDecode"/>: the RGB
    /// fixture's main header gains the Part 2 and MCT capability bits, COD value 2, a CBD of three
    /// 8-bit components, an int16 offset array of 128s, an MCC naming the identity and those
    /// offsets, and an MCO of that stage. The identity plus 128 is what Annex G's level shift would
    /// have done, so every sample must equal the source exactly — the wiring of the transformation
    /// into the spine, which unit tests of <c>Resolve</c> and <c>Apply</c> could not see.
    /// </summary>
    [Fact]
    public void TheBaselineTransformation_DecodesAnRgbFixtureEndToEnd()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Rgb40x24_NoMct_Blocks8;
        byte[] cs = fixture.Codestream;
        int sot = IndexOf(cs, 0xFF, 0x90);
        int cod = IndexOf(cs, 0xFF, 0x52);
        Assert.True(cod > 0 && cod < sot);
        byte[] part2 = J.Concat(Cbd(7, 7, 7), Mct(1, JpxMctArrayType.Offset, 0, Shorts(128, 128, 128)),
            Mcc(1, Collection(1, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, transformIndex: 0, offsetIndex: 1)), Mco(1));
        byte[] spliced = J.Concat(cs.Take(sot).ToArray(), part2, cs.Skip(sot).ToArray());
        spliced[6] = 0x81; spliced[7] = 0x00;   // Rsiz: Part 2, with the multiple component transformation bit
        spliced[cod + 8] = 2;                   // SGcod's transformation byte: the array-based one (T.801 Table A.8)

        Assert.True(JpxDecoder.TryDecode(spliced, 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Equal(0x0100, result.Codestream.Part2Capabilities);
        Assert.Empty(result.Deviations);
        JpxTileSamples tile = Assert.Single(result.Tiles);
        for (int c = 0; c < 3; c++)
            Assert.Equal(fixture.Planes[c].Select(b => (int)b).ToArray(), tile.Components[c].Samples);
        byte[] interleaved = result.Image!.ToInterleaved8Bit();
        for (int i = 0; i < fixture.Width * fixture.Height; i++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(fixture.Planes[c][i], interleaved[i * 3 + c]);

        // Potency: the same splice with offsets of 0 leaves every sample 128 short, so the offsets
        // (and not a level shift) are what placed them.
        byte[] noOffsets = J.Concat(cs.Take(sot).ToArray(),
            J.Concat(Cbd(7, 7, 7), Mct(1, JpxMctArrayType.Offset, 0, Shorts(0, 0, 0)),
                Mcc(1, Collection(1, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, transformIndex: 0, offsetIndex: 1)), Mco(1)),
            cs.Skip(sot).ToArray());
        noOffsets[6] = 0x81; noOffsets[7] = 0x00; noOffsets[cod + 8] = 2;
        Assert.True(JpxDecoder.TryDecode(noOffsets, 0, out JpxDecodeResult shifted, out reason), reason);
        int[] red = shifted.Tiles[0].Components[0].Samples;
        Assert.Contains(red, v => v != 0);
        for (int i = 0; i < red.Length; i++)
            Assert.Equal(Math.Max(0, fixture.Planes[0][i] - 128), red[i]);
    }

    private static int IndexOf(byte[] data, byte first, byte second, int from = 0)
    {
        for (int i = from; i + 1 < data.Length; i++)
            if (data[i] == first && data[i + 1] == second) return i;
        return -1;
    }

    /// <summary>
    /// A non-linearity that changes a component's depth (T.801 K.1.3) must do so in every tile: the
    /// image's planes hold one depth, sized from the first tile. The nine-tile grey fixture with a
    /// sign-magnitude NLT to 12 bits spliced into tile 1's tile-part header alone is refused by
    /// name; the same NLT in the main header applies to every tile and decodes to 12-bit planes.
    /// </summary>
    [Fact]
    public void ALaterTilesNonLinearityWithAnotherDepth_IsRefused_ButOneForEveryTileDecodes()
    {
        byte[] cs = JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel.Codestream;
        // A two-point LUT (Table A.46: Npoints − 1, Dmin, Dmax, PTval, the points) to 12 bits — the
        // form that may change a depth; K.2.3 keeps the sign-magnitude form at the input's.
        byte[] nlt = Nlt(cnlt: 0, bdnlt: 11, tnlt: 2, stnlt: J.Concat(U16(1), U32(0), U32(-1), new byte[] { 8, 0, 255 }));
        int firstSot = IndexOf(cs, 0xFF, 0x90);
        int secondSot = IndexOf(cs, 0xFF, 0x90, firstSot + 12);
        Assert.True(secondSot > firstSot);

        // Tile 1 only: the segment goes after its 12-byte SOT, and Psot grows by the segment's length.
        byte[] tileOnly = J.Concat(cs.Take(secondSot + 12).ToArray(), nlt, cs.Skip(secondSot + 12).ToArray());
        tileOnly[6] = 0x80; tileOnly[7] = 0x00;   // Rsiz: Part 2, so the NLT is kept
        long psot = JpxHeader.ReadU32(cs, secondSot + 6) + nlt.Length;
        tileOnly[secondSot + 6] = (byte)(psot >> 24); tileOnly[secondSot + 7] = (byte)(psot >> 16);
        tileOnly[secondSot + 8] = (byte)(psot >> 8); tileOnly[secondSot + 9] = (byte)psot;
        Assert.False(JpxDecoder.TryDecode(tileOnly, 0, out _, out string reason));
        Assert.Contains("tile 1 reconstructs component 0 as 12-bit unsigned samples where the image's plane is 8-bit unsigned", reason, StringComparison.Ordinal);

        // Every tile: the same NLT in the main header.
        byte[] everyTile = J.Concat(cs.Take(firstSot).ToArray(), nlt, cs.Skip(firstSot).ToArray());
        everyTile[6] = 0x80; everyTile[7] = 0x00;
        Assert.True(JpxDecoder.TryDecode(everyTile, 0, out JpxDecodeResult result, out reason), reason);
        Assert.Equal(9, result.Tiles.Count);
        Assert.Equal((12, false), (result.Image!.Components[0].Precision, result.Image.Components[0].IsSigned));
        Assert.All(result.Tiles, t => Assert.Equal(12, t.Components[0].Precision));
    }

    /// <summary>
    /// The identity (array index 0) over the largest collection one MCC segment holds — 16 000
    /// components in and out with 16-bit indices, 64 KB of the segment's 16-bit length — is applied
    /// without a matrix: none is materialised (16 000² doubles, 2 GB, for a 110 KB file), and the
    /// work charged is one unit per output copy, not one per multiply-add (62 501 units for one
    /// sample, which would exhaust the budget given here).
    /// </summary>
    [Fact]
    public void TheIdentityOverEveryComponent_IsAppliedWithoutAMatrix()
    {
        const int n = 16000;
        var indices = new List<byte>();
        for (int i = 0; i < n; i++) indices.AddRange(U16(i));
        byte[] collection = J.Concat(new byte[] { 1 }, U16(0x8000 | n), indices.ToArray(), U16(0x8000 | n), indices.ToArray(), new byte[] { 0, 0, 0 });
        byte[] cbd = J.Segment(0xFF78, J.Concat(U16(0x8000 | n), new byte[] { 7 }));
        JpxMultipleComponentTransform t = Resolve(Baseline(siz: J.Siz64(n, rsiz: 0x8100), cbd: cbd,
            mct: Array.Empty<byte>(), offsets: Array.Empty<byte>(), mcc: Mcc(1, collection)));

        Assert.Null(t.Matrix);
        Assert.Equal(n, t.Inputs.Length);
        Assert.Equal(n, t.ReconstructedComponents.Length);

        var inputs = new double[n][];
        var outputs = new double[n][];
        for (int i = 0; i < n; i++) { inputs[i] = new[] { (double)i }; outputs[i] = new double[1]; }
        var budget = new WorkBudget(1000);
        t.Apply(inputs, outputs, budget);
        Assert.Equal(1 + n / JpxDecoder.SamplesPerUnit, budget.Spent);
        for (int i = 0; i < n; i++) Assert.Equal((double)i, outputs[i][0]);
    }

    /// <summary>
    /// The elements' encodings of Table A.33: 16- and 32-bit integers, 32- and 64-bit floats. An
    /// offset array shares its index with the decorrelation array — index and type together are
    /// the key (Imct's ten low-order bits), so both resolve.
    /// </summary>
    [Fact]
    public void ArrayElements_AreReadByTheirDeclaredType()
    {
        double[] diagonal = { 2, 0, 0, 0, 3, 0, 0, 0, 4 };
        JpxMultipleComponentTransform t = Resolve(Baseline(mct: Mct(1, JpxMctArrayType.Decorrelation, 3, Doubles(diagonal))));
        Assert.Equal(diagonal, t.Matrix);
        Assert.Equal(new double[] { 128, 128, 128 }, t.Offsets);

        t = Resolve(Baseline(mct: Mct(1, JpxMctArrayType.Decorrelation, 1, Ints(1, 0, 0, 0, -1, 0, 0, 0, 1)),
            offsets: Mct(1, JpxMctArrayType.Offset, 1, Ints(-5, 65536, 7))));
        Assert.Equal(new double[] { 1, 0, 0, 0, -1, 0, 0, 0, 1 }, t.Matrix);
        Assert.Equal(new double[] { -5, 65536, 7 }, t.Offsets);

        t = Resolve(Baseline(mct: Mct(1, JpxMctArrayType.Decorrelation, 0, Shorts(1, 0, 0, 0, 1, 0, 0, 0, 0xFFFE))));
        Assert.Equal(-2, t.Matrix[8]);   // 0xFFFE is −2 as a 16-bit signed integer
    }

    /// <summary>
    /// A series (J.2): two MCT marker segments with the same Imct, Zmct 0 (with Ymct 1) and 1, are
    /// concatenated in Zmct order. A series missing its last segment is refused.
    /// </summary>
    [Fact]
    public void AnMctSeries_IsConcatenatedInZmctOrder()
    {
        byte[] first = Mct(1, JpxMctArrayType.Decorrelation, 2, Floats(InverseIctMatrix.Take(4).ToArray()), zmct: 0, ymct: 1);
        byte[] second = Mct(1, JpxMctArrayType.Decorrelation, 2, Floats(InverseIctMatrix.Skip(4).ToArray()), zmct: 1);
        JpxMultipleComponentTransform t = Resolve(Baseline(mct: J.Concat(second, first)));
        Assert.Equal(InverseIctMatrix.Select(v => (double)v), t.Matrix);

        string reason = RefusalReason(Baseline(mct: first));
        Assert.Contains("Ymct 1 but has 1", reason, StringComparison.Ordinal);
    }

    /// <summary>A tile-part header's MCT with the same index and type overrides the main header's for that tile (§A.3.7); the main header's stays for a tile without one.</summary>
    [Fact]
    public void ATilePartHeadersArray_OverridesTheMainHeaders()
    {
        double[] doubled = { 2, 0, 0, 0, 2, 0, 0, 0, 2 };
        byte[] tileMct = Mct(1, JpxMctArrayType.Decorrelation, 3, Doubles(doubled));
        JpxMultipleComponentTransform t = Resolve(Baseline(tileHeader: tileMct));
        Assert.Equal(doubled, t.Matrix);
        Assert.Equal(new double[] { 128, 128, 128 }, t.Offsets);   // the offsets still come from the main header

        // A different index in the tile header overrides nothing.
        t = Resolve(Baseline(tileHeader: Mct(2, JpxMctArrayType.Decorrelation, 3, Doubles(doubled))));
        Assert.Equal(InverseIctMatrix.Select(v => (double)v), t.Matrix);
    }

    /// <summary>CBD's one-depth form (Ncbd bit 15): three reconstructed components, each 12-bit signed.</summary>
    [Fact]
    public void CbdWithOneDepth_AppliesItToEveryReconstructedComponent()
    {
        byte[] cbd = J.Segment(0xFF78, J.Concat(U16(0x8000 | 3), new byte[] { 0x8B }));
        JpxMultipleComponentTransform t = Resolve(Baseline(cbd: cbd));
        Assert.Equal(3, t.ReconstructedComponents.Length);
        Assert.All(t.ReconstructedComponents, c => Assert.Equal((12, true), (c.Precision, c.IsSigned)));
    }

    /// <summary>No MCO, or an MCO of Nmco 0, means no transformation (J.2.1): null, and true.</summary>
    [Fact]
    public void NoStage_MeansNoTransformation()
    {
        Assert.True(JpxMultipleComponentTransform.TryResolve(Parse(Baseline(mco: Array.Empty<byte>())), 0, out JpxMultipleComponentTransform? t, out string reason), reason);
        Assert.Null(t);
        Assert.True(JpxMultipleComponentTransform.TryResolve(Parse(Baseline(mco: Mco())), 0, out t, out reason), reason);
        Assert.Null(t);
        // A Part 1 codestream has nothing to resolve either.
        byte[] part1 = J.Concat(J.Soc, J.Siz64(3), J.Cod(mct: 1), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.True(JpxMultipleComponentTransform.TryResolve(Parse(part1), 0, out t, out reason), reason);
        Assert.Null(t);
    }

    /// <summary>
    /// §M.9.2.3's four restrictions, each refused by name, against the accepted baseline: a
    /// reversible transformation, two stages, a dependency collection, a wavelet collection, two
    /// collections (Qmcc 2).
    /// </summary>
    [Fact]
    public void OutsideTheBaseline_IsRefusedByName()
    {
        Assert.NotNull(Resolve(Baseline()));

        string reason = RefusalReason(Baseline(mcc: Mcc(1, Collection(1, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 1, 1, reversible: true))));
        Assert.Contains("reversible", reason, StringComparison.Ordinal);

        reason = RefusalReason(Baseline(mco: Mco(1, 1)));
        Assert.Contains("2 transformation stages", reason, StringComparison.Ordinal);

        reason = RefusalReason(Baseline(mcc: Mcc(1, Collection(0, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 1, 1))));
        Assert.Contains("dependency", reason, StringComparison.Ordinal);

        reason = RefusalReason(Baseline(mcc: Mcc(1, WaveletCollection(new[] { 0, 1, 2 }, new[] { 0, 1, 2 }))));
        Assert.Contains("wavelet-based", reason, StringComparison.Ordinal);

        reason = RefusalReason(Baseline(mcc: Mcc(1,
            Collection(1, new[] { 0, 1 }, new[] { 0, 1 }, 0, 0),
            Collection(1, new[] { 2 }, new[] { 2 }, 0, 0))));
        Assert.Contains("Qmcc 1", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Malformed sets: no CBD; an MCO naming an undefined MCC; a collection naming an undefined array,
    /// or one of the wrong size; an input past Csiz or an output past Ncbd; an input left out; two
    /// outputs alike; inputs of different sub-sampling.
    /// </summary>
    [Fact]
    public void MalformedSets_AreRefusedWithTheReason()
    {
        Assert.Contains("CBD", RefusalReason(Baseline(cbd: Array.Empty<byte>())), StringComparison.Ordinal);
        Assert.Contains("MCC 2, which no header defines", RefusalReason(Baseline(mco: Mco(2))), StringComparison.Ordinal);
        Assert.Contains("decorrelation array 3, which no MCT", RefusalReason(Baseline(mcc: Mcc(1, Collection(1, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 3, 1)))), StringComparison.Ordinal);
        Assert.Contains("offset array 3, which no MCT", RefusalReason(Baseline(mcc: Mcc(1, Collection(1, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 1, 3)))), StringComparison.Ordinal);
        Assert.Contains("holds 4 elements", RefusalReason(Baseline(mct: Mct(1, JpxMctArrayType.Decorrelation, 2, Floats(1, 0, 0, 1)))), StringComparison.Ordinal);
        Assert.Contains("holds 2 elements", RefusalReason(Baseline(offsets: Mct(1, JpxMctArrayType.Offset, 0, Shorts(1, 2)))), StringComparison.Ordinal);
        Assert.Contains("reads component 3", RefusalReason(Baseline(mcc: Mcc(1, Collection(1, new[] { 0, 1, 3 }, new[] { 0, 1, 2 }, 1, 1)))), StringComparison.Ordinal);
        Assert.Contains("writes reconstructed component 3", RefusalReason(Baseline(mcc: Mcc(1, Collection(1, new[] { 0, 1, 2 }, new[] { 0, 1, 3 }, 1, 1)))), StringComparison.Ordinal);
        Assert.Contains("leaves component 2 out", RefusalReason(Baseline(mcc: Mcc(1, Collection(1, new[] { 0, 1, 1 }, new[] { 0, 1, 2 }, 1, 1)))), StringComparison.Ordinal);
        Assert.Contains("component 1 twice", RefusalReason(Baseline(mcc: Mcc(1, Collection(1, new[] { 0, 1, 2 }, new[] { 0, 1, 1 }, 1, 1)))), StringComparison.Ordinal);

        byte[] subsampled = J.Siz(64, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1), (0x07, 2, 2), (0x07, 1, 1) }, rsiz: 0x8100);
        Assert.Contains("sub-sampled 2x2", RefusalReason(Baseline(siz: subsampled)), StringComparison.Ordinal);

        Assert.Contains("array index 0", RefusalReason(Baseline(mct: Mct(0, JpxMctArrayType.Decorrelation, 2, Floats(InverseIctMatrix)))), StringComparison.Ordinal);
        Assert.Contains("not a whole number of 4-byte", RefusalReason(Baseline(mct: Mct(1, JpxMctArrayType.Decorrelation, 2, new byte[] { 1, 2, 3 }))), StringComparison.Ordinal);
    }

    /// <summary>A codestream whose Rsiz lacks the MCT capability bit but carries the segments is decoded, and the omission reported to the caller's list, once.</summary>
    [Fact]
    public void SegmentsWithoutTheCapabilityBit_AreAppliedAndReported()
    {
        JpxCodestream cs = Parse(Baseline(siz: J.Siz64(3, rsiz: 0x8000), codMct: 0));
        var deviations = new List<string>();
        Assert.True(JpxMultipleComponentTransform.TryResolve(cs, 0, deviations, out JpxMultipleComponentTransform? t, out string reason), reason);
        Assert.NotNull(t);
        Assert.True(JpxMultipleComponentTransform.TryResolve(cs, 0, deviations, out _, out reason), reason);
        string deviation = Assert.Single(deviations);
        Assert.Contains("capability", deviation, StringComparison.Ordinal);
        Assert.Equal(JpxMultipleComponentTransform.CapabilityNote, deviation);
        Assert.Empty(cs.Deviations);   // the codestream's own list was merged before any tile resolves

        // Control: with the bit, nothing is reported.
        cs = Parse(Baseline());
        deviations.Clear();
        Assert.True(JpxMultipleComponentTransform.TryResolve(cs, 0, deviations, out _, out reason), reason);
        Assert.Empty(deviations);
    }

    /// <summary>
    /// Through the whole decode: the nine-tile grey fixture with a 1x1 identity transformation in
    /// its main header under a Part 2 Rsiz that lacks the MCT capability bit decodes to the
    /// fixture's samples, and the result's deviations hold the note once — every tile resolves
    /// the same headers, and the spine merges the note once (the review of 2026-09-26, row 15:
    /// it went to the codestream's list after that list had been merged, and reached nothing).
    /// Four workers, so the merge is across threads.
    /// </summary>
    [Fact]
    public void SegmentsWithoutTheCapabilityBit_ReachTheDecodesDeviations_Once()
    {
        JpxLosslessFixtures.Fixture f = JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel;
        byte[] segments = J.Concat(Cbd(7),
            Mct(1, JpxMctArrayType.Decorrelation, 2, Floats(1f)),
            Mct(1, JpxMctArrayType.Offset, 0, Shorts(128)),
            Mcc(1, Collection(1, new[] { 0 }, new[] { 0 }, transformIndex: 1, offsetIndex: 1)),
            Mco(1));
        byte[] withoutTheBit = SpliceMainHeader(f.Codestream, segments, rsiz: 0x8000);
        Assert.True(JpxDecoder.TryDecode(withoutTheBit, 0, out JpxDecodeResult result, out string reason, maxThreads: 4), reason);
        Assert.Equal(9, result.Tiles.Count);
        Assert.Equal(f.Planes[0], Array.ConvertAll(result.Image!.Plane(0), v => (byte)v));
        Assert.Single(result.Deviations, d => d == JpxMultipleComponentTransform.CapabilityNote);

        // Control: with the bit, the same decode reports no such thing.
        byte[] withTheBit = SpliceMainHeader(f.Codestream, segments, rsiz: 0x8100);
        Assert.True(JpxDecoder.TryDecode(withTheBit, 0, out result, out reason, maxThreads: 4), reason);
        Assert.DoesNotContain(result.Deviations, d => d.Contains("capability", StringComparison.Ordinal));
    }

    /// <summary>
    /// A tile whose own MCO reconstructs another number of components than the first tile's — a
    /// two-output CBD reached through tile 1's tile-part header alone — is refused by name; the
    /// image's planes were sized by the first tile (the review of 2026-09-26, row 30: it assembled
    /// silently). The control puts the MCO in the main header, and every tile reconstructs two.
    /// </summary>
    [Fact]
    public void ATileWhoseTransformationReconstructsAnotherComponentCount_IsRefused_ButOneForEveryTileDecodes()
    {
        JpxLosslessFixtures.Fixture f = JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel;
        byte[] main = J.Concat(Cbd(7, 7),
            Mct(1, JpxMctArrayType.Decorrelation, 2, Floats(1f, 1f)),
            Mct(1, JpxMctArrayType.Offset, 0, Shorts(128, 128)),
            Mcc(1, Collection(1, new[] { 0 }, new[] { 0, 1 }, transformIndex: 1, offsetIndex: 1)));
        byte[] tileOnly = SpliceTilePartHeader(SpliceMainHeader(f.Codestream, main, rsiz: 0x8100), tile: 1, segment: Mco(1));
        Assert.False(JpxDecoder.TryDecode(tileOnly, 0, out _, out string reason));
        Assert.Contains("tile 1 reconstructs 2 component(s) where the image, described by the first tile, has 1", reason, StringComparison.Ordinal);

        byte[] everyTile = SpliceMainHeader(f.Codestream, J.Concat(main, Mco(1)), rsiz: 0x8100);
        Assert.True(JpxDecoder.TryDecode(everyTile, 0, out JpxDecodeResult result, out reason), reason);
        Assert.Equal(2, result.Image!.Components.Length);
        Assert.Equal(f.Planes[0], Array.ConvertAll(result.Image.Plane(0), v => (byte)v));
        Assert.Equal(f.Planes[0], Array.ConvertAll(result.Image.Plane(1), v => (byte)v));
    }

    /// <summary>
    /// T.801 K.2.3: the sign-magnitude conversion keeps the depth and the sign, so a segment
    /// declaring 12-bit output for 8-bit input is followed (the planes are 12-bit) and reported
    /// against the clause, once per tile; one declaring the input's depth is not.
    /// </summary>
    [Fact]
    public void SignMagnitudeNonLinearity_DeclaringAnotherDepth_IsReportedAgainstK23()
    {
        JpxLosslessFixtures.Fixture f = JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel;
        byte[] changed = SpliceMainHeader(f.Codestream, Nlt(cnlt: 0, bdnlt: 11, tnlt: 3), rsiz: 0x8000);
        Assert.True(JpxDecoder.TryDecode(changed, 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Equal(12, result.Image!.Components[0].Precision);
        Assert.Equal(9, result.Deviations.Count(d => d.Contains("T.801 K.2.3", StringComparison.Ordinal)));

        byte[] kept = SpliceMainHeader(f.Codestream, Nlt(cnlt: 0, bdnlt: 7, tnlt: 3), rsiz: 0x8000);
        Assert.True(JpxDecoder.TryDecode(kept, 0, out result, out reason), reason);
        Assert.Equal(8, result.Image!.Components[0].Precision);
        Assert.DoesNotContain(result.Deviations, d => d.Contains("K.2.3", StringComparison.Ordinal));
    }

    /// <summary>The segments before the first SOT, and Rsiz set — the main-header splice the multi-tile tests share.</summary>
    private static byte[] SpliceMainHeader(byte[] cs, byte[] segments, int rsiz)
    {
        int firstSot = IndexOf(cs, 0xFF, 0x90);
        byte[] spliced = J.Concat(cs.Take(firstSot).ToArray(), segments, cs.Skip(firstSot).ToArray());
        spliced[6] = (byte)(rsiz >> 8); spliced[7] = (byte)rsiz;
        return spliced;
    }

    /// <summary>The segment after the given tile's 12-byte SOT, with Psot grown by its length.</summary>
    private static byte[] SpliceTilePartHeader(byte[] cs, int tile, byte[] segment)
    {
        int sot = IndexOf(cs, 0xFF, 0x90);
        for (int i = 0; i < tile; i++) sot = IndexOf(cs, 0xFF, 0x90, sot + 12);
        Assert.True(sot > 0);
        byte[] spliced = J.Concat(cs.Take(sot + 12).ToArray(), segment, cs.Skip(sot + 12).ToArray());
        long psot = JpxHeader.ReadU32(cs, sot + 6) + segment.Length;
        spliced[sot + 6] = (byte)(psot >> 24); spliced[sot + 7] = (byte)(psot >> 16);
        spliced[sot + 8] = (byte)(psot >> 8); spliced[sot + 9] = (byte)psot;
        return spliced;
    }

    // ---- The non-linearity (Annex K) ---------------------------------------------------------------

    /// <summary>The BT.709 curve of Figure K.2 — E 0.45, S 4.5, T 0.081, A 1.099, B 0.099 — as Table A.45 stores it.</summary>
    private static byte[] Bt709 => GammaParams(0.45, 4.5, 0.081, 1.099, 0.099);

    private static JpxNonLinearity[] ResolveNlt(byte[] data, int components = 3)
    {
        Assert.True(JpxNonLinearity.TryResolve(Parse(data), 0, components, out JpxNonLinearity[] perComponent, out string reason), reason);
        return perComponent;
    }

    private static byte[] Part1With(byte[] mainHeader, byte[]? tileHeader = null) =>
        J.Concat(J.Soc, J.Siz64(3, rsiz: 0x8200), J.Cod(), J.QcdNone(), mainHeader, J.TilePart(0, 0, tileHeader ?? Array.Empty<byte>(), Data5), J.Eoc);

    /// <summary>
    /// K-2 on the BT.709 curve, on 8-bit unsigned samples normalised by K-3 (255): below the toe
    /// threshold the linear branch (10 → 10/255/4.5 · 255 = 2.2 → 2; 20 → 4, 21 → 5), above it the
    /// power branch (200 → 157), and the ends fixed (0 → 0, 255 → 255). The parameters come back as
    /// stored, at 1/65536.
    /// </summary>
    [Fact]
    public void GammaNonLinearity_FollowsEquationK2()
    {
        JpxNonLinearity[] nlt = ResolveNlt(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.Gamma, Bt709)));
        Assert.All(nlt, n => Assert.NotNull(n));
        JpxNonLinearity n0 = nlt[0];
        Assert.Equal(JpxNonLinearity.Gamma, n0.Type);
        Assert.Equal(29491 / 65536.0, n0.E);
        Assert.Equal(4.5, n0.S);
        Assert.Equal(5308 / 65536.0, n0.T);
        Assert.Equal(72024 / 65536.0, n0.A);
        Assert.Equal(6488 / 65536.0, n0.B);
        Assert.Equal((8, false), (n0.OutputPrecision, n0.OutputSigned));

        int[] samples = { 0, 10, 20, 21, 200, 255 };
        n0.Apply(samples, inputPrecision: 8, inputSigned: false);
        Assert.Equal(new[] { 0, 2, 4, 5, 157, 255 }, samples);
    }

    /// <summary>
    /// K-3 and K-4: an unsigned 8-bit input to a 12-bit output scales the result by 4095 (10 → 36,
    /// 200 → 2520); a signed 8-bit input normalises by 128, and the negative branch of K-2 mirrors the
    /// positive one (−100 → −78, 100 → 78, −5 → −1, 5 → 1).
    /// </summary>
    [Fact]
    public void GammaNonLinearity_ScalesToTheDepthsOfK3()
    {
        JpxNonLinearity deeper = ResolveNlt(Part1With(Nlt(JpxNonLinearity.AllComponents, 11, JpxNonLinearity.Gamma, Bt709)))[0];
        int[] samples = { 10, 200 };
        deeper.Apply(samples, 8, false);
        Assert.Equal(new[] { 36, 2520 }, samples);
        Assert.Equal((12, false), (deeper.OutputPrecision, deeper.OutputSigned));

        JpxNonLinearity signed = ResolveNlt(Part1With(Nlt(JpxNonLinearity.AllComponents, 0x87, JpxNonLinearity.Gamma, Bt709)))[0];
        samples = new[] { -100, -5, 5, 100 };
        signed.Apply(samples, 8, true);
        Assert.Equal(new[] { -78, -1, 1, 78 }, samples);
    }

    /// <summary>
    /// K-6 to K-9 with three points (0, 64, 255)/255 over Dmin 0 to Dmax 1: linear between the points
    /// (64 → 32, 128 → 65, 200 → 173), the ends exact; a signed input is shifted onto [0, 1] first
    /// (K-7: −128 → 0, 0 → 65, 127 → 255); and with Dmin 0.25, Dmax 0.75 and two points, inputs
    /// outside the range clip to the end values (64 → 0, 191 → 255) while 128 maps to 129.
    /// </summary>
    [Fact]
    public void LookupNonLinearity_InterpolatesBetweenThePointsAndClipsToDminDmax()
    {
        byte[] threePoints = LutParams(pointsField: 2, dminRaw: 0, dmaxRaw: 0xFFFFFFFF, ptval: 8, 0, 64, 255);
        JpxNonLinearity lut = ResolveNlt(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.Lookup, threePoints)))[0];
        Assert.Equal(new[] { 0.0, 64 / 255.0, 1.0 }, lut.Table);
        Assert.Equal((0.0, 1.0), (lut.Dmin, lut.Dmax));

        int[] samples = { 0, 64, 128, 200, 255 };
        lut.Apply(samples, 8, false);
        Assert.Equal(new[] { 0, 32, 65, 173, 255 }, samples);

        samples = new[] { -128, 0, 127 };
        lut.Apply(samples, 8, true);
        Assert.Equal(new[] { 0, 65, 255 }, samples);

        byte[] twoPoints = LutParams(1, 1073741824, 3221225471, 16, 0, 65535);
        JpxNonLinearity clipped = ResolveNlt(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.Lookup, twoPoints)))[0];
        samples = new[] { 0, 64, 128, 191, 255 };
        clipped.Apply(samples, 8, false);
        Assert.Equal(new[] { 0, 0, 129, 255, 255 }, samples);
    }

    /// <summary>K.2.3 on a signed 8-bit component: −1 ↔ −128, −128 ↔ −1, non-negatives unchanged; the identity on an unsigned one.</summary>
    [Fact]
    public void SignMagnitudeConversion_MapsNegativesAsK23Says()
    {
        JpxNonLinearity signed = ResolveNlt(Part1With(Nlt(JpxNonLinearity.AllComponents, 0x87, JpxNonLinearity.SignMagnitude)))[0];
        int[] samples = { -1, -128, -2, 0, 5, 127 };
        signed.Apply(samples, 8, true);
        Assert.Equal(new[] { -128, -1, -127, 0, 5, 127 }, samples);

        JpxNonLinearity unsigned = ResolveNlt(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.SignMagnitude)))[0];
        samples = new[] { 0, 5, 255 };
        unsigned.Apply(samples, 8, false);
        Assert.Equal(new[] { 0, 5, 255 }, samples);
    }

    /// <summary>
    /// §A.3.10's precedence: a tile-part header's per-component segment beats its default, which
    /// beats the main header's per-component segment, which beats the main default; a segment of
    /// type 0 disables the one it beats; a component nothing names has none.
    /// </summary>
    [Fact]
    public void NonLinearities_ResolveByHeaderPrecedence()
    {
        byte[] mainGammaDefault = Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.Gamma, Bt709);
        byte[] mainLutFor1 = Nlt(1, 7, JpxNonLinearity.Lookup, LutParams(1, 0, 0xFFFFFFFF, 8, 0, 255));
        byte[] tileNoneFor2 = Nlt(2, 7, JpxNonLinearity.None);

        JpxNonLinearity[] nlt = ResolveNlt(Part1With(J.Concat(mainGammaDefault, mainLutFor1), tileNoneFor2));
        Assert.Equal(JpxNonLinearity.Gamma, nlt[0].Type);
        Assert.Equal(JpxNonLinearity.Lookup, nlt[1].Type);
        Assert.Null(nlt[2]);

        byte[] tileSignMagnitudeDefault = Nlt(JpxNonLinearity.AllComponents, 0x87, JpxNonLinearity.SignMagnitude);
        nlt = ResolveNlt(Part1With(J.Concat(mainGammaDefault, mainLutFor1), J.Concat(tileSignMagnitudeDefault, tileNoneFor2)));
        Assert.Equal(JpxNonLinearity.SignMagnitude, nlt[0].Type);
        Assert.Equal(JpxNonLinearity.SignMagnitude, nlt[1].Type);   // the tile default beats the main per-component segment
        Assert.Null(nlt[2]);

        nlt = ResolveNlt(Part1With(mainLutFor1));
        Assert.Null(nlt[0]);
        Assert.NotNull(nlt[1]);
        Assert.Null(nlt[2]);
    }

    /// <summary>A gamma segment with E 0 (K-2 undefined), a LUT segment holding fewer values than it declares, a reserved type, and a second segment for one component are refused by name.</summary>
    [Fact]
    public void MalformedNonLinearities_AreRefusedWithTheReason()
    {
        Assert.True(JpxNonLinearity.TryResolve(Parse(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.Gamma, Bt709))), 0, 3, out _, out string reason), reason);

        Assert.False(JpxNonLinearity.TryResolve(Parse(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.Gamma, GammaParams(0, 4.5, 0.081, 1.099, 0.099)))), 0, 3, out _, out reason));
        Assert.Contains("zero E, S or A", reason, StringComparison.Ordinal);

        Assert.False(JpxNonLinearity.TryResolve(Parse(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.Lookup, LutParams(2, 0, 0xFFFFFFFF, 8, 0, 255)))), 0, 3, out _, out reason));
        Assert.Contains("declares 3 points of 1 bytes but holds 2", reason, StringComparison.Ordinal);

        Assert.False(JpxNonLinearity.TryResolve(Parse(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, 7))), 0, 3, out _, out reason));
        Assert.Contains("reserved type 7", reason, StringComparison.Ordinal);

        Assert.False(JpxNonLinearity.TryResolve(Parse(Part1With(J.Concat(Nlt(1, 7, JpxNonLinearity.None), Nlt(1, 7, JpxNonLinearity.None)))), 0, 3, out _, out reason));
        Assert.Contains("two NLT marker segments for component 1", reason, StringComparison.Ordinal);

        Assert.False(JpxNonLinearity.TryResolve(Parse(Part1With(Nlt(JpxNonLinearity.AllComponents, 7, JpxNonLinearity.Lookup, LutParams(1, 0xFFFFFFFF, 0, 8, 0, 255)))), 0, 3, out _, out reason));
        Assert.Contains("Dmax at or below Dmin", reason, StringComparison.Ordinal);
    }
}
