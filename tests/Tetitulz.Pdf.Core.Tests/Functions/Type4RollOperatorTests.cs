// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Functions;

/// <summary>
/// The Type 4 <c>roll</c> operator, against the worked examples in the PostScript Language
/// Reference Manual §8.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two defects sat on one line.</b> PostScript is <c>any(n-1) … any0 n j roll</c>, so <c>j</c>
/// is pushed last and pops first — but the code read them with
/// <c>int rn = (int)Pop(stack), rj = (int)Pop(stack);</c>, and C# evaluates declarators left to
/// right, so the SHIFT was taken as the count and the count as the shift. The rotation direction
/// was inverted on top of that: the loop moved the bottom-most element of the segment to the front
/// (a right rotation) where PLRM's positive <c>j</c> requires the top element to move to the
/// bottom.
/// </para>
/// <para>
/// The visible cost is colour. A Separation or DeviceN <c>/TintTransform</c> of the standard CMYK
/// shape <c>{ 4 1 roll 1 exch sub 3 { 3 index add neg 1 add 0 max 4 1 roll } repeat pop pop pop }</c>
/// evaluated against an unrotated stack, so every tint painted the wrong ink. With the old code
/// <c>3 1 roll</c> was a silent no-op (<c>rn</c> = 1, so the segment was one element) and
/// <c>3 -1 roll</c> was skipped entirely (<c>rn</c> = -1 fails the <c>rn &gt; 0</c> guard) — both
/// returning the stack unchanged, which is why nothing ever threw.
/// </para>
/// <para>
/// Results are read bottom-of-stack first, which is the order <c>EvalType4</c> returns.
/// </para>
/// </remarks>
public class Type4RollOperatorTests
{
    /// <summary>PLRM: <c>1 2 3  3 1 roll</c> ⇒ <c>3 1 2</c>.</summary>
    [Fact]
    public void Roll_PositiveJ_MovesTopElementToTheBottom()
    {
        float[] result = Run("{ 3 1 roll }", 1f, 2f, 3f);

        Assert.Equal(new[] { 3f, 1f, 2f }, result);
    }

    /// <summary>PLRM: <c>1 2 3  3 -1 roll</c> ⇒ <c>2 3 1</c>.</summary>
    [Fact]
    public void Roll_NegativeJ_MovesBottomElementToTheTop()
    {
        float[] result = Run("{ 3 -1 roll }", 1f, 2f, 3f);

        Assert.Equal(new[] { 2f, 3f, 1f }, result);
    }

    /// <summary>
    /// <c>j = 0</c> is the identity. This is the case the old code accidentally got right for
    /// <c>3 1 roll</c>, so asserting it alone would have proved nothing — it is here to pin that
    /// the fix did not turn a no-op into a rotation.
    /// </summary>
    [Fact]
    public void Roll_ZeroJ_LeavesTheStackUnchanged()
    {
        float[] result = Run("{ 3 0 roll }", 1f, 2f, 3f);

        Assert.Equal(new[] { 1f, 2f, 3f }, result);
    }

    /// <summary>
    /// <c>j</c> larger than <c>n</c> wraps: <c>4 5 roll</c> is <c>4 1 roll</c>.
    /// </summary>
    [Fact]
    public void Roll_ShiftLargerThanCount_WrapsModuloCount()
    {
        float[] rolledByFive = Run("{ 4 5 roll }", 1f, 2f, 3f, 4f);
        float[] rolledByOne = Run("{ 4 1 roll }", 1f, 2f, 3f, 4f);

        Assert.Equal(new[] { 4f, 1f, 2f, 3f }, rolledByFive);
        Assert.Equal(rolledByOne, rolledByFive);
    }

    /// <summary>
    /// <c>roll</c> touches only the top <c>n</c> elements; anything deeper must be left where it is.
    /// Reading the operands the wrong way round produced a segment of the wrong size, so this is
    /// the assertion that catches a swap which happens to leave the visible top in place.
    /// </summary>
    [Fact]
    public void Roll_LeavesElementsBelowTheSegmentUntouched()
    {
        float[] result = Run("{ 3 1 roll }", 9f, 1f, 2f, 3f);

        Assert.Equal(new[] { 9f, 3f, 1f, 2f }, result);
    }

    // ----------------------------------------------------------------
    //  Helper
    // ----------------------------------------------------------------

    /// <summary>
    /// Evaluates a Type 4 program over <paramref name="inputs"/>, returning the stack
    /// bottom-first. Domain and Range are wide so no value is clamped on the way in or out.
    /// </summary>
    private static float[] Run(string psCode, params float[] inputs)
    {
        var fn = new PdfStream();
        fn.Set(PdfName.FunctionType, new PdfNumber(4));

        var domain = new PdfArray();
        var range = new PdfArray();
        for (int i = 0; i < inputs.Length; i++)
        {
            domain.Add(new PdfNumber(-1000)); domain.Add(new PdfNumber(1000));
            range.Add(new PdfNumber(-1000)); range.Add(new PdfNumber(1000));
        }
        fn.Set(PdfName.Domain, domain);
        fn.Set(PdfName.Range, range);
        fn.RawData = System.Text.Encoding.ASCII.GetBytes(psCode);

        return PdfFunctionEvaluator.Evaluate(fn, inputs);
    }
}
