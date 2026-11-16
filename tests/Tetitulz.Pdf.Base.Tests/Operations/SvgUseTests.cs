// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> draws <c>&lt;use&gt;</c> (SVG 1.1 §5.6): the referenced element
/// again, after the use's transform and a translation by its <c>x</c> and <c>y</c>, inheriting from
/// the use rather than from where it is defined.
/// </summary>
/// <remarks>
/// A <c>&lt;use&gt;</c> was reported and dropped, so an icon sprite, a repeated marker or any
/// drawing an editor deduplicates came out with holes. A reference can reach its own container or
/// double at every level, so the expansion is bounded (<see cref="PdfLimits.MaxSvgUseExpansions"/>)
/// and a cycle is refused, each reported.
/// </remarks>
public class SvgUseTests
{
    private const string Defs = @"<defs><rect id=""r"" width=""10"" height=""10""/></defs>";

    [Fact]
    public void AUseDrawsItsTargetAtItsPosition()
    {
        string c = Content(Defs + @"<use href=""#r"" x=""20"" y=""30""/>");

        Assert.Equal(new[] { 20f, 30, 10, 10 }, Assert.Single(Operands(c, "re")));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    [Fact]
    public void AnXlinkHrefIsRead()
    {
        string c = Content(Defs + @"<use xlink:href=""#r"" x=""5""/>", @" xmlns:xlink=""http://www.w3.org/1999/xlink""");

        Assert.Equal(new[] { 5f, 0, 10, 10 }, Assert.Single(Operands(c, "re")));
    }

    /// <summary>The use's transform applies first to the translation: scale(2) with x=5 draws at 10, twice the size.</summary>
    [Fact]
    public void TheUsesTransformAppliesToItsTranslation()
    {
        string c = Content(Defs + @"<use href=""#r"" x=""5"" transform=""scale(2)""/>");

        Assert.Equal(new[] { 10f, 0, 20, 20 }, Assert.Single(Operands(c, "re")));
    }

    /// <summary>
    /// The copy inherits from the use — blue — and not from the group that defines it — green.
    /// </summary>
    [Fact]
    public void TheCopyInheritsFromTheUse()
    {
        string c = Content(
            @"<defs><g fill=""#00ff00""><rect id=""r"" width=""10"" height=""10""/></g></defs>" +
            @"<use href=""#r"" fill=""#0000ff""/>");

        Assert.Contains("0.0000 0.0000 1.0000 rg", Lines(c));
        Assert.DoesNotContain("0.0000 1.0000 0.0000 rg", Lines(c));
    }

    /// <summary>A definition is drawn only where it is used: two uses, two rects.</summary>
    [Fact]
    public void ADefinitionIsDrawnWhereItIsUsed()
    {
        string c = Content(Defs + @"<use href=""#r""/><use href=""#r"" x=""20""/>");

        Assert.Equal(2, Operands(c, "re").Length);
    }

    [Theory]
    [InlineData(@"<use href=""#nothing""/>", "#nothing")]
    [InlineData(@"<use href=""other.svg#r""/>", "other.svg#r")]
    [InlineData(@"<use/>", "no href")]
    public void AReferenceThatCannotBeDrawnIsReported(string use, string named)
    {
        string c = Content(Defs + use);

        Assert.Empty(Operands(c, "re"));
        Assert.Contains(named, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>Of two elements with one id, the first is the one a use draws, as <c>getElementById</c> finds it.</summary>
    [Fact]
    public void ADuplicateIdDrawsTheFirst()
    {
        string c = Content(
            @"<defs><rect id=""r"" width=""10"" height=""10""/><rect id=""r"" width=""5"" height=""5""/></defs><use href=""#r""/>");

        Assert.Equal(new[] { 0f, 0, 10, 10 }, Assert.Single(Operands(c, "re")));
    }

    /// <summary>A group holding a use of itself is drawn once and the cycle reported.</summary>
    [Fact]
    public void ACycleIsRefusedAndReported()
    {
        string c = Content(@"<g id=""a""><rect width=""10"" height=""10""/><use href=""#a"" x=""20""/></g>");

        Assert.Single(Operands(c, "re"));
        Assert.Contains("contains it", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Hostile input: 30 levels, each using the one below twice, ask for 2^30 rects. The
    /// expansion stops at the limit and says so; nothing near a billion is drawn.
    /// </summary>
    [Fact]
    public void AUseBombIsBoundedAndReported()
    {
        var svg = new StringBuilder(@"<defs><g id=""g0""><rect width=""1"" height=""1""/></g>");
        for (int i = 1; i <= 30; i++)
            svg.Append($@"<g id=""g{i}""><use href=""#g{i - 1}""/><use href=""#g{i - 1}""/></g>");
        svg.Append(@"</defs><use href=""#g30""/>");

        string c = Content(svg.ToString());

        int drawn = Operands(c, "re").Length;
        Assert.InRange(drawn, 1, PdfLimits.MaxSvgUseExpansions);
        Assert.Contains("<use> references past", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>The control: a sprite of ten elements used a hundred times is drawn whole, a thousand rectangles, with no report.</summary>
    [Fact]
    public void ASmallSpriteUsedManyTimes_IsDrawnWhole()
    {
        string group = @"<defs><g id=""t"">" + string.Concat(Enumerable.Repeat(@"<rect width=""1"" height=""1""/>", 10)) + "</g></defs>";
        string c = Content(group + string.Concat(Enumerable.Repeat(@"<use href=""#t""/>", 100)));

        Assert.Equal(1_000, Operands(c, "re").Length);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    // ------------------------------------------------------------------ helpers

    private static string Content(string body, string rootAttributes = "")
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg""" + rootAttributes + @" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();

    private static float[][] Operands(string content, string op)
        => Lines(content)
            .Where(l => l.EndsWith(" " + op, StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            .Select(parts => parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
}

[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class SvgUseCopyBudgetTests
{
    /// <summary>
    /// A group of two thousand rectangles referenced two thousand times: inside the reference count,
    /// each reference cloning the group whole — 86 KB of SVG converted in 21 s, allocated 8 GB and
    /// wrote 4 million rectangles. The copied elements are charged now, and the references past the
    /// ceiling are skipped and reported.
    /// </summary>
    [Fact]
    public void AdversarialLargeGroupUsedManyTimes_IsBoundedByTheCopiedElements()
    {
        string group = @"<defs><g id=""t"">" + string.Concat(Enumerable.Repeat(@"<rect width=""1"" height=""1""/>", 2_000)) + "</g></defs>";
        string uses = string.Concat(Enumerable.Repeat(@"<use href=""#t""/>", 2_000));

        try
        {
            PdfLimits.MaxSvgUseExpandedElements = 20_000;
            byte[] pdf = SvgToPdfConverter.Convert(
                @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + group + uses + "</svg>",
                new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            string c = Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());

            int drawn = c.Split('\n').Count(l => l.TrimEnd().EndsWith(" re", StringComparison.Ordinal));
            Assert.InRange(drawn, 2_000, 20_000);   // was 4 000 000
            Assert.Contains("copied elements were skipped", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
