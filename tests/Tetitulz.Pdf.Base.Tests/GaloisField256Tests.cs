// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// The GF(256) arithmetic behind QR and Data Matrix, which those two encoders carried a
/// token-for-token copy of each until they were merged.
/// </summary>
/// <remarks>
/// <b>The primitive polynomial is the one thing the merge could get wrong, and nothing would have
/// noticed.</b> Making the shared field ignore its constructor argument and always use QR's 0x11D
/// left the entire Base suite green: no test read a Data Matrix error-correction codeword. A Data
/// Matrix symbol built over the wrong field still has the right size, the right modules in the
/// right places and the right data region — it simply does not scan. That is the failure these
/// tests exist for, and it is why they compare the two fields against each other rather than
/// asserting that either "returns something".
/// </remarks>
public class GaloisField256Tests
{
    /// <summary>
    /// The polynomial first bites at α⁸ — below that the two fields agree by construction, because
    /// doubling has not yet overflowed 8 bits. Degree 10 is comfortably past it.
    /// </summary>
    private const int DegreePastAlpha8 = 10;

    private static GaloisField256 Qr() => new GaloisField256(GaloisField256.QrPrimitive, 32);
    private static GaloisField256 Dm() => new GaloisField256(GaloisField256.DataMatrixPrimitive, 32);

    [Fact]
    public void TheTwoStandardsAreDifferentFieldsAndProduceDifferentGenerators()
    {
        int[] qr = Qr().Generator(DegreePastAlpha8);
        int[] dm = Dm().Generator(DegreePastAlpha8);

        Assert.Equal(qr.Length, dm.Length);          // same degree, so same shape
        Assert.NotEqual(qr, dm);                     // …and different coefficients
    }

    [Fact]
    public void TheTwoStandardsProduceDifferentErrorCorrectionForTheSameData()
    {
        int[] data = { 0x41, 0x92, 0x0B, 0xEF, 0x77, 0x10, 0xFF, 0x01 };

        int[] qr = Qr().ErrorCorrection(data, DegreePastAlpha8);
        int[] dm = Dm().ErrorCorrection(data, DegreePastAlpha8);

        Assert.Equal(DegreePastAlpha8, qr.Length);
        Assert.Equal(DegreePastAlpha8, dm.Length);
        Assert.NotEqual(qr, dm);
    }

    /// <summary>
    /// Below α⁸ the two fields necessarily agree, which is why a small-degree fixture cannot detect
    /// a wrong polynomial. Pinned so the choice of degree 10 above reads as deliberate rather than
    /// arbitrary, and so a future simplification to "degree 2" fails instead of quietly passing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BelowAlpha8TheTwoFieldsAgreeWhichIsWhyASmallFixtureProvesNothing(int degree)
    {
        Assert.Equal(Qr().Generator(degree), Dm().Generator(degree));
    }

    /// <summary>
    /// Every generator is monic and one degree longer than its index — the structural invariant of
    /// (x − α⁰)…(x − α^(n−1)), which catches an off-by-one in the construction loop that the
    /// field-comparison tests above would not.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(26)]
    public void AGeneratorIsMonicAndOneLongerThanItsDegree(int degree)
    {
        int[] g = Qr().Generator(degree);

        Assert.Equal(degree + 1, g.Length);
        Assert.Equal(1, g[0]);
    }

    /// <summary>
    /// A degree beyond the precomputed table is computed on the spot and must match what the table
    /// would have held — the fallback path, which no encoder reaches today and which would
    /// therefore rot silently.
    /// </summary>
    [Fact]
    public void ADegreeBeyondThePrecomputedTableStillComputesTheSameGenerator()
    {
        var small = new GaloisField256(GaloisField256.QrPrimitive, maxGeneratorDegree: 4);
        var large = new GaloisField256(GaloisField256.QrPrimitive, maxGeneratorDegree: 20);

        Assert.Equal(large.Generator(12), small.Generator(12));
    }

    /// <summary>
    /// Every error-correction codeword is a GF(256) element, so it fits in a byte. This is what
    /// makes the QR encoder's narrowing to <c>byte</c> lossless, and therefore what makes the two
    /// element types (byte[] there, int[] here) not a divergence.
    /// </summary>
    [Fact]
    public void EveryCodewordFitsInAByte()
    {
        int[] data = { 0xFF, 0xFE, 0x80, 0x7F, 0x00, 0x01 };

        foreach (int c in Dm().ErrorCorrection(data, 20))
            Assert.InRange(c, 0, 255);
    }
}
