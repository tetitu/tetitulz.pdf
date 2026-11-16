// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Functions;

/// <summary>
/// Pins the Type 4 <c>roll</c> operator against the PostScript operator definition
/// (§7.10.5 / PLRM "roll"), which spells it <c>n j roll</c>: <b>j is pushed last and pops
/// first</b>, and a positive j moves each element of the top-n segment one place toward the
/// <b>top</b>, with the top element wrapping round to the bottom.
///
/// <para>Both halves of that were wrong. The operands were read as
/// <c>int rn = (int)Pop(stack), rj = (int)Pop(stack);</c> — C# evaluates declarators left to
/// right, so the count received j and the shift received n — and the rotation ran the other
/// way. The two together made <c>1 2 3  3 1 roll</c> a silent no-op and
/// <c>1 2 3  3 -1 roll</c> (a negative count after the swap) a silent no-op as well, so every
/// DeviceN / Separation <c>/TintTransform</c> of the standard CMYK shape
/// <c>{ 4 1 roll 1 exch sub 3 { 3 index add neg 1 add 0 max 4 1 roll } repeat pop pop pop }</c>
/// evaluated against an unrotated stack and painted the wrong ink.</para>
///
/// <para><b>Every expected value below is computed from the operator definition, not from the
/// implementation</b> — the point of the fix is that the previous output was self-consistent
/// and wrong, so a baseline captured from it would have pinned the defect.
/// <c>Plugins.PostScript</c>'s <c>PsStackOps.Roll</c> is the second implementation of the same
/// rule and agrees with these.</para>
/// </summary>
public class PostScriptType4RollTests
{
    // a b c  3 1 roll → c a b   (PLRM's own example, renamed to 1 2 3 → 3 1 2)
    [Fact]
    public void Roll_PositiveShift_MovesTopElementToTheBottomOfTheSegment()
    {
        var fn = BuildType4Function("{ pop 1 2 3 3 1 roll }", outputCount: 3);
        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });

        // Stack bottom→top must be 3 1 2; output i is the i-th value from the bottom.
        Assert.Equal(new[] { 3f, 1f, 2f }, r);
    }

    // a b c  3 -1 roll → b c a
    [Fact]
    public void Roll_NegativeShift_MovesBottomElementToTheTopOfTheSegment()
    {
        var fn = BuildType4Function("{ pop 1 2 3 3 -1 roll }", outputCount: 3);
        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });

        Assert.Equal(new[] { 2f, 3f, 1f }, r);
    }

    [Fact]
    public void Roll_ShiftOfZero_LeavesTheSegmentUntouched()
    {
        var fn = BuildType4Function("{ pop 1 2 3 3 0 roll }", outputCount: 3);
        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });

        Assert.Equal(new[] { 1f, 2f, 3f }, r);
    }

    [Fact]
    public void Roll_ShiftLargerThanSegment_WrapsModuloTheCount()
    {
        // 3 4 roll ≡ 3 1 roll.
        var fn = BuildType4Function("{ pop 1 2 3 3 4 roll }", outputCount: 3);
        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });

        Assert.Equal(new[] { 3f, 1f, 2f }, r);
    }

    [Fact]
    public void Roll_OnlyRotatesTheTopN_LeavingDeeperOperandsInPlace()
    {
        // 9 sits below the rolled window and must not move: 9 1 2 3, roll the top 3 by 1.
        var fn = BuildType4Function("{ pop 9 1 2 3 3 1 roll }", outputCount: 4);
        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });

        Assert.Equal(new[] { 9f, 3f, 1f, 2f }, r);
    }

    /// <summary>
    /// The shape a real <c>/TintTransform</c> opens with: four colorants on the stack and
    /// <c>4 1 roll</c> bringing the last one to the bottom. This is the case whose wrong answer
    /// reached the page as the wrong colour.
    /// </summary>
    [Fact]
    public void Roll_OverFourColorantInputs_RotatesThemAsTheTintTransformExpects()
    {
        // Inputs are pushed in order, so the stack is bottom→top 0.1 0.2 0.3 0.4.
        // `4 1 roll` must leave 0.4 0.1 0.2 0.3.
        var fn = BuildType4Function("{ 4 1 roll }", inputCount: 4, outputCount: 4);
        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.1f, 0.2f, 0.3f, 0.4f });

        Assert.Equal(0.4f, r[0], 4);
        Assert.Equal(0.1f, r[1], 4);
        Assert.Equal(0.2f, r[2], 4);
        Assert.Equal(0.3f, r[3], 4);
    }

    private static PdfStream BuildType4Function(string psCode, int outputCount, int inputCount = 1)
    {
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));

        var domain = new PdfArray();
        for (int i = 0; i < inputCount; i++)
        {
            domain.Add(new PdfNumber(-10000));
            domain.Add(new PdfNumber(10000));
        }
        fn.Set(new PdfName("Domain"), domain);

        var range = new PdfArray();
        for (int i = 0; i < outputCount; i++)
        {
            range.Add(new PdfNumber(-10000));
            range.Add(new PdfNumber(10000));
        }
        fn.Set(new PdfName("Range"), range);

        fn.RawData = System.Text.Encoding.ASCII.GetBytes(psCode);
        return fn;
    }
}
