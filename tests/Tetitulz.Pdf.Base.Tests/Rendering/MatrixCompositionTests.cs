// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.3.3 matrix composition, and the argument-order convention four separate copies of it were
/// relying on everyone remembering.
///
/// <para><c>Compose</c> existed in <c>GraphicsState</c>, <c>PdfRedactionEngine</c>,
/// <c>PdfTemplateEngine</c> and <c>PdfStructuredTextExtractor</c>. All four were correct and
/// byte-identical — the state the CMYK conversion was in before one of its nine copies got fixed —
/// and one already carried the tell, a comment reading "matches
/// PdfStructuredTextExtractor.Compose". They now forward to one implementation.</para>
///
/// <para><b>What is worth a test is the convention, not the arithmetic.</b> Reversing the arguments
/// produces a matrix that is well-formed, finite and wrong, so nothing downstream can notice. These
/// assert on a transformed <i>point</i>, where the two orders give different answers, rather than on
/// the six numbers, where "it returned a matrix" says nothing.</para>
///
/// <para>Tested through <c>GraphicsState.Compose</c> because that is the public face of it; the
/// three operations-side copies now forward at compile time, which the compiler checks and a test
/// cannot add to.</para>
/// </summary>
public sealed class MatrixCompositionTests
{
    private static readonly float[] ScaleBy2    = { 2, 0, 0, 2, 0, 0 };
    private static readonly float[] TranslateX10 = { 1, 0, 0, 1, 10, 0 };

    /// <summary>
    /// <c>Compose(A, B)</c> is "A applied first, then B" — the order a <c>cm</c> concatenation
    /// means. Scale then translate takes (1,1) to (12,2).
    /// </summary>
    [Fact]
    public void Compose_AppliesTheFirstArgumentFirst()
    {
        float[] m = GraphicsState.Compose(ScaleBy2, TranslateX10);

        var (x, y) = GraphicsState.Transform(m, 1, 1);
        Assert.Equal(12f, x, 4);
        Assert.Equal(2f, y, 4);
    }

    /// <summary>
    /// The reverse order is a different transform, which is what makes the test above a claim.
    /// Translate then scale takes (1,1) to (22,2).
    /// </summary>
    [Fact]
    public void ReversingTheArguments_IsADifferentTransform()
    {
        float[] m = GraphicsState.Compose(TranslateX10, ScaleBy2);

        var (x, y) = GraphicsState.Transform(m, 1, 1);
        Assert.Equal(22f, x, 4);
        Assert.Equal(2f, y, 4);
    }

    /// <summary>Composing with the identity on either side changes nothing.</summary>
    [Fact]
    public void ComposingWithTheIdentity_IsANoOp()
    {
        float[] identity = { 1, 0, 0, 1, 0, 0 };
        float[] arbitrary = { 3, 1, -2, 4, 7, -5 };

        Assert.Equal(arbitrary, GraphicsState.Compose(arbitrary, identity));
        Assert.Equal(arbitrary, GraphicsState.Compose(identity, arbitrary));
    }

    /// <summary>
    /// Composition is associative, which is the property every nested-form-XObject CTM stack relies
    /// on: the renderer concatenates one matrix at a time and must land where composing the whole
    /// chain first would.
    /// </summary>
    [Fact]
    public void CompositionIsAssociative()
    {
        float[] rotate90 = { 0, 1, -1, 0, 0, 0 };

        float[] left  = GraphicsState.Compose(GraphicsState.Compose(ScaleBy2, rotate90), TranslateX10);
        float[] right = GraphicsState.Compose(ScaleBy2, GraphicsState.Compose(rotate90, TranslateX10));

        for (int i = 0; i < 6; i++) Assert.Equal(left[i], right[i], 4);
    }

    /// <summary>
    /// The translation components compose through the rotation, which is the part a naive
    /// element-wise implementation gets wrong: a translate-then-rotate must move the origin.
    /// </summary>
    [Fact]
    public void TranslationIsCarriedThroughTheLinearPart()
    {
        float[] rotate90 = { 0, 1, -1, 0, 0, 0 };

        float[] m = GraphicsState.Compose(TranslateX10, rotate90);
        var (x, y) = GraphicsState.Transform(m, 0, 0);

        // (0,0) translates to (10,0), which rotating 90° takes to (0,10).
        Assert.Equal(0f, x, 4);
        Assert.Equal(10f, y, 4);
    }
}
