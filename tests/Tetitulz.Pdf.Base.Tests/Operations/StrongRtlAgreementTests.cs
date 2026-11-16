// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The extractor's strong-RTL predicate must agree with the bidi classifier the reorder uses,
/// because a single method consults both.
/// </summary>
/// <remarks>
/// <para><c>PdfStructuredTextExtractor.ExtractLines</c> picks its glyph sort direction and stamps
/// <c>PdfTextWord.IsRtl</c> from <c>TextDirection.IsRtlGroup</c>, then builds the line text through
/// <c>ToReadingOrder</c>, which routes to <c>UnicodeBidi.Reorder</c>. When the two disagree the
/// result is not merely inconsistent — a Syriac line came out with bidi-reordered line text beside
/// visual-order words all reporting <c>IsRtl == false</c>, and with the extractor's default
/// <c>ApplyBidi = false</c> the whole word was simply reversed.</para>
///
/// <para><c>IsRtlChar</c> listed seven ranges by hand and covered only <c>0750–077F</c> and
/// <c>08A0–08FF</c> of the <c>0700–08FF</c> block the classifier returns <c>AL</c> for. Six scripts
/// fell in that gap.</para>
/// </remarks>
public class StrongRtlAgreementTests
{
    /// <summary>
    /// <b>The gap.</b> Every one of these was strong RTL to Core and left-to-right to the extractor.
    /// </summary>
    [Theory]
    [InlineData(0x0710, "Syriac alaph")]
    [InlineData(0x0780, "Thaana haa")]
    [InlineData(0x07C0, "NKo digit zero")]
    [InlineData(0x0800, "Samaritan alaf")]
    [InlineData(0x0840, "Mandaic halqa")]
    [InlineData(0x0860, "Syriac Supplement")]
    [InlineData(0x0870, "Arabic Extended-B")]
    public void AScriptInTheGapIsStrongRtlToBothSides(int codePoint, string what)
    {
        char c = (char)codePoint;

        Assert.True(UnicodeBidi.IsStrongRtl(c), what);
        Assert.True(TextDirection.IsRtlChar(c), what);
    }

    /// <summary>
    /// The scripts that always worked, so the theory above cannot pass by the predicate having been
    /// widened into "always true".
    /// </summary>
    [Theory]
    [InlineData(0x05D0)]   // Hebrew alef
    [InlineData(0x0627)]   // Arabic alef
    [InlineData(0xFB2A)]   // Hebrew presentation form
    [InlineData(0xFE8D)]   // Arabic presentation form B
    public void TheScriptsThatAlwaysWorkedStillDo(int codePoint)
    {
        char c = (char)codePoint;

        Assert.True(UnicodeBidi.IsStrongRtl(c));
        Assert.True(TextDirection.IsRtlChar(c));
    }

    /// <summary>
    /// <b>The floor.</b> Left-to-right and neutral characters must be RTL to neither — this is what
    /// stops the fix being "return true".
    /// </summary>
    [Theory]
    [InlineData('A')]
    [InlineData('z')]
    [InlineData('0')]
    [InlineData(' ')]
    [InlineData('.')]
    [InlineData((char)0x00E9)]   // Latin e-acute
    [InlineData((char)0x0400)]   // Cyrillic
    [InlineData((char)0x4E00)]   // CJK
    [InlineData((char)0x0900)]   // Devanagari - just past the RTL block
    [InlineData((char)0xFB06)]   // the Latin ligature inside the presentation-forms range
    public void ANonRtlCharacterIsRtlToNeitherSide(char c)
    {
        Assert.False(UnicodeBidi.IsStrongRtl(c));
        Assert.False(TextDirection.IsRtlChar(c));
    }

    /// <summary>
    /// The general claim, swept over the whole BMP: the two predicates are the same function.
    /// </summary>
    /// <remarks>
    /// Now that one forwards to the other this is close to a tautology, and it is kept for the same
    /// reason as the other post-merge agreement tests here — it fails the moment someone
    /// re-introduces a hand-written range list on either side, which is exactly how this defect
    /// arose. The correctness weight sits on the named cases above.
    /// </remarks>
    [Fact]
    public void TheTwoPredicatesAgreeAcrossTheWholeBasicMultilingualPlane()
    {
        var disagreements = new List<int>();
        for (int cp = 0; cp <= 0xFFFF; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF) continue;   // surrogates are not characters
            char c = (char)cp;
            if (UnicodeBidi.IsStrongRtl(c) != TextDirection.IsRtlChar(c)) disagreements.Add(cp);
        }

        Assert.Empty(disagreements.Take(20));
    }
}
