// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <c>ContentStreamParser</c>'s operand nesting is bounded by <see cref="PdfLimits.MaxObjectGraphDepth"/>:
/// a <c>[</c> or <c>&lt;&lt;</c> met at the limit reads as <c>null</c> and what it enclosed becomes
/// the enclosing container's items. The recursion was unbounded, so two hundred thousand <c>[</c> —
/// a 200 KB stream, a few hundred bytes deflated — overflowed the stack, which nothing catches.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public class ContentStreamNestingDepthTests
{
    /// <summary>
    /// The hostile stream, at the default limit: one <c>TJ</c> whose operand nests exactly to the
    /// limit, and the number past the limit still arrives, in the deepest array.
    /// </summary>
    [Fact]
    public void AHostileRunOfOpeningBrackets_IsBoundedAtTheObjectGraphDepth()
    {
        const int brackets = 200_000;
        var sb = new StringBuilder(2 * brackets + 8);
        sb.Append('[', brackets).Append(" 1 ").Append(']', brackets).Append(" TJ");

        var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes(sb.ToString()));

        var op = Assert.Single(ops);
        Assert.Equal("TJ", op.Operator);
        var operand = Assert.Single(op.Operands);
        Assert.Equal(PdfLimits.DefaultMaxObjectGraphDepth, Depth(operand, out PdfArray deepest));
        Assert.True(Holds(deepest, item => item is PdfNumber n && n.IntValue == 1), "the number past the limit");
        Assert.True(Holds(deepest, item => item is PdfNull), "a null for a bracket past the limit");
    }

    /// <summary>The limit is the parameter: at 3, a five-deep array stops at three; at the default, it is five deep.</summary>
    [Theory]
    [InlineData(3, 3)]
    [InlineData(PdfLimits.DefaultMaxObjectGraphDepth, 5)]
    public void ArraysNestToTheObjectGraphDepthLimit(int limit, int expectedDepth)
    {
        PdfLimits.MaxObjectGraphDepth = limit;
        try
        {
            var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes("[[[[[ 1 ]]]]] TJ"));

            var op = Assert.Single(ops);
            Assert.Equal(expectedDepth, Depth(op.Operands[0], out PdfArray deepest));
            Assert.True(Holds(deepest, item => item is PdfNumber n && n.IntValue == 1), "the number");
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>Dictionaries are bounded by the same count: at 1, the inner dictionary is null; at the default, it is read.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(PdfLimits.DefaultMaxObjectGraphDepth, true)]
    public void DictionariesNestToTheObjectGraphDepthLimit(int limit, bool innerIsRead)
    {
        PdfLimits.MaxObjectGraphDepth = limit;
        try
        {
            var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes("/Span << /Outer << /Inner 1 >> >> BDC"));

            var op = Assert.Single(ops);
            Assert.Equal("BDC", op.Operator);
            var outer = Assert.IsType<PdfDictionary>(op.Operands[1]);
            if (innerIsRead)
            {
                var inner = Assert.IsType<PdfDictionary>(outer[new PdfName("Outer")]);
                Assert.Equal(1, Assert.IsType<PdfNumber>(inner[new PdfName("Inner")]).IntValue);
            }
            else
            {
                Assert.IsType<PdfNull>(outer[new PdfName("Outer")]);
            }
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private static bool Holds(PdfArray array, System.Func<PdfObject, bool> test)
    {
        for (int i = 0; i < array.Count; i++)
            if (test(array[i])) return true;
        return false;
    }

    /// <summary>How many arrays enclose the first non-array item, and the innermost of them.</summary>
    private static int Depth(PdfObject value, out PdfArray deepest)
    {
        int depth = 0;
        deepest = null;
        while (value is PdfArray array)
        {
            depth++;
            deepest = array;
            value = array.Count > 0 ? array[0] : null;
        }
        return depth;
    }
}
