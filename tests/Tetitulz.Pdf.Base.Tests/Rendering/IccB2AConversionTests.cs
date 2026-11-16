// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>B2A0</c> — converting sRGB <b>into</b> a destination profile's device space.
/// </summary>
/// <remarks>
/// <para><b>The direction the tree had never gone.</b> Everything else runs device → PCS → sRGB;
/// this runs sRGB → PCS → device, which is what converting a document into a press profile needs.
/// The table type is the same either way — an ICC tag type does not change with its direction, only
/// the meaning of its numbers does — so this reuses <c>IccLut</c>'s reader rather than adding a
/// second one.</para>
///
/// <para><b>Expected values come from the CLUT's definition, not from the reader.</b> A synthetic
/// profile whose expectations were produced by running the parser would assert only that the code
/// agrees with itself, which is CLAUDE.md's third blind spot. Here the CLUT is written to a stated
/// rule — cyan follows the lightness axis, the other inks are zero — and the assertions are that
/// rule, computed independently.</para>
///
/// <para><b>And there is a second opinion, in both sample widths.</b>
/// <see cref="RealCmykProfile_Mft2_RoundTripsThroughItsOwnA2B"/> and
/// <see cref="RealCmykProfile_Mft1_RoundTripsThroughItsOwnA2B"/> run a real press profile's
/// <c>A2B0</c> and <c>B2A0</c> against each other. Those are two <i>separate tables</i> that the
/// profile's author made mutually inverse, so closing the loop is evidence about this code rather
/// than about its own encoder. They are host-gated, and say so when they skip.</para>
///
/// <para><b>Why two profiles rather than one.</b> A survey of this machine's colour directory found
/// 101 ICC profiles, of which 13 carry a <c>B2A0</c> — and <b>twelve of those thirteen are
/// <c>mft1</c></b>, the 8-bit form. <c>RSWOP.icm</c> is the host's only <c>mft2</c>. Validating
/// against it alone would have left the <i>common</i> real-world shape resting entirely on the
/// synthetic fixtures below, whose samples this file writes itself.</para>
/// </remarks>
public class IccB2AConversionTests
{
    // ================================================================
    //  A synthetic profile whose answer is known from its definition
    // ================================================================

    /// <summary>Black maps to the ink the CLUT's dark corner names.</summary>
    [Fact]
    public void BlackTakesTheClutsDarkEndOfTheLightnessAxis()
    {
        float[] cmyk = Convert(LightnessRampCmykProfile(), SKColors.Black);

        Assert.Equal(0f, cmyk[0], 2);   // C = 0 at L* = 0
        Assert.Equal(0f, cmyk[1], 2);
        Assert.Equal(0f, cmyk[2], 2);
        Assert.Equal(0f, cmyk[3], 2);
    }

    /// <summary>Near-white maps to the other end, so the PCS input really varies the answer.</summary>
    /// <remarks>
    /// <b>The pair is what makes either meaningful.</b> A constant CLUT would satisfy the black
    /// case alone, and so would a converter that ignored its input entirely.
    /// </remarks>
    [Fact]
    public void NearWhiteTakesTheClutsLightEndOfTheLightnessAxis()
    {
        float[] cmyk = Convert(LightnessRampCmykProfile(), NearWhite);

        Assert.Equal(0.9927f, cmyk[0], 3);   // C = L*/100.39 (legacy 16-bit Lab), L* 99.65 at sRGB 254
        Assert.Equal(0f, cmyk[1], 2);
    }

    /// <summary>A mid grey lands between them, which only interpolation can produce.</summary>
    /// <remarks>
    /// sRGB 128 is L* ≈ 53.6, not 50 — the transfer function is not linear in lightness. The
    /// assertion is therefore a band rather than a value: what is being pinned is that the result
    /// is strictly between the two ends, which a nearest-corner lookup would not give.
    /// </remarks>
    [Fact]
    public void AMidGreyLandsBetweenTheTwoEnds()
    {
        float[] cmyk = Convert(LightnessRampCmykProfile(), new SKColor(128, 128, 128));

        Assert.InRange(cmyk[0], 0.35f, 0.75f);
    }

    /// <summary>A near-black grey exercises CIE companding's linear segment.</summary>
    /// <remarks>
    /// <b>Neither black nor mid grey can test this.</b> CIE 1976 replaces the cube root with a
    /// straight line below t = 216/24389, which only a very dark colour reaches — and at pure black
    /// both branches give 0, so deleting the linear segment left every other case here green. sRGB
    /// 10 lands inside the segment with room to see it: Y = 0.00304 gives L* = 2.74 through the
    /// line and L* = 0.79 through the cube root alone, and this CLUT maps the first PCS axis
    /// straight onto cyan.
    /// </remarks>
    [Fact]
    public void ANearBlackGreyUsesTheLinearSegmentOfTheLabCompanding()
    {
        float[] cmyk = Convert(LightnessRampCmykProfile(), new SKColor(10, 10, 10));

        // L* 2.74 / 100.39 = 0.0273 with the linear segment; 0.0079 with the cube root alone.
        Assert.InRange(cmyk[0], 0.015f, 0.045f);
    }

    /// <summary>The output curves are applied, not skipped.</summary>
    /// <remarks>
    /// <b>Identity output curves cannot show this, and every other fixture here has them</b> — so
    /// a mutation dropping the curve stage entirely left the suite green. These curves descend, so
    /// applying them inverts the ramp: white asks for <i>no</i> cyan and black for all of it, the
    /// exact opposite of <see cref="NearWhiteTakesTheClutsLightEndOfTheLightnessAxis"/>.
    /// </remarks>
    [Fact]
    public void TheOutputCurvesAreApplied()
    {
        byte[] profile = LightnessRampCmykProfile(invertedOutputCurves: true);

        float[] white = Convert(profile, NearWhite);
        float[] black = Convert(profile, SKColors.Black);

        Assert.InRange(white[0], 0f, 0.05f);      // 0.996 through the CLUT, inverted to 0.004
        Assert.InRange(black[0], 0.95f, 1f);      // 0 through the CLUT, inverted to 1
    }

    /// <summary>The <b>input</b> curves are applied, not skipped.</summary>
    /// <remarks>
    /// <para><b>The twin of <see cref="TheOutputCurvesAreApplied"/>, and it was missing.</b> GAPS.md
    /// §9 carried it: every fixture here wrote a two-entry identity input curve, which is
    /// indistinguishable from no curve at all, so replacing the curve lookup with the raw input
    /// survived the whole suite. An identity curve cannot show that a stage runs — that is the
    /// same argument the output-curve test above is built on, applied to the other end of the
    /// pipeline.</para>
    ///
    /// <para><b>The expectation is the fixture's rule, not a captured number.</b> These input
    /// curves descend, so the L* axis is read backwards: white (L* = 100) lands on the CLUT's
    /// <i>dark</i> corner and black on its light one — the exact inversion of
    /// <see cref="NearWhiteTakesTheClutsLightEndOfTheLightnessAxis"/>, reached through the input stage
    /// rather than the output one. With the curve skipped, white would take the light end and ask
    /// for full cyan.</para>
    /// </remarks>
    [Fact]
    public void TheInputCurvesAreApplied()
    {
        byte[] profile = LightnessRampCmykProfile(invertedInputCurves: true);

        float[] white = Convert(profile, NearWhite);
        float[] black = Convert(profile, SKColors.Black);

        Assert.InRange(white[0], 0f, 0.05f);      // L* 1.0 → curve → 0 → the CLUT's dark corner
        Assert.InRange(black[0], 0.95f, 1f);      // L* 0   → curve → 1 → its light corner
    }

    /// <summary>
    /// A header declaring more CLUT samples than a <c>long</c> can count is refused, not wrapped.
    /// </summary>
    /// <remarks>
    /// <para><b>The guard this exercises is the <c>break</c> in the sample-count loop</b>, and
    /// GAPS.md §9 carried it as unexercised. The field widths permit eight input channels, fifteen
    /// outputs and 255 grid points, so the declared sample count is 15 × 255⁸ ≈ 2.7 × 10²⁰ — which
    /// does not fit in a <c>long</c>. Multiplying it out wraps, and a wrapped total can compare
    /// <i>smaller</i> than the tag, so the size check passes a profile it should refuse and the
    /// parser goes on to index with a nonsense offset.</para>
    ///
    /// <para><b>The refusal alone does not show the guard works, and checking that was the
    /// finding.</b> Removing the <c>break</c> still refuses this profile: the wrapped product
    /// happens to land on a large positive number, so the size comparison holds anyway. What the
    /// guard actually protects is that the reported count is <b>exact</b> — the loop stops as soon
    /// as the running total passes the profile-byte cap, at which point the answer cannot change,
    /// so the number in the message is a real byte count rather than whatever the multiplication
    /// wrapped to. This therefore asserts the number, stated from the format's own arithmetic
    /// rather than captured from a run.</para>
    ///
    /// <para>The body is the header and nothing else — the fixture cannot carry the tables it
    /// declares, which is the whole point of declaring them.</para>
    /// </remarks>
    [Fact]
    public void AClutDeclaringMoreSamplesThanALongCanCount_IsRefused_WithAnExactCount()
    {
        // The count the guard should report, from the fixture's declared fields:
        //   the mft2 header, the two curve tables, and the CLUT as far as the cap.
        //   15 outputs x 255^3 is the first product past the 16 MiB cap, so the loop stops there.
        const long header = 52;                       // mft2: 4+4+4+36+4
        const long inputTable = 8L * 2 * 2;           // 8 channels x 2 entries x 2 bytes
        const long outputTable = 15L * 2 * 2;         // 15 channels x 2 entries x 2 bytes
        const long clut = 15L * 255 * 255 * 255 * 2;  // samples x 2 bytes
        const long expected = header + inputTable + outputTable + clut;

        // Read as an A2B0, whose input may have eight channels; a B2A0's input is the PCS, three,
        // and is refused for that before its size is counted.
        byte[] profile = OversizedDeclaredClutProfile();
        const int tagOffset = 128 + 16;
        IccLut lut = IccLut.Parse(profile, tagOffset, profile.Length - tagOffset, labPcs: true, out string reason);

        Assert.Null(lut);   // a profile declaring 15 x 255^8 samples must not be read
        Assert.Contains("bytes of tables", reason, StringComparison.Ordinal);
        Assert.Contains(expected.ToString(CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <c>A2B0</c> whose header declares the largest table the field widths allow, over a tag
    /// body consisting of that header alone.
    /// </summary>
    private static byte[] OversizedDeclaredClutProfile()
    {
        var lut = new MemoryStream();
        WriteAscii(lut, "mft2");
        WriteU32(lut, 0);
        lut.WriteByte(8);      // input channels — the evaluator's maximum
        lut.WriteByte(15);     // output channels — its maximum
        lut.WriteByte(255);    // grid points per axis — the widest a byte can say
        lut.WriteByte(0);

        int[] identity = { 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000 };
        foreach (int v in identity) WriteU32(lut, (uint)v);

        WriteU16(lut, 2);      // input curve entries
        WriteU16(lut, 2);      // output curve entries

        // …and nothing else. The tables this header promises are larger than the address space.
        return WrapInProfile("CMYK", "Lab ", "A2B0", lut.ToArray());
    }

    /// <summary>An XYZ PCS is encoded as XYZ, not as Lab.</summary>
    /// <remarks>
    /// <b>Both encodings are three values in [0,1], so nothing structural separates them</b> — a
    /// converter running the Lab path over an XYZ profile produces a plausible ink mixture and no
    /// error at all, which is why there has to be a fixture whose PCS is XYZ. The two disagree
    /// numerically, and that is the only way to tell them apart: sRGB white is L* = 100, which
    /// fills the first axis, but X = 0.9642 in u1Fixed15, which is just under half of it.
    /// </remarks>
    [Fact]
    public void AnXyzPcsProfileIsEncodedAsXyz()
    {
        float[] cmyk = Convert(LightnessRampCmykProfile(pcs: "XYZ "), NearWhite);

        // 0.9642 * 32768/65535 = 0.482. The Lab path would drive the axis to 0.996.
        Assert.InRange(cmyk[0], 0.35f, 0.65f);
    }

    /// <summary>
    /// A <c>lut16Type</c> whose input is PCSXYZ runs its 3×3 matrix before the input curves (ICC.1:
    /// "the matrix is used only when the input colour space is PCSXYZ"). Doubling X here moves cyan
    /// from 0.482 (the identity, <see cref="AnXyzPcsProfileIsEncodedAsXyz"/>) to 0.964. The reader
    /// used to skip the matrix entirely.
    /// </summary>
    [Fact]
    public void AnXyzInputRunsTheLutsMatrixBeforeItsCurves()
    {
        int[] doubleX = { 0x20000, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000 };

        float[] scaled = Convert(LightnessRampCmykProfile(pcs: "XYZ ", matrix: doubleX), NearWhite);
        float[] identity = Convert(LightnessRampCmykProfile(pcs: "XYZ "), NearWhite);

        Assert.InRange(scaled[0], 0.93f, 0.99f);
        Assert.InRange(identity[0], 0.45f, 0.51f);
    }

    /// <summary>
    /// And only then: a Lab-PCS table ignores its matrix. A matrix that zeroes the first axis would
    /// take cyan to 0; the lightness ramp's own answer near white stands.
    /// </summary>
    [Fact]
    public void ALabInputIgnoresTheLutsMatrix()
    {
        int[] zeroFirst = { 0, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000 };

        float[] cmyk = Convert(LightnessRampCmykProfile(matrix: zeroFirst), NearWhite);

        Assert.InRange(cmyk[0], 0.98f, 1.0f);
    }

    /// <summary>
    /// A grey destination's <c>B2A0</c> has one output — a device channel, not a PCS — and
    /// <c>ParseB2A</c> accepts a GRAY data space for exactly that. The lut reader refused any table
    /// with fewer than three outputs, which is A2B's rule. Here the one output follows L*.
    /// </summary>
    [Fact]
    public void AGreyDestinationConvertsThroughItsOneOutputChannel()
    {
        var clut = new int[2 * 2 * 2];
        for (int cell = 4; cell < 8; cell++) clut[cell] = 0xFFFF;   // L* index 1: the light half

        IccOutputTransform transform = IccOutputTransform.TryCreate(
            BuildB2AProfile("GRAY", inputs: 3, outputs: 1, grid: 2, clut: clut, type: "mft2"),
            IccRenderingIntent.Perceptual, blackPointCompensation: false, out string reason);
        Assert.True(transform != null, $"conversion refused: {reason}");
        Assert.Equal(1, transform.Channels);

        var grey = new float[1];
        Assert.True(transform.TryConvert(NearWhite, grey));
        Assert.InRange(grey[0], 0.98f, 1.0f);
        Assert.True(transform.TryConvert(new SKColor(0, 0, 0), grey));
        Assert.InRange(grey[0], 0f, 0.02f);
    }

    /// <summary>
    /// White is kept at the destination's own white — the reference colour engine's <c>_cmsEndPointsBySpace</c>: no ink for
    /// CMYK, full scale for RGB and grey. Writing zeros for every space turned sRGB white black in an
    /// RGB or grey destination. The tables here answer 0.5 everywhere, so only the fix-up reaches the
    /// ends.
    /// </summary>
    [Fact]
    public void WhiteGoesToTheDestinationsOwnWhite()
    {
        float[] White(string space, int outputs)
        {
            var clut = new int[2 * 2 * 2 * outputs];
            for (int i = 0; i < clut.Length; i++) clut[i] = 0x8000;
            IccOutputTransform t = IccOutputTransform.TryCreate(
                BuildB2AProfile(space, inputs: 3, outputs: outputs, grid: 2, clut: clut, type: "mft2"),
                IccRenderingIntent.Perceptual, blackPointCompensation: false, out string reason);
            Assert.True(t != null, $"conversion refused: {reason}");
            var device = new float[outputs];
            Assert.True(t.TryConvert(SKColors.White, device));
            return device;
        }

        Assert.Equal(new[] { 1f, 1f, 1f }, White("RGB ", 3));
        Assert.Equal(new[] { 1f }, White("GRAY", 1));
        Assert.Equal(new[] { 0f, 0f, 0f, 0f }, White("CMYK", 4));
    }

    // ================================================================
    //  Refusals
    // ================================================================

    /// <summary>
    /// A CLUT of one point per axis has no cell to interpolate across and is refused by name rather
    /// than read as a constant colour; two points is the control.
    /// </summary>
    [Fact]
    public void AOnePointGridIsRefused()
    {
        byte[] onePoint = BuildB2AProfile("CMYK", inputs: 3, outputs: 4, grid: 1, clut: new int[4], type: "mft2");
        byte[] twoPoint = BuildB2AProfile("CMYK", inputs: 3, outputs: 4, grid: 2, clut: new int[2 * 2 * 2 * 4], type: "mft2");

        Assert.Null(IccProfile.ParseB2A(onePoint, out _, out string reason));
        Assert.Contains("grid point", reason);
        Assert.NotNull(IccProfile.ParseB2A(twoPoint, out _, out _));
    }

    /// <summary>A <c>B2A</c> lut's input is the PCS, so one declaring four inputs is refused for that.</summary>
    [Fact]
    public void AB2ALutWithoutThreePcsInputsIsRefused()
    {
        byte[] profile = BuildB2AProfile("GRAY", inputs: 4, outputs: 1, grid: 2,
                                         clut: new int[2 * 2 * 2 * 2], type: "mft2");

        Assert.Null(IccProfile.ParseB2A(profile, out _, out string reason));
        Assert.Contains("input is the PCS", reason);
    }

    /// <summary>
    /// The one-output allowance is B2A's alone: an <c>A2B0</c> lut with one output is still refused,
    /// as its outputs are the PCS. The control reads the same table as a <c>B2A0</c>.
    /// </summary>
    [Fact]
    public void AnA2BLutWithFewerThanThreeOutputsIsStillRefused()
    {
        byte[] profile = BuildB2AProfile("GRAY", inputs: 3, outputs: 1, grid: 2,
                                         clut: new int[2 * 2 * 2], type: "mft2", tag: "A2B0");
        const int tagOffset = 128 + 16;
        int size = profile.Length - tagOffset;

        Assert.Null(IccLut.Parse(profile, tagOffset, size, labPcs: true, out string reason));
        Assert.Contains("PCS needs at least 3", reason);
        Assert.NotNull(IccLut.Parse(profile, tagOffset, size, labPcs: true, out _, bToA: true));
    }

    /// <summary>A profile with no <c>B2A0</c> is refused by name.</summary>
    /// <remarks>
    /// Most profiles in the wild carry only <c>A2B0</c> — a scanner or camera profile has no
    /// reason to describe the other direction — so this is the common answer, not an edge case.
    /// </remarks>
    [Fact]
    public void AProfileWithNoB2A0IsRefusedWithAReason()
    {
        Assert.Null(IccProfile.ParseB2A(A2BOnlyProfile(), out _, out string reason));
        Assert.Contains("no B2A0", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    //  The v4 lutBToAType ('mBA ') form, ICC.1:2022 §10.13
    // ================================================================

    /// <summary>An <c>mBA </c> table converts through its B curves, CLUT and A curves.</summary>
    /// <remarks>
    /// <b>The CLUT crosses its axes on purpose</b>: cyan follows the <i>second</i> PCS axis (a*) and
    /// magenta the <i>first</i> (L*). sRGB white is L* = 100, a* = b* = 0, which the v4 encoding puts at
    /// 1.0 and 128/255 — so magenta comes out full and cyan at 0.502, and a pipeline that read the
    /// axes in the wrong order, or wrote outputs to the wrong inks, lands elsewhere.
    /// </remarks>
    [Fact]
    public void AnMbaTableConvertsThroughItsBCurvesClutAndACurves()
    {
        float[] cmyk = Convert(MbaCrossedAxesProfile(), NearWhite);

        Assert.InRange(cmyk[0], 0.495f, 0.51f);   // C = a* position, 128/255
        Assert.InRange(cmyk[1], 0.99f, 1f);       // M = L* position
        Assert.Equal(0f, cmyk[2], 3);
        Assert.Equal(0f, cmyk[3], 3);
    }

    /// <summary>The B curves run on the PCS side, before the CLUT.</summary>
    /// <remarks>
    /// <b>This is the order the type exists to get right.</b> An inverted first B curve turns
    /// L* = 100 into 0 <i>before</i> the table, so it is magenta — the ink that follows L* — that
    /// empties. Applied after the CLUT instead, as <c>lutAToBType</c>'s order would have it, the same
    /// curve would reach the first <i>output</i> and invert cyan.
    /// </remarks>
    [Fact]
    public void TheBCurvesRunBeforeTheClut()
    {
        float[] cmyk = Convert(MbaCrossedAxesProfile(invertFirstBCurve: true), NearWhite);

        Assert.InRange(cmyk[0], 0.495f, 0.51f);   // cyan untouched
        Assert.InRange(cmyk[1], 0f, 0.01f);       // magenta emptied: L* was inverted on the way in
    }

    /// <summary>The A curves run on the device side, after the CLUT.</summary>
    /// <remarks>
    /// <b>The inverted curve is magenta's, not cyan's, and that choice is the test.</b> White puts
    /// a* at 128/255, so inverting cyan moves it by one count — a first version did exactly that,
    /// asserted a range holding both 127/255 and 128/255, and survived the A curves being deleted.
    /// Magenta sits at 1.0 and inverts to 0. Applied before the CLUT instead, the same curve would
    /// reach the second <i>input</i>, a*, and move cyan while leaving magenta full.
    /// </remarks>
    [Fact]
    public void TheACurvesRunAfterTheClut()
    {
        float[] cmyk = Convert(MbaCrossedAxesProfile(invertSecondACurve: true), NearWhite);

        Assert.InRange(cmyk[0], 0.495f, 0.51f);   // cyan untouched
        Assert.InRange(cmyk[1], 0f, 0.01f);       // magenta inverted after the table: 1 → 0
    }

    /// <summary>The matrix and M curves run between the B curves and the CLUT.</summary>
    /// <remarks>
    /// A matrix that swaps the first two PCS channels hands the CLUT a* where L* was and L* where
    /// a* was, so cyan and magenta trade values. Dropping the matrix stage leaves them where the
    /// plain fixture has them.
    /// </remarks>
    [Fact]
    public void TheMatrixRunsBeforeTheClut()
    {
        double[] swap = { 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0 };
        float[] cmyk = Convert(MbaCrossedAxesProfile(matrix: swap), NearWhite);

        Assert.InRange(cmyk[0], 0.99f, 1f);       // C now follows what was L*
        Assert.InRange(cmyk[1], 0.495f, 0.51f);   // M now follows what was a*
    }

    /// <summary>
    /// The M curves run after the matrix: with an identity matrix, inverting the first M curve turns
    /// near-white's L* to the dark end, so magenta (which follows L*) goes from full to none.
    /// </summary>
    [Fact]
    public void TheMCurvesRunAfterTheMatrix()
    {
        double[] identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };

        float[] plain = Convert(MbaCrossedAxesProfile(matrix: identity), NearWhite);
        float[] inverted = Convert(MbaCrossedAxesProfile(matrix: identity, invertFirstMCurve: true), NearWhite);

        Assert.InRange(plain[1], 0.99f, 1f);
        Assert.InRange(inverted[1], 0f, 0.01f);
    }

    /// <summary>An <c>mBA </c> table is fed the v4 PCS Lab encoding, not the legacy one.</summary>
    /// <remarks>
    /// <b>Below white on purpose</b>, for the reason <c>IccLutPipeline</c>'s remarks give: at L* = 100
    /// the legacy scale's error is clamped away. sRGB 128 is linear 0.21586, so L* = 116 ·
    /// 0.21586^(1/3) − 16 = 53.585, which the v4 encoding puts at 0.53585 and the legacy 16-bit one
    /// at 0.53377 — magenta follows L* straight through.
    /// </remarks>
    [Fact]
    public void AnMbaTableIsFedTheV4LabEncoding()
    {
        float[] cmyk = Convert(MbaCrossedAxesProfile(), new SKColor(128, 128, 128));

        Assert.InRange(cmyk[1], 0.5352f, 0.5366f);
    }

    /// <summary>Each v4 type is accepted only in the direction it runs.</summary>
    [Fact]
    public void AV4TableInTheWrongDirectionIsRefusedByName()
    {
        byte[] mbaAsA2B = MbaCrossedAxesProfile(tag: "A2B0");
        byte[] mabAsB2A = MbaCrossedAxesProfile(signature: "mAB ");

        Assert.Null(IccProfile.ParseB2A(mabAsB2A, out _, out string b2aReason));
        Assert.Contains("runs device to PCS", b2aReason, StringComparison.Ordinal);

        int off = 128 + 4 + 12;
        Assert.Null(IccLut.Parse(mbaAsA2B, off, mbaAsA2B.Length - off, labPcs: true, out string a2bReason));
        Assert.Contains("runs PCS to device", a2bReason, StringComparison.Ordinal);
    }

    /// <summary>A BToA pipeline evaluates raw and refuses to answer as a PCS decode.</summary>
    /// <remarks>
    /// <c>IccLut</c> never hands a <c>B2A</c> table to the PCS-decoding path, so only the pipeline
    /// itself can show that its own guard holds: device values decoded as Lab would be a colour
    /// from the wrong side of the table.
    /// </remarks>
    [Fact]
    public void AnMbaPipelineAnswersOnlyInItsOwnDirection()
    {
        byte[] profile = MbaCrossedAxesProfile();
        const int off = 128 + 4 + 12;
        IccLutPipeline pipeline = IccLutPipeline.Parse(profile, off, profile.Length - off, bToA: true, out string reason);
        Assert.True(pipeline != null, reason);

        var white = new float[] { 1f, 128f / 255f, 128f / 255f };
        Assert.False(pipeline.TryEvaluate(white, out _, out _, out _));

        var outputs = new double[4];
        Assert.True(pipeline.TryEvaluateRaw(white, outputs));
        Assert.Equal(1.0, outputs[1], 3);   // magenta follows L*
    }

    /// <summary>An <c>mBA </c> whose input is not the three-component PCS is refused by name.</summary>
    [Fact]
    public void AnMbaTableWithoutAThreeChannelInputIsRefusedByName()
    {
        Assert.Null(IccProfile.ParseB2A(MbaCrossedAxesProfile(declaredInputs: 4), out _, out string reason));
        Assert.Contains("three-component PCS", reason, StringComparison.Ordinal);
    }
    /// <summary>A table producing the wrong number of inks for its data space is refused.</summary>
    /// <remarks>
    /// <b>A profile whose header says CMYK and whose table yields three outputs is malformed</b>,
    /// and evaluating it would fill three ink planes and leave the fourth holding whatever the
    /// caller's buffer held. Refusing names the disagreement; the alternative is a document whose
    /// black plate is uninitialised memory. No other fixture here disagrees with itself, so
    /// nothing else reaches this guard.
    /// </remarks>
    [Fact]
    public void ATableWhoseChannelCountContradictsTheDataSpaceIsRefused()
    {
        byte[] profile = BuildB2AProfile("CMYK", inputs: 3, outputs: 3, grid: 2,
                                         clut: new int[2 * 2 * 2 * 3], type: "mft2");

        Assert.Null(IccProfile.ParseB2A(profile, out _, out string reason));

        // "channels" alone would also match IccLut's own output-channel refusal, which three
        // outputs do not trigger; "data space" is this guard's.
        Assert.Contains("data space", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An adversarial profile that is not a profile at all is refused, not read.</summary>
    [Fact]
    public void AnAdversarialNonProfileIsRefused()
    {
        Assert.Null(IccProfile.ParseB2A(new byte[400], out _, out string reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    // ================================================================
    //  The second opinion: a real press profile, against itself
    // ================================================================

    /// <summary>
    /// A real CMYK profile's <c>lut16Type</c> <c>B2A0</c> approximately inverts its own
    /// <c>A2B0</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Two separate tables, so this is not a round trip against my own encoder.</b> The
    /// profile's author built <c>A2B0</c> (CMYK → Lab) and <c>B2A0</c> (Lab → CMYK) to be mutually
    /// inverse; if this code reads either one wrongly, the loop will not close. That is the only
    /// independent check available without a reference CMM.</para>
    ///
    /// <para><b>The tolerance is wide on purpose.</b> An A2B/B2A pair is not exactly invertible —
    /// the tables are sampled, the intent is perceptual, and out-of-gamut colours are clipped by
    /// construction. What a structural misreading produces is not a small error but a wildly
    /// different ink mixture, which is what this catches.</para>
    /// </remarks>
    [RealIccProfileFact("RSWOP.icm")]
    public void RealCmykProfile_Mft2_RoundTripsThroughItsOwnA2B()
        => RoundTripThroughOwnTables("RSWOP.icm");

    /// <summary>
    /// The same, for the <c>lut8Type</c> form — which is what most real profiles use.
    /// </summary>
    /// <remarks>
    /// <b>An 8-bit CLUT is a different read, not a smaller one.</b> <c>mft1</c> has a 48-byte
    /// header rather than 52, carries no declared curve-entry counts (they are fixed at 256), and
    /// stores one-byte samples — so every bound and every sample fetch differs from <c>mft2</c>'s.
    /// Twelve of this host's thirteen <c>B2A0</c> profiles take this form, and until this test
    /// existed not one of them had been read in this direction.
    /// </remarks>
    [RealIccProfileFact("USWebCoatedSWOP.icc")]
    public void RealCmykProfile_Mft1_RoundTripsThroughItsOwnA2B()
        => RoundTripThroughOwnTables("USWebCoatedSWOP.icc");

    /// <summary>
    /// A real profile whose data space this does not convert into is refused, by name.
    /// </summary>
    /// <remarks>
    /// <b>A refusal fixture nobody here wrote.</b> <c>stdpyccl.icm</c> is a PhotoYCC colour-space
    /// profile: a well-formed <c>B2A0</c>, a Lab PCS, and a <c>3CLR</c> data space that names no
    /// particular colorants. Refusing it names the data space; the alternative is emitting three
    /// components as though their meaning were known.
    /// </remarks>
    [RealIccProfileFact("stdpyccl.icm")]
    public void ARealProfileWithAnUnconvertibleDataSpaceIsRefused()
    {
        byte[] profile = File.ReadAllBytes(RealIccProfile.Path("stdpyccl.icm"));

        Assert.Null(IccProfile.ParseB2A(profile, out _, out string reason));
        Assert.Contains("3CLR", reason, StringComparison.Ordinal);
    }

    private static void RoundTripThroughOwnTables(string fileName)
    {
        byte[] profile = File.ReadAllBytes(RealIccProfile.Path(fileName));

        // Device → sRGB through A2B0, then sRGB → device through B2A0.
        IccProfile forward = IccProfile.Parse(profile);
        Assert.Equal(4, forward.ComponentCount);

        var ink = new float[] { 0.2f, 0.35f, 0.15f, 0.05f };

        // PERCEPTUAL EXPLICITLY, because the pair has to match. Convert reads B2A0,
        // which is the perceptual table; the default intent is media-relative colorimetric, so the
        // parameterless overload would evaluate A2B1 forward and B2A0 back. Those are two tables
        // the profile's author never built to be inverses, and pairing them would leave this test
        // passing on a wider tolerance while no longer checking what its remarks claim.
        Assert.True(forward.TryEvaluate(ink, IccRenderingIntent.Perceptual, out SKColor rendered),
                    "the profile's own A2B0 could not evaluate an in-gamut ink mixture");

        float[] back = Convert(profile, rendered);

        for (int i = 0; i < 4; i++)
            Assert.InRange(back[i], ink[i] - 0.30f, ink[i] + 0.30f);
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    /// <summary>
    /// The table's answer for <paramref name="colour"/>: the perceptual <c>B2A0</c> through
    /// <c>IccOutputTransform</c>, the one route into an output profile, without black-point
    /// compensation — so the CLUT's own value comes back, which is what every rule here states.
    /// </summary>
    private static float[] Convert(byte[] profile, SKColor colour)
    {
        IccOutputTransform transform = IccOutputTransform.TryCreate(
            profile, IccRenderingIntent.Perceptual, blackPointCompensation: false, out string reason);
        Assert.True(transform != null, $"conversion refused: {reason}");
        var cmyk = new float[4];
        Assert.True(transform.TryConvert(colour, cmyk), "the table refused a colour");
        return cmyk;
    }

    /// <summary>
    /// The probe for the table's light end. Pure sRGB white takes the transform's white fix-up
    /// (no ink, the reference colour engine's rule) before any table is read, so the light end is probed one level below:
    /// L* 99.65, inside every tolerance here that white's L* 100 was.
    /// </summary>
    private static readonly SKColor NearWhite = new(254, 254, 254);

    /// <summary>
    /// A CMYK profile whose <c>B2A0</c> sets cyan from the lightness axis and every other ink to
    /// zero.
    /// </summary>
    /// <remarks>
    /// <b>A stated rule rather than captured output.</b> The CLUT's 2×2×2 grid takes its first
    /// index from L*, so cyan is 0 at the dark corner and full at the light one, whatever a* and
    /// b* are. Every expectation in this file is that sentence, not a number the parser produced.
    /// </remarks>
    /// <param name="invertedOutputCurves">
    /// Descending output curves instead of identity ones. Identity curves are indistinguishable
    /// from no curves at all, so the default cannot pin that the stage runs.
    /// </param>
    /// <param name="pcs">
    /// The header's PCS signature. With <c>"XYZ "</c> the first CLUT index is X rather than L*,
    /// which is what makes the two encodings tell apart.
    /// </param>
    private static byte[] LightnessRampCmykProfile(
        bool invertedOutputCurves = false, string pcs = "Lab ", bool invertedInputCurves = false,
        int[] matrix = null)
    {
        // grid^inputs cells, four outputs each; the first input varies least rapidly (ICC.1).
        const int grid = 2, inputs = 3, outputs = 4;
        var clut = new int[grid * grid * grid * outputs];

        for (int l = 0; l < grid; l++)
            for (int a = 0; a < grid; a++)
                for (int b = 0; b < grid; b++)
                {
                    int cell = (((l * grid) + a) * grid) + b;
                    clut[(cell * outputs) + 0] = l == 0 ? 0 : 0xFFFF;   // C follows L*
                }

        return BuildB2AProfile("CMYK", inputs, outputs, grid, clut, "mft2",
                               pcs: pcs, invertedOutputCurves: invertedOutputCurves,
                               invertedInputCurves: invertedInputCurves, matrix: matrix);
    }

    /// <summary>
    /// A CMYK profile whose <c>B2A0</c> is an <c>'mBA '</c>: B curves, optionally a matrix and M
    /// curves, a 2×2×2 CLUT, and A curves.
    /// </summary>
    /// <remarks>
    /// <b>The CLUT's rule, stated rather than captured:</b> cyan is the grid position along the
    /// second PCS axis, magenta along the first, yellow and black zero. Every expectation above is
    /// that sentence, and the layout — header, B curves, matrix, M curves, CLUT, A curves, each at a
    /// four-byte boundary — is ICC.1:2022 Table 47's.
    /// </remarks>
    private static byte[] MbaCrossedAxesProfile(
        bool invertFirstBCurve = false, bool invertSecondACurve = false, double[] matrix = null,
        string tag = "B2A0", string signature = "mBA ", int declaredInputs = 3,
        bool invertFirstMCurve = false)
    {
        const int inputs = 3, outputs = 4, grid = 2;

        var b = new MemoryStream();
        for (int i = 0; i < inputs; i++) WriteCurve(b, invert: i == 0 && invertFirstBCurve);

        var m = new MemoryStream();
        var mat = new MemoryStream();
        if (matrix != null)
        {
            for (int i = 0; i < inputs; i++) WriteCurve(m, invert: i == 0 && invertFirstMCurve);
            foreach (double v in matrix) WriteU32(mat, unchecked((uint)(int)Math.Round(v * 65536.0)));
        }

        var clut = new MemoryStream();
        for (int i = 0; i < 16; i++) clut.WriteByte((byte)(i < inputs ? grid : 0));
        clut.WriteByte(2);                                    // 16-bit samples
        clut.WriteByte(0); clut.WriteByte(0); clut.WriteByte(0);
        for (int l = 0; l < grid; l++)                        // the first input varies least rapidly
            for (int a = 0; a < grid; a++)
                for (int bb = 0; bb < grid; bb++)
                {
                    WriteU16(clut, a == 0 ? 0 : 0xFFFF);      // C follows a*
                    WriteU16(clut, l == 0 ? 0 : 0xFFFF);      // M follows L*
                    WriteU16(clut, 0);
                    WriteU16(clut, 0);
                }

        var aCurves = new MemoryStream();
        for (int k = 0; k < outputs; k++) WriteCurve(aCurves, invert: k == 1 && invertSecondACurve);

        byte[] bBytes = b.ToArray(), matBytes = mat.ToArray(), mBytes = m.ToArray();
        byte[] clutBytes = clut.ToArray(), aBytes = aCurves.ToArray();

        int bOffset = 32;
        int matrixOffset = bOffset + bBytes.Length;
        int mOffset = matrixOffset + matBytes.Length;
        int clutOffset = mOffset + mBytes.Length;
        int aOffset = clutOffset + clutBytes.Length;

        var lut = new MemoryStream();
        WriteAscii(lut, signature);
        WriteU32(lut, 0);
        lut.WriteByte((byte)declaredInputs);
        lut.WriteByte(outputs);
        WriteU16(lut, 0);
        WriteU32(lut, (uint)bOffset);
        WriteU32(lut, matrix != null ? (uint)matrixOffset : 0u);
        WriteU32(lut, matrix != null ? (uint)mOffset : 0u);
        WriteU32(lut, (uint)clutOffset);
        WriteU32(lut, (uint)aOffset);
        lut.Write(bBytes, 0, bBytes.Length);
        lut.Write(matBytes, 0, matBytes.Length);
        lut.Write(mBytes, 0, mBytes.Length);
        lut.Write(clutBytes, 0, clutBytes.Length);
        lut.Write(aBytes, 0, aBytes.Length);

        return WrapInProfile("CMYK", "Lab ", tag, lut.ToArray());
    }

    /// <summary>A two-entry sampled <c>curveType</c>, 16 bytes: identity, or 0 → 1 and 1 → 0.</summary>
    private static void WriteCurve(Stream s, bool invert)
    {
        WriteAscii(s, "curv");
        WriteU32(s, 0);
        WriteU32(s, 2);
        WriteU16(s, invert ? 0xFFFF : 0);
        WriteU16(s, invert ? 0 : 0xFFFF);
    }

    /// <summary>A profile carrying only <c>A2B0</c>, which is what most real profiles are.</summary>
    private static byte[] A2BOnlyProfile()
        => BuildB2AProfile("CMYK", 3, 4, 2, new int[2 * 2 * 2 * 4], "mft2", tag: "A2B0");

    /// <summary>
    /// A structurally valid ICC profile with one tag: header, one-entry tag table, and a
    /// <c>lut16Type</c> table with identity curves and the caller's CLUT.
    /// </summary>
    internal static byte[] BuildB2AProfile(
        string dataSpace, int inputs, int outputs, int grid, int[] clut, string type,
        string tag = "B2A0", string pcs = "Lab ", bool invertedOutputCurves = false,
        bool invertedInputCurves = false, int[] matrix = null, int curveEntries = 2)
    {
        var lut = new MemoryStream();
        WriteAscii(lut, type);
        WriteU32(lut, 0);
        lut.WriteByte((byte)inputs);
        lut.WriteByte((byte)outputs);
        lut.WriteByte((byte)grid);
        lut.WriteByte(0);

        // The matrix, s15Fixed16 — only consulted for an XYZ PCS, present regardless; identity by default.
        int[] identity = { 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000 };
        foreach (int v in matrix ?? identity) WriteU32(lut, (uint)v);

        WriteU16(lut, curveEntries);   // input curve entries
        WriteU16(lut, curveEntries);   // output curve entries

        // An identity ramp of curveEntries points, or its reverse.
        void Curve(bool inverted)
        {
            for (int e = 0; e < curveEntries; e++)
            {
                int v = (int)((long)e * 0xFFFF / (curveEntries - 1));
                WriteU16(lut, inverted ? 0xFFFF - v : v);
            }
        }

        for (int i = 0; i < inputs; i++) Curve(invertedInputCurves);
        foreach (int sample in clut) WriteU16(lut, sample);
        for (int i = 0; i < outputs; i++) Curve(invertedOutputCurves);

        return WrapInProfile(dataSpace, pcs, tag, lut.ToArray());
    }

    /// <summary>A header and a one-entry tag table around one tag.</summary>
    private static byte[] WrapInProfile(string dataSpace, string pcs, string tag, byte[] tagData)
    {
        const int headerBytes = 128;
        int tableBytes = 4 + 12;
        int tagOffset = headerBytes + tableBytes;

        var profile = new MemoryStream();
        WriteU32(profile, (uint)(tagOffset + tagData.Length));   // profile size
        while (profile.Length < 16) profile.WriteByte(0);
        WriteAscii(profile, dataSpace);                          // data colour space @16
        WriteAscii(profile, pcs);                                // PCS @20
        while (profile.Length < 36) profile.WriteByte(0);
        WriteAscii(profile, "acsp");                             // file signature @36
        while (profile.Length < headerBytes) profile.WriteByte(0);

        WriteU32(profile, 1);                                    // tag count
        WriteAscii(profile, tag);
        WriteU32(profile, (uint)tagOffset);
        WriteU32(profile, (uint)tagData.Length);
        profile.Write(tagData, 0, tagData.Length);

        return profile.ToArray();
    }

    private static void WriteAscii(Stream s, string text)
    {
        byte[] b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
    }

    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void WriteU16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
}
