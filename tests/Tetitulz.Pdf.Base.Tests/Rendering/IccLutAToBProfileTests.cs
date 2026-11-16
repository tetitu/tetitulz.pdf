// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// ICC.1:2010 §10.12 <c>lutAToBType</c> (<c>'mAB '</c>) — the v4 <c>A2B0</c> form.
/// </summary>
/// <remarks>
/// <para><b>A different layout, not a variant.</b> The v2 <c>mft1</c>/<c>mft2</c> tags carry their
/// curves as sampled tables inline in the header and one grid size for every axis;
/// <c>lutAToBType</c> carries five independently-optional stages addressed by offset, its curves as
/// tagged elements padded to four-byte boundaries, and a grid size <b>per input channel</b>.</para>
///
/// <para><b>The PCS Lab encoding differs too, and that is the whole reason these fixtures write
/// their own landmarks.</b> ICC.1 §6.3.4.2 gives legacy 16-bit PCSLAB <c>0xFF00</c> for L* = 100 —
/// which is what <c>lut16Type</c> uses — while <c>lutAToBType</c> uses the v4 encoding, where
/// <c>0xFFFF</c> is L* = 100 and <c>0x8080</c> is a* = b* = 0. The numbers below come from the
/// specification, so an evaluator that inherited the legacy scale disagrees with them rather than
/// with itself.</para>
///
/// <para><b>Constant CLUTs are deliberate</b> where the test is about plumbing or encoding: it
/// makes the expected colour independent of interpolation. The one test that is about
/// interpolation says so and varies the table.</para>
/// </remarks>
public class IccLutAToBProfileTests
{
    // ================================================================ the pipeline

    /// <summary>A CMYK <c>mAB </c> profile resolves a fill through its own pipeline.</summary>
    /// <remarks>
    /// The CLUT encodes a green no CMYK-to-RGB formula produces for these operands, and the
    /// control below asserts the painted colour is <i>not</i> the device fallback — without it,
    /// "the profile was consulted" would be satisfied by a run that ignored it.
    /// </remarks>
    [Fact]
    public void ACmykMabProfile_IsResolvedThroughItsOwnPipeline()
    {
        var lab = (l: 60.0, a: -60.0, b: 40.0);
        byte[] profile = MabProfile("CMYK", inputs: 4, lab: lab);

        using var bmp = PixelAssert.Render(IccFillPdf(profile, 4, "0.2 0.2 0.2 0.2"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        SKColor expected = PdfColor.LabToSRgb((float)lab.l, (float)lab.a, (float)lab.b);
        Assert.True(PixelAssert.Near(painted, expected, tolerance: 6),
            $"expected the profile's own colour {expected}, got {painted}");

        SKColor deviceFallback = PdfColor.CmykToRgb(0.2f, 0.2f, 0.2f, 0.2f);
        Assert.False(PixelAssert.Near(painted, deviceFallback, tolerance: 24),
            $"the painted colour {painted} is the DeviceCMYK fallback — the profile was not " +
            "consulted and the assertion above is passing by accident");
    }

    /// <summary>
    /// The v4 Lab encoding is used, not the legacy one the <c>lut16Type</c> form needs.
    /// </summary>
    /// <remarks>
    /// <para><b>The white point cannot tell the two apart, and assuming it could is how this test
    /// was wrong the first time.</b> The legacy scale multiplies by 65535/65280, so at
    /// <c>0xFFFF</c> it produces L* = 100.39 — which sRGB <i>clamps</i> to the same white as
    /// L* = 100. The v2 form's own white-point test works for the opposite reason: it drops the
    /// scale, landing on L* = 99.61, and a count below white is visible. Above it, nothing
    /// is.</para>
    ///
    /// <para><b>So the sample sits just below white</b>, where the 0.4% is worth about two sRGB
    /// counts. And the assertion names <i>both</i> readings: near the v4 one and NOT near the
    /// legacy one. Asserting only the first would be satisfied by any implementation whose answer
    /// happened to land within the tolerance, which for a difference this small is most of
    /// them.</para>
    /// </remarks>
    [Fact]
    public void TheV4LabEncodingIsUsedRatherThanTheLegacyOne()
    {
        // Raw v4 landmarks rather than the (l, a, b) helper, because the claim IS the encoding:
        // L* spans 0x0000..0xFFFF and 0x8080 is a* = b* = 0.
        const int rawL = 64880;                       // L* = 99.0 under the v4 encoding
        byte[] profile = MabProfileRaw("CMYK", inputs: 4, new[] { rawL, 0x8080, 0x8080 });

        using var bmp = PixelAssert.Render(IccFillPdf(profile, 4, "0 0 0 0"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        const double LegacyScale = 65535.0 / 65280.0;
        double v4L = rawL / 65535.0 * 100.0;
        double legacyL = v4L * LegacyScale;
        double legacyChroma = (0x8080 / 65535.0 * 255.0 * LegacyScale) - 128.0;

        SKColor v4 = PdfColor.LabToSRgb((float)v4L, 0f, 0f);
        SKColor legacy = PdfColor.LabToSRgb(
            (float)legacyL, (float)legacyChroma, (float)legacyChroma);

        Assert.True(PixelAssert.Near(painted, v4, tolerance: 1),
            $"expected the v4 reading {v4}, got {painted}");
        Assert.False(PixelAssert.Near(painted, legacy, tolerance: 1),
            $"the painted colour {painted} is the LEGACY reading {legacy}; the v4 form inherited " +
            "lut16Type's scale");
    }

    /// <summary>The CLUT is interpolated across a grid that differs per axis.</summary>
    /// <remarks>
    /// <b>The per-axis grid is the structural difference from the v2 forms</b>, and a cube-shaped
    /// fixture cannot see it: with 2×2 the flat index is the same however the multipliers are
    /// ordered. This is 2×3, and the second axis is the one that varies, so an evaluator using one
    /// multiplier for both axes reads a different cell.
    /// </remarks>
    [Fact]
    public void ACLutWithADifferentSizePerAxisIsIndexedCorrectly()
    {
        // Two inputs, grid 2 x 3. Every cell is mid-grey except the ones at the second axis's top,
        // which are black -- so the answer depends on reading the second axis with ITS length.
        var grid = new[] { 2, 3 };
        var cells = new List<int[]>();
        for (int i0 = 0; i0 < grid[0]; i0++)
            for (int i1 = 0; i1 < grid[1]; i1++)
                cells.Add(i1 == grid[1] - 1
                    ? new[] { 0x0000, 0x8080, 0x8080 }        // L* 0
                    : new[] { 0xFFFF, 0x8080, 0x8080 });      // L* 100

        byte[] profile = MabProfileRaw("2CLR", inputs: 2, cells: cells, grid: grid);

        // Second channel at 1.0 lands on the last row of the second axis: black.
        using var black = PixelAssert.Render(IccFillPdf(profile, 2, "0 1"));
        SKColor atTop = PixelAssert.At(black, 50, 50);
        Assert.True(atTop.Red < 40, $"expected the black cell, got {atTop}");

        // First channel at 1.0 stays on the second axis's first row: white.
        using var white = PixelAssert.Render(IccFillPdf(profile, 2, "1 0"));
        SKColor atSide = PixelAssert.At(white, 50, 50);
        Assert.True(atSide.Red > 215, $"expected the white cell, got {atSide}");
    }

    /// <summary>The matrix and M curves are applied, in ICC.1's order.</summary>
    /// <remarks>
    /// <b>No CLUT here</b>, which is one of the four element combinations ICC.1 permits: three
    /// inputs pass straight through the M curves, the matrix, and the B curves. The matrix maps
    /// every channel to L* alone, so a run that skipped it would paint a colour rather than a grey.
    /// </remarks>
    [Fact]
    public void TheMatrixIsAppliedBetweenTheMAndBCurves()
    {
        // The input is (1, 0, 0). The matrix sends channel 0 to output 0 (L*) and sets the two
        // chroma outputs to the encoded zero, so the result is L* = 100 with a* = b* = 0: white.
        double[] matrix =
        {
            1, 0, 0,
            0, 0, 0,
            0, 0, 0,
            0.0, 0x8080 / 65535.0, 0x8080 / 65535.0,   // the three offsets
        };

        byte[] profile = MabMatrixProfile("3CLR", matrix);

        using var bmp = PixelAssert.Render(IccFillPdf(profile, 3, "1 0 0"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        Assert.True(painted.Red > 240 && painted.Green > 240 && painted.Blue > 240,
            $"expected the matrix to route channel 0 to L*, got {painted}");
    }

    /// <summary>Two curve elements in a row are read across the padding between them.</summary>
    /// <remarks>
    /// <para><b>ICC.1 §7.2.5 pads each element to a four-byte boundary and the elements carry no
    /// length of their own</b>, so a reader that advanced by the element's own size starts the next
    /// signature check one to three bytes early, fails to find <c>curv</c>, and refuses the whole
    /// profile. A single-entry <c>curveType</c> is 14 bytes and pads to 16, so a run of them is
    /// misaligned at every element but the first — which is why this fixture uses them and the
    /// others use 2-entry curves that happen to align.</para>
    ///
    /// <para><b>The colour has to be one no fallback produces, and the first version of this test
    /// got that wrong.</b> It asked for CMYK 0,0,0,0 and asserted near-white — which is exactly
    /// what the <i>device</i> fallback paints when the profile is refused, so the assertion was
    /// satisfied by the feature being absent and a mutation removing the padding survived it. The
    /// green below is a colour <c>PdfColor.CmykToRgb</c> cannot return for these operands, and the
    /// control says so explicitly.</para>
    /// </remarks>
    [Fact]
    public void CurveElementsAreReadAcrossTheirPadding()
    {
        // Single-entry 'curv' elements: a u8Fixed8 gamma of 1.0 -- the identity -- at 14 bytes
        // plus 2 of padding each, one per channel.
        var lab = (l: 60.0, a: -60.0, b: 40.0);
        var sample = new[]
        {
            (int)Math.Round(lab.l / 100.0 * 0xFFFF),
            (int)Math.Round((lab.a + 128.0) / 255.0 * 0xFFFF),
            (int)Math.Round((lab.b + 128.0) / 255.0 * 0xFFFF),
        };
        byte[] profile = MabProfileRaw("CMYK", inputs: 4, sample, unalignedCurves: true);

        using var bmp = PixelAssert.Render(IccFillPdf(profile, 4, "0.2 0.2 0.2 0.2"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        SKColor expected = PdfColor.LabToSRgb((float)lab.l, (float)lab.a, (float)lab.b);
        Assert.True(PixelAssert.Near(painted, expected, tolerance: 6),
            $"the misaligned curve run was not read across its padding; expected {expected}, " +
            $"got {painted}");

        SKColor deviceFallback = PdfColor.CmykToRgb(0.2f, 0.2f, 0.2f, 0.2f);
        Assert.False(PixelAssert.Near(painted, deviceFallback, tolerance: 24),
            $"the painted colour {painted} is the DeviceCMYK fallback — the profile was refused, " +
            "which is what a reader that ignored the padding does");
    }

    /// <summary>All three matrix offsets are added.</summary>
    /// <remarks>
    /// <b>The 3×3 is entirely zero here, so every output is its offset and nothing else.</b> The
    /// sibling test above routes a channel through the coefficients and leaves its first offset at
    /// zero — which made dropping that one offset a no-op, and a mutation doing exactly that
    /// survived. ICC.1 §10.12.4 numbers the offsets after the nine coefficients, so an
    /// implementation reading them from the wrong index is reading a coefficient.
    /// </remarks>
    [Fact]
    public void TheMatrixOffsetsAreAllAdded()
    {
        double[] matrix =
        {
            0, 0, 0,
            0, 0, 0,
            0, 0, 0,
            1.0, 0x8080 / 65535.0, 0x8080 / 65535.0,   // L* = 100, a* = b* = 0
        };

        byte[] profile = MabMatrixProfile("3CLR", matrix);

        // Every input is 0, so without the offsets the PCS is (0, 0, 0) -- Lab (0, -128, -128),
        // which is nothing like white.
        using var bmp = PixelAssert.Render(IccFillPdf(profile, 3, "0 0 0"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        Assert.True(painted.Red > 240 && painted.Green > 240 && painted.Blue > 240,
            $"the three matrix offsets should have produced white on their own, got {painted}");
    }

    /// <summary>The A curves are applied, before the CLUT reads its axis.</summary>
    /// <remarks>
    /// <b>An identity curve cannot show that a stage ran</b>, and every other fixture here uses
    /// one because the tests around them are about something else. This one inverts the first A
    /// curve over a CLUT whose first axis runs black → white, so feeding 0 lands on the white end:
    /// the answer is the opposite of what skipping the stage produces, rather than merely
    /// different from it.
    /// </remarks>
    [Fact]
    public void TheACurvesAreAppliedBeforeTheCLut()
    {
        // One input, two cells: 0 → black, 1 → white. The A curve inverts, so input 0 reads white.
        var cells = new List<int[]>
        {
            new[] { 0x0000, 0x8080, 0x8080 },
            new[] { 0xFFFF, 0x8080, 0x8080 },
        };

        byte[] profile = MabProfileRaw("GRAY", inputs: 1, cells: cells, grid: new[] { 2 },
                                       invertFirstACurve: true);

        using var bmp = PixelAssert.Render(IccFillPdf(profile, 1, "0"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        Assert.True(painted.Red > 215,
            $"input 0 with an inverting A curve should read the white cell, got {painted}");
    }

    /// <summary>The B curves are applied, after everything else.</summary>
    /// <remarks>
    /// The mirror of the A-curve test and for the same reason: the CLUT is a constant white, so
    /// only the B curve can make the result black.
    /// </remarks>
    [Fact]
    public void TheBCurvesAreAppliedLast()
    {
        var cells = new List<int[]>
        {
            new[] { 0xFFFF, 0x8080, 0x8080 },
            new[] { 0xFFFF, 0x8080, 0x8080 },
        };

        byte[] profile = MabProfileRaw("GRAY", inputs: 1, cells: cells, grid: new[] { 2 },
                                       invertFirstBCurve: true);

        // Input 1, not 0: DeviceGray's fallback for 0 is black, which is the answer this asserts,
        // so a refused profile would pass. For 1 the fallback is white and cannot.
        using var bmp = PixelAssert.Render(IccFillPdf(profile, 1, "1"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        Assert.True(painted.Red < 40,
            $"an inverting B curve over a white CLUT should paint black, got {painted}");
    }

    /// <summary>The M curves are applied, before the matrix.</summary>
    /// <remarks>
    /// <b>Order matters here and the fixture pins it.</b> The M curve inverts channel 0 and the
    /// matrix then routes channel 0 to L*, so input 1 becomes L* = 0. Running the matrix first
    /// would route the un-inverted 1 and paint white — the opposite answer, not a near one.
    /// </remarks>
    [Fact]
    public void TheMCurvesAreAppliedBeforeTheMatrix()
    {
        double[] matrix =
        {
            1, 0, 0,
            0, 0, 0,
            0, 0, 0,
            0.0, 0x8080 / 65535.0, 0x8080 / 65535.0,
        };

        byte[] profile = MabMatrixProfile("3CLR", matrix, invertFirstMCurve: true);

        using var bmp = PixelAssert.Render(IccFillPdf(profile, 3, "1 0 0"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        Assert.True(painted.Red < 40,
            $"the M curve should have inverted channel 0 before the matrix read it, got {painted}");
    }

    // ================================================================ refusals

    /// <summary>
    /// Each malformed shape is refused <b>by its own name</b> rather than evaluated as if the
    /// missing stage were an identity.
    /// </summary>
    /// <remarks>
    /// <para>Every row here is a shape ICC.1 §10.12 does not define. Guessing an identity for the
    /// absent half would produce a plausible colour from a profile this does not understand, which
    /// is the failure the reason strings exist to prevent — and a caller reads
    /// <c>IccTransform.LastUnsupportedProfile</c> to tell "no profile" from "profile present,
    /// fallback used".</para>
    ///
    /// <para><b>Each row asserts its own substring</b>, so a refusal arriving from a different
    /// check cannot pass for the one under test.</para>
    /// </remarks>
    [Theory]
    [InlineData(Malformation.NoBCurves, "no B curves")]
    [InlineData(Malformation.ClutWithoutACurves, "CLUT without A curves")]
    [InlineData(Malformation.MatrixWithoutMCurves, "matrix without M curves")]
    [InlineData(Malformation.SingleGridPoint, "grid points")]
    [InlineData(Malformation.BadPrecision, "precision byte")]
    [InlineData(Malformation.OffsetPastTheTag, "outside the tag")]
    [InlineData(Malformation.ClutGridLargerThanTheTag, "grid is larger than the tag")]
    [InlineData(Malformation.ClutDataRunsPastTheTag, "CLUT data runs past the end of the tag")]
    public void AMalformedPipelineIsRefusedByName(Malformation how, string expected)
    {
        byte[] tag = MalformedMabTag(how);

        IccLutPipeline result = IccLutPipeline.Parse(tag, 0, tag.Length, bToA: false, out string reason);

        Assert.Null(result);
        Assert.NotNull(reason);
        Assert.Contains(expected, reason, StringComparison.Ordinal);
    }

    /// <summary>The acceptance control for the refusals: a well-formed tag is read.</summary>
    /// <remarks>
    /// A file of refusals with no acceptance case is satisfied by a parser that refuses
    /// everything, which is the cheapest wrong implementation of all of them.
    /// </remarks>
    [Fact]
    public void AWellFormedPipelineIsAccepted()
    {
        byte[] tag = MalformedMabTag(Malformation.None);

        IccLutPipeline result = IccLutPipeline.Parse(tag, 0, tag.Length, bToA: false, out string reason);

        Assert.NotNull(result);
        Assert.Null(reason);
        Assert.Equal(4, result!.InputChannels);
        Assert.Equal(3, result.OutputChannels);
    }

    /// <summary>What each malformed fixture does to an otherwise valid tag.</summary>
    public enum Malformation
    {
        None,
        NoBCurves,
        ClutWithoutACurves,
        MatrixWithoutMCurves,
        SingleGridPoint,
        BadPrecision,
        OffsetPastTheTag,

        /// <summary>Grid sizes whose product alone exceeds the tag.</summary>
        /// <remarks>
        /// <b>A different fact from the one below</b>, and worth its own row: this one is caught
        /// while multiplying the axes, before a byte count exists, and it is the bound that stops
        /// the product overflowing on the way to the data check.
        /// </remarks>
        ClutGridLargerThanTheTag,

        /// <summary>A plausible grid whose sample data does not fit the tag.</summary>
        ClutDataRunsPastTheTag,
    }

    /// <summary>An AToB pipeline refuses to answer raw.</summary>
    /// <remarks>
    /// Its outputs are PCS values by construction; handing them back as device channels would be
    /// inventing ink amounts. <c>IccLut</c> never asks, so only the pipeline can show the guard.
    /// </remarks>
    [Fact]
    public void AnMabPipelineAnswersOnlyInItsOwnDirection()
    {
        byte[] profile = MabProfileRaw("CMYK", 4, new[] { 0x8000, 0x8080, 0x8080 });
        const int off = 128 + 4 + 12;
        IccLutPipeline pipeline = IccLutPipeline.Parse(profile, off, profile.Length - off, bToA: false, out string reason);
        Assert.True(pipeline != null, reason);

        Assert.False(pipeline.TryEvaluateRaw(new float[4], new double[3]));
        Assert.True(pipeline.TryEvaluate(new float[4], out double l, out _, out _));
        Assert.Equal(0x8000 / 65535.0, l, 3);
    }

    // ================================================================ builders

    /// <summary>An <c>'mAB '</c> tag with A curves, a CLUT and B curves, optionally broken.</summary>
    private static byte[] MalformedMabTag(Malformation how)
    {
        const int inputs = 4, outputs = 3, gridPoints = 2;

        var aCurves = new MemoryStream();
        for (int i = 0; i < inputs; i++) WriteIdentityCurve(aCurves);
        byte[] a = aCurves.ToArray();

        var bCurves = new MemoryStream();
        for (int i = 0; i < outputs; i++) WriteIdentityCurve(bCurves);
        byte[] b = bCurves.ToArray();

        int cells = 1;
        for (int i = 0; i < inputs; i++) cells *= gridPoints;

        var clut = new MemoryStream();
        for (int i = 0; i < 16; i++)
            clut.WriteByte((byte)(i < inputs
                ? (how == Malformation.SingleGridPoint ? 1 : gridPoints)
                : 0));
        clut.WriteByte(how == Malformation.BadPrecision ? (byte)3 : (byte)2);
        clut.WriteByte(0); clut.WriteByte(0); clut.WriteByte(0);
        for (int cell = 0; cell < cells; cell++)
            for (int k = 0; k < outputs; k++)
                WriteU16(clut, k == 0 ? 0xFFFF : 0x8080);
        byte[] c = clut.ToArray();

        // Layout: header, B curves, CLUT, A curves.
        int bOffset = 32;
        int clutOffset = bOffset + b.Length;
        int aOffset = clutOffset + c.Length;
        int total = aOffset + a.Length;

        var tag = new byte[total];
        WriteAsciiAt(tag, 0, "mAB ");
        tag[8] = inputs;
        tag[9] = outputs;

        WriteU32At(tag, 12, how == Malformation.NoBCurves ? 0u : (uint)bOffset);
        WriteU32At(tag, 16, how == Malformation.MatrixWithoutMCurves ? (uint)bOffset : 0u);
        WriteU32At(tag, 20, 0);
        WriteU32At(tag, 24, how == Malformation.OffsetPastTheTag
            ? (uint)(total + 64) : (uint)clutOffset);
        WriteU32At(tag, 28, how == Malformation.ClutWithoutACurves ? 0u : (uint)aOffset);

        Buffer.BlockCopy(b, 0, tag, bOffset, b.Length);
        Buffer.BlockCopy(c, 0, tag, clutOffset, c.Length);
        Buffer.BlockCopy(a, 0, tag, aOffset, a.Length);

        if (how == Malformation.ClutGridLargerThanTheTag)
        {
            // Every axis 16 points wide: 65 536 cells, which the axis product rejects before any
            // byte count is computed.
            for (int i = 0; i < inputs; i++) tag[clutOffset + i] = 16;
        }

        if (how == Malformation.ClutDataRunsPastTheTag)
        {
            // One wider axis: 6 x 2 x 2 x 2 = 48 cells, a product the tag can hold, whose 288
            // bytes of samples it cannot. That is the data bound rather than the grid one.
            tag[clutOffset] = 6;
        }

        return tag;
    }

    internal static byte[] MabProfile(string dataSpace, int inputs, (double l, double a, double b) lab)
    {
        // The v4 PCSLAB encoding, written from ICC.1 §6.3.4.2 rather than from the evaluator:
        // L* 0..100 spans 0x0000..0xFFFF and a*/b* -128..+127.996 span the same range.
        var sample = new[]
        {
            (int)Math.Round(lab.l / 100.0 * 0xFFFF),
            (int)Math.Round((lab.a + 128.0) / 255.0 * 0xFFFF),
            (int)Math.Round((lab.b + 128.0) / 255.0 * 0xFFFF),
        };
        return MabProfileRaw(dataSpace, inputs, sample);
    }

    private static byte[] MabProfileRaw(string dataSpace, int inputs, int[] sample,
                                        bool unalignedCurves = false)
    {
        var grid = new int[inputs];
        for (int i = 0; i < inputs; i++) grid[i] = 2;

        int cells = 1;
        for (int i = 0; i < inputs; i++) cells *= grid[i];

        var cellList = new List<int[]>(cells);
        for (int i = 0; i < cells; i++) cellList.Add(sample);

        return MabProfileRaw(dataSpace, inputs, cellList, grid, unalignedCurves);
    }

    /// <summary>
    /// A minimal but structurally valid ICC profile whose <c>A2B0</c> is an <c>'mAB '</c> with
    /// A curves, a CLUT and B curves.
    /// </summary>
    private static byte[] MabProfileRaw(string dataSpace, int inputs, List<int[]> cells,
                                        int[] grid, bool unalignedCurves = false,
                                        bool invertFirstACurve = false,
                                        bool invertFirstBCurve = false)
    {
        const int outputs = 3;

        var aCurves = new MemoryStream();
        for (int i = 0; i < inputs; i++)
        {
            if (i == 0 && invertFirstACurve) WriteInvertingCurve(aCurves);
            else if (unalignedCurves) WriteGammaCurve(aCurves);
            else WriteIdentityCurve(aCurves);
        }

        var bCurves = new MemoryStream();
        for (int i = 0; i < outputs; i++)
        {
            if (i == 0 && invertFirstBCurve) WriteInvertingCurve(bCurves);
            else if (unalignedCurves) WriteGammaCurve(bCurves);
            else WriteIdentityCurve(bCurves);
        }

        var clut = new MemoryStream();
        for (int i = 0; i < 16; i++) clut.WriteByte((byte)(i < inputs ? grid[i] : 0));
        clut.WriteByte(2);                               // 16-bit samples
        clut.WriteByte(0); clut.WriteByte(0); clut.WriteByte(0);
        foreach (int[] cell in cells)
            for (int k = 0; k < outputs; k++) WriteU16(clut, cell[k]);

        byte[] a = aCurves.ToArray(), b = bCurves.ToArray(), c = clut.ToArray();

        int bOffset = 32;
        int clutOffset = bOffset + b.Length;
        int aOffset = clutOffset + c.Length;
        int tagBytes = aOffset + a.Length;

        var tag = new byte[tagBytes];
        WriteAsciiAt(tag, 0, "mAB ");
        tag[8] = (byte)inputs;
        tag[9] = outputs;
        WriteU32At(tag, 12, (uint)bOffset);
        WriteU32At(tag, 16, 0);                          // no matrix
        WriteU32At(tag, 20, 0);                          // no M curves
        WriteU32At(tag, 24, (uint)clutOffset);
        WriteU32At(tag, 28, (uint)aOffset);
        Buffer.BlockCopy(b, 0, tag, bOffset, b.Length);
        Buffer.BlockCopy(c, 0, tag, clutOffset, c.Length);
        Buffer.BlockCopy(a, 0, tag, aOffset, a.Length);

        return WrapInProfile(dataSpace, tag);
    }

    /// <summary>An <c>'mAB '</c> with M curves, a matrix and B curves, and no CLUT.</summary>
    private static byte[] MabMatrixProfile(string dataSpace, double[] matrix,
                                           bool invertFirstMCurve = false)
    {
        const int channels = 3;

        var mCurves = new MemoryStream();
        for (int i = 0; i < channels; i++)
        {
            if (i == 0 && invertFirstMCurve) WriteInvertingCurve(mCurves);
            else WriteIdentityCurve(mCurves);
        }

        var bCurves = new MemoryStream();
        for (int i = 0; i < channels; i++) WriteIdentityCurve(bCurves);

        var matrixBytes = new MemoryStream();
        foreach (double v in matrix) WriteU32(matrixBytes, unchecked((uint)(int)Math.Round(v * 65536.0)));

        byte[] m = mCurves.ToArray(), b = bCurves.ToArray(), mat = matrixBytes.ToArray();

        int bOffset = 32;
        int matrixOffset = bOffset + b.Length;
        int mOffset = matrixOffset + mat.Length;
        int tagBytes = mOffset + m.Length;

        var tag = new byte[tagBytes];
        WriteAsciiAt(tag, 0, "mAB ");
        tag[8] = channels;
        tag[9] = channels;
        WriteU32At(tag, 12, (uint)bOffset);
        WriteU32At(tag, 16, (uint)matrixOffset);
        WriteU32At(tag, 20, (uint)mOffset);
        WriteU32At(tag, 24, 0);                          // no CLUT
        WriteU32At(tag, 28, 0);                          // no A curves
        Buffer.BlockCopy(b, 0, tag, bOffset, b.Length);
        Buffer.BlockCopy(mat, 0, tag, matrixOffset, mat.Length);
        Buffer.BlockCopy(m, 0, tag, mOffset, m.Length);

        return WrapInProfile(dataSpace, tag);
    }

    /// <summary>A header and a one-entry tag table around an <c>A2B0</c> tag.</summary>
    private static byte[] WrapInProfile(string dataSpace, byte[] tag)
    {
        const int headerBytes = 128;
        int tagOffset = headerBytes + 4 + 12;
        int total = tagOffset + tag.Length;

        var profile = new byte[total];
        WriteU32At(profile, 0, (uint)total);
        WriteAsciiAt(profile, 12, "prtr");
        WriteAsciiAt(profile, 16, dataSpace);
        WriteAsciiAt(profile, 20, "Lab ");
        WriteAsciiAt(profile, 36, "acsp");
        WriteU32At(profile, 128, 1);
        WriteAsciiAt(profile, 132, "A2B0");
        WriteU32At(profile, 136, (uint)tagOffset);
        WriteU32At(profile, 140, (uint)tag.Length);
        Buffer.BlockCopy(tag, 0, profile, tagOffset, tag.Length);
        return profile;
    }

    /// <summary>A two-entry sampled <c>curveType</c>: 0 → 0, 1 → 1. 16 bytes, already aligned.</summary>
    private static void WriteIdentityCurve(MemoryStream s)
    {
        WriteAscii(s, "curv");
        WriteU32(s, 0);
        WriteU32(s, 2);
        WriteU16(s, 0x0000);
        WriteU16(s, 0xFFFF);
    }

    /// <summary>A two-entry sampled <c>curveType</c>: 0 → 1, 1 → 0.</summary>
    /// <remarks>
    /// <b>An identity curve cannot show that its stage ran.</b> Every fixture whose subject is
    /// something else uses one, so a mutation deleting a whole curve stage survived them all;
    /// this is what the three stage tests use instead.
    /// </remarks>
    private static void WriteInvertingCurve(MemoryStream s)
    {
        WriteAscii(s, "curv");
        WriteU32(s, 0);
        WriteU32(s, 2);
        WriteU16(s, 0xFFFF);
        WriteU16(s, 0x0000);
    }

    /// <summary>
    /// A one-entry <c>curveType</c> holding a u8Fixed8 gamma of 1.0 — the identity, in 14 bytes,
    /// so the next element starts two bytes into the padding.
    /// </summary>
    private static void WriteGammaCurve(MemoryStream s)
    {
        WriteAscii(s, "curv");
        WriteU32(s, 0);
        WriteU32(s, 1);
        WriteU16(s, 0x0100);        // 1.0 in u8Fixed8
        WriteU16(s, 0);             // the padding to the next four-byte boundary
    }

    private static void WriteAscii(MemoryStream s, string v)
    {
        byte[] b = Encoding.ASCII.GetBytes(v);
        s.Write(b, 0, b.Length);
    }

    private static void WriteU16(MemoryStream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteU32(MemoryStream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void WriteU32At(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16);
        d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }

    private static void WriteAsciiAt(byte[] d, int o, string v)
    {
        byte[] b = Encoding.ASCII.GetBytes(v);
        Buffer.BlockCopy(b, 0, d, o, b.Length);
    }

    private static byte[] IccFillPdf(byte[] profile, int components, string operands)
    {
        var offsets = new List<int>();
        string content = $"q /CS0 cs {operands} sc 0 0 100 100 re f Q";
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            null,
        };

        var bytes = new MemoryStream();
        void Append(string s) => bytes.Write(Encoding.ASCII.GetBytes(s), 0,
                                             Encoding.ASCII.GetByteCount(s));

        Append("%PDF-1.7\n");
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add((int)bytes.Length);
            Append($"{i + 1} 0 obj\n");
            if (objects[i] != null)
            {
                Append(objects[i]);
            }
            else
            {
                Append($"<< /N {components} /Length {profile.Length} >>\nstream\n");
                bytes.Write(profile, 0, profile.Length);
                Append("\nendstream");
            }
            Append("\nendobj\n");
        }

        int xref = (int)bytes.Length;
        Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) Append(off.ToString("D10") + " 00000 n \n");
        Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        Append($"startxref\n{xref}\n%%EOF\n");
        return bytes.ToArray();
    }
}
